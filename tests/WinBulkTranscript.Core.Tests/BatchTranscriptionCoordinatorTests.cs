using WinBulkTranscript.Core.Batch;
using WinBulkTranscript.Core.Domain;
using WinBulkTranscript.Core.Ports;

namespace WinBulkTranscript.Core.Tests;

public sealed class BatchTranscriptionCoordinatorTests
{
    [Fact]
    public async Task RunAsync_ProcessesSnapshotSequentially_LoadsOnce_AndCompletesEveryJob()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        var alpha = workspace.CreateTextFile(Path.Combine("input", "alpha.mp4"));
        var beta = workspace.CreateTextFile(Path.Combine("input", "nested", "beta.mp4"));
        var tracker = new AsyncOperationTracker();
        var extractor = new FakeAudioExtractor(tracker);
        var detector = new FakeVoiceActivityDetector(tracker);
        var recognizer = new FakeSpeechRecognizer(tracker);
        var writer = new FakeTranscriptWriter(tracker);
        var model = new FakeModelHost(tracker);
        var resolver = new FakeExistingOutputPolicyResolver(ExistingOutputPolicy.SkipExisting);
        var snapshots = new InlineProgress<BatchProgressSnapshot>();
        var coordinator = CreateCoordinator(extractor, detector, recognizer, writer, model, resolver);

        await coordinator.RunAsync(BatchRequest.Create(inputRoot, outputRoot), snapshots, CancellationToken.None);

        Assert.Equal([alpha, beta], extractor.InputPaths);
        Assert.Equal(2, detector.CallCount);
        Assert.Equal(2, recognizer.CallCount);
        Assert.Equal(2, writer.Calls.Count);
        Assert.All(writer.Calls, call => Assert.Equal(TranscriptCommitMode.FailIfExists, call.CommitMode));
        Assert.Empty(resolver.Calls);
        Assert.Equal(1, model.LoadCount);
        Assert.Equal(1, model.DisposeCount);
        Assert.Equal(2, extractor.CleanupCount);
        Assert.Equal(1, tracker.MaximumConcurrent);

