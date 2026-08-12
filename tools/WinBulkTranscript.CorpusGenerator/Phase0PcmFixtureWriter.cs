using System.Security.Cryptography;
using System.Text.Json;

namespace WinBulkTranscript.CorpusGenerator;

/// <summary>
/// Creates one retained raw PCM speech fixture with immutable provenance for the Phase 0 probe.
/// </summary>
internal static class Phase0PcmFixtureWriter
{
    /// <summary>The fixed English text retained beside the PCM hash in the sidecar.</summary>
    public const string Phrase = """
        This carefully recorded Windows speech fixture verifies that compatible x64 and ARM64 systems can stream known English audio through the local transcription model without artificial pacing. The fixture uses one selected voice, preserves its exact raw PCM bytes, and expects a complete nonempty English transcript after the model has loaded.
        """;

    private const int MinimumSamples = 5 * Pcm16Audio.SampleRate;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// Synthesizes and writes a CreateNew-only PCM fixture with voice and hash provenance.
    /// </summary>
    public static async Task<Phase0FixtureResult> WriteAsync(
        string requestedPath,
        WindowsTtsAndMedia media,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
        ArgumentNullException.ThrowIfNull(media);
        cancellationToken.ThrowIfCancellationRequested();

        var pcmPath = Path.GetFullPath(requestedPath);
        var manifestPath = $"{pcmPath}.json";
        if (File.Exists(pcmPath) || File.Exists(manifestPath))
        {
            throw new IOException("Refusing to overwrite an existing Phase 0 fixture or provenance sidecar.");
        }

        var directory = Path.GetDirectoryName(pcmPath)
            ?? throw new InvalidOperationException("The Phase 0 fixture path must include a directory.");
        Directory.CreateDirectory(directory);

        var audio = await media.SynthesizeMasterPcmAsync(Phrase, cancellationToken).ConfigureAwait(false);
        if (audio.SampleCount < MinimumSamples)
        {
            throw new InvalidDataException("The selected voice produced a Phase 0 fixture shorter than five seconds.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(audio.Samples));
        var manifest = new Phase0FixtureManifest
        {
            SchemaVersion = 1,
            Phrase = Phrase,
            Sha256 = hash,
            SampleRate = Pcm16Audio.SampleRate,
            Channels = Pcm16Audio.Channels,
            BitsPerSample = Pcm16Audio.BitsPerSample,
            ByteLength = audio.Samples.Length,
            DurationSeconds = audio.DurationSeconds,
            GeneratedUtc = DateTimeOffset.UtcNow,
            Voice = media.VoiceManifest,
        };

        try
        {
            await using (var stream = new FileStream(pcmPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await stream.WriteAsync(audio.Samples, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            await using var sidecar = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true);
            await using var writer = new StreamWriter(sidecar);
            await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            await sidecar.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(pcmPath);
            TryDelete(manifestPath);
            throw;
        }

        return new Phase0FixtureResult(pcmPath, manifestPath, hash);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception) when (File.Exists(path)) { }
    }
}

internal sealed record Phase0FixtureResult(string PcmPath, string ManifestPath, string Sha256);

internal sealed class Phase0FixtureManifest
{
    public required int SchemaVersion { get; init; }
    public required string Phrase { get; init; }
    public required string Sha256 { get; init; }
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required int BitsPerSample { get; init; }
    public required int ByteLength { get; init; }
    public required double DurationSeconds { get; init; }
    public required DateTimeOffset GeneratedUtc { get; init; }
    public required VoiceManifest Voice { get; init; }
}
