using WinBulkTranscript.Core.Domain;

namespace WinBulkTranscript.App.ViewModels;

/// <summary>Dispatcher-bound visual representation of one immutable Core job snapshot.</summary>
public sealed class JobRowViewModel : ObservableObject
{
    private JobState _state;
    private ProcessingStage _stage;
    private double _progress;
    private string _detail;
    private int _cueCount;
    private string _timingText = "Waiting";

    /// <summary>Initializes a row from the first snapshot that names the input file.</summary>
    public JobRowViewModel(JobSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        InputPath = snapshot.InputPath;
        RelativePath = snapshot.RelativePath;
        OutputPath = snapshot.OutputPath;
        FileName = Path.GetFileName(snapshot.InputPath);
        _state = snapshot.State;
        _stage = snapshot.Stage;
        _progress = snapshot.Progress;
        _detail = snapshot.Detail;
        _cueCount = snapshot.CueCount;
    }

    /// <summary>Gets the full input path used as the stable row identity.</summary>
    public string InputPath { get; }

    /// <summary>Gets the input path relative to the selected root.</summary>
    public string RelativePath { get; }

    /// <summary>Gets the output transcript path.</summary>
    public string OutputPath { get; }

    /// <summary>Gets the display file name.</summary>
    public string FileName { get; }

    /// <summary>Gets the public job state.</summary>
    public JobState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    /// <summary>Gets the detailed processing stage.</summary>
    public ProcessingStage Stage
    {
        get => _stage;
        private set => SetProperty(ref _stage, value);
    }

    /// <summary>Gets monotonic current-file progress.</summary>
    public double Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, value))
            {
                OnPropertyChanged(nameof(ProgressPercent));
            }
        }
    }

    /// <summary>Gets progress normalized for a percentage-based progress bar.</summary>
    public double ProgressPercent => Math.Clamp(Progress, 0, 1) * 100;

    /// <summary>Gets concise row detail.</summary>
    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    /// <summary>Gets the number of non-empty cues written for the file.</summary>
    public int CueCount
    {
        get => _cueCount;
        private set => SetProperty(ref _cueCount, value);
    }

    /// <summary>Gets elapsed and estimated time for this file.</summary>
    public string TimingText
    {
        get => _timingText;
        private set => SetProperty(ref _timingText, value);
    }

    /// <summary>Gets the non-color state label shown beside the icon.</summary>
    public string StatusText => State switch
    {
        JobState.Pending => "Pending",
        JobState.Transcribing => "Transcribing",
        JobState.Complete => "Complete",
        JobState.Failed => "Failed",
        JobState.Cancelled => "Cancelled",
        _ => "Unknown",
    };

    /// <summary>Gets a symbolic state icon rendered with Segoe Fluent Icons.</summary>
    public string StatusGlyph => State switch
    {
        JobState.Pending => "\uE121",
        JobState.Transcribing => "\uE895",
        JobState.Complete => "\uE73E",
        JobState.Failed => "\uEA39",
        JobState.Cancelled => "\uE711",
        _ => "\uE783",
    };

    /// <summary>Gets an accessible combined description for a job row.</summary>
    public string AccessibleDescription => $"{RelativePath}. {StatusText}. {Detail}";

    /// <summary>Updates the transient timing summary owned by the batch view model.</summary>
    public void SetTimingText(string value) => TimingText = value;

    /// <summary>Applies the next immutable snapshot on the UI thread.</summary>
    public void Apply(JobSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        State = snapshot.State;
        Stage = snapshot.Stage;
        Progress = snapshot.Progress;
        Detail = snapshot.Detail;
        CueCount = snapshot.CueCount;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(AccessibleDescription));
    }
}
