using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using WinBulkTranscript.App.Composition;
using WinBulkTranscript.App.Services;
using WinBulkTranscript.Core.Batch;
using WinBulkTranscript.Core.Domain;
using WinBulkTranscript.Core.Ports;

namespace WinBulkTranscript.App.ViewModels;

/// <summary>Owns page state, input validation, commands, and dispatcher-safe Core snapshot mapping.</summary>
public sealed class MainViewModel : ObservableObject
{
    private const int TimingWindowSize = 5;
    private readonly ITranscriptionBatchRunner _batchRunner;
    private readonly IExistingOutputPolicyResolver _existingOutputPolicyResolver;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<string, JobRowViewModel> _jobsByInputPath = new(StringComparer.OrdinalIgnoreCase);
    private QueuedSnapshot? _pendingSnapshot;
    private int _batchGeneration;
    private CancellationTokenSource? _batchCancellation;
    private string _inputFolderPath = string.Empty;
    private string _outputFolderPath = string.Empty;
    private string _inputValidationMessage = "Select an input folder.";
    private string _outputValidationMessage = "Select an output folder.";
    private int _mp4FileCount;
    private bool _isInputValid;
    private bool _isOutputValid;
    private bool _isRunning;
    private bool _isCancelling;
    private int _snapshotDispatchQueued;
    private string _currentFileName = "No batch is running";
    private string _currentStageText = "Choose input and output folders to begin.";
    private double _currentFileProgressPercent;
    private bool _isProgressIndeterminate;
    private int _completedFileCount;
    private int _totalFileCount;
    private bool _isInfoBarOpen;
    private string _infoBarTitle = string.Empty;
    private string _infoBarMessage = string.Empty;
    private InfoBarSeverity _infoBarSeverity = InfoBarSeverity.Informational;
    private int _validationVersion;
    private readonly Queue<TimeSpan> _recentChunkDurations = new();
    private readonly Queue<TimeSpan> _recentFileDurations = new();
    private Stopwatch? _batchStopwatch;
    private Stopwatch? _fileStopwatch;
    private Stopwatch? _chunkStopwatch;
    private string? _activeInputPath;
    private int _activeChunkIndex;
    private int _activeChunkCount;
    private string _batchElapsedText = "Elapsed —";
    private string _batchEtaText = "ETA —";
    private string _currentFileTimingText = "Elapsed —  •  ETA —";

    /// <summary>Initializes a main view model with a Core bridge, collision dialog, and UI dispatcher.</summary>
    public MainViewModel(
        ITranscriptionBatchRunner batchRunner,
        IExistingOutputPolicyResolver existingOutputPolicyResolver,
        IUiDispatcher dispatcher)
    {
        _batchRunner = batchRunner ?? throw new ArgumentNullException(nameof(batchRunner));
        _existingOutputPolicyResolver = existingOutputPolicyResolver ?? throw new ArgumentNullException(nameof(existingOutputPolicyResolver));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        StartCommand = new AsyncDelegateCommand(StartAsync, () => CanStart, ShowUnexpectedError);
        CancelCommand = new DelegateCommand(RequestCancellation, () => IsRunning && !IsCancelling);
    }

    /// <summary>Raised after a running batch has finished cleanup and command availability has been restored.</summary>
    public event EventHandler? BatchFinished;

    /// <summary>Gets input-folder path selected through the picker.</summary>
    public string InputFolderPath
    {
        get => _inputFolderPath;
        private set => SetProperty(ref _inputFolderPath, value);
    }

    /// <summary>Gets output-folder path selected through the picker.</summary>
    public string OutputFolderPath
    {
        get => _outputFolderPath;
        private set => SetProperty(ref _outputFolderPath, value);
    }

    /// <summary>Gets inline input validation text.</summary>
    public string InputValidationMessage
    {
        get => _inputValidationMessage;
        private set => SetProperty(ref _inputValidationMessage, value);
    }

    /// <summary>Gets inline output validation text.</summary>
    public string OutputValidationMessage
    {
        get => _outputValidationMessage;
        private set => SetProperty(ref _outputValidationMessage, value);
    }

    /// <summary>Gets the current recursive MP4 count.</summary>
    public int Mp4FileCount
    {
        get => _mp4FileCount;
        private set
        {
            if (SetProperty(ref _mp4FileCount, value))
            {
                OnPropertyChanged(nameof(FoundMp4Text));
            }
        }
    }

