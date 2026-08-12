using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.UI;

namespace WinBulkTranscript.CorpusGenerator;

/// <summary>
/// Creates a local, provenance-bound candidate set for the opt-in media failure-fixture matrix.
/// </summary>
/// <remarks>
/// The writer is deliberately opt-in and CreateNew-only. It is not invoked by normal tests and
/// does not establish legal redistribution rights for an installed Windows voice.
/// </remarks>
internal static class MediaFixtureMatrixWriter
{
    private const string MalformedFileName = "malformed-truncated.mp4";
    private const string NoAudioFileName = "no-audio-video-only.mp4";
    private const string EmptyAudioFileName = "empty-audio-track.mp4";
    private const string UnsupportedCodecFileName = "unsupported-audio-codec.mp4";
    private const string ValidControlFileName = "valid-short-control.mp4";
    private const string ManifestFileName = "media-fixture-matrix.provenance.json";
    private const string UnsupportedSampleEntryType = "wbtx";
    private static readonly string[] FixtureFileNames =
    [
        MalformedFileName,
        NoAudioFileName,
        EmptyAudioFileName,
        UnsupportedCodecFileName,
        ValidControlFileName,
    ];
    private static readonly string[] OptionalSampleReferenceBoxTypes =
    [
        "ctts",
        "stss",
        "stps",
        "stsh",
        "sbgp",
        "subs",
    ];
    private static readonly HashSet<string> ContainerBoxTypes = new(StringComparer.Ordinal)
    {
        "dinf",
        "edts",
        "ilst",
        "iprp",
        "meta",
        "mdia",
        "minf",
        "moof",
        "moov",
        "mvex",
        "sinf",
        "stbl",
        "traf",
        "trak",
        "udta",
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>The sole spoken utterance used by the short valid-control MP4.</summary>
    public const string ValidControlText = "This short locally synthesized speech fixture is the valid AAC control for Windows media extraction testing.";

    /// <summary>
    /// Creates every named matrix fixture in an initially empty or non-conflicting local directory.
    /// </summary>
    /// <param name="requestedRoot">The local output directory, never a repository-shipped fixture path.</param>
    /// <param name="media">The explicitly selected Windows speech/media service.</param>
    /// <param name="cancellationToken">A token that stops the operation before publication.</param>
    /// <returns>Published artifact paths and hashes.</returns>
    public static async Task<MediaFixtureMatrixResult> WriteAsync(
        string requestedRoot,
        WindowsTtsAndMedia media,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedRoot);
        ArgumentNullException.ThrowIfNull(media);
        cancellationToken.ThrowIfCancellationRequested();

        var root = Path.GetFullPath(requestedRoot);
        var repositoryTestAssets = Path.Combine(GeneratorOptions.FindRepositoryRoot(), "test-assets");
        if (GeneratorOptions.IsPathWithinDirectory(root, repositoryTestAssets))
        {
            throw new ArgumentException(
                $"The media fixture matrix root '{root}' must be outside repository test-assets. " +
                "Matrix binaries are opt-in evidence artifacts and must not replace or masquerade as shipped test assets.",
                nameof(requestedRoot));
        }
        if (File.Exists(root))
        {
            throw new IOException($"The matrix root '{root}' is an existing file.");
        }

        Directory.CreateDirectory(root);
        var manifestPath = Path.Combine(root, ManifestFileName);
        var finalPaths = FixtureFileNames.Select(fileName => Path.Combine(root, fileName)).ToArray();
        if (finalPaths.Any(File.Exists) || File.Exists(manifestPath))
        {
            throw new IOException("Refusing to overwrite an existing media matrix fixture or provenance sidecar.");
        }

        var stagingRoot = Path.Combine(root, $".media-fixture-matrix-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        var publishedPaths = new List<string>();
        try
        {
            var controlAudio = await media.SynthesizeMasterPcmAsync(ValidControlText, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Creating {ValidControlFileName} from locally synthesized Windows speech.");
            if (controlAudio.SampleCount <= 0)
            {
                throw new InvalidDataException("The selected Windows voice returned no PCM samples for the valid-control fixture.");
            }

            var stagedControlWave = Path.Combine(stagingRoot, "valid-short-control.wav");
            var stagedControlPath = Path.Combine(stagingRoot, ValidControlFileName);
            await PcmWaveFile.WriteAsync(stagedControlWave, controlAudio, cancellationToken).ConfigureAwait(false);
            await WindowsTtsAndMedia.EncodeAudioOnlyMp4Async(stagedControlWave, stagedControlPath, cancellationToken).ConfigureAwait(false);
            var validInspection = await WindowsTtsAndMedia.InspectAsync(stagedControlPath, cancellationToken).ConfigureAwait(false);
            ValidateInspection(stagedControlPath, validInspection, expectedAudioTracks: 1, expectedVideoTracks: 0, requirePositiveDuration: true);

            var stagedVideoOnlyPath = Path.Combine(stagingRoot, NoAudioFileName);
            Console.WriteLine($"Creating {NoAudioFileName} through Windows MediaComposition.");
            await EncodeVideoOnlyMp4Async(stagedVideoOnlyPath, cancellationToken).ConfigureAwait(false);
            var videoOnlyInspection = await WindowsTtsAndMedia.InspectAsync(stagedVideoOnlyPath, cancellationToken).ConfigureAwait(false);
            ValidateInspection(stagedVideoOnlyPath, videoOnlyInspection, expectedAudioTracks: 0, expectedVideoTracks: 1, requirePositiveDuration: true);

            var stagedEmptyWave = Path.Combine(stagingRoot, "empty-audio-track.source.wav");
            Console.WriteLine($"Creating {EmptyAudioFileName} from a native silent AAC source with zeroed sample metadata.");
            var stagedEmptySourcePath = Path.Combine(stagingRoot, "empty-audio-track.source.mp4");
            var stagedEmptyPath = Path.Combine(stagingRoot, EmptyAudioFileName);
            await PcmWaveFile.WriteAsync(stagedEmptyWave, Pcm16Audio.CreateSilence(Pcm16Audio.SampleRate), cancellationToken).ConfigureAwait(false);
            await WindowsTtsAndMedia.EncodeAudioOnlyMp4Async(stagedEmptyWave, stagedEmptySourcePath, cancellationToken).ConfigureAwait(false);
            Console.WriteLine("Native silent AAC source encoded.");
            var emptySourceInspection = await WindowsTtsAndMedia.InspectAsync(stagedEmptySourcePath, cancellationToken).ConfigureAwait(false);
            ValidateInspection(stagedEmptySourcePath, emptySourceInspection, expectedAudioTracks: 1, expectedVideoTracks: 0, requirePositiveDuration: true);
            var emptyAudioTrackValidation = await WriteEmptyAudioTrackMp4Async(stagedEmptySourcePath, stagedEmptyPath, cancellationToken).ConfigureAwait(false);
            Console.WriteLine("Empty-track sample metadata rewritten and structurally verified; extraction behavior will be recorded by MediaIntegrationProbe.");

            var stagedMalformedPath = Path.Combine(stagingRoot, MalformedFileName);
            Console.WriteLine($"Creating {MalformedFileName} with a deliberately truncated ISO-BMFF payload.");
            await WriteDeliberatelyTruncatedMp4Async(stagedMalformedPath, cancellationToken).ConfigureAwait(false);

            var stagedUnsupportedPath = Path.Combine(stagingRoot, UnsupportedCodecFileName);
            Console.WriteLine($"Creating {UnsupportedCodecFileName} by preserving the native container and changing only its audio sample-entry type.");
            var originalSampleEntry = await WriteUnsupportedCodecMp4Async(stagedControlPath, stagedUnsupportedPath, cancellationToken).ConfigureAwait(false);

            var fixtureDetails = new List<MediaFixtureMatrixFixture>
            {
                await CreateFixtureAsync(
                    stagingRoot,
                    MalformedFileName,
                    "An ISO base-media ftyp header followed by an mdat box whose declared payload is deliberately truncated.",
                    "Failure",
                    mediaInspection: null,
                    cancellationToken).ConfigureAwait(false),
                await CreateFixtureAsync(
                    stagingRoot,
                    NoAudioFileName,
                    "Windows MediaComposition solid-color video rendered with an MP4 profile whose Audio property is null.",
                    "Failure",
                    videoOnlyInspection,
                    cancellationToken).ConfigureAwait(false),
                await CreateFixtureAsync(
                    stagingRoot,
                    EmptyAudioFileName,
                    "A native silent AAC MP4 whose movie, track, and media durations plus stts, stsc, stsz, and chunk-offset entry counts are deterministically rewritten to zero. Post-write ISO-BMFF validation proves one audio-only track, zero duration, and no sample references; the sidecar retains those values and source hash.",
                    "Failure or header-only success",
                    mediaInspection: null,
                    emptyAudioTrackStructuralValidation: emptyAudioTrackValidation,
                    cancellationToken: cancellationToken).ConfigureAwait(false),
                await CreateFixtureAsync(
                    stagingRoot,
                    UnsupportedCodecFileName,
                    $"A byte-for-byte copy of {ValidControlFileName} with only its audio stsd sample-entry type changed from '{originalSampleEntry}' to '{UnsupportedSampleEntryType}'; all box lengths remain unchanged.",
                    "Failure",
                    mediaInspection: null,
                    cancellationToken).ConfigureAwait(false),
                await CreateFixtureAsync(
                    stagingRoot,
                    ValidControlFileName,
                    "Locally synthesized Windows speech converted to PCM16 and encoded by Windows MediaTranscoder as audio-only AAC/MP4.",
                    "Success",
                    validInspection,
                    cancellationToken).ConfigureAwait(false),
            };

            var manifest = CreateManifest(media.VoiceManifest, fixtureDetails);
            var stagedManifestPath = Path.Combine(stagingRoot, ManifestFileName);
            await WriteManifestAsync(stagedManifestPath, manifest, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var fileName in FixtureFileNames)
            {
                var sourcePath = Path.Combine(stagingRoot, fileName);
                var destinationPath = Path.Combine(root, fileName);
                File.Move(sourcePath, destinationPath);
                publishedPaths.Add(destinationPath);
            }

            File.Move(stagedManifestPath, manifestPath);
            publishedPaths.Add(manifestPath);
            return new MediaFixtureMatrixResult(root, manifestPath, fixtureDetails);
        }
        catch
        {
            foreach (var path in publishedPaths)
            {
                TryDelete(path);
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private static async Task EncodeVideoOnlyMp4Async(string outputPath, CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("The no-audio fixture path must include a directory.");
        var folder = await StorageFolder.GetFolderFromPathAsync(outputDirectory);
        var destination = await folder.CreateFileAsync(Path.GetFileName(outputPath), CreationCollisionOption.FailIfExists);
        var composition = new MediaComposition();
        composition.Clips.Add(MediaClip.CreateFromColor(
            new Color { A = byte.MaxValue, R = 31, G = 45, B = 61 },
            TimeSpan.FromSeconds(2)));
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Qvga);
        profile.Audio = null;
        var failureReason = await composition
            .RenderToFileAsync(destination, MediaTrimmingPreference.Precise, profile)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (failureReason != TranscodeFailureReason.None)
        {
            throw new InvalidOperationException($"Windows could not render the video-only MP4: {failureReason}.");
        }
    }

    private static async Task WriteDeliberatelyTruncatedMp4Async(string outputPath, CancellationToken cancellationToken)
    {
        var bytes = new byte[35];
        WriteBoxHeader(bytes.AsSpan(0, 8), 24, "ftyp");
        "isom"u8.CopyTo(bytes.AsSpan(8, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12, 4), 0x0000_0200);
        "isom"u8.CopyTo(bytes.AsSpan(16, 4));
        "iso2"u8.CopyTo(bytes.AsSpan(20, 4));
        WriteBoxHeader(bytes.AsSpan(24, 8), 32, "mdat");
        bytes[32] = 0x57;
        bytes[33] = 0x42;
        bytes[34] = 0x54;
        await WriteBytesCreateNewAsync(outputPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<EmptyAudioTrackStructuralValidation> WriteEmptyAudioTrackMp4Async(
        string sourcePath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var sourceBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var sourceLayout = FindSingleAudioTrackLayout(sourceBytes);
        ValidateNonEmptyAudioSource(sourceBytes, sourceLayout);
        var sourceSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes));

        var bytes = sourceBytes.ToArray();
        var layout = FindSingleAudioTrackLayout(bytes);

        ClearRequiredEntryCount(bytes, layout.SampleTableChildren, "stts");
        ClearRequiredEntryCount(bytes, layout.SampleTableChildren, "stsc");
        ClearRequiredSampleCount(bytes, layout.SampleTableChildren);
        ClearRequiredChunkOffsetEntryCount(bytes, layout.SampleTableChildren);

        foreach (var optionalReferenceBoxType in OptionalSampleReferenceBoxTypes)
        {
            TryClearEntryCount(bytes, layout.SampleTableChildren, optionalReferenceBoxType);
        }

        if (layout.EditList is not null)
        {
            ClearEntryCount(bytes, layout.EditList, "elst");
        }

        ZeroDuration(bytes, layout.MovieHeader, "mvhd", 16, 24);
        ZeroDuration(bytes, layout.TrackHeader, "tkhd", 20, 28);
        ZeroDuration(bytes, layout.MediaHeader, "mdhd", 16, 24);

        await WriteBytesCreateNewAsync(outputPath, bytes, cancellationToken).ConfigureAwait(false);
        var finalBytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
        return ValidateEmptyAudioTrackStructure(finalBytes, sourceSha256, sourceBytes.LongLength);

    }

    private static async Task<string> WriteUnsupportedCodecMp4Async(
        string sourcePath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var sampleEntryTypeOffset = FindAacSampleEntryTypeOffset(bytes);
        var originalSampleEntry = System.Text.Encoding.ASCII.GetString(bytes, sampleEntryTypeOffset, 4);
        if (!string.Equals(originalSampleEntry, "mp4a", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The valid AAC control did not expose an mp4a sample entry; found '{originalSampleEntry}'.");
        }

        System.Text.Encoding.ASCII.GetBytes(UnsupportedSampleEntryType).CopyTo(bytes, sampleEntryTypeOffset);
        await WriteBytesCreateNewAsync(outputPath, bytes, cancellationToken).ConfigureAwait(false);
        return originalSampleEntry;
    }

    private static int FindAacSampleEntryTypeOffset(ReadOnlySpan<byte> bytes)
    {
        var matchingOffsets = new List<int>();
        for (var offset = 0; offset <= bytes.Length - 24; offset++)
        {
            if (!bytes.Slice(offset + 4, 4).SequenceEqual("stsd"u8))
            {
                continue;
            }

            var boxSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (boxSize < 24 || boxSize > bytes.Length - offset)
            {
                continue;
            }

            var entryCount = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 12, 4));
            var sampleEntrySize = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 16, 4));
            if (entryCount == 1
                && sampleEntrySize >= 8
                && sampleEntrySize <= boxSize - 16
                && bytes.Slice(offset + 20, 4).SequenceEqual("mp4a"u8))
            {
                matchingOffsets.Add(offset + 20);
            }
        }

        return matchingOffsets.Count switch
        {
            1 => matchingOffsets[0],
            0 => throw new InvalidDataException("The valid AAC control contains no structurally valid mp4a sample entry in an stsd box."),
            _ => throw new InvalidDataException("The valid AAC control contains more than one candidate mp4a sample entry; refusing ambiguous codec mutation."),
        };
    }

    private static List<IsoBox> ParseBoxes(ReadOnlySpan<byte> bytes)
        => ParseBoxes(bytes, 0, bytes.Length);

    private static List<IsoBox> ParseBoxes(ReadOnlySpan<byte> bytes, int startOffset, int endOffset)
    {
        var result = new List<IsoBox>();
        for (var offset = startOffset; offset < endOffset;)
        {
            if (endOffset - offset < 8)
            {
                throw new InvalidDataException("The native AAC MP4 contains a truncated ISO-BMFF box header.");
            }

            var boxSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            var headerSize = 8;
            if (boxSize == 1)
            {
                if (endOffset - offset < 16)
                {
                    throw new InvalidDataException("The native AAC MP4 contains a truncated extended ISO-BMFF box header.");
                }

                var extendedSize = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(offset + 8, 8));
                if (extendedSize > int.MaxValue)
                {
                    throw new InvalidDataException("The native AAC MP4 contains an ISO-BMFF box too large for the local matrix generator.");
                }

                boxSize = checked((uint)extendedSize);
                headerSize = 16;
            }
            else if (boxSize == 0)
            {
                boxSize = checked((uint)(endOffset - offset));
            }

            if (boxSize < headerSize || boxSize > endOffset - offset)
            {
                throw new InvalidDataException("The native AAC MP4 contains an invalid ISO-BMFF box size.");
            }

            var type = System.Text.Encoding.ASCII.GetString(bytes.Slice(offset + 4, 4));
            var size = checked((int)boxSize);
            IReadOnlyList<IsoBox>? children = null;
            if (ContainerBoxTypes.Contains(type))
            {
                var childStart = checked(offset + headerSize);
                if (string.Equals(type, "meta", StringComparison.Ordinal))
                {
                    if (size - headerSize < 4)
                    {
                        throw new InvalidDataException("The native AAC MP4 contains a truncated meta full-box header.");
                    }

                    childStart += 4;
                }

                children = ParseBoxes(bytes, childStart, checked(offset + size));
            }

            result.Add(new IsoBox(type, offset, size, headerSize, children));
            offset = checked(offset + size);
        }

        return result;
    }

