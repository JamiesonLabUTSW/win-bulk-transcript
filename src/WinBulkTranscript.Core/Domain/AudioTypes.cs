namespace WinBulkTranscript.Core.Domain;

/// <summary>Describes the only PCM representation used between adapters.</summary>
public readonly record struct PcmFormat(int SampleRate, short Channels, short BitsPerSample, short BlockAlign)
{
    public static PcmFormat Required { get; } = new(16_000, 1, 16, 2);

    public bool IsRequired => SampleRate == Required.SampleRate
        && Channels == Required.Channels
        && BitsPerSample == Required.BitsPerSample
        && BlockAlign == Required.BlockAlign;

    public long BytesPerSecond => checked((long)SampleRate * BlockAlign);
}

/// <summary>A validated data chunk inside a PCM WAVE file.</summary>
public sealed record PcmWaveFile(string Path, PcmFormat Format, long DataOffset, long DataLength)
{
    public long SampleCount => DataLength / Format.BlockAlign;

    public FileStream OpenRead() => new(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
}

/// <summary>Owns an application temporary PCM file until processing finishes.</summary>
public sealed class TemporaryPcmWave : IAsyncDisposable
{
    private readonly Func<ValueTask>? _cleanup;
    private int _disposed;

    public TemporaryPcmWave(PcmWaveFile waveFile, Func<ValueTask>? cleanup = null)
    {
        WaveFile = waveFile ?? throw new ArgumentNullException(nameof(waveFile));
        _cleanup = cleanup;
    }

    public PcmWaveFile WaveFile { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_cleanup is not null)
        {
            await _cleanup().ConfigureAwait(false);
        }
    }
}

/// <summary>A half-open speech region expressed only in source PCM sample indices.</summary>
public readonly record struct SpeechInterval(long StartSample, long EndSample)
{
    public long LengthSamples => EndSample - StartSample;

    public bool IsValid => StartSample >= 0 && EndSample > StartSample;

    public static SpeechInterval Clamp(long startSample, long endSample, long totalSamples)
    {
        var start = Math.Clamp(startSample, 0, totalSamples);
        var end = Math.Clamp(endSample, start, totalSamples);
        return new SpeechInterval(start, end);
    }
}

/// <summary>Text paired with the VAD interval that owns its source timeline.</summary>
public sealed record TranscriptCue(SpeechInterval Interval, string Text);