    /// <summary>Gets user-facing MP4 count text.</summary>
    public string FoundMp4Text => Mp4FileCount switch
    {
        0 => "No MP4 files found yet.",
        1 => "Found 1 MP4 file.",
        _ => $"Found {Mp4FileCount} MP4 files.",
    };

    /// <summary>Gets whether both selected folders and the input snapshot are valid.</summary>
    public bool CanStart => !IsRunning && _isInputValid && _isOutputValid && Mp4FileCount > 0;

    /// <summary>Gets whether picker controls can be changed.</summary>
    public bool IsFolderSelectionEnabled => !IsRunning;

    /// <summary>Gets whether the coordinator currently owns a batch.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(IsFolderSelectionEnabled));
                RefreshCommandAvailability();
            }
        }
    }

    /// <summary>Gets whether cooperative cancellation has been requested.</summary>
    public bool IsCancelling
    {
        get => _isCancelling;
        private set
        {
            if (SetProperty(ref _isCancelling, value))
            {
                OnPropertyChanged(nameof(CancelButtonText));
                OnPropertyChanged(nameof(CancelButtonAccessibleName));
                RefreshCommandAvailability();
            }
        }
    }

    /// <summary>Gets the accessible current-file label.</summary>
    public string CurrentFileName
    {
        get => _currentFileName;
        private set => SetProperty(ref _currentFileName, value);
    }

    /// <summary>Gets the current stage text.</summary>
    public string CurrentStageText
    {
        get => _currentStageText;
        private set => SetProperty(ref _currentStageText, value);
    }

    /// <summary>Gets determinate current-file progress in percent.</summary>
    public double CurrentFileProgressPercent
    {
        get => _currentFileProgressPercent;
        private set
        {
            if (SetProperty(ref _currentFileProgressPercent, value))
            {
                OnPropertyChanged(nameof(BatchProgressPercent));
            }
        }
    }

    /// <summary>Gets whether model setup should show an indeterminate progress bar.</summary>
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    /// <summary>Gets current batch completion text.</summary>
    public string BatchCountText => TotalFileCount == 0
        ? "No files queued."
        : $"{CompletedFileCount} of {TotalFileCount} files finished";

    /// <summary>Gets combined completed-file and active-file batch progress.</summary>
    public double BatchProgressPercent => TotalFileCount == 0
        ? 0
        : Math.Clamp(
            ((double)CompletedFileCount + (_activeInputPath is null ? 0 : CurrentFileProgressPercent / 100)) / TotalFileCount,
            0,
            1) * 100;

    /// <summary>Gets the live batch elapsed-time label.</summary>
    public string BatchElapsedText
    {
        get => _batchElapsedText;
        private set => SetProperty(ref _batchElapsedText, value);
    }

    /// <summary>Gets the empirical batch remaining-time estimate.</summary>
    public string BatchEtaText
    {
        get => _batchEtaText;
        private set => SetProperty(ref _batchEtaText, value);
    }

    /// <summary>Gets elapsed and empirical remaining time for the active file.</summary>
    public string CurrentFileTimingText
    {
        get => _currentFileTimingText;
        private set => SetProperty(ref _currentFileTimingText, value);
    }

    /// <summary>Gets the number of terminal job rows.</summary>
    public int CompletedFileCount
    {
        get => _completedFileCount;
        private set
        {
            if (SetProperty(ref _completedFileCount, value))
            {
                OnPropertyChanged(nameof(BatchCountText));
                OnPropertyChanged(nameof(BatchProgressPercent));
            }
        }
    }

    /// <summary>Gets the number of snapshot job rows.</summary>
    public int TotalFileCount
    {
        get => _totalFileCount;
        private set
        {
            if (SetProperty(ref _totalFileCount, value))
            {
                OnPropertyChanged(nameof(BatchCountText));
                OnPropertyChanged(nameof(BatchProgressPercent));
            }
        }
    }

    /// <summary>Gets the visible UI-bound job collection.</summary>
    public ObservableCollection<JobRowViewModel> Jobs { get; } = [];

    /// <summary>Gets the primary command.</summary>
    public AsyncDelegateCommand StartCommand { get; }

    /// <summary>Gets the cancellation command.</summary>
    public DelegateCommand CancelCommand { get; }

    /// <summary>Gets whether an app-level InfoBar message is visible.</summary>
    public bool IsInfoBarOpen
    {
        get => _isInfoBarOpen;
        set => SetProperty(ref _isInfoBarOpen, value);
    }

    /// <summary>Gets the InfoBar title.</summary>
    public string InfoBarTitle
    {
        get => _infoBarTitle;
        private set => SetProperty(ref _infoBarTitle, value);
    }

    /// <summary>Gets the InfoBar message.</summary>
    public string InfoBarMessage
    {
        get => _infoBarMessage;
        private set => SetProperty(ref _infoBarMessage, value);
    }

    /// <summary>Gets the InfoBar severity.</summary>
    public InfoBarSeverity InfoBarSeverity
    {
        get => _infoBarSeverity;
        private set => SetProperty(ref _infoBarSeverity, value);
    }

    /// <summary>Gets the current cancel button label.</summary>
    public string CancelButtonText => IsCancelling ? "Cancelling…" : "Cancel";

    /// <summary>Gets the current accessible cancel-button name.</summary>
    public string CancelButtonAccessibleName => IsCancelling ? "Cancelling transcription" : "Cancel transcription";

    /// <summary>Updates the selected input folder and refreshes validation/counting asynchronously.</summary>
    public async Task SetInputFolderAsync(string path)
    {
        InputFolderPath = NormalizePath(path);
        await RefreshValidationAsync();
    }

    /// <summary>Updates the selected output folder and refreshes validation/counting asynchronously.</summary>
    public async Task SetOutputFolderAsync(string path)
    {
        OutputFolderPath = NormalizePath(path);
        await RefreshValidationAsync();
    }

    /// <summary>Requests cooperative cancellation; the UI remains disabled until batch cleanup completes.</summary>
    public void RequestCancellation()
    {
        if (!IsRunning || IsCancelling)
        {
            return;
        }

        IsCancelling = true;
        CurrentStageText = "Cancelling…";
        _batchCancellation?.Cancel();
    }

    /// <summary>Refreshes stopwatch-backed labels between coordinator progress snapshots.</summary>
    public void RefreshTimingDisplay() => UpdateTimingDisplay();

    private async Task StartAsync()
    {
        var batchGeneration = Interlocked.Increment(ref _batchGeneration);
        await RefreshValidationAsync();
        if (!CanStart)
        {
            return;
        }

        Jobs.Clear();
        _jobsByInputPath.Clear();
        CompletedFileCount = 0;
        TotalFileCount = 0;
        CurrentFileName = "Preparing batch";
        CurrentStageText = "Finding MP4 files";
        CurrentFileProgressPercent = 0;
        IsProgressIndeterminate = false;
        IsInfoBarOpen = false;
        IsCancelling = false;
        ResetTiming();
        IsRunning = true;

        using var cancellation = new CancellationTokenSource();
        _batchCancellation = cancellation;
        try
        {
            var request = BatchRequest.Create(InputFolderPath, OutputFolderPath);
            await _batchRunner.RunAsync(
                request,
                new SnapshotProgress(snapshot => QueueSnapshot(batchGeneration, snapshot)),
                _existingOutputPolicyResolver,
                cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ShowInfo("Batch cancelled", "The transcription batch was cancelled.", InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            ShowUnexpectedError(exception);
        }
        finally
        {
            _batchCancellation = null;
            IsRunning = false;
            IsCancelling = false;
            if (string.Equals(CurrentStageText, "Cancelling…", StringComparison.Ordinal))
            {
                CurrentStageText = "Cancelled";
            }

            FinishActiveFile(includeDuration: false);
            _batchStopwatch?.Stop();
            UpdateTimingDisplay();
            BatchFinished?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task RefreshValidationAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var version = Interlocked.Increment(ref _validationVersion);
        var inputPath = InputFolderPath;
        var outputPath = OutputFolderPath;
        var inspection = await Task.Run(() => InspectFolders(inputPath, outputPath));
        if (version != Volatile.Read(ref _validationVersion) || IsRunning)
        {
            return;
        }

        _isInputValid = inspection.IsInputValid;
        _isOutputValid = inspection.IsOutputValid;
        InputValidationMessage = inspection.InputMessage;
        OutputValidationMessage = inspection.OutputMessage;
        Mp4FileCount = inspection.Mp4FileCount;
        OnPropertyChanged(nameof(CanStart));
        RefreshCommandAvailability();
    }

    private void QueueSnapshot(int batchGeneration, BatchProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (batchGeneration != Volatile.Read(ref _batchGeneration))
        {
            return;
        }

        Interlocked.Exchange(ref _pendingSnapshot, new QueuedSnapshot(batchGeneration, snapshot));
        if (Interlocked.CompareExchange(ref _snapshotDispatchQueued, 1, 0) != 0)
        {
            return;
        }

        if (!_dispatcher.TryEnqueue(DrainSnapshotQueue))
        {
            Interlocked.Exchange(ref _snapshotDispatchQueued, 0);
        }
    }

    private void DrainSnapshotQueue()
    {
        var queuedSnapshot = Interlocked.Exchange(ref _pendingSnapshot, null);
        Interlocked.Exchange(ref _snapshotDispatchQueued, 0);
        if (queuedSnapshot is not null && queuedSnapshot.BatchGeneration == Volatile.Read(ref _batchGeneration))
        {
            ApplySnapshot(queuedSnapshot.Snapshot);
        }

        if (Volatile.Read(ref _pendingSnapshot) is not null
            && Interlocked.CompareExchange(ref _snapshotDispatchQueued, 1, 0) == 0)
        {
            if (!_dispatcher.TryEnqueue(DrainSnapshotQueue))
            {
                Interlocked.Exchange(ref _snapshotDispatchQueued, 0);
            }
        }
    }

    private void ApplySnapshot(BatchProgressSnapshot snapshot)
    {
        SyncRows(snapshot.Jobs);
        UpdateTimingState(snapshot);
        CompletedFileCount = snapshot.CompletedFileCount;
        TotalFileCount = snapshot.TotalFileCount;
        CurrentFileName = string.IsNullOrWhiteSpace(snapshot.CurrentFileName)
            ? (snapshot.IsRunning ? "Preparing batch" : "No file active")
            : snapshot.CurrentFileName;
        CurrentStageText = string.IsNullOrWhiteSpace(snapshot.StageText) ? "Working…" : snapshot.StageText;
        CurrentFileProgressPercent = Math.Clamp(snapshot.CurrentFileProgress, 0, 1) * 100;
        IsProgressIndeterminate = snapshot.IsRunning && snapshot.CurrentStage == ProcessingStage.LoadingModel;
        UpdateTimingDisplay();

        if (!string.IsNullOrWhiteSpace(snapshot.FatalError))
        {
            ShowInfo("Batch could not continue", snapshot.FatalError, InfoBarSeverity.Error);
        }
        else if (!snapshot.IsRunning)
        {
            ShowCompletionMessage(snapshot);
        }
    }

    private void SyncRows(IReadOnlyList<JobSnapshot> snapshots)
    {
        var incomingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            incomingPaths.Add(snapshot.InputPath);
            if (!_jobsByInputPath.TryGetValue(snapshot.InputPath, out var row))
            {
                row = new JobRowViewModel(snapshot);
                _jobsByInputPath.Add(snapshot.InputPath, row);
                Jobs.Insert(index, row);
            }
            else
            {
                row.Apply(snapshot);
                var currentIndex = Jobs.IndexOf(row);
                if (currentIndex != index)
                {
                    Jobs.Move(currentIndex, index);
                }
            }
        }

        for (var index = Jobs.Count - 1; index >= 0; index--)
        {
            var row = Jobs[index];
            if (incomingPaths.Contains(row.InputPath))
            {
                continue;
            }

            _jobsByInputPath.Remove(row.InputPath);
            Jobs.RemoveAt(index);
        }
    }

    private void ResetTiming()
    {
        _recentChunkDurations.Clear();
        _recentFileDurations.Clear();
        _activeInputPath = null;
        _activeChunkIndex = 0;
        _activeChunkCount = 0;
        _fileStopwatch = null;
        _chunkStopwatch = null;
        _batchStopwatch = Stopwatch.StartNew();
        BatchElapsedText = "Elapsed 0:00";
        BatchEtaText = "ETA estimating…";
        CurrentFileTimingText = "Elapsed 0:00  •  ETA estimating…";
    }

    private void UpdateTimingState(BatchProgressSnapshot snapshot)
    {
        var activeJob = snapshot.Jobs.FirstOrDefault(static job => job.State == JobState.Transcribing);
        var nextActivePath = activeJob?.InputPath;

        if (!string.Equals(_activeInputPath, nextActivePath, StringComparison.OrdinalIgnoreCase))
        {
            var previousCompleted = _activeInputPath is not null
                && snapshot.Jobs.Any(job => string.Equals(job.InputPath, _activeInputPath, StringComparison.OrdinalIgnoreCase)
                    && job.State == JobState.Complete);
            FinishActiveFile(previousCompleted);
            if (nextActivePath is not null)
            {
                _activeInputPath = nextActivePath;
                _fileStopwatch = Stopwatch.StartNew();
            }

            OnPropertyChanged(nameof(BatchProgressPercent));
        }

        if (_activeInputPath is null)
        {
            return;
        }

        if (snapshot.CurrentStage == ProcessingStage.Transcribing && snapshot.CurrentChunkIndex > 0)
        {
            if (_activeChunkIndex != snapshot.CurrentChunkIndex)
            {
                FinishActiveChunk();
                _activeChunkIndex = snapshot.CurrentChunkIndex;
                _activeChunkCount = snapshot.CurrentChunkCount;
                _chunkStopwatch = Stopwatch.StartNew();
            }
        }
        else
        {
            FinishActiveChunk();
        }
    }

    private void FinishActiveChunk()
    {
        if (_chunkStopwatch is not null)
        {
            AddRollingDuration(_recentChunkDurations, _chunkStopwatch.Elapsed);
        }

        _chunkStopwatch = null;
        _activeChunkIndex = 0;
        _activeChunkCount = 0;
    }

    private void FinishActiveFile(bool includeDuration = true)
    {
        if (_activeInputPath is null)
        {
            return;
        }

        if (includeDuration)
        {
            FinishActiveChunk();
        }
        else
        {
            _chunkStopwatch = null;
            _activeChunkIndex = 0;
            _activeChunkCount = 0;
        }

        if (_fileStopwatch is not null)
        {
            var elapsed = _fileStopwatch.Elapsed;
            if (includeDuration)
            {
                AddRollingDuration(_recentFileDurations, elapsed);
            }

            if (_jobsByInputPath.TryGetValue(_activeInputPath, out var row))
            {
                row.SetTimingText(includeDuration
                    ? $"Finished in {FormatDuration(elapsed)}"
                    : $"Stopped after {FormatDuration(elapsed)}");
            }
        }

        _fileStopwatch = null;
        _activeInputPath = null;
    }

    private void UpdateTimingDisplay()
    {
        if (_batchStopwatch is null)
        {
            return;
        }

        BatchElapsedText = $"Elapsed {FormatDuration(_batchStopwatch.Elapsed)}";
        if (IsRunning)
        {
            var remainingFiles = Math.Max(0, TotalFileCount - CompletedFileCount);
            BatchEtaText = remainingFiles > 0 && TryAverage(_recentFileDurations, out var averageFile)
                ? $"About {FormatDuration(Scale(averageFile, remainingFiles))} remaining"
                : "ETA estimating…";
        }
        else
        {
            BatchEtaText = $"Finished in {FormatDuration(_batchStopwatch.Elapsed)}";
        }

        if (_activeInputPath is null || _fileStopwatch is null)
        {
            CurrentFileTimingText = IsRunning ? "Waiting for file timing…" : "Elapsed —  •  ETA —";
            return;
        }

        var etaText = "ETA estimating…";
        if (_activeChunkIndex > 0 && TryAverage(_recentChunkDurations, out var averageChunk))
        {
            var laterChunks = Math.Max(0, _activeChunkCount - _activeChunkIndex);
            var currentRemaining = _chunkStopwatch is null
                ? TimeSpan.Zero
                : TimeSpan.FromTicks(Math.Max(0, averageChunk.Ticks - _chunkStopwatch.Elapsed.Ticks));
            etaText = $"About {FormatDuration(currentRemaining + Scale(averageChunk, laterChunks))} remaining";
        }

        CurrentFileTimingText = $"Elapsed {FormatDuration(_fileStopwatch.Elapsed)}  •  {etaText}";
        if (_jobsByInputPath.TryGetValue(_activeInputPath, out var row))
        {
            row.SetTimingText(CurrentFileTimingText);
        }
    }

    private static void AddRollingDuration(Queue<TimeSpan> durations, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        durations.Enqueue(duration);
        while (durations.Count > TimingWindowSize)
        {
            durations.Dequeue();
        }
    }

    private static bool TryAverage(Queue<TimeSpan> durations, out TimeSpan average)
    {
        if (durations.Count == 0)
        {
            average = default;
            return false;
        }

        average = TimeSpan.FromTicks((long)durations.Average(static duration => (double)duration.Ticks));
        return true;
    }

    private static TimeSpan Scale(TimeSpan duration, int count)
    {
        count = Math.Max(0, count);
        return count > 0 && duration.Ticks > TimeSpan.MaxValue.Ticks / count
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(duration.Ticks * count);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";
    }

    private void ShowCompletionMessage(BatchProgressSnapshot snapshot)
    {
        var failed = snapshot.Jobs.Count(static job => job.State == JobState.Failed);
        var cancelled = snapshot.Jobs.Count(static job => job.State == JobState.Cancelled);
        if (snapshot.CurrentStage == ProcessingStage.Cancelled || cancelled > 0)
        {
            ShowInfo("Batch cancelled", $"{snapshot.CompletedFileCount} of {snapshot.TotalFileCount} files finished before cancellation.", InfoBarSeverity.Warning);
        }
        else if (failed > 0)
        {
            ShowInfo("Batch complete with failures", $"{snapshot.CompletedFileCount} of {snapshot.TotalFileCount} files finished; {failed} failed.", InfoBarSeverity.Warning);
        }
        else
        {
            ShowInfo("Batch complete", $"{snapshot.CompletedFileCount} of {snapshot.TotalFileCount} files finished.", InfoBarSeverity.Success);
        }
    }

    private void ShowUnexpectedError(Exception exception)
        => ShowInfo("Unable to start batch", CompactMessage(exception.Message), InfoBarSeverity.Error);

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        InfoBarTitle = title;
        InfoBarMessage = message;
        InfoBarSeverity = severity;
        IsInfoBarOpen = true;
    }

    private void RefreshCommandAvailability()
    {
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private static FolderInspection InspectFolders(string inputPath, string outputPath)
    {
        var inputMessage = ValidateDirectory(inputPath, "Select an input folder.", out var inputValid);
        var outputMessage = ValidateDirectory(outputPath, "Select an output folder.", out var outputValid);
        var count = 0;

        if (inputValid)
        {
            try
            {
                var discovery = Mp4Discovery.Discover(inputPath, CancellationToken.None);
                if (discovery.Issues.Count > 0)
                {
                    inputValid = false;
                    inputMessage = discovery.Issues.Count == 1
                        ? "The input folder contains an item that cannot be inspected."
                        : $"{discovery.Issues.Count} input locations cannot be inspected.";
                }
                else
                {
                    count = discovery.Files.Count;
                    inputMessage = count == 0 ? "No MP4 files were found in this folder." : string.Empty;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                inputValid = false;
                inputMessage = "The input folder cannot be inspected.";
            }
        }

        return new FolderInspection(inputValid, outputValid, inputMessage, outputMessage, count);
    }

    private static string ValidateDirectory(string path, string emptyMessage, out bool isValid)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            isValid = false;
            return emptyMessage;
        }

        if (!Directory.Exists(path))
        {
            isValid = false;
            return "The selected folder is no longer available.";
        }

        isValid = true;
        return string.Empty;
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static string CompactMessage(string message)
    {
        const int maximumLength = 300;
        var compact = string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= maximumLength ? compact : string.Concat(compact.AsSpan(0, maximumLength - 1), "…");
    }

    private sealed record FolderInspection(
        bool IsInputValid,
        bool IsOutputValid,
        string InputMessage,
        string OutputMessage,
        int Mp4FileCount);

    private sealed record QueuedSnapshot(int BatchGeneration, BatchProgressSnapshot Snapshot);

    private sealed class SnapshotProgress : IProgress<BatchProgressSnapshot>
    {
        private readonly Action<BatchProgressSnapshot> _report;

        public SnapshotProgress(Action<BatchProgressSnapshot> report)
        {
            _report = report ?? throw new ArgumentNullException(nameof(report));
        }

        public void Report(BatchProgressSnapshot value) => _report(value);
    }
}