    private static IEnumerable<IsoBox> FindBoxes(IEnumerable<IsoBox> boxes, string type)
    {
        foreach (var box in boxes)
        {
            if (string.Equals(box.Type, type, StringComparison.Ordinal))
            {
                yield return box;
            }

            if (box.Children is not null)
            {
                foreach (var descendant in FindBoxes(box.Children, type))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static void ClearRequiredEntryCount(byte[] bytes, IReadOnlyList<IsoBox> boxes, string type)
    {
        var matches = boxes.Where(box => string.Equals(box.Type, type, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException($"The native AAC sample table contains {matches.Length} '{type}' boxes; expected one.");
        }

        ClearEntryCount(bytes, matches[0], type);
    }

    private static bool TryClearEntryCount(byte[] bytes, IReadOnlyList<IsoBox> boxes, string type)
    {
        var matches = boxes.Where(box => string.Equals(box.Type, type, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0)
        {
            return false;
        }

        if (matches.Length != 1)
        {
            throw new InvalidDataException($"The native AAC sample table contains {matches.Length} '{type}' boxes; expected at most one.");
        }

        ClearEntryCount(bytes, matches[0], type);
        return true;
    }

    private static void ClearEntryCount(byte[] bytes, IsoBox box, string type)
    {
        var entryCountOffset = checked(box.Offset + box.HeaderSize + 4);
        if (entryCountOffset > bytes.Length - sizeof(uint))
        {
            throw new InvalidDataException($"The native AAC '{type}' box has no entry-count field.");
        }

        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entryCountOffset, sizeof(uint)), 0);
    }

    private static void ClearRequiredSampleCount(byte[] bytes, IReadOnlyList<IsoBox> boxes)
    {
        var matches = boxes.Where(box => string.Equals(box.Type, "stsz", StringComparison.Ordinal)
            || string.Equals(box.Type, "stz2", StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException($"The native AAC sample table contains {matches.Length} stsz/stz2 boxes; expected one.");
        }

        var sampleCountOffset = checked(matches[0].Offset + matches[0].HeaderSize + 8);
        if (sampleCountOffset > bytes.Length - sizeof(uint))
        {
            throw new InvalidDataException($"The native AAC '{matches[0].Type}' box has no sample-count field.");
        }

        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(sampleCountOffset, sizeof(uint)), 0);
    }

    private static void ValidateZeroSampleMetadata(byte[] bytes, IReadOnlyList<IsoBox> boxes)
    {
        var timeToSample = boxes.SingleOrDefault(box => string.Equals(box.Type, "stts", StringComparison.Ordinal));
        var sampleSize = boxes.SingleOrDefault(box => string.Equals(box.Type, "stsz", StringComparison.Ordinal)
            || string.Equals(box.Type, "stz2", StringComparison.Ordinal));
        if (timeToSample is null || sampleSize is null)
        {
            throw new InvalidDataException("The transformed empty-audio fixture lost required stts or stsz/stz2 metadata.");
        }

        var timeToSampleEntryCountOffset = checked(timeToSample.Offset + timeToSample.HeaderSize + 4);
        var sampleCountOffset = checked(sampleSize.Offset + sampleSize.HeaderSize + 8);
        if (timeToSampleEntryCountOffset > bytes.Length - sizeof(uint)
            || sampleCountOffset > bytes.Length - sizeof(uint)
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(timeToSampleEntryCountOffset, sizeof(uint))) != 0
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(sampleCountOffset, sizeof(uint))) != 0)
        {
            throw new InvalidDataException("The transformed empty-audio fixture does not contain zero stts/stsz sample metadata.");
        }
    }

    private static void ZeroDuration(
        byte[] bytes,
        IsoBox box,
        string type,
        int versionZeroDurationOffsetAfterPayload,
        int versionOneDurationOffsetAfterPayload)
    {
        var payloadOffset = checked(box.Offset + box.HeaderSize);
        if (payloadOffset >= bytes.Length)
        {
            throw new InvalidDataException($"The native AAC '{type}' box has no full-box payload.");
        }

        var version = bytes[payloadOffset];
        var durationOffset = checked(payloadOffset + (version switch
        {
            0 => versionZeroDurationOffsetAfterPayload,
            1 => versionOneDurationOffsetAfterPayload,
            _ => throw new InvalidDataException($"The native AAC '{type}' box uses unsupported full-box version {version}."),
        }));
        var durationLength = version == 0 ? sizeof(uint) : sizeof(ulong);
        if (durationOffset > bytes.Length - durationLength)
        {
            throw new InvalidDataException($"The native AAC '{type}' box has a truncated duration field.");
        }

        bytes.AsSpan(durationOffset, durationLength).Clear();
    }

    private static AudioTrackLayout FindSingleAudioTrackLayout(byte[] bytes)
    {
        var boxes = ParseBoxes(bytes);
        var movie = RequireExactlyOne(boxes, "moov", "MP4 root");
        var movieChildren = RequireChildren(movie, "movie");
        var movieHeader = RequireExactlyOne(movieChildren, "mvhd", "movie");
        var tracks = movieChildren.Where(box => string.Equals(box.Type, "trak", StringComparison.Ordinal)).ToArray();
        if (tracks.Length != 1)
        {
            throw new InvalidDataException($"The native AAC source contains {tracks.Length} movie tracks; expected exactly one audio track.");
        }

        var track = tracks[0];
        var trackChildren = RequireChildren(track, "audio track");
        var trackHeader = RequireExactlyOne(trackChildren, "tkhd", "audio track");
        var media = RequireExactlyOne(trackChildren, "mdia", "audio track");
        var mediaChildren = RequireChildren(media, "audio media");
        var mediaHeader = RequireExactlyOne(mediaChildren, "mdhd", "audio media");
        var handler = RequireExactlyOne(mediaChildren, "hdlr", "audio media");
        ValidateHandlerType(bytes, handler, "soun");
        var mediaInformation = RequireExactlyOne(mediaChildren, "minf", "audio media");
        var mediaInformationChildren = RequireChildren(mediaInformation, "audio media information");
        var sampleTable = RequireExactlyOne(mediaInformationChildren, "stbl", "audio media information");
        var sampleTableChildren = RequireChildren(sampleTable, "audio sample table");

        IsoBox? editList = null;
        var editBoxes = trackChildren.Where(box => string.Equals(box.Type, "edts", StringComparison.Ordinal)).ToArray();
        if (editBoxes.Length > 1)
        {
            throw new InvalidDataException($"The native AAC source contains {editBoxes.Length} edit boxes; expected at most one.");
        }

        if (editBoxes.Length == 1)
        {
            editList = RequireExactlyOne(RequireChildren(editBoxes[0], "audio edit box"), "elst", "audio edit box");
        }

        return new AudioTrackLayout(movieHeader, trackHeader, mediaHeader, sampleTableChildren, editList);
    }

    private static IReadOnlyList<IsoBox> RequireChildren(IsoBox box, string context)
        => box.Children ?? throw new InvalidDataException($"The {context} box contains no child boxes.");

    private static IsoBox RequireExactlyOne(IReadOnlyList<IsoBox> boxes, string type, string context)
    {
        var matches = boxes.Where(box => string.Equals(box.Type, type, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException($"The {context} contains {matches.Length} '{type}' boxes; expected exactly one.");
        }

        return matches[0];
    }

    private static IsoBox RequireExactlyOneOf(IReadOnlyList<IsoBox> boxes, IReadOnlyList<string> types, string context)
    {
        var matches = boxes.Where(box => types.Contains(box.Type, StringComparer.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"The {context} contains {matches.Length} boxes matching {string.Join("/", types)}; expected exactly one.");
        }

        return matches[0];
    }

    private static void ValidateHandlerType(byte[] bytes, IsoBox handler, string expectedType)
    {
        var handlerTypeOffset = checked(handler.Offset + handler.HeaderSize + 8);
        if (handlerTypeOffset > bytes.Length - 4)
        {
            throw new InvalidDataException("The native AAC handler box is truncated before its handler type.");
        }

        var actualType = System.Text.Encoding.ASCII.GetString(bytes, handlerTypeOffset, 4);
        if (!string.Equals(actualType, expectedType, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The only track's handler type is '{actualType}', expected '{expectedType}'.");
        }
    }

    private static void ValidateNonEmptyAudioSource(byte[] bytes, AudioTrackLayout layout)
    {
        var timeToSample = RequireExactlyOne(layout.SampleTableChildren, "stts", "audio sample table");
        var sampleToChunk = RequireExactlyOne(layout.SampleTableChildren, "stsc", "audio sample table");
        var sampleSize = RequireExactlyOneOf(layout.SampleTableChildren, ["stsz", "stz2"], "audio sample table");
        var chunkOffset = RequireExactlyOneOf(layout.SampleTableChildren, ["stco", "co64"], "audio sample table");
        if (ReadDuration(bytes, layout.MovieHeader, "mvhd", 16, 24) == 0
            || ReadDuration(bytes, layout.TrackHeader, "tkhd", 20, 28) == 0
            || ReadDuration(bytes, layout.MediaHeader, "mdhd", 16, 24) == 0
            || ReadEntryCount(bytes, timeToSample, "stts") == 0
            || ReadEntryCount(bytes, sampleToChunk, "stsc") == 0
            || ReadSampleCount(bytes, sampleSize) == 0
            || ReadEntryCount(bytes, chunkOffset, chunkOffset.Type) == 0)
        {
            throw new InvalidDataException("The native silent AAC source is already empty and cannot prove the zero-reference mutation.");
        }
    }

    private static void ClearRequiredChunkOffsetEntryCount(byte[] bytes, IReadOnlyList<IsoBox> boxes)
    {
        var chunkOffset = RequireExactlyOneOf(boxes, ["stco", "co64"], "audio sample table");
        ClearEntryCount(bytes, chunkOffset, chunkOffset.Type);
    }

    private static EmptyAudioTrackStructuralValidation ValidateEmptyAudioTrackStructure(
        byte[] bytes,
        string sourceSha256,
        long sourceByteLength)
    {
        var layout = FindSingleAudioTrackLayout(bytes);
        var sampleDescription = RequireExactlyOne(layout.SampleTableChildren, "stsd", "audio sample table");
        var timeToSample = RequireExactlyOne(layout.SampleTableChildren, "stts", "audio sample table");
        var sampleToChunk = RequireExactlyOne(layout.SampleTableChildren, "stsc", "audio sample table");
        var sampleSize = RequireExactlyOneOf(layout.SampleTableChildren, ["stsz", "stz2"], "audio sample table");
        var chunkOffset = RequireExactlyOneOf(layout.SampleTableChildren, ["stco", "co64"], "audio sample table");
        var optionalEntryCounts = OptionalSampleReferenceBoxTypes.ToDictionary(
            type => type,
            type => GetOptionalEntryCount(bytes, layout.SampleTableChildren, type),
            StringComparer.Ordinal);
        uint? editListEntryCount = layout.EditList is null ? null : ReadEntryCount(bytes, layout.EditList, "elst");
        var movieDuration = ReadDuration(bytes, layout.MovieHeader, "mvhd", 16, 24);
        var trackDuration = ReadDuration(bytes, layout.TrackHeader, "tkhd", 20, 28);
        var mediaDuration = ReadDuration(bytes, layout.MediaHeader, "mdhd", 16, 24);
        var sampleDescriptionEntryCount = ReadEntryCount(bytes, sampleDescription, "stsd");
        var timeToSampleEntryCount = ReadEntryCount(bytes, timeToSample, "stts");
        var sampleToChunkEntryCount = ReadEntryCount(bytes, sampleToChunk, "stsc");
        var sampleCount = ReadSampleCount(bytes, sampleSize);
        var chunkOffsetEntryCount = ReadEntryCount(bytes, chunkOffset, chunkOffset.Type);
        var hasNoSampleReferences = timeToSampleEntryCount == 0
            && sampleToChunkEntryCount == 0
            && sampleCount == 0
            && chunkOffsetEntryCount == 0
            && optionalEntryCounts.Values.All(count => !count.HasValue || count.Value == 0)
            && (!editListEntryCount.HasValue || editListEntryCount.Value == 0);

        if (sampleDescriptionEntryCount != 1
            || movieDuration != 0
            || trackDuration != 0
            || mediaDuration != 0
            || !hasNoSampleReferences)
        {
            throw new InvalidDataException(
                "Post-mutation ISO-BMFF validation failed: the empty audio fixture must retain one audio description while all durations and sample references are zero.");
        }

        return new EmptyAudioTrackStructuralValidation(
            sourceSha256,
            sourceByteLength,
            PostMutationStructureValidated: true,
            AudioTrackCount: 1,
            VideoTrackCount: 0,
            sampleDescriptionEntryCount,
            movieDuration,
            trackDuration,
            mediaDuration,
            timeToSampleEntryCount,
            sampleToChunkEntryCount,
            sampleCount,
            chunkOffset.Type,
            chunkOffsetEntryCount,
            editListEntryCount,
            optionalEntryCounts,
            hasNoSampleReferences);
    }

    private static uint? GetOptionalEntryCount(byte[] bytes, IReadOnlyList<IsoBox> boxes, string type)
    {
        var matches = boxes.Where(box => string.Equals(box.Type, type, StringComparison.Ordinal)).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => ReadEntryCount(bytes, matches[0], type),
            _ => throw new InvalidDataException($"The audio sample table contains {matches.Length} '{type}' boxes; expected at most one."),
        };
    }

    private static uint ReadEntryCount(byte[] bytes, IsoBox box, string type)
    {
        var entryCountOffset = checked(box.Offset + box.HeaderSize + 4);
        if (entryCountOffset > bytes.Length - sizeof(uint))
        {
            throw new InvalidDataException($"The native AAC '{type}' box has no entry-count field.");
        }

        return BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entryCountOffset, sizeof(uint)));
    }

