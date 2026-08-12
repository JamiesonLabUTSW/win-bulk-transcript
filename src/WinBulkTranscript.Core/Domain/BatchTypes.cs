namespace WinBulkTranscript.Core.Domain;

public enum JobState
{
    Pending,
    Transcribing,
    Complete,
    Failed,
    Cancelled,
}

public enum ProcessingStage
{
    None,
    Preflight,
    LoadingModel,
    DownloadingModel,
    ExtractingAudio,
    DetectingSpeech,
    Transcribing,
    WritingVtt,
    Complete,
    Cancelled,
    Failed,
}

public enum ExistingOutputPolicy
{
    SkipExisting,
    OverwriteAll,
    Cancel,
}

public enum TranscriptCommitMode
{
    FailIfExists,
    Overwrite,
}

public enum TranscriptWriteDisposition
{
    Written,
    SkippedExisting,
}

/// <summary>Describes a discovered input file and its mirrored final output path.</summary>
public sealed record BatchItem(string InputPath, string RelativePath, string OutputPath)
{
    public string FileName => Path.GetFileName(InputPath);
}

/// <summary>An immutable row intended for safe marshaling onto the UI dispatcher.</summary>
public sealed record JobSnapshot(
    string InputPath,
    string RelativePath,
    string OutputPath,
    JobState State,
    ProcessingStage Stage,
    double Progress,
    string Detail,
    int CueCount);

/// <summary>A complete current batch state. Values are normalized to monotonic stage progress.</summary>
public sealed record BatchProgressSnapshot(
    IReadOnlyList<JobSnapshot> Jobs,
    int CompletedFileCount,
    int TotalFileCount,
    string CurrentFileName,
    ProcessingStage CurrentStage,
    string StageText,
    double CurrentFileProgress,
    bool IsRunning,
    bool IsCancelling,
    string? FatalError);

public sealed record BatchRequest(string InputRoot, string OutputRoot)
{
    public static BatchRequest Create(string inputRoot, string outputRoot)
        => new(Path.GetFullPath(inputRoot), Path.GetFullPath(outputRoot));
}

public sealed record DiscoveryIssue(string Path, string Reason);

public sealed record DiscoveryResult(IReadOnlyList<string> Files, IReadOnlyList<DiscoveryIssue> Issues);

public sealed record PreflightResult(IReadOnlyList<BatchItem> Items, IReadOnlyList<BatchItem> ExistingOutputs);

public sealed record TranscriptWriteResult(TranscriptWriteDisposition Disposition, int CueCount);
