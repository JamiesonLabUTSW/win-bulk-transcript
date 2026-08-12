using System.Diagnostics;
using WinBulkTranscript.Core.Domain;
using WinBulkTranscript.Core.Output;
using WinBulkTranscript.Core.Ports;

namespace WinBulkTranscript.Core.Batch;

/// <summary>Owns the one-file-at-a-time batch workflow and all legal job state transitions.</summary>
public sealed class BatchTranscriptionCoordinator
{
    private static readonly TimeSpan MinimumProgressInterval = TimeSpan.FromMilliseconds(75);
    private readonly IAudioExtractor _audioExtractor;
    private readonly IVoiceActivityDetector _voiceActivityDetector;
    private readonly ISpeechRecognizer _speechRecognizer;
    private readonly ITranscriptWriter _transcriptWriter;
    private readonly IModelHost _modelHost;
    private readonly IExistingOutputPolicyResolver _existingOutputPolicyResolver;

    public BatchTranscriptionCoordinator(
        IAudioExtractor audioExtractor,
        IVoiceActivityDetector voiceActivityDetector,
        ISpeechRecognizer speechRecognizer,
        ITranscriptWriter transcriptWriter,
        IModelHost modelHost,
        IExistingOutputPolicyResolver existingOutputPolicyResolver)
    {
        _audioExtractor = audioExtractor ?? throw new ArgumentNullException(nameof(audioExtractor));
        _voiceActivityDetector = voiceActivityDetector ?? throw new ArgumentNullException(nameof(voiceActivityDetector));
        _speechRecognizer = speechRecognizer ?? throw new ArgumentNullException(nameof(speechRecognizer));
        _transcriptWriter = transcriptWriter ?? throw new ArgumentNullException(nameof(transcriptWriter));
        _modelHost = modelHost ?? throw new ArgumentNullException(nameof(modelHost));
        _existingOutputPolicyResolver = existingOutputPolicyResolver ?? throw new ArgumentNullException(nameof(existingOutputPolicyResolver));
    }