    private static uint ReadSampleCount(byte[] bytes, IsoBox sampleSize)
    {
        var sampleCountOffset = checked(sampleSize.Offset + sampleSize.HeaderSize + 8);
        if (sampleCountOffset > bytes.Length - sizeof(uint))
        {
            throw new InvalidDataException($"The native AAC '{sampleSize.Type}' box has no sample-count field.");
        }

        return BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(sampleCountOffset, sizeof(uint)));
    }

    private static ulong ReadDuration(
        byte[] bytes,
        IsoBox box,
        string type,
        int versionZeroDurationOffsetAfterPayload,
        int versionOneDurationOffsetAfterPayload)
    {
        var payloadOffset = checked(box.Offset + box.HeaderSize);
        if (payloadOffset >= bytes.Length)
        {
            throw new InvalidDataException($"The native AAC '{type}' box has no full-box payload.");
        }

        var version = bytes[payloadOffset];
        var durationOffset = checked(payloadOffset + (version switch
        {
            0 => versionZeroDurationOffsetAfterPayload,
            1 => versionOneDurationOffsetAfterPayload,
            _ => throw new InvalidDataException($"The native AAC '{type}' box uses unsupported full-box version {version}."),
        }));
        return version switch
        {
            0 when durationOffset <= bytes.Length - sizeof(uint)
                => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(durationOffset, sizeof(uint))),
            1 when durationOffset <= bytes.Length - sizeof(ulong)
                => BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(durationOffset, sizeof(ulong))),
            _ => throw new InvalidDataException($"The native AAC '{type}' box has a truncated duration field."),
        };
    }

    private static async Task<MediaFixtureMatrixFixture> CreateFixtureAsync(
        string stagingRoot,
        string fileName,
        string construction,
        string expectedExtractionOutcome,
        MediaInspection? mediaInspection,
        CancellationToken cancellationToken,
        EmptyAudioTrackStructuralValidation? emptyAudioTrackStructuralValidation = null)
    {
        var path = Path.Combine(stagingRoot, fileName);
        return new MediaFixtureMatrixFixture(
            fileName,
            construction,
            expectedExtractionOutcome,
            await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false),
            new FileInfo(path).Length,
            mediaInspection?.AudioTrackCount,
            mediaInspection?.VideoTrackCount,
            mediaInspection?.DurationSeconds,
            mediaInspection?.ContainerSubtype,
            mediaInspection?.AudioSubtype,
            emptyAudioTrackStructuralValidation);
    }

    private static MediaFixtureMatrixManifest CreateManifest(
        VoiceManifest voice,
        IReadOnlyList<MediaFixtureMatrixFixture> fixtures)
        => new()
        {
            SchemaVersion = 1,
            Purpose = "Locally generated candidate fixture set for the separate MediaIntegrationProbe failure-path matrix. It is opt-in evidence, not a normal test input and not a release-readiness claim.",
            GeneratorVersion = GetGeneratorVersion(),
            GeneratedUtc = DateTimeOffset.UtcNow,
            Host = new HostManifest
            {
                WindowsBuild = Environment.OSVersion.Version.ToString(),
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
            },
            Voice = voice,
            ValidControlText = ValidControlText,
            RightsAndDistribution = "No externally sourced audio, video, image, or codec sample was used: content was synthesized locally from the selected installed Windows voice, zero PCM, a solid color, and fixed ISO-BMFF bytes. Retain this sidecar and confirm any applicable Windows voice/output and internal-artifact-store authorization before redistributing the binaries.",
            UnsupportedCodecDeclaration = $"The unsupported-codec fixture preserves a native AAC MP4's box lengths and bytes except for one audio stsd sample-entry four-character code, changed from mp4a to {UnsupportedSampleEntryType}. This is an intentionally unknown codec declaration, not an externally sourced codec sample.",
            Fixtures = fixtures,
        };

    private static void ValidateInspection(
        string path,
        MediaInspection inspection,
        int expectedAudioTracks,
        int expectedVideoTracks,
        bool requirePositiveDuration)
    {
        if (inspection.AudioTrackCount != expectedAudioTracks || inspection.VideoTrackCount != expectedVideoTracks)
        {
            throw new InvalidDataException(
                $"Windows media inspection of '{Path.GetFileName(path)}' returned audioTracks={inspection.AudioTrackCount} and videoTracks={inspection.VideoTrackCount}; expected audioTracks={expectedAudioTracks} and videoTracks={expectedVideoTracks}.");
        }

        if (!string.Equals(inspection.ContainerSubtype, "MPEG4", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Windows media inspection of '{Path.GetFileName(path)}' reported container subtype '{inspection.ContainerSubtype ?? "<none>"}' instead of MPEG4.");
        }

        if (expectedAudioTracks == 1 && !string.Equals(inspection.AudioSubtype, "AAC", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Windows media inspection of '{Path.GetFileName(path)}' reported audio subtype '{inspection.AudioSubtype ?? "<none>"}' instead of AAC.");
        }

        if (!double.IsFinite(inspection.DurationSeconds)
            || (requirePositiveDuration && inspection.DurationSeconds <= 0)
            || (!requirePositiveDuration && inspection.DurationSeconds < 0))
        {
            throw new InvalidDataException(
                $"Windows media inspection of '{Path.GetFileName(path)}' reported invalid duration {inspection.DurationSeconds:F6} seconds.");
        }
    }

    private static async Task WriteManifestAsync(
        string path,
        MediaFixtureMatrixManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task WriteBytesCreateNewAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void WriteBoxHeader(Span<byte> destination, uint size, string type)
    {
        if (destination.Length != 8 || type.Length != 4)
        {
            throw new ArgumentException("An ISO-BMFF box header requires eight bytes and a four-character type.");
        }

        BinaryPrimitives.WriteUInt32BigEndian(destination, size);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(destination.Slice(4, 4));
    }

    private static string GetGeneratorVersion()
    {
        var assembly = typeof(MediaFixtureMatrixWriter).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The file was created only by this invocation; retain a recoverable diagnostic if it remains locked.
        }
        catch (UnauthorizedAccessException)
        {
            // See the cleanup comment above.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A Windows media operation may still be releasing a staged file after a failed run.
        }
        catch (UnauthorizedAccessException)
        {
            // See the cleanup comment above.
        }
    }

    private sealed record AudioTrackLayout(
        IsoBox MovieHeader,
        IsoBox TrackHeader,
        IsoBox MediaHeader,
        IReadOnlyList<IsoBox> SampleTableChildren,
        IsoBox? EditList);
    private sealed record IsoBox(
        string Type,
        int Offset,
        int Size,
        int HeaderSize,
        IReadOnlyList<IsoBox>? Children);
}

/// <summary>Describes a locally published candidate matrix fixture set.</summary>
internal sealed record MediaFixtureMatrixResult(
    string RootPath,
    string ManifestPath,
    IReadOnlyList<MediaFixtureMatrixFixture> Fixtures);

/// <summary>Records one fixture's construction, hash, and any successful Windows inspection.</summary>
internal sealed record MediaFixtureMatrixFixture(
    string FileName,
    string Construction,
    string ExpectedExtractionOutcome,
    string Sha256,
    long ByteLength,
    int? AudioTrackCount,
    int? VideoTrackCount,
    double? DecodedDurationSeconds,
    string? ContainerSubtype,
    string? AudioSubtype,
    EmptyAudioTrackStructuralValidation? EmptyAudioTrackStructuralValidation = null);

/// <summary>JSON-serializable provenance for a locally generated candidate failure-fixture matrix.</summary>
/// <summary>Records post-write ISO-BMFF proof that the empty audio fixture has no duration or sample references.</summary>
internal sealed record EmptyAudioTrackStructuralValidation(
    string SourceSha256,
    long SourceByteLength,
    bool PostMutationStructureValidated,
    int AudioTrackCount,
    int VideoTrackCount,
    uint SampleDescriptionEntryCount,
    ulong MovieDurationUnits,
    ulong TrackDurationUnits,
    ulong MediaDurationUnits,
    uint TimeToSampleEntryCount,
    uint SampleToChunkEntryCount,
    uint SampleCount,
    string ChunkOffsetBoxType,
    uint ChunkOffsetEntryCount,
    uint? EditListEntryCount,
    IReadOnlyDictionary<string, uint?> OptionalSampleReferenceEntryCounts,
    bool HasNoSampleReferences);

internal sealed class MediaFixtureMatrixManifest
{
    public required int SchemaVersion { get; init; }
    public required string Purpose { get; init; }
    public required string GeneratorVersion { get; init; }
    public required DateTimeOffset GeneratedUtc { get; init; }
    public required HostManifest Host { get; init; }
    public required VoiceManifest Voice { get; init; }
    public required string ValidControlText { get; init; }
    public required string RightsAndDistribution { get; init; }
    public required string UnsupportedCodecDeclaration { get; init; }
    public required IReadOnlyList<MediaFixtureMatrixFixture> Fixtures { get; init; }
}
