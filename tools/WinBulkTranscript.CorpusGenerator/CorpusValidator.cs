using System.Security.Cryptography;

namespace WinBulkTranscript.CorpusGenerator;

/// <summary>
/// Performs the corpus acceptance checks after a fully staged generation run.
/// </summary>
internal static class CorpusValidator
{
    private const int FixtureCount = 30;
    private const int MinimumUtteranceSamples = 5 * Pcm16Audio.SampleRate;
    private const int MaximumUtteranceSamples = 10 * Pcm16Audio.SampleRate;
    private const int ConservativeFullPathLimit = 240;
    private const string ShortDurationCoverageBand = "short";
    private const string MediumDurationCoverageBand = "medium";
    private const string LongDurationCoverageBand = "long";

    /// <summary>
    /// Verifies every acceptance condition in the synthetic corpus design.
    /// </summary>
    /// <param name="corpusRoot">The staged corpus root.</param>
    /// <param name="manifest">The manifest reloaded from disk.</param>
    /// <param name="expectRetainedMasterPcm">Whether the staged corpus must contain hash-bound retained master PCM evidence.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An asynchronous task.</returns>
    public static async Task ValidateAsync(string corpusRoot, CorpusManifest manifest, bool expectRetainedMasterPcm, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.SchemaVersion != SyntheticCorpusGenerator.ManifestSchemaVersion)
        {
            throw new InvalidDataException($"Unexpected corpus manifest schema version {manifest.SchemaVersion}.");
        }

        ValidateCorpusMetadata(manifest);
        if (manifest.Fixtures.Count != FixtureCount)
        {
            throw new InvalidDataException($"The manifest must contain exactly {FixtureCount} fixtures.");
        }

        var flatRoot = Path.Combine(corpusRoot, "flat");
        var nestedRoot = Path.Combine(corpusRoot, "nested");
        var vttRoot = Path.Combine(corpusRoot, "expected-vtt");
        var retainedMasterPcmRoot = Path.Combine(corpusRoot, "retained-master-pcm");
        EnsureDirectory(flatRoot, "flat input root");
        EnsureDirectory(nestedRoot, "nested input root");
        EnsureDirectory(vttRoot, "expected VTT root");
        if (expectRetainedMasterPcm)
        {
            EnsureDirectory(retainedMasterPcmRoot, "retained master PCM root");
        }

        var flatFiles = EnumerateMp4Files(flatRoot, SearchOption.TopDirectoryOnly);
        var nestedFiles = EnumerateMp4Files(nestedRoot, SearchOption.AllDirectories);
        if (flatFiles.Count != FixtureCount || nestedFiles.Count != FixtureCount)
        {
            throw new InvalidDataException($"Expected {FixtureCount} MP4 files in each input root but found flat={flatFiles.Count}, nested={nestedFiles.Count}.");
        }

        var fixtureIds = new HashSet<string>(StringComparer.Ordinal);
        var expectedFlatFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expectedNestedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sawSpace = false;
        var sawUnicode = false;
        var depths = new HashSet<int>();