        var final = GetFinalSnapshot(snapshots);
        Assert.False(final.IsRunning);
        Assert.False(final.IsCancelling);
        Assert.Equal(ProcessingStage.Complete, final.CurrentStage);
        Assert.Equal(2, final.CompletedFileCount);
        Assert.Equal(2, final.TotalFileCount);
        Assert.All(final.Jobs, job =>
        {
            Assert.Equal(JobState.Complete, job.State);
            Assert.Equal(ProcessingStage.Complete, job.Stage);
            Assert.Equal(1d, job.Progress);
            Assert.Equal(1, job.CueCount);
        });
    }

    [Fact]
    public async Task RunAsync_ContinuesAfterPerFileFailure_AndLeavesLaterJobComplete()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        workspace.CreateTextFile(Path.Combine("input", "bad.mp4"));
        var good = workspace.CreateTextFile(Path.Combine("input", "good.mp4"));
        var tracker = new AsyncOperationTracker();
        var extractor = new FakeAudioExtractor(tracker)
        {
            FailureForInput = path => Path.GetFileName(path).Equals("bad.mp4", StringComparison.OrdinalIgnoreCase)
                ? new InvalidOperationException("decoder exploded")
                : null,
        };
        var detector = new FakeVoiceActivityDetector(tracker);
        var recognizer = new FakeSpeechRecognizer(tracker);
        var writer = new FakeTranscriptWriter(tracker);
        var model = new FakeModelHost(tracker);
        var coordinator = CreateCoordinator(
            extractor,
            detector,
            recognizer,
            writer,
            model,
            new FakeExistingOutputPolicyResolver(ExistingOutputPolicy.SkipExisting));
        var snapshots = new InlineProgress<BatchProgressSnapshot>();

        await coordinator.RunAsync(BatchRequest.Create(inputRoot, outputRoot), snapshots, CancellationToken.None);

        Assert.Equal(["good.vtt"], writer.Calls.Select(call => call.OutputPath).Select(Path.GetFileName));
        Assert.Equal(1, model.LoadCount);
        Assert.Equal(1, model.DisposeCount);
        Assert.Equal(1, extractor.CleanupCount);
        var final = GetFinalSnapshot(snapshots);
        Assert.Null(final.FatalError);
        Assert.Equal(ProcessingStage.Complete, final.CurrentStage);
        Assert.Equal(JobState.Failed, final.Jobs[0].State);
        Assert.Contains("decoder exploded", final.Jobs[0].Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JobState.Complete, final.Jobs[1].State);
    }

    [Fact]
    public async Task RunAsync_RecognitionFailureIncludesSegmentOrdinalAndSampleRange()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        workspace.CreateTextFile(Path.Combine("input", "failure.mp4"));
        var tracker = new AsyncOperationTracker();
        var recognizer = new FakeSpeechRecognizer(tracker)
        {
            Failure = new InvalidOperationException("native recognizer rejected the stream"),
        };
        var detector = new FakeVoiceActivityDetector(tracker)
        {
            Intervals = [new SpeechInterval(20, 100)],
        };
        var coordinator = CreateCoordinator(
            new FakeAudioExtractor(tracker),
            detector,
            recognizer,
            new FakeTranscriptWriter(tracker),
            new FakeModelHost(tracker),
            new FakeExistingOutputPolicyResolver(ExistingOutputPolicy.SkipExisting));
        var snapshots = new InlineProgress<BatchProgressSnapshot>();

        await coordinator.RunAsync(BatchRequest.Create(inputRoot, outputRoot), snapshots, CancellationToken.None);

        var failedJob = Assert.Single(GetFinalSnapshot(snapshots).Jobs);
        Assert.Equal(JobState.Failed, failedJob.State);
        Assert.Contains("segment 1 of 1", failedJob.Detail, StringComparison.Ordinal);
        Assert.Contains("samples [20, 100)", failedJob.Detail, StringComparison.Ordinal);
        Assert.Contains("native recognizer rejected the stream", failedJob.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CancellationDuringRecognition_CancelsCurrentAndPendingJobsAndDisposesTemporaryWave()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        workspace.CreateTextFile(Path.Combine("input", "first.mp4"));
        workspace.CreateTextFile(Path.Combine("input", "second.mp4"));
        using var cancellation = new CancellationTokenSource();
        var tracker = new AsyncOperationTracker();
        var extractor = new FakeAudioExtractor(tracker);
        var detector = new FakeVoiceActivityDetector(tracker);
        var recognizer = new FakeSpeechRecognizer(tracker)
        {
            CancelSource = cancellation,
            CancelOnCall = 1,
        };
        var writer = new FakeTranscriptWriter(tracker);
        var model = new FakeModelHost(tracker);
        var coordinator = CreateCoordinator(
            extractor,
            detector,
            recognizer,
            writer,
            model,
            new FakeExistingOutputPolicyResolver(ExistingOutputPolicy.SkipExisting));
        var snapshots = new InlineProgress<BatchProgressSnapshot>();

        await coordinator.RunAsync(BatchRequest.Create(inputRoot, outputRoot), snapshots, cancellation.Token);

        Assert.Single(extractor.InputPaths);
        Assert.Equal(1, detector.CallCount);
        Assert.Equal(1, recognizer.CallCount);
        Assert.Empty(writer.Calls);
        Assert.Equal(1, extractor.CleanupCount);
        Assert.Equal(1, model.DisposeCount);
        var final = GetFinalSnapshot(snapshots);
        Assert.Equal(ProcessingStage.Cancelled, final.CurrentStage);
        Assert.False(final.IsRunning);
        Assert.All(final.Jobs, job => Assert.Equal(JobState.Cancelled, job.State));
    }

    [Fact]
    public async Task RunAsync_PreCancelled_DoesNotCreateOutputRootOrStartPorts()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = Path.Combine(workspace.Root, "not-created-output");
        workspace.CreateTextFile(Path.Combine("input", "first.mp4"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var tracker = new AsyncOperationTracker();
        var extractor = new FakeAudioExtractor(tracker);
        var model = new FakeModelHost(tracker);
        var snapshots = new InlineProgress<BatchProgressSnapshot>();
        var coordinator = CreateCoordinator(
            extractor,
            new FakeVoiceActivityDetector(tracker),
            new FakeSpeechRecognizer(tracker),
            new FakeTranscriptWriter(tracker),
            model,
            new FakeExistingOutputPolicyResolver(ExistingOutputPolicy.SkipExisting));

        await coordinator.RunAsync(BatchRequest.Create(inputRoot, outputRoot), snapshots, cancellation.Token);

        Assert.False(Directory.Exists(outputRoot));
        Assert.Empty(extractor.InputPaths);
        Assert.Equal(0, model.LoadCount);
        Assert.Equal(1, model.DisposeCount);
        var final = GetFinalSnapshot(snapshots);
        Assert.Equal(ProcessingStage.Cancelled, final.CurrentStage);
        Assert.False(final.IsRunning);
        Assert.False(final.IsCancelling);
        Assert.Empty(final.Jobs);
    }

    [Fact]
    public async Task RunAsync_CancellationDoesNotEscapeWhenModelDisposalFails()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        workspace.CreateTextFile(Path.Combine("input", "first.mp4"));
        using var cancellation = new CancellationTokenSource();
        var tracker = new AsyncOperationTracker();
        var model = new FakeModelHost(tracker)
        {
            DisposeException = new InvalidOperationException("native unload failed"),
        };
        var recognizer = new FakeSpeechRecognizer(tracker)
        {
            CancelSource = cancellation,
            CancelOnCall = 1,
        };
        var coordinator = CreateCoordinator(
            new FakeAudioExtractor(tracker),
            new FakeVoiceActivityDetector(tracker),
            recognizer,
            new FakeTranscriptWriter(tracker),
            model,
            new FakeExistingOutputPolicyResolver(ExistingOutputPolicy.SkipExisting));
        var snapshots = new InlineProgress<BatchProgressSnapshot>();

        await coordinator.RunAsync(BatchRequest.Create(inputRoot, outputRoot), snapshots, cancellation.Token);

        Assert.Equal(1, model.DisposeCount);
        var final = GetFinalSnapshot(snapshots);
        Assert.Equal(ProcessingStage.Cancelled, final.CurrentStage);
        Assert.False(final.IsRunning);
        Assert.All(final.Jobs, job => Assert.Equal(JobState.Cancelled, job.State));
    }

    [Fact]
    public async Task RunAsync_CancellationAfterWriterReturns_CancelsRatherThanFailingTheActiveJob()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        workspace.CreateTextFile(Path.Combine("input", "first.mp4"));
        using var cancellation = new CancellationTokenSource();
        var tracker = new AsyncOperationTracker();
        var writer = new FakeTranscriptWriter(tracker) { CancelSource = cancellation };
        var coordinator = CreateCoordinator(
            new FakeAudioExtractor(tracker),
            new FakeVoiceActivityDetector(tracker),
            new FakeSpeechRecognizer(tracker),
            writer,
            new FakeModelHost(tracker),
            new FakeExistingOutputPolicyResolver(ExistingOutputPolicy.SkipExisting));
        var snapshots = new InlineProgress<BatchProgressSnapshot>();

        await coordinator.RunAsync(BatchRequest.Create(inputRoot, outputRoot), snapshots, cancellation.Token);

        Assert.Single(writer.Calls);
        var final = GetFinalSnapshot(snapshots);
        Assert.Equal(ProcessingStage.Cancelled, final.CurrentStage);
        Assert.False(final.IsRunning);
        Assert.Equal(JobState.Cancelled, Assert.Single(final.Jobs).State);
    }

    [Fact]
    public async Task RunAsync_SkipExisting_IsBatchWideAndDoesNotProcessSkippedFiles()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        workspace.CreateTextFile(Path.Combine("input", "already.mp4"));
        workspace.CreateTextFile(Path.Combine("input", "fresh.mp4"));
        workspace.CreateTextFile(Path.Combine("output", "already.vtt"), "old");
        var tracker = new AsyncOperationTracker();
        var extractor = new FakeAudioExtractor(tracker);
        var detector = new FakeVoiceActivityDetector(tracker);
        var recognizer = new FakeSpeechRecognizer(tracker);
        var writer = new FakeTranscriptWriter(tracker);
        var model = new FakeModelHost(tracker);
        var resolver = new FakeExistingOutputPolicyResolver(ExistingOutputPolicy.SkipExisting);
        var coordinator = CreateCoordinator(extractor, detector, recognizer, writer, model, resolver);
        var snapshots = new InlineProgress<BatchProgressSnapshot>();

        await coordinator.RunAsync(BatchRequest.Create(inputRoot, outputRoot), snapshots, CancellationToken.None);

        var existingPrompt = Assert.Single(resolver.Calls);
        Assert.Single(existingPrompt);
        Assert.Equal("already.mp4", Path.GetFileName(existingPrompt[0].InputPath));
        Assert.Equal(["fresh.mp4"], extractor.InputPaths.Select(Path.GetFileName));
        Assert.Single(writer.Calls);
        Assert.Equal(1, model.LoadCount);
        var final = GetFinalSnapshot(snapshots);
        Assert.Equal("Existing VTT skipped", final.Jobs[0].Detail);
        Assert.Equal(JobState.Complete, final.Jobs[0].State);
        Assert.Equal(JobState.Complete, final.Jobs[1].State);
    }

    [Fact]
    public async Task RunAsync_AllExistingOutputsSkipped_DoesNotLoadTheModelOrInvokeFilePorts()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        workspace.CreateTextFile(Path.Combine("input", "already.mp4"));
        workspace.CreateTextFile(Path.Combine("output", "already.vtt"), "existing");
        var tracker = new AsyncOperationTracker();
        var extractor = new FakeAudioExtractor(tracker);
        var detector = new FakeVoiceActivityDetector(tracker);
        var recognizer = new FakeSpeechRecognizer(tracker);
        var writer = new FakeTranscriptWriter(tracker);
        var model = new FakeModelHost(tracker);
        var resolver = new FakeExistingOutputPolicyResolver(ExistingOutputPolicy.SkipExisting);
        var snapshots = new InlineProgress<BatchProgressSnapshot>();
        var coordinator = CreateCoordinator(extractor, detector, recognizer, writer, model, resolver);

        await coordinator.RunAsync(BatchRequest.Create(inputRoot, outputRoot), snapshots, CancellationToken.None);

        Assert.Single(resolver.Calls);
        Assert.Empty(extractor.InputPaths);
        Assert.Equal(0, detector.CallCount);
        Assert.Equal(0, recognizer.CallCount);
        Assert.Empty(writer.Calls);
        Assert.Equal(0, model.LoadCount);
        Assert.Equal(1, model.DisposeCount);
        var final = GetFinalSnapshot(snapshots);
        Assert.Equal(ProcessingStage.Complete, final.CurrentStage);
        var job = Assert.Single(final.Jobs);
        Assert.Equal(JobState.Complete, job.State);
        Assert.Equal("Existing VTT skipped", job.Detail);
    }

    [Fact]
    public async Task RunAsync_OverwriteAll_UsesOverwriteCommitModeForEveryFile()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        workspace.CreateTextFile(Path.Combine("input", "existing.mp4"));
        workspace.CreateTextFile(Path.Combine("input", "new.mp4"));
        workspace.CreateTextFile(Path.Combine("output", "existing.vtt"), "old");
        var tracker = new AsyncOperationTracker();
        var writer = new FakeTranscriptWriter(tracker);
        var coordinator = CreateCoordinator(
            new FakeAudioExtractor(tracker),
            new FakeVoiceActivityDetector(tracker),
            new FakeSpeechRecognizer(tracker),
            writer,
            new FakeModelHost(tracker),
            new FakeExistingOutputPolicyResolver(ExistingOutputPolicy.OverwriteAll));

        await coordinator.RunAsync(BatchRequest.Create(inputRoot, outputRoot), progress: null, CancellationToken.None);

        Assert.Equal(2, writer.Calls.Count);
        Assert.All(writer.Calls, call => Assert.Equal(TranscriptCommitMode.Overwrite, call.CommitMode));
    }

    [Fact]
    public async Task RunAsync_CancelExistingOutputPrompt_CancelsWithoutLoadingModelOrProcessing()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        workspace.CreateTextFile(Path.Combine("input", "existing.mp4"));
        workspace.CreateTextFile(Path.Combine("output", "existing.vtt"), "old");
        var tracker = new AsyncOperationTracker();
        var extractor = new FakeAudioExtractor(tracker);
        var model = new FakeModelHost(tracker);
        var snapshots = new InlineProgress<BatchProgressSnapshot>();
        var coordinator = CreateCoordinator(
            extractor,
            new FakeVoiceActivityDetector(tracker),
            new FakeSpeechRecognizer(tracker),
            new FakeTranscriptWriter(tracker),
            model,
            new FakeExistingOutputPolicyResolver(ExistingOutputPolicy.Cancel));

        await coordinator.RunAsync(BatchRequest.Create(inputRoot, outputRoot), snapshots, CancellationToken.None);

        Assert.Equal(0, model.LoadCount);
        Assert.Equal(1, model.DisposeCount);
        Assert.Empty(extractor.InputPaths);
        var final = GetFinalSnapshot(snapshots);
        Assert.Equal(ProcessingStage.Cancelled, final.CurrentStage);
        Assert.Single(final.Jobs);
        Assert.Equal(JobState.Cancelled, final.Jobs[0].State);
    }

    [Fact]
    public async Task RunAsync_ModelLoadFailure_FailsAllPendingJobsAndSkipsFilePorts()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        workspace.CreateTextFile(Path.Combine("input", "first.mp4"));
        workspace.CreateTextFile(Path.Combine("input", "second.mp4"));
        var tracker = new AsyncOperationTracker();
        var extractor = new FakeAudioExtractor(tracker);
        var model = new FakeModelHost(tracker) { LoadException = new InvalidOperationException("model unavailable") };
        var snapshots = new InlineProgress<BatchProgressSnapshot>();
        var coordinator = CreateCoordinator(
            extractor,
            new FakeVoiceActivityDetector(tracker),
            new FakeSpeechRecognizer(tracker),
            new FakeTranscriptWriter(tracker),
            model,
            new FakeExistingOutputPolicyResolver(ExistingOutputPolicy.SkipExisting));

        await coordinator.RunAsync(BatchRequest.Create(inputRoot, outputRoot), snapshots, CancellationToken.None);

        Assert.Empty(extractor.InputPaths);
        Assert.Equal(1, model.LoadCount);
        Assert.Equal(1, model.DisposeCount);
        var final = GetFinalSnapshot(snapshots);
        Assert.Equal(ProcessingStage.Failed, final.CurrentStage);
        Assert.Contains("model unavailable", final.FatalError!, StringComparison.OrdinalIgnoreCase);
        Assert.All(final.Jobs, job => Assert.Equal(JobState.Failed, job.State));
    }

    [Fact]
    public async Task RunAsync_NoSpeechStillWritesHeaderOnlyTranscriptWithoutRecognizerCall()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        workspace.CreateTextFile(Path.Combine("input", "quiet.mp4"));
        var tracker = new AsyncOperationTracker();
        var recognizer = new FakeSpeechRecognizer(tracker);
        var writer = new FakeTranscriptWriter(tracker);
        var coordinator = CreateCoordinator(
            new FakeAudioExtractor(tracker),
            new FakeVoiceActivityDetector(tracker) { Intervals = [] },
            recognizer,
            writer,
            new FakeModelHost(tracker),
            new FakeExistingOutputPolicyResolver(ExistingOutputPolicy.SkipExisting));
        var snapshots = new InlineProgress<BatchProgressSnapshot>();

        await coordinator.RunAsync(BatchRequest.Create(inputRoot, outputRoot), snapshots, CancellationToken.None);

        Assert.Equal(0, recognizer.CallCount);
        var call = Assert.Single(writer.Calls);
        Assert.Empty(call.Cues);
        var final = GetFinalSnapshot(snapshots);
        Assert.Equal("No speech detected", final.Jobs[0].Detail);
        Assert.Equal(JobState.Complete, final.Jobs[0].State);
    }

    [Fact]
    public async Task RunAsync_CancellationStaysCancellingUntilModelDisposalCompletes()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        workspace.CreateTextFile(Path.Combine("input", "first.mp4"));
        using var cancellation = new CancellationTokenSource();
        var tracker = new AsyncOperationTracker();
        var model = new BlockingDisposeModelHost(cancellation);
        var snapshots = new InlineProgress<BatchProgressSnapshot>();
        var coordinator = CreateCoordinator(
            new FakeAudioExtractor(tracker),
            new FakeVoiceActivityDetector(tracker),
            new FakeSpeechRecognizer(tracker),
            new FakeTranscriptWriter(tracker),
            model,
            new FakeExistingOutputPolicyResolver(ExistingOutputPolicy.SkipExisting));

        var run = coordinator.RunAsync(BatchRequest.Create(inputRoot, outputRoot), snapshots, cancellation.Token);
        await model.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelling = GetFinalSnapshot(snapshots);
        Assert.True(cancelling.IsRunning);
        Assert.True(cancelling.IsCancelling);
        Assert.Equal(ProcessingStage.Cancelled, cancelling.CurrentStage);
        Assert.False(run.IsCompleted);
        Assert.All(cancelling.Jobs, job => Assert.Equal(JobState.Cancelled, job.State));

        model.ReleaseDisposal.TrySetResult(true);
        await run;

        var final = GetFinalSnapshot(snapshots);
        Assert.False(final.IsRunning);
        Assert.False(final.IsCancelling);
        Assert.Equal(ProcessingStage.Cancelled, final.CurrentStage);
    }

    [Fact]
    public async Task RunAsync_IgnoresLateProgressFromAnEarlierStage()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        workspace.CreateTextFile(Path.Combine("input", "first.mp4"));
        var tracker = new AsyncOperationTracker();
        var extractor = new FakeAudioExtractor(tracker);
        var detector = new BlockingLateProgressVoiceActivityDetector(
            tracker,
            () => extractor.CapturedProgress ?? throw new InvalidOperationException("The extractor did not receive a progress callback."));
        var snapshots = new InlineProgress<BatchProgressSnapshot>();
        var coordinator = CreateCoordinator(
            extractor,
            detector,
            new FakeSpeechRecognizer(tracker),
            new FakeTranscriptWriter(tracker),
            new FakeModelHost(tracker),
            new FakeExistingOutputPolicyResolver(ExistingOutputPolicy.SkipExisting));

        var run = coordinator.RunAsync(BatchRequest.Create(inputRoot, outputRoot), snapshots, CancellationToken.None);
        await detector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var duringDetection = GetFinalSnapshot(snapshots);
        Assert.Equal(ProcessingStage.DetectingSpeech, duringDetection.CurrentStage);
        Assert.Equal("Detecting speech", duringDetection.StageText);
        Assert.Equal(0.30d, duringDetection.CurrentFileProgress);

        detector.Release.TrySetResult(true);
        await run;
        Assert.Equal(ProcessingStage.Complete, GetFinalSnapshot(snapshots).CurrentStage);
    }

    [Fact]
    public async Task RunAsync_IgnoresNonFiniteModelAndStageProgress()
    {
        using var workspace = new TestWorkspace();
        var inputRoot = workspace.CreateDirectory("input");
        var outputRoot = workspace.CreateDirectory("output");
        workspace.CreateTextFile(Path.Combine("input", "first.mp4"));
        var tracker = new AsyncOperationTracker();
        var model = new FakeModelHost(tracker)
        {
            ProgressUpdates =
            [
                new ModelLoadProgress(ProcessingStage.DownloadingModel, double.NaN, "NaN"),
                new ModelLoadProgress(ProcessingStage.DownloadingModel, double.PositiveInfinity, "positive infinity"),
                new ModelLoadProgress(ProcessingStage.DownloadingModel, double.NegativeInfinity, "negative infinity"),
            ],
        };
        var extractor = new FakeAudioExtractor(tracker)
        {
            ProgressUpdates = [double.NaN, double.PositiveInfinity, double.NegativeInfinity],
        };
        var snapshots = new InlineProgress<BatchProgressSnapshot>();
        var coordinator = CreateCoordinator(
            extractor,
            new FakeVoiceActivityDetector(tracker),
            new FakeSpeechRecognizer(tracker),
            new FakeTranscriptWriter(tracker),
            model,
            new FakeExistingOutputPolicyResolver(ExistingOutputPolicy.SkipExisting));

        await coordinator.RunAsync(BatchRequest.Create(inputRoot, outputRoot), snapshots, CancellationToken.None);

        Assert.All(snapshots.Values, snapshot =>
        {
            Assert.True(double.IsFinite(snapshot.CurrentFileProgress));
            Assert.InRange(snapshot.CurrentFileProgress, 0d, 1d);
            Assert.All(snapshot.Jobs, job =>
            {
                Assert.True(double.IsFinite(job.Progress));
                Assert.InRange(job.Progress, 0d, 1d);
            });
        });
    }

    private static BatchTranscriptionCoordinator CreateCoordinator(
        IAudioExtractor audioExtractor,
        IVoiceActivityDetector voiceActivityDetector,
        ISpeechRecognizer speechRecognizer,
        ITranscriptWriter transcriptWriter,
        IModelHost modelHost,
        IExistingOutputPolicyResolver existingOutputPolicyResolver)
        => new(
            audioExtractor,
            voiceActivityDetector,
            speechRecognizer,
            transcriptWriter,
            modelHost,
            existingOutputPolicyResolver);

    private static BatchProgressSnapshot GetFinalSnapshot(InlineProgress<BatchProgressSnapshot> progress)
    {
        Assert.NotEmpty(progress.Values);
        return progress.Values[^1];
    }

    private sealed class AsyncOperationTracker
    {
        private readonly object _gate = new();
        private int _active;

        public int MaximumConcurrent { get; private set; }

        public async Task<IDisposable> EnterAsync()
        {
            lock (_gate)
            {
                _active++;
                MaximumConcurrent = Math.Max(MaximumConcurrent, _active);
            }

            await Task.Yield();
            return new Lease(this);
        }

        private void Leave()
        {
            lock (_gate)
            {
                _active--;
            }
        }

        private sealed class Lease(AsyncOperationTracker owner) : IDisposable
        {
            public void Dispose() => owner.Leave();
        }
    }

    private sealed class FakeAudioExtractor(AsyncOperationTracker tracker) : IAudioExtractor
    {
        public List<string> InputPaths { get; } = [];
        public Func<string, Exception?>? FailureForInput { get; init; }
        public IReadOnlyList<double> ProgressUpdates { get; init; } = [];
        public IProgress<double>? CapturedProgress { get; private set; }
        public int CleanupCount { get; private set; }

        public async Task<TemporaryPcmWave> ExtractAsync(string inputPath, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            using var operation = await tracker.EnterAsync();
            cancellationToken.ThrowIfCancellationRequested();
            InputPaths.Add(inputPath);
            CapturedProgress = progress;
            foreach (var update in ProgressUpdates)
            {
                progress?.Report(update);
            }

            var exception = FailureForInput?.Invoke(inputPath);
            if (exception is not null)
            {
                throw exception;
            }

            var wave = new PcmWaveFile(inputPath, PcmFormat.Required, 0, 200);
            return new TemporaryPcmWave(wave, () =>
            {
                CleanupCount++;
                return ValueTask.CompletedTask;
            });
        }
    }

    private sealed class FakeVoiceActivityDetector(AsyncOperationTracker tracker) : IVoiceActivityDetector
    {
        public IReadOnlyList<SpeechInterval> Intervals { get; init; } = [new SpeechInterval(0, 100)];
        public int CallCount { get; private set; }

        public async Task<IReadOnlyList<SpeechInterval>> DetectAsync(PcmWaveFile waveFile, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            using var operation = await tracker.EnterAsync();
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Intervals;
        }
    }

    private sealed class FakeSpeechRecognizer(AsyncOperationTracker tracker) : ISpeechRecognizer
    {
        public CancellationTokenSource? CancelSource { get; init; }
        public int CancelOnCall { get; init; }
        public Exception? Failure { get; init; }
        public int CallCount { get; private set; }

        public async Task<string> RecognizeAsync(PcmWaveFile waveFile, SpeechInterval interval, CancellationToken cancellationToken)
        {
            using var operation = await tracker.EnterAsync();
            CallCount++;
            if (CancelSource is not null && CallCount == CancelOnCall)
            {
                CancelSource.Cancel();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            return "recognized text";
        }
    }

    private sealed class FakeTranscriptWriter(AsyncOperationTracker tracker) : ITranscriptWriter
    {
        public CancellationTokenSource? CancelSource { get; init; }
        public List<WriteCall> Calls { get; } = [];

        public async Task<TranscriptWriteResult> WriteAsync(
            string outputPath,
            IReadOnlyList<TranscriptCue> cues,
            TranscriptCommitMode commitMode,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            using var operation = await tracker.EnterAsync();
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new WriteCall(outputPath, cues.ToArray(), commitMode));
            CancelSource?.Cancel();
            return new TranscriptWriteResult(TranscriptWriteDisposition.Written, cues.Count);
        }
    }

    private sealed record WriteCall(string OutputPath, IReadOnlyList<TranscriptCue> Cues, TranscriptCommitMode CommitMode);

    private sealed class FakeModelHost(AsyncOperationTracker tracker) : IModelHost
    {
        public Exception? LoadException { get; init; }
        public Exception? DisposeException { get; init; }
        public IReadOnlyList<ModelLoadProgress> ProgressUpdates { get; init; } = [];
        public int LoadCount { get; private set; }
        public int DisposeCount { get; private set; }

        public async Task LoadAsync(IProgress<ModelLoadProgress>? progress, CancellationToken cancellationToken)
        {
            using var operation = await tracker.EnterAsync();
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            foreach (var update in ProgressUpdates)
            {
                progress?.Report(update);
            }

            if (LoadException is not null)
            {
                throw LoadException;
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return DisposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeException);
        }
    }

    private sealed class BlockingDisposeModelHost(CancellationTokenSource cancellation) : IModelHost
    {
        public TaskCompletionSource<bool> DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task LoadAsync(IProgress<ModelLoadProgress>? progress, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            DisposeEntered.TrySetResult(true);
            await ReleaseDisposal.Task.ConfigureAwait(false);
        }
    }

    private sealed class BlockingLateProgressVoiceActivityDetector(
        AsyncOperationTracker tracker,
        Func<IProgress<double>> lateProgress) : IVoiceActivityDetector
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<SpeechInterval>> DetectAsync(
            PcmWaveFile waveFile,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            using var operation = await tracker.EnterAsync();
            cancellationToken.ThrowIfCancellationRequested();
            lateProgress().Report(1d);
            Entered.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return [new SpeechInterval(0, 100)];
        }
    }

    private sealed class FakeExistingOutputPolicyResolver(ExistingOutputPolicy policy) : IExistingOutputPolicyResolver
    {
        public List<IReadOnlyList<BatchItem>> Calls { get; } = [];

        public Task<ExistingOutputPolicy> ResolveAsync(IReadOnlyList<BatchItem> existingOutputs, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(existingOutputs.ToArray());
            return Task.FromResult(policy);
        }
    }
}