    public async Task RunAsync(BatchRequest request, IProgress<BatchProgressSnapshot>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var jobs = new List<MutableJob>();
        var state = new RunState(jobs, progress);
        var reporter = new SnapshotReporter(state);

        try
        {
            // A cancellation requested before a batch begins must not create an output root or
            // run stale-temporary cleanup. Both are filesystem mutations outside the actual
            // processing pipeline, so observe cancellation before root validation does either.
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRoots(request);
            state.CurrentStage = ProcessingStage.Preflight;
            state.StageText = "Finding MP4 files";
            reporter.Publish(force: true);

            var discovery = Mp4Discovery.Discover(request.InputRoot, cancellationToken);
            if (discovery.Issues.Count > 0)
            {
                throw new BatchPreflightException(BuildDiscoveryFailure(discovery.Issues));
            }

            if (discovery.Files.Count == 0)
            {
                throw new BatchPreflightException("No MP4 files were found in the selected input folder.");
            }

            var preflight = BatchPreflight.Create(request, discovery.Files);
            foreach (var item in preflight.Items)
            {
                jobs.Add(new MutableJob(item));
            }

            reporter.Publish(force: true);

            var policy = await ResolveExistingOutputsAsync(preflight, state, reporter, cancellationToken).ConfigureAwait(false);
            if (policy == ExistingOutputPolicy.Cancel)
            {
                BeginCancellation(state, reporter, "Batch cancelled before processing.");
                return;
            }

            if (policy == ExistingOutputPolicy.SkipExisting)
            {
                foreach (var item in preflight.ExistingOutputs)
                {
                    var job = FindJob(jobs, item);
                    job.Complete("Existing VTT skipped", 0);
                }

                reporter.Publish(force: true);
            }

            var work = jobs.Where(static job => job.State == JobState.Pending).ToArray();
            if (work.Length == 0)
            {
                lock (state.SyncRoot)
                {
                    state.IsRunning = false;
                    state.CurrentStage = ProcessingStage.Complete;
                    state.StageText = "Complete";
                    reporter.Publish(force: true);
                }

                return;
            }

            state.CurrentStage = ProcessingStage.LoadingModel;
            state.StageText = "Loading speech model";
            reporter.Publish(force: true);
            var modelProgress = new CallbackProgress<ModelLoadProgress>(update =>
            {
                if (update is null)
                {
                    return;
                }

                lock (state.SyncRoot)
                {
                    if (!state.IsRunning
                        || state.IsCancelling
                        || state.CurrentStage is not (ProcessingStage.LoadingModel or ProcessingStage.DownloadingModel)
                        || update.Stage is not (ProcessingStage.LoadingModel or ProcessingStage.DownloadingModel))
                    {
                        return;
                    }

                    state.CurrentStage = update.Stage;
                    state.StageText = update.Detail;
                    if (TryNormalizeProgress(update.Fraction, out var normalizedProgress))
                    {
                        state.CurrentFileProgress = Math.Max(state.CurrentFileProgress, normalizedProgress);
                    }

                    reporter.Publish();
                }
            });

            try
            {
                await _modelHost.LoadAsync(modelProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                BeginCancellation(state, reporter, "Batch cancelled while loading the speech model.");
                return;
            }
            catch (Exception exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    BeginCancellation(state, reporter, "Batch cancelled while loading the speech model.");
                }
                else
                {
                    lock (state.SyncRoot)
                    {
                        MarkPendingFailed(jobs, "Speech model unavailable");
                        state.FatalError = $"Could not load the speech model: {exception.Message}";
                        state.IsRunning = false;
                        state.CurrentStage = ProcessingStage.Failed;
                        state.StageText = "Speech model unavailable";
                        reporter.Publish(force: true);
                    }
                }

                return;
            }

            var commitMode = policy == ExistingOutputPolicy.OverwriteAll
                ? TranscriptCommitMode.Overwrite
                : TranscriptCommitMode.FailIfExists;

            foreach (var job in work)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    BeginCancellation(state, reporter, "Batch cancelled.");
                    return;
                }

                await ProcessFileAsync(job, commitMode, state, reporter, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (state.SyncRoot)
            {
                if (state.CancellationRequested)
                {
                    return;
                }

                state.IsRunning = false;
                state.CurrentFileName = string.Empty;
                state.CurrentStage = ProcessingStage.Complete;
                state.StageText = "Complete";
                state.CurrentFileProgress = 1;
                reporter.Publish(force: true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            BeginCancellation(state, reporter, "Batch cancelled.");
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                BeginCancellation(state, reporter, "Batch cancelled.");
            }
            else
            {
                lock (state.SyncRoot)
                {
                    MarkPendingFailed(jobs, "Preflight failed");
                    state.FatalError = exception.Message;
                    state.IsRunning = false;
                    state.CurrentStage = ProcessingStage.Failed;
                    state.StageText = "Preflight failed";
                    reporter.Publish(force: true);
                }
            }
        }
        finally
        {
            try
            {
                await _modelHost.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    lock (state.SyncRoot)
                    {
                        state.FatalError ??= $"The speech model could not be unloaded cleanly: {exception.Message}";
                        reporter.Publish(force: true);
                    }
                }
            }
            finally
            {
                FinalizeCancellation(state, reporter);
            }
        }
    }

