using WinBulkTranscript.Core.Domain;

namespace WinBulkTranscript.Core.Ports;

/// <summary>Extracts a source file to a caller-owned temporary PCM WAVE file.</summary>
public interface IAudioExtractor
{
    Task<TemporaryPcmWave> ExtractAsync(string inputPath, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>Produces source-timeline speech intervals from validated PCM.</summary>
public interface IVoiceActivityDetector
{
    Task<IReadOnlyList<SpeechInterval>> DetectAsync(PcmWaveFile waveFile, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>Transcribes exactly one VAD interval from a PCM source.</summary>
public interface ISpeechRecognizer
{
    Task<string> RecognizeAsync(PcmWaveFile waveFile, SpeechInterval interval, CancellationToken cancellationToken);
}

/// <summary>Loads the shared local ASR model once per batch.</summary>
public interface IModelHost : IAsyncDisposable
{
    Task LoadAsync(IProgress<ModelLoadProgress>? progress, CancellationToken cancellationToken);
}

public sealed record ModelLoadProgress(ProcessingStage Stage, double? Fraction, string Detail);

/// <summary>Safely persists VTT cues at a final destination.</summary>
public interface ITranscriptWriter
{
    Task<TranscriptWriteResult> WriteAsync(
        string outputPath,
        IReadOnlyList<TranscriptCue> cues,
        TranscriptCommitMode commitMode,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Gets the single batch-wide response to existing VTT outputs.</summary>
public interface IExistingOutputPolicyResolver
{
    Task<ExistingOutputPolicy> ResolveAsync(IReadOnlyList<BatchItem> existingOutputs, CancellationToken cancellationToken);
}