        foreach (var fixture in manifest.Fixtures.OrderBy(value => value.FixtureId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!fixtureIds.Add(fixture.FixtureId))
            {
                throw new InvalidDataException($"Duplicate fixture ID '{fixture.FixtureId}'.");
            }

            ValidateFixtureGroundTruth(fixture);
            var flatPath = ResolveCorpusPath(corpusRoot, fixture.FlatPath);
            var nestedPath = ResolveCorpusPath(corpusRoot, fixture.NestedRelativePath);
            var vttPath = ResolveCorpusPath(corpusRoot, fixture.ExpectedVttPath);
            expectedFlatFiles.Add(flatPath);
            expectedNestedFiles.Add(nestedPath);
            ValidateWindowsPath(fixture.FlatPath, ref sawSpace, ref sawUnicode);
            ValidateWindowsPath(fixture.NestedRelativePath, ref sawSpace, ref sawUnicode);
            if (flatPath.Length > ConservativeFullPathLimit || nestedPath.Length > ConservativeFullPathLimit || vttPath.Length > ConservativeFullPathLimit)
            {
                throw new InvalidDataException($"Fixture '{fixture.FixtureId}' exceeds the conservative {ConservativeFullPathLimit}-character path limit.");
            }

            if (!File.Exists(flatPath) || !File.Exists(nestedPath))
            {
                throw new InvalidDataException($"Fixture '{fixture.FixtureId}' is missing its flat or nested MP4.");
            }

            if (expectRetainedMasterPcm)
            {
                await ValidateRetainedMasterPcmAsync(retainedMasterPcmRoot, fixture, cancellationToken).ConfigureAwait(false);
            }

            var flatHash = await ComputeSha256Async(flatPath, cancellationToken);
            var nestedHash = await ComputeSha256Async(nestedPath, cancellationToken);
            if (!string.Equals(flatHash, fixture.Sha256, StringComparison.Ordinal) || !string.Equals(flatHash, nestedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Flat and nested copies of '{fixture.FixtureId}' are not byte-identical SHA-256 matches.");
            }

            var inspection = await WindowsTtsAndMedia.InspectAsync(flatPath, cancellationToken);
            if (inspection.AudioTrackCount != 1 || inspection.VideoTrackCount != 0)
            {
                throw new InvalidDataException($"Fixture '{fixture.FixtureId}' has audioTracks={inspection.AudioTrackCount}, videoTracks={inspection.VideoTrackCount}; expected one audio and no video tracks.");
            }

            if (!string.Equals(inspection.ContainerSubtype, "MPEG4", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(inspection.AudioSubtype, "AAC", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Fixture '{fixture.FixtureId}' has container subtype '{inspection.ContainerSubtype ?? "<none>"}' and audio subtype '{inspection.AudioSubtype ?? "<none>"}'; expected MPEG4/AAC.");
            }

            if (fixture.AudioTrackCount != inspection.AudioTrackCount || fixture.VideoTrackCount != inspection.VideoTrackCount)
            {
                throw new InvalidDataException($"Fixture '{fixture.FixtureId}' track metadata does not agree with Windows media inspection.");
            }

            if (inspection.DurationSeconds < 30.0 || inspection.DurationSeconds > 120.0)
            {
                throw new InvalidDataException($"Fixture '{fixture.FixtureId}' has decoded duration {inspection.DurationSeconds:F3}s outside the required 30-120 second range.");
            }

            if (Math.Abs(inspection.DurationSeconds - fixture.DecodedDurationSeconds) > 0.001)
            {
                throw new InvalidDataException($"Fixture '{fixture.FixtureId}' decoded duration no longer agrees with the manifest.");
            }

            var expectedVtt = VttWriter.Render(fixture);
            var actualVtt = await File.ReadAllTextAsync(vttPath, cancellationToken);
            if (!string.Equals(actualVtt, expectedVtt, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Expected VTT for '{fixture.FixtureId}' does not agree with the manifest.");
            }

            var relativeNestedPath = Path.GetRelativePath(nestedRoot, nestedPath);
            var depth = relativeNestedPath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
                .Length - 1;
            if (depth is < 1 or > 3)
            {
                throw new InvalidDataException($"Nested fixture '{fixture.FixtureId}' has invalid depth {depth}.");
            }

            depths.Add(depth);
        }

        if (!flatFiles.SetEquals(expectedFlatFiles) || !nestedFiles.SetEquals(expectedNestedFiles))
        {
            throw new InvalidDataException("The input directories contain an unexpected MP4 or are missing a manifest-referenced MP4.");
        }

        if (!depths.SetEquals([1, 2, 3]))
        {
            throw new InvalidDataException("Nested fixture paths do not cover all required depths one, two, and three.");
        }

        if (!sawSpace || !sawUnicode)
        {
            throw new InvalidDataException("The generated paths did not exercise both spaces and Unicode names.");
        }

        ValidateDurationCoverage(manifest.Fixtures);

        var replayedLayout = NestedLayoutPlanner.Create(manifest.RandomSeed, manifest.Fixtures.Select(fixture => fixture.FixtureId));
        foreach (var fixture in manifest.Fixtures)
        {
            var expectedRelativePath = $"nested/{replayedLayout[fixture.FixtureId].RelativePath}";
            if (!string.Equals(fixture.NestedRelativePath, expectedRelativePath, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Nested layout replay differs for fixture '{fixture.FixtureId}'.");
            }
        }
    }

    private static void ValidateCorpusMetadata(CorpusManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.GeneratorVersion)
            || string.IsNullOrWhiteSpace(manifest.GenerationTimestampUtc)
            || manifest.Host is null
            || string.IsNullOrWhiteSpace(manifest.Host.WindowsBuild)
            || string.IsNullOrWhiteSpace(manifest.Host.Architecture)
            || manifest.Voice is null
            || string.IsNullOrWhiteSpace(manifest.Voice.Id)
            || string.IsNullOrWhiteSpace(manifest.Voice.DisplayName)
            || string.IsNullOrWhiteSpace(manifest.Voice.Language)
            || !manifest.Voice.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            || manifest.Voice.Options is null)
        {
            throw new InvalidDataException("The corpus manifest is missing required generator, host, or English TTS voice provenance.");
        }

        if (manifest.MasterPcm is null
            || !string.Equals(manifest.MasterPcm.Encoding, "PCM signed little-endian", StringComparison.Ordinal)
            || manifest.MasterPcm.SampleRate != Pcm16Audio.SampleRate
            || manifest.MasterPcm.Channels != Pcm16Audio.Channels
            || manifest.MasterPcm.BitsPerSample != Pcm16Audio.BitsPerSample)
        {
            throw new InvalidDataException("The corpus manifest does not record the required PCM16/16 kHz/mono master format.");
        }

        if (manifest.OutputMedia is null
            || !string.Equals(manifest.OutputMedia.Container, "MPEG-4 audio-only", StringComparison.Ordinal)
            || !string.Equals(manifest.OutputMedia.AudioCodec, "AAC", StringComparison.Ordinal)
            || !string.Equals(manifest.OutputMedia.FileExtension, ".mp4", StringComparison.OrdinalIgnoreCase)
            || manifest.OutputMedia.SampleRate != Pcm16Audio.SampleRate
            || manifest.OutputMedia.Channels != Pcm16Audio.Channels
            || manifest.OutputMedia.Bitrate <= 0)
        {
            throw new InvalidDataException("The corpus manifest does not record the required audio-only MPEG-4/AAC output contract.");
        }

        if (manifest.Normalization is null
            || !string.Equals(manifest.Normalization.UnicodeNormalization, "FormKC", StringComparison.Ordinal)
            || !string.Equals(manifest.Normalization.Casing, "Invariant lowercase", StringComparison.Ordinal)
            || !string.Equals(manifest.Normalization.Punctuation, "Removed", StringComparison.Ordinal)
            || !string.Equals(manifest.Normalization.Whitespace, "Collapsed to single spaces and trimmed", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The corpus manifest does not record the required transcript-normalization contract.");
        }
    }

    private static void ValidateDurationCoverage(IReadOnlyList<FixtureManifest> fixtures)
    {
        ValidateDurationBand(fixtures, ShortDurationCoverageBand, minimumSeconds: 34d, maximumSeconds: 55d);
        ValidateDurationBand(fixtures, MediumDurationCoverageBand, minimumSeconds: 58d, maximumSeconds: 86d);
        ValidateDurationBand(fixtures, LongDurationCoverageBand, minimumSeconds: 82d, maximumSeconds: 116d);

        if (fixtures.Any(fixture => !string.Equals(fixture.DurationCoverageBand, ShortDurationCoverageBand, StringComparison.Ordinal)
            && !string.Equals(fixture.DurationCoverageBand, MediumDurationCoverageBand, StringComparison.Ordinal)
            && !string.Equals(fixture.DurationCoverageBand, LongDurationCoverageBand, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The corpus manifest contains an unrecognized duration-coverage band.");
        }
    }

    private static void ValidateDurationBand(
        IReadOnlyList<FixtureManifest> fixtures,
        string durationCoverageBand,
        double minimumSeconds,
        double maximumSeconds)
    {
        var bandFixtures = fixtures
            .Where(fixture => string.Equals(fixture.DurationCoverageBand, durationCoverageBand, StringComparison.Ordinal))
            .ToArray();
        if (bandFixtures.Length != FixtureCount / 3)
        {
            throw new InvalidDataException($"Expected {FixtureCount / 3} '{durationCoverageBand}' duration fixtures but found {bandFixtures.Length}.");
        }

        if (bandFixtures.Any(fixture => fixture.RequestedTargetDurationSeconds < minimumSeconds || fixture.RequestedTargetDurationSeconds > maximumSeconds))
        {
            throw new InvalidDataException($"The '{durationCoverageBand}' duration coverage band has a requested target outside {minimumSeconds:F1}-{maximumSeconds:F1} seconds.");
        }
    }

    private static void ValidateFixtureGroundTruth(FixtureManifest fixture)
    {
        if (!string.Equals(fixture.FileName, $"{fixture.FixtureId}.mp4", StringComparison.Ordinal)
            || !string.Equals(fixture.FlatPath, $"flat/{fixture.FileName}", StringComparison.Ordinal)
            || !fixture.NestedRelativePath.StartsWith("nested/", StringComparison.Ordinal)
            || !string.Equals(fixture.ExpectedVttPath, $"expected-vtt/{fixture.FixtureId}.vtt", StringComparison.Ordinal)
            || !IsSha256(fixture.Sha256)
            || !IsSha256(fixture.MasterPcmDataSha256))
        {
            throw new InvalidDataException($"Fixture '{fixture.FixtureId}' has inconsistent manifest paths.");
        }

        if (fixture.Utterances.Count < 2 || fixture.InterUtteranceSilences.Count != fixture.Utterances.Count - 1)
        {
            throw new InvalidDataException($"Fixture '{fixture.FixtureId}' does not contain a valid multi-utterance silence layout.");
        }

        if (!double.IsFinite(fixture.RequestedTargetDurationSeconds)
            || !double.IsFinite(fixture.TargetDurationSeconds)
            || !double.IsFinite(fixture.DecodedDurationSeconds)
            || fixture.RequestedTargetDurationSeconds <= 0d
            || fixture.TargetDurationSeconds <= 0d
            || fixture.DecodedDurationSeconds <= 0d)
        {
            throw new InvalidDataException($"Fixture '{fixture.FixtureId}' has invalid requested, final, or decoded duration metadata.");
        }

        if (fixture.MasterDurationSamples <= 0 || Math.Abs(fixture.MasterDurationSeconds - (fixture.MasterDurationSamples / (double)Pcm16Audio.SampleRate)) > 0.000001)
        {
            throw new InvalidDataException($"Fixture '{fixture.FixtureId}' has inconsistent master duration metadata.");
        }

        if (Math.Abs(fixture.TargetDurationSeconds - fixture.MasterDurationSeconds) > (1.0 / Pcm16Audio.SampleRate))
        {
            throw new InvalidDataException($"Fixture '{fixture.FixtureId}' target duration does not agree with its assembled PCM duration.");
        }

        if (fixture.LeadingSilence.Samples is < 0 or > (2 * Pcm16Audio.SampleRate)
            || fixture.TrailingSilence.Samples is < 0 or > (2 * Pcm16Audio.SampleRate))
        {
            throw new InvalidDataException($"Fixture '{fixture.FixtureId}' has invalid leading or trailing silence.");
        }

        var expectedStart = fixture.LeadingSilence.Samples;
        for (var index = 0; index < fixture.Utterances.Count; index++)
        {
            var utterance = fixture.Utterances[index];
            if (utterance.StartSample != expectedStart || utterance.EndSample <= utterance.StartSample || utterance.EndSample > fixture.MasterDurationSamples)
            {
                throw new InvalidDataException($"Fixture '{fixture.FixtureId}' has unordered or out-of-range utterance boundaries.");
            }

            var duration = utterance.EndSample - utterance.StartSample;
            if (duration < MinimumUtteranceSamples || duration > MaximumUtteranceSamples)
            {
                throw new InvalidDataException($"Fixture '{fixture.FixtureId}' has an utterance outside the required 5-10 second master PCM range.");
            }

            if (Math.Abs(utterance.StartSeconds - (utterance.StartSample / (double)Pcm16Audio.SampleRate)) > 0.000001
                || Math.Abs(utterance.EndSeconds - (utterance.EndSample / (double)Pcm16Audio.SampleRate)) > 0.000001
                || !string.Equals(utterance.NormalizedExpectedText, TranscriptNormalizer.Normalize(utterance.AuthoredText), StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Fixture '{fixture.FixtureId}' has inconsistent utterance transcript or timing metadata.");
            }

            expectedStart = utterance.EndSample;
            if (index < fixture.InterUtteranceSilences.Count)
            {
                var silence = fixture.InterUtteranceSilences[index];
                if (silence.Samples < (Pcm16Audio.SampleRate * 3 / 4) || silence.Samples > (3 * Pcm16Audio.SampleRate))
                {
                    throw new InvalidDataException($"Fixture '{fixture.FixtureId}' has inter-utterance silence outside the required 0.75-3.0 second range.");
                }

                expectedStart += silence.Samples;
            }
        }

        if (expectedStart + fixture.TrailingSilence.Samples != fixture.MasterDurationSamples)
        {
            throw new InvalidDataException($"Fixture '{fixture.FixtureId}' silences do not fill its master PCM duration.");
        }

        ValidateSilenceSeconds(fixture.LeadingSilence, fixture.FixtureId);
        ValidateSilenceSeconds(fixture.TrailingSilence, fixture.FixtureId);
        foreach (var silence in fixture.InterUtteranceSilences)
        {
            ValidateSilenceSeconds(silence, fixture.FixtureId);
        }
    }

    private static void ValidateSilenceSeconds(SilenceManifest silence, string fixtureId)
    {
        if (silence.Samples < 0 || Math.Abs(silence.Seconds - (silence.Samples / (double)Pcm16Audio.SampleRate)) > 0.000001)
        {
            throw new InvalidDataException($"Fixture '{fixtureId}' has inconsistent silence metadata.");
        }
    }

    private static async Task ValidateRetainedMasterPcmAsync(string root, FixtureManifest fixture, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, $"{fixture.FixtureId}.wav");
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Fixture '{fixture.FixtureId}' is missing its retained master PCM WAV.");
        }

        var waveBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var pcm = PcmWaveFile.ReadMasterPcm16Mono(waveBytes);
        var actualHash = Convert.ToHexString(SHA256.HashData(pcm.Samples));
        if (pcm.SampleCount != fixture.MasterDurationSamples
            || !string.Equals(actualHash, fixture.MasterPcmDataSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Fixture '{fixture.FixtureId}' retained master PCM does not match manifest provenance.");
        }
    }

    private static bool IsSha256(string? value)
    {
        return value is { Length: 64 } && value.All(Uri.IsHexDigit);
    }

    private static HashSet<string> EnumerateMp4Files(string root, SearchOption searchOption)
    {
        return Directory.EnumerateFiles(root, "*", searchOption)
            .Where(path => string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveCorpusPath(string corpusRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("A corpus manifest path must be nonempty and relative.");
        }

        var root = Path.GetFullPath(corpusRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalizedRelative));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Manifest path '{relativePath}' escapes the corpus root.");
        }

        return fullPath;
    }

    private static void ValidateWindowsPath(string relativePath, ref bool sawSpace, ref bool sawUnicode)
    {
        var pathParts = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in pathParts)
        {
            if (part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsReservedWindowsName(part))
            {
                throw new InvalidDataException($"'{part}' is not a valid conservative Windows path segment.");
            }

            sawSpace |= part.Contains(' ');
            sawUnicode |= part.Any(character => character > 0x7F);
        }
    }

    private static bool IsReservedWindowsName(string name)
    {
        var baseName = Path.GetFileNameWithoutExtension(name);
        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return baseName.Length == 4
            && (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && baseName[3] is >= '1' and <= '9';
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static void EnsureDirectory(string path, string displayName)
    {
        if (!Directory.Exists(path))
        {
            throw new InvalidDataException($"The {displayName} '{path}' is missing.");
        }
    }
}