    private async Task<ExistingOutputPolicy> ResolveExistingOutputsAsync(
        PreflightResult preflight,
        RunState state,
        SnapshotReporter reporter,
        CancellationToken cancellationToken)
    {
        if (preflight.ExistingOutputs.Count == 0)
        {
            return ExistingOutputPolicy.SkipExisting;
        }

        state.CurrentStage = ProcessingStage.Preflight;
        state.StageText = $"{preflight.ExistingOutputs.Count} existing VTT file(s) found";
        reporter.Publish(force: true);
        return await _existingOutputPolicyResolver.ResolveAsync(preflight.ExistingOutputs, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessFileAsync(
        MutableJob job,
        TranscriptCommitMode commitMode,
        RunState state,
        SnapshotReporter reporter,
        CancellationToken cancellationToken)
    {
        lock (state.SyncRoot)
        {
            if (state.CancellationRequested)
            {
                return;
            }

            job.Start();
            state.CurrentFileName = job.Item.FileName;
            state.CurrentFileProgress = 0;
            SetStageCore(job, state, ProcessingStage.ExtractingAudio, "Extracting audio", 0, reporter, force: true);
        }

        try
        {
            var extractionProgress = CreateStageProgress(job, state, reporter, ProcessingStage.ExtractingAudio, "Extracting audio", 0, 0.30);
            await using var temporaryWave = await _audioExtractor
                .ExtractAsync(job.Item.InputPath, extractionProgress, cancellationToken)
                .ConfigureAwait(false);

            SetStage(job, state, ProcessingStage.DetectingSpeech, "Detecting speech", 0.30, reporter, force: true);
            var vadProgress = CreateStageProgress(job, state, reporter, ProcessingStage.DetectingSpeech, "Detecting speech", 0.30, 0.45);
            var intervals = await _voiceActivityDetector
                .DetectAsync(temporaryWave.WaveFile, vadProgress, cancellationToken)
                .ConfigureAwait(false);

            var orderedIntervals = ValidateIntervals(intervals, temporaryWave.WaveFile.SampleCount);
            var cues = new List<TranscriptCue>(orderedIntervals.Count);

            if (orderedIntervals.Count > 0)
            {
                var totalSpeechSamples = orderedIntervals.Sum(static interval => interval.LengthSamples);
                var completedSpeechSamples = 0L;
                for (var index = 0; index < orderedIntervals.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var interval = orderedIntervals[index];
                    SetStage(
                        job,
                        state,
                        ProcessingStage.Transcribing,
                        $"Transcribing {index + 1} of {orderedIntervals.Count}",
                        0.45 + (0.50 * completedSpeechSamples / totalSpeechSamples),
                        reporter,
                        force: true);

                    string text;
                    try
                    {
                        text = await _speechRecognizer
                            .RecognizeAsync(temporaryWave.WaveFile, interval, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        throw new InvalidOperationException(
                            $"Speech recognition failed for segment {index + 1} of {orderedIntervals.Count} " +
                            $"(samples [{interval.StartSample}, {interval.EndSample})): {exception.Message}",
                            exception);
                    }

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        cues.Add(new TranscriptCue(interval, text));
                    }

                    completedSpeechSamples += interval.LengthSamples;
                    var transcriptionProgress = 0.45 + (0.50 * completedSpeechSamples / totalSpeechSamples);
                    SetStage(job, state, ProcessingStage.Transcribing, $"Transcribing {index + 1} of {orderedIntervals.Count}", transcriptionProgress, reporter);
                }
            }

            SetStage(job, state, ProcessingStage.WritingVtt, "Writing VTT", 0.95, reporter, force: true);
            var writeProgress = CreateStageProgress(job, state, reporter, ProcessingStage.WritingVtt, "Writing VTT", 0.95, 1);
            var writeResult = await _transcriptWriter
                .WriteAsync(job.Item.OutputPath, cues, commitMode, writeProgress, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            lock (state.SyncRoot)
            {
                if (state.CancellationRequested)
                {
                    return;
                }

                if (writeResult.Disposition == TranscriptWriteDisposition.SkippedExisting)
                {
                    job.Complete("Existing VTT skipped", 0);
                }
                else if (orderedIntervals.Count == 0)
                {
                    job.Complete("No speech detected", 0);
                }
                else
                {
                    job.Complete($"{writeResult.CueCount} cue(s)", writeResult.CueCount);
                }

                state.CurrentFileProgress = 1;
                state.CurrentStage = ProcessingStage.Complete;
                state.StageText = job.Detail;
                reporter.Publish(force: true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            BeginCancellation(state, reporter, "Cancelled");
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                BeginCancellation(state, reporter, "Cancelled");
                return;
            }

            lock (state.SyncRoot)
            {
                if (state.CancellationRequested)
                {
                    return;
                }

                job.Fail(TrimForRow(exception.Message));
                state.CurrentStage = ProcessingStage.Failed;
                state.StageText = $"Failed: {job.Item.FileName}";
                reporter.Publish(force: true);
            }
        }
    }

    private static List<SpeechInterval> ValidateIntervals(IReadOnlyList<SpeechInterval> intervals, long totalSamples)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var result = new List<SpeechInterval>(intervals.Count);
        var previousEnd = 0L;

        foreach (var interval in intervals)
        {
            if (!interval.IsValid || interval.EndSample > totalSamples || interval.StartSample < previousEnd)
            {
                throw new InvalidOperationException("The voice activity detector returned invalid or overlapping sample intervals.");
            }

            result.Add(interval);
            previousEnd = interval.EndSample;
        }

        return result;
    }

    private static CallbackProgress<double> CreateStageProgress(
        MutableJob job,
        RunState state,
        SnapshotReporter reporter,
        ProcessingStage stage,
        string stageText,
        double start,
        double end)
        => new CallbackProgress<double>(fraction =>
        {
            if (!TryNormalizeProgress(fraction, out var bounded))
            {
                return;
            }

            lock (state.SyncRoot)
            {
                if (!state.IsRunning || state.IsCancelling || job.State != JobState.Transcribing || state.CurrentStage != stage)
                {
                    return;
                }

                SetStageCore(job, state, stage, stageText, start + ((end - start) * bounded), reporter);
            }
        });

    private static void SetStage(
        MutableJob job,
        RunState state,
        ProcessingStage stage,
        string stageText,
        double progress,
        SnapshotReporter reporter,
        bool force = false)
    {
        lock (state.SyncRoot)
        {
            SetStageCore(job, state, stage, stageText, progress, reporter, force);
        }
    }

    private static void SetStageCore(
        MutableJob job,
        RunState state,
        ProcessingStage stage,
        string stageText,
        double progress,
        SnapshotReporter reporter,
        bool force = false)
    {
        if (!state.IsRunning || state.IsCancelling || job.State != JobState.Transcribing)
        {
            return;
        }

        var boundedProgress = TryNormalizeProgress(progress, out var normalizedProgress)
            ? normalizedProgress
            : job.Progress;
        var monotonicProgress = Math.Max(job.Progress, boundedProgress);
        job.Update(stage, monotonicProgress, stageText);
        state.CurrentStage = stage;
        state.StageText = stageText;
        state.CurrentFileProgress = monotonicProgress;
        reporter.Publish(force);
    }

    private static void ValidateRoots(BatchRequest request)
    {
        if (!Directory.Exists(request.InputRoot))
        {
            throw new BatchPreflightException($"The input folder does not exist: {request.InputRoot}");
        }

        Directory.CreateDirectory(request.OutputRoot);
        if (!Directory.Exists(request.OutputRoot))
        {
            throw new BatchPreflightException($"The output folder could not be created: {request.OutputRoot}");
        }

        WebVttWriter.CleanupStaleTemporaryFiles(request.OutputRoot);
    }

    private static string BuildDiscoveryFailure(IReadOnlyList<DiscoveryIssue> issues)
    {
        var first = issues[0];
        return issues.Count == 1
            ? $"Could not inspect '{first.Path}': {first.Reason}"
            : $"Could not inspect {issues.Count} locations; first: '{first.Path}': {first.Reason}";
    }

    private static MutableJob FindJob(IEnumerable<MutableJob> jobs, BatchItem item)
        => jobs.First(job => string.Equals(job.Item.InputPath, item.InputPath, StringComparison.OrdinalIgnoreCase));

    private static void BeginCancellation(RunState state, SnapshotReporter reporter, string detail)
    {
        lock (state.SyncRoot)
        {
            if (state.CancellationRequested)
            {
                return;
            }

            state.CancellationRequested = true;
            state.IsCancelling = true;
            MarkUnfinishedCancelled(state.Jobs, detail);
            state.CurrentStage = ProcessingStage.Cancelled;
            state.StageText = "Cancelling…";
            reporter.Publish(force: true);
        }
    }

    private static void FinalizeCancellation(RunState state, SnapshotReporter reporter)
    {
        lock (state.SyncRoot)
        {
            if (!state.CancellationRequested)
            {
                return;
            }

            state.IsRunning = false;
            state.IsCancelling = false;
            state.CurrentFileName = string.Empty;
            state.CurrentStage = ProcessingStage.Cancelled;
            state.StageText = "Cancelled";
            reporter.Publish(force: true);
        }
    }

    private static void MarkUnfinishedCancelled(IEnumerable<MutableJob> jobs, string detail)
    {
        foreach (var job in jobs.Where(static job => job.State is JobState.Pending or JobState.Transcribing))
        {
            job.Cancel(detail);
        }
    }

    private static void MarkPendingFailed(IEnumerable<MutableJob> jobs, string detail)
    {
        foreach (var job in jobs.Where(static job => job.State == JobState.Pending))
        {
            job.Fail(detail);
        }
    }

    private static string TrimForRow(string message)
    {
        const int maximumLength = 180;
        var compact = string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= maximumLength ? compact : string.Concat(compact.AsSpan(0, maximumLength - 1), "…");
    }

    private sealed class MutableJob
    {
        public MutableJob(BatchItem item)
        {
            Item = item;
        }

        public BatchItem Item { get; }
        public JobState State { get; private set; } = JobState.Pending;
        public ProcessingStage Stage { get; private set; } = ProcessingStage.None;
        public double Progress { get; private set; }
        public string Detail { get; private set; } = "Pending";
        public int CueCount { get; private set; }

        public void Start()
        {
            if (State != JobState.Pending)
            {
                throw new InvalidOperationException("Only pending jobs can start.");
            }

            State = JobState.Transcribing;
        }

        public void Update(ProcessingStage stage, double progress, string detail)
        {
            if (State != JobState.Transcribing)
            {
                throw new InvalidOperationException("Only active jobs can report processing progress.");
            }

            Stage = stage;
            Progress = Math.Max(Progress, progress);
            Detail = detail;
        }

        public void Complete(string detail, int cueCount)
        {
            RequirePendingOrActive();
            State = JobState.Complete;
            Stage = ProcessingStage.Complete;
            Progress = 1;
            Detail = detail;
            CueCount = cueCount;
        }

        public void Fail(string detail)
        {
            RequirePendingOrActive();
            State = JobState.Failed;
            Stage = ProcessingStage.Failed;
            Detail = detail;
        }

        public void Cancel(string detail)
        {
            RequirePendingOrActive();
            State = JobState.Cancelled;
            Stage = ProcessingStage.Cancelled;
            Detail = detail;
        }

        public JobSnapshot Snapshot() => new(Item.InputPath, Item.RelativePath, Item.OutputPath, State, Stage, Progress, Detail, CueCount);

        private void RequirePendingOrActive()
        {
            if (State is not (JobState.Pending or JobState.Transcribing))
            {
                throw new InvalidOperationException("Job already reached a terminal state.");
            }
        }
    }

    private sealed class RunState
    {
        public RunState(List<MutableJob> jobs, IProgress<BatchProgressSnapshot>? progress)
        {
            Jobs = jobs;
            Progress = progress;
        }

        public List<MutableJob> Jobs { get; }
        public IProgress<BatchProgressSnapshot>? Progress { get; }
        public object SyncRoot { get; } = new();
        public bool CancellationRequested { get; set; }
        public bool IsRunning { get; set; } = true;
        public bool IsCancelling { get; set; }
        public string CurrentFileName { get; set; } = string.Empty;
        public ProcessingStage CurrentStage { get; set; } = ProcessingStage.None;
        public string StageText { get; set; } = string.Empty;
        public double CurrentFileProgress { get; set; }
        public string? FatalError { get; set; }
    }

    private sealed class SnapshotReporter
    {
        private readonly RunState _state;
        private long _lastPublishTimestamp;

        public SnapshotReporter(RunState state)
        {
            _state = state;
        }

        public void Publish(bool force = false)
        {
            lock (_state.SyncRoot)
            {
            if (_state.Progress is null)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var elapsed = _lastPublishTimestamp == 0
                ? TimeSpan.MaxValue
                : Stopwatch.GetElapsedTime(_lastPublishTimestamp, now);
            if (!force && elapsed < MinimumProgressInterval)
            {
                return;
            }

            _lastPublishTimestamp = now;
            var snapshots = _state.Jobs.Select(static job => job.Snapshot()).ToArray();
            var completeCount = snapshots.Count(static job => job.State is JobState.Complete or JobState.Failed or JobState.Cancelled);
            _state.Progress.Report(new BatchProgressSnapshot(
                snapshots,
                completeCount,
                snapshots.Length,
                _state.CurrentFileName,
                _state.CurrentStage,
                _state.StageText,
                _state.CurrentFileProgress,
                _state.IsRunning,
                _state.IsCancelling,
                _state.FatalError));
            }
        }
    }

    private static bool TryNormalizeProgress(double? value, out double normalizedProgress)
    {
        if (value is not { } fraction || !double.IsFinite(fraction))
        {
            normalizedProgress = 0;
            return false;
        }

        normalizedProgress = Math.Clamp(fraction, 0d, 1d);
        return true;
    }

    private sealed class CallbackProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;

        public CallbackProgress(Action<T> callback)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        public void Report(T value) => _callback(value);
    }
}
