using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinBulkTranscript.CorpusGenerator;

/// <summary>
/// Creates a disposable long MP4 for observing production media-extraction cancellation after native progress begins.
/// </summary>
internal static class CancellationProbeFixtureWriter
{
    /// <summary>The fixed pre-encode duration used to keep the fixture long enough for an in-flight media progress signal.</summary>
    public const int TargetDurationSeconds = 20 * 60;

    private const int MinimumDecodedDurationSeconds = 15 * 60;
    private const int MaximumDecodedDurationSeconds = 30 * 60;
    private const int InterRepetitionSilenceSeconds = 2;

    /// <summary>The English utterance repeated throughout this evidence-only fixture.</summary>
    public const string RepeatedUtterance = """
        This disposable Windows media cancellation fixture repeats clear English speech with short pauses so a long audio-only AAC MP4 can expose an in-flight native transcode progress point before cancellation is requested.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// Synthesizes, stages, inspects, and publishes a CreateNew-only cancellation-probe MP4 with provenance.
    /// </summary>
    /// <param name="requestedPath">The requested final MP4 path.</param>
    /// <param name="media">The configured Windows speech and media helper.</param>
    /// <param name="cancellationToken">A token that stops the operation before publication.</param>
    /// <returns>The final paths, MP4 hash, and decoded duration.</returns>
    public static async Task<CancellationProbeFixtureResult> WriteAsync(
        string requestedPath,
        WindowsTtsAndMedia media,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
        ArgumentNullException.ThrowIfNull(media);
        cancellationToken.ThrowIfCancellationRequested();

        var mp4Path = Path.GetFullPath(requestedPath);
        if (!string.Equals(Path.GetExtension(mp4Path), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The cancellation fixture path must name an .mp4 output file.", nameof(requestedPath));
        }

        var manifestPath = $"{mp4Path}.json";
        if (File.Exists(mp4Path) || File.Exists(manifestPath))
        {
            throw new IOException("Refusing to overwrite an existing cancellation fixture or provenance sidecar.");
        }

        var outputDirectory = Path.GetDirectoryName(mp4Path)
            ?? throw new InvalidOperationException("The cancellation fixture path must include a directory.");
        Directory.CreateDirectory(outputDirectory);

        var fileName = Path.GetFileName(mp4Path);
        var stagingDirectory = Path.Combine(outputDirectory, $".{fileName}.staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        var stagedWavePath = Path.Combine(stagingDirectory, $"{Path.GetFileNameWithoutExtension(fileName)}.wav");
        var stagedMp4Path = Path.Combine(stagingDirectory, fileName);
        var stagedManifestPath = Path.Combine(stagingDirectory, $"{fileName}.json");
        var mp4Published = false;
        var manifestPublished = false;

        try
        {
            var assembly = await CreateMasterAudioAsync(media, cancellationToken).ConfigureAwait(false);
            var masterPcmHash = Convert.ToHexString(SHA256.HashData(assembly.Audio.Samples));
            await PcmWaveFile.WriteAsync(stagedWavePath, assembly.Audio, cancellationToken).ConfigureAwait(false);
            await WindowsTtsAndMedia.EncodeAudioOnlyMp4Async(stagedWavePath, stagedMp4Path, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var inspection = await WindowsTtsAndMedia.InspectAsync(stagedMp4Path, cancellationToken).ConfigureAwait(false);
            ValidateInspection(inspection);
            var mp4Hash = await ComputeSha256Async(stagedMp4Path, cancellationToken).ConfigureAwait(false);
            var mp4Length = new FileInfo(stagedMp4Path).Length;
            var manifest = CreateManifest(fileName, media.VoiceManifest, assembly, masterPcmHash, mp4Hash, mp4Length, inspection);
            await WriteManifestAsync(stagedManifestPath, manifest, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            File.Move(stagedMp4Path, mp4Path);
            mp4Published = true;
            try
            {
                File.Move(stagedManifestPath, manifestPath);
                manifestPublished = true;
            }
            catch
            {
                TryDelete(mp4Path);
                mp4Published = false;
                throw;
            }

            return new CancellationProbeFixtureResult(mp4Path, manifestPath, mp4Hash, inspection.DurationSeconds);
        }
        finally
        {
            if (mp4Published && !manifestPublished)
            {
                TryDelete(mp4Path);
            }

            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static async Task<CancellationFixtureAssembly> CreateMasterAudioAsync(
        WindowsTtsAndMedia media,
        CancellationToken cancellationToken)
    {
        var repeatedSpeech = await media.SynthesizeMasterPcmAsync(RepeatedUtterance, cancellationToken).ConfigureAwait(false);
        if (repeatedSpeech.SampleCount <= 0 || repeatedSpeech.SampleCount >= TargetDurationSamples)
        {
            throw new InvalidDataException("The selected voice did not produce a usable source utterance for the long cancellation fixture.");
        }

        var repeatedParts = new List<Pcm16Audio>();
        var interRepetitionSilenceSamples = checked(InterRepetitionSilenceSeconds * Pcm16Audio.SampleRate);
        var remainingSamples = TargetDurationSamples;
        var repetitionCount = 0;

        while (remainingSamples >= repeatedSpeech.SampleCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            repeatedParts.Add(repeatedSpeech);
            remainingSamples -= repeatedSpeech.SampleCount;
            repetitionCount++;

            var silenceSamples = Math.Min(interRepetitionSilenceSamples, remainingSamples);
            if (silenceSamples > 0)
            {
                repeatedParts.Add(Pcm16Audio.CreateSilence(silenceSamples));
                remainingSamples -= silenceSamples;
            }
        }

        var trailingSilenceSamples = remainingSamples;
        if (trailingSilenceSamples > 0)
        {
            repeatedParts.Add(Pcm16Audio.CreateSilence(trailingSilenceSamples));
        }

        var audio = Pcm16Audio.Concatenate(repeatedParts);
        if (audio.SampleCount != TargetDurationSamples || repetitionCount == 0)
        {
            throw new InvalidDataException("The long cancellation fixture did not assemble to its required target duration.");
        }

        return new CancellationFixtureAssembly(audio, repeatedSpeech.DurationSeconds, repetitionCount, trailingSilenceSamples);
    }

    private static CancellationProbeFixtureManifest CreateManifest(
        string outputFileName,
        VoiceManifest voice,
        CancellationFixtureAssembly assembly,
        string masterPcmHash,
        string mp4Hash,
        long mp4Length,
        MediaInspection inspection)
    {
        return new CancellationProbeFixtureManifest
        {
            SchemaVersion = 1,
            Purpose = "Disposable MediaIntegrationProbe cancellation evidence fixture; not a corpus or acceptance-fixture-matrix artifact.",
            GeneratorVersion = GetGeneratorVersion(),
            GeneratedUtc = DateTimeOffset.UtcNow,
            Host = new HostManifest
            {
                WindowsBuild = Environment.OSVersion.Version.ToString(),
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
            },
            Voice = voice,
            MasterPcm = new PcmManifest
            {
                Encoding = "PCM signed little-endian",
                SampleRate = Pcm16Audio.SampleRate,
                Channels = Pcm16Audio.Channels,
                BitsPerSample = Pcm16Audio.BitsPerSample,
            },
            OutputMedia = new OutputMediaManifest
            {
                Container = "MPEG-4 audio-only",
                AudioCodec = "AAC",
                FileExtension = ".mp4",
                SampleRate = Pcm16Audio.SampleRate,
                Channels = Pcm16Audio.Channels,
                Bitrate = 96_000,
            },
            OutputFileName = outputFileName,
            RepeatedUtterance = RepeatedUtterance,
            RepeatedUtteranceDurationSeconds = assembly.RepeatedUtteranceDurationSeconds,
            RepetitionCount = assembly.RepetitionCount,
            InterRepetitionSilenceSeconds = InterRepetitionSilenceSeconds,
            TrailingSilenceSamples = assembly.TrailingSilenceSamples,
            TargetDurationSeconds = TargetDurationSeconds,
            MasterDurationSamples = assembly.Audio.SampleCount,
            MasterDurationSeconds = assembly.Audio.DurationSeconds,
            DecodedDurationSeconds = inspection.DurationSeconds,
            AudioTrackCount = inspection.AudioTrackCount,
            VideoTrackCount = inspection.VideoTrackCount,
            ContainerSubtype = inspection.ContainerSubtype,
            AudioSubtype = inspection.AudioSubtype,
            MasterPcmDataSha256 = masterPcmHash,
            Mp4Sha256 = mp4Hash,
            Mp4ByteLength = mp4Length,
        };
    }

    private static void ValidateInspection(MediaInspection inspection)
    {
        if (inspection.AudioTrackCount != 1 || inspection.VideoTrackCount != 0)
        {
            throw new InvalidDataException(
                $"The cancellation fixture must be audio-only with one audio track, but Windows reported {inspection.AudioTrackCount} audio and {inspection.VideoTrackCount} video tracks.");
        }
        if (!string.Equals(inspection.ContainerSubtype, "MPEG4", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(inspection.AudioSubtype, "AAC", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The cancellation fixture must be encoded as MPEG4/AAC, but Windows reported container subtype '{inspection.ContainerSubtype ?? "<none>"}' and audio subtype '{inspection.AudioSubtype ?? "<none>"}'.");
        }


        if (!double.IsFinite(inspection.DurationSeconds)
            || inspection.DurationSeconds < MinimumDecodedDurationSeconds
            || inspection.DurationSeconds > MaximumDecodedDurationSeconds)
        {
            throw new InvalidDataException(
                $"The cancellation fixture decoded duration must remain between {MinimumDecodedDurationSeconds} and {MaximumDecodedDurationSeconds} seconds, but Windows reported {inspection.DurationSeconds:F3} seconds.");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task WriteManifestAsync(
        string path,
        CancellationProbeFixtureManifest manifest,
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

    private static string GetGeneratorVersion()
    {
        var assembly = typeof(CancellationProbeFixtureWriter).Assembly;
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
            // The fixture was never fully published; leave a recoverable diagnostic only if Windows retains a handle.
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
            // A native transcode may still be releasing the staged file after a failed run.
        }
        catch (UnauthorizedAccessException)
        {
            // See the cleanup comment above.
        }
    }

    private static long TargetDurationSamples => checked((long)TargetDurationSeconds * Pcm16Audio.SampleRate);
}

/// <summary>Describes the published cancellation-fixture artifact.</summary>
internal sealed record CancellationProbeFixtureResult(
    string Mp4Path,
    string ManifestPath,
    string Sha256,
    double DecodedDurationSeconds);

/// <summary>Holds the assembled master audio and its repetition metadata before encoding.</summary>
internal sealed record CancellationFixtureAssembly(
    Pcm16Audio Audio,
    double RepeatedUtteranceDurationSeconds,
    int RepetitionCount,
    long TrailingSilenceSamples);

/// <summary>JSON-serializable provenance for the disposable long cancellation fixture.</summary>
internal sealed class CancellationProbeFixtureManifest
{
    public required int SchemaVersion { get; init; }
    public required string Purpose { get; init; }
    public required string GeneratorVersion { get; init; }
    public required DateTimeOffset GeneratedUtc { get; init; }
    public required HostManifest Host { get; init; }
    public required VoiceManifest Voice { get; init; }
    public required PcmManifest MasterPcm { get; init; }
    public required OutputMediaManifest OutputMedia { get; init; }
    public required string OutputFileName { get; init; }
    public required string RepeatedUtterance { get; init; }
    public required double RepeatedUtteranceDurationSeconds { get; init; }
    public required int RepetitionCount { get; init; }
    public required int InterRepetitionSilenceSeconds { get; init; }
    public required long TrailingSilenceSamples { get; init; }
    public required int TargetDurationSeconds { get; init; }
    public required long MasterDurationSamples { get; init; }
    public required double MasterDurationSeconds { get; init; }
    public required double DecodedDurationSeconds { get; init; }
    public required int AudioTrackCount { get; init; }
    public required int VideoTrackCount { get; init; }
    public string? ContainerSubtype { get; init; }
    public string? AudioSubtype { get; init; }
    public required string MasterPcmDataSha256 { get; init; }
    public required string Mp4Sha256 { get; init; }
    public required long Mp4ByteLength { get; init; }
}
