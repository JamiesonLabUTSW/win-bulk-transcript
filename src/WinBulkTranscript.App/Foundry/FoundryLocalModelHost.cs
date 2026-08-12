using Microsoft.AI.Foundry.Local;
using Microsoft.AI.Foundry.Local.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;
using WinBulkTranscript.Core.Domain;
using WinBulkTranscript.Core.Ports;
using WinBulkTranscript.Core.Transcription;

namespace WinBulkTranscript.App.Foundry;

/// <summary>Loads one configured exact CPU model candidate and exposes live transcription sessions for a batch.</summary>
public sealed class FoundryLocalModelHost : IModelHost
{
    public const string InitialCandidateModelVariant = FoundryModelContract.InitialCandidateModelVariant;

    private static readonly TimeSpan AbortedSessionCleanupTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HostShutdownCleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly string _modelVariantId;
    // A batch processes segments sequentially, so one gate keeps model load, model use, and
    // unload mutually exclusive without allowing an unload to race a live session.
    private readonly SemaphoreSlim _lifetimeGate = new(1, 1);
    private readonly object _disposeSync = new();
    private IModel? _model;
    private OpenAIAudioClient? _audioClient;
    private Task? _disposeTask;
    private int _disposeStarted;
    private Task? _deferredDisposalTask;
    private Task? _deferredModelUnloadTask;

    public FoundryLocalModelHost(string modelVariantId = InitialCandidateModelVariant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelVariantId);
        _modelVariantId = modelVariantId;
    }

    public async Task LoadAsync(IProgress<ModelLoadProgress>? progress, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _lifetimeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_audioClient is not null)
            {
                return;
            }

            progress?.Report(new ModelLoadProgress(ProcessingStage.LoadingModel, null, "Initializing local speech runtime"));
            if (!FoundryLocalManager.IsInitialized)
            {
                var configuration = new Configuration
                {
                    AppName = "WinBulkTranscript",
                };
                await FoundryLocalManager.CreateAsync(configuration, NullLogger.Instance, cancellationToken).ConfigureAwait(false);
            }

            var manager = FoundryLocalManager.Instance;
            var catalog = await manager.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(new ModelLoadProgress(ProcessingStage.LoadingModel, null, $"Resolving {_modelVariantId}"));

            // Keep the candidate local until every setup step has succeeded. In particular, a
            // cancelled download/load must not leave a half-initialized model that a later retry
            // silently overwrites without unloading.
            IModel? candidate = null;
            var candidateTransferredToHost = false;
            try
            {
                // This intentionally is not GetModelAsync(alias): an alias could choose a different
                // hardware variant or version. The exact candidate becomes a release contract only after Phase 0 evidence.
                candidate = await catalog.GetModelVariantAsync(_modelVariantId, cancellationToken).ConfigureAwait(false)
                    ?? throw new FoundryModelCompatibilityException($"The configured exact model variant '{_modelVariantId}' was not found in the Foundry Local catalog.");
                ValidateExactCpuVariant(candidate, _modelVariantId);

                progress?.Report(new ModelLoadProgress(ProcessingStage.DownloadingModel, 0, $"Downloading {_modelVariantId}"));
                await candidate.DownloadAsync(fraction =>
                {
                    var normalized = Math.Clamp(fraction / 100d, 0, 1);
                    progress?.Report(new ModelLoadProgress(ProcessingStage.DownloadingModel, normalized, $"Downloading speech model ({fraction:F0}%)"));
                }, cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ModelLoadProgress(ProcessingStage.LoadingModel, null, "Loading speech model"));
                await candidate.LoadAsync(cancellationToken).ConfigureAwait(false);
                var audioClient = await candidate.GetAudioClientAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                audioClient.Settings.Language = "en";
                audioClient.Settings.Temperature = 0;

                _model = candidate;
                _audioClient = audioClient;
                candidateTransferredToHost = true;
                progress?.Report(new ModelLoadProgress(ProcessingStage.LoadingModel, 1, "Speech model ready"));
            }
            catch
            {
                if (!candidateTransferredToHost)
                {
                    _audioClient = null;
                    _model = null;
                    if (candidate is not null)
                    {
                        await UnloadModelBestEffortAsync(candidate).ConfigureAwait(false);
                    }
                }

                throw;
            }
        }
        finally
        {
            _lifetimeGate.Release();
        }
    }

    /// <summary>Transcribes one VAD interval by streaming raw source PCM in bounded 100 ms writes.</summary>
    public async Task<string> RecognizeAsync(PcmWaveFile waveFile, SpeechInterval interval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(waveFile);
        ValidatePcmSource(waveFile, interval);
        ThrowIfDisposed();
        await _lifetimeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var lifetimeGateLease = new LifetimeGateLease(_lifetimeGate);

        try
        {
            ThrowIfDisposed();
            var audioClient = _audioClient ?? throw new InvalidOperationException("The speech model has not been loaded.");
            return await RecognizeLoadedAsync(audioClient, waveFile, interval, lifetimeGateLease, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifetimeGateLease.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Task disposalTask;
        lock (_disposeSync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            Volatile.Write(ref _disposeStarted, 1);
            disposalTask = DisposeCoreAsync();
            _disposeTask = disposalTask;
        }

        return new ValueTask(disposalTask);
    }

    /// <summary>
    /// Waits for native cleanup that outlived the bounded <see cref="DisposeAsync"/> shutdown path.
    /// This is intended for opt-in evidence tools after disposal, not for the interactive close path.
    /// </summary>
    public async Task WaitForQuiescenceAsync()
    {
        Task? disposalTask;
        lock (_disposeSync)
        {
            disposalTask = _disposeTask;
        }

        if (disposalTask is null)
        {
            throw new InvalidOperationException("Foundry host quiescence can only be observed after disposal begins.");
        }

        await disposalTask.ConfigureAwait(false);

        Task? deferredDisposal;
        lock (_disposeSync)
        {
            deferredDisposal = _deferredDisposalTask;
        }

        if (deferredDisposal is not null)
        {
            await deferredDisposal.ConfigureAwait(false);
        }

        Task? deferredModelUnload;
        lock (_disposeSync)
        {
            deferredModelUnload = _deferredModelUnloadTask;
        }

        if (deferredModelUnload is not null)
        {
            await deferredModelUnload.ConfigureAwait(false);
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (!await _lifetimeGate.WaitAsync(HostShutdownCleanupTimeout).ConfigureAwait(false))
        {
            // Never unload while an SDK session still owns the model. The eventual cleanup is
            // deliberately detached from window-close so a stuck native operation cannot keep the
            // UI alive indefinitely.
            var deferredDisposal = DisposeAfterActiveOperationAsync();
            ObserveFault(deferredDisposal);
            lock (_disposeSync)
            {
                _deferredDisposalTask = deferredDisposal;
            }
            return;
        }
        try
        {
            await UnloadModelAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifetimeGate.Release();
        }
    }
    private async Task DisposeAfterActiveOperationAsync()
    {
        await _lifetimeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await UnloadModelAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifetimeGate.Release();
        }
    }
    private async Task UnloadModelAsync()
    {
        var model = _model;
        _audioClient = null;
        _model = null;
        if (model is null)
        {
            return;
        }

        var deferredModelUnload = await AwaitModelUnloadAsync(model).ConfigureAwait(false);
        if (deferredModelUnload is not null)
        {
            lock (_disposeSync)
            {
                _deferredModelUnloadTask ??= deferredModelUnload;
            }
        }
    }

    private async Task UnloadModelBestEffortAsync(IModel model)
    {
        try
        {
            var deferredModelUnload = await AwaitModelUnloadAsync(model).ConfigureAwait(false);
            if (deferredModelUnload is not null)
            {
                lock (_disposeSync)
                {
                    _deferredModelUnloadTask ??= deferredModelUnload;
                }
            }
        }
        catch (Exception)
        {
            // Preserve the download/load failure or cancellation that triggered cleanup. The
            // coordinator will not treat a best-effort cleanup failure as a second primary error.
        }
    }

    private static async Task<Task?> AwaitModelUnloadAsync(IModel model)
    {
        var unloadTask = model.UnloadAsync(CancellationToken.None);
        try
        {
            await unloadTask.WaitAsync(HostShutdownCleanupTimeout).ConfigureAwait(false);
            return null;
        }
        catch (TimeoutException)
        {
            ObserveFault(unloadTask);
            return unloadTask;
        }
    }

    private static async Task<string> RecognizeLoadedAsync(
        OpenAIAudioClient audioClient,
        PcmWaveFile waveFile,
        SpeechInterval interval,
        LifetimeGateLease lifetimeGateLease,
        CancellationToken cancellationToken)
    {
        var session = audioClient.CreateLiveTranscriptionSession();
        session.Settings.SampleRate = PcmFormat.Required.SampleRate;
        session.Settings.Channels = PcmFormat.Required.Channels;
        session.Settings.BitsPerSample = PcmFormat.Required.BitsPerSample;
        session.Settings.Language = "en";
        // The SDK queue is bounded. This small value makes backpressure visible and bounds the
        // producer even if a future SDK changes its default capacity.
        session.Settings.PushQueueCapacity = 50;

        var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var started = false;
        var stopped = false;
        Task<string>? readerTask = null;

        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            started = true;
            // Start response consumption before producing audio. AppendAsync is awaited below;
            // no real-time delay is inserted for offline input.
            readerTask = ReadResponseAsync(session, readerCancellation.Token);
            await AppendIntervalAsync(session, waveFile, interval, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
            stopped = true;
            return await readerTask.ConfigureAwait(false);
        }
        finally
        {
            var cleanupTask = CleanupSessionAsync(session, started && !stopped, readerCancellation, readerTask);
            if (!await AwaitCleanupOperationAsync(cleanupTask).ConfigureAwait(false))
            {
                // The operation may have returned to the coordinator, but its native session
                // still owns the model. Keep the gate until every late cleanup task completes.
                lifetimeGateLease.ReleaseWhenCleanupCompletes(cleanupTask);
            }
        }
    }

    private static async Task CleanupSessionAsync(
        LiveAudioTranscriptionSession session,
        bool stopRequired,
        CancellationTokenSource readerCancellation,
        Task? readerTask)
    {
        List<Task>? lateCleanupTasks = null;
        CancellationTokenSource? stopCancellation = null;
        try
        {
            CancelBestEffort(readerCancellation);
            if (stopRequired)
            {
                // StopAsync drains the SDK push queue. Time-bound that drain before attempting
                // DisposeAsync, then retain the gate until any late StopAsync completes.
                try
                {
                    stopCancellation = new CancellationTokenSource(AbortedSessionCleanupTimeout);
                    var stopTask = session.StopAsync(stopCancellation.Token);
                    if (!await AwaitCleanupOperationAsync(stopTask).ConfigureAwait(false))
                    {
                        (lateCleanupTasks ??= []).Add(stopTask);
                    }
                }
                catch (Exception)
                {
                    // Disposal still runs after a failed best-effort stop.
                }
            }
            try
            {
                var disposeTask = session.DisposeAsync().AsTask();
                if (!await AwaitCleanupOperationAsync(disposeTask).ConfigureAwait(false))
                {
                    (lateCleanupTasks ??= []).Add(disposeTask);
                }
            }
            catch (Exception)
            {
                // A disposal failure is observed without replacing the primary operation result.
            }
            if (readerTask is not null && !await AwaitCleanupOperationAsync(readerTask).ConfigureAwait(false))
            {
                (lateCleanupTasks ??= []).Add(readerTask);
            }
            if (lateCleanupTasks is not null)
            {
                await AwaitLateCleanupTasksAsync(lateCleanupTasks).ConfigureAwait(false);
            }
        }
        finally
        {
            stopCancellation?.Dispose();
            readerCancellation.Dispose();
        }
    }

    private static void CancelBestEffort(CancellationTokenSource cancellation)
    {
        if (cancellation.IsCancellationRequested)
        {
            return;
        }
        try
        {
            cancellation.Cancel();
        }
        catch (Exception)
        {
            // A cancellation callback must not prevent cleanup of the native session.
        }
    }
    private static async Task<bool> AwaitCleanupOperationAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(AbortedSessionCleanupTimeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            ObserveFault(operation);
            return false;
        }
        catch (Exception)
        {
            // Cleanup failures are observed here. They must not replace the operation result
            // that caused the session to be torn down.
            return true;
        }
    }
    private static async Task AwaitLateCleanupTasksAsync(List<Task> lateCleanupTasks)
    {
        foreach (var lateCleanupTask in lateCleanupTasks)
        {
            try
            {
                await lateCleanupTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The fault was observed; remaining native cleanup still needs to complete.
            }
        }
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            static completedTask => { _ = completedTask.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
    private sealed class LifetimeGateLease
    {
        private readonly SemaphoreSlim _lifetimeGate;
        private int _releaseMode;
        public LifetimeGateLease(SemaphoreSlim lifetimeGate)
        {
            _lifetimeGate = lifetimeGate;
        }
        public void Release()
        {
            if (Interlocked.CompareExchange(ref _releaseMode, 1, 0) == 0)
            {
                _lifetimeGate.Release();
            }
        }
        public void ReleaseWhenCleanupCompletes(Task cleanupTask)
        {
            if (Interlocked.CompareExchange(ref _releaseMode, 2, 0) != 0)
            {
                return;
            }
            _ = cleanupTask.ContinueWith(
                static (completedTask, state) =>
                {
                    try
                    {
                        if (completedTask.IsFaulted)
                        {
                            _ = completedTask.Exception;
                        }
                    }
                    finally
                    {
                        try
                        {
                            ((LifetimeGateLease)state!)._lifetimeGate.Release();
                        }
                        catch (Exception)
                        {
                            // The lease is one-shot; a continuation fault must not escape.
                        }
                    }
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static void ValidateExactCpuVariant(IModel model, string expectedId)
    {
        if (!string.Equals(model.Id, expectedId, StringComparison.Ordinal))
        {
            throw new FoundryModelCompatibilityException(
                $"Foundry Local resolved '{model.Id}', but this release requires exact model variant '{expectedId}'.");
        }

        if (model.Info.Runtime?.DeviceType != DeviceType.CPU)
        {
            var actual = model.Info.Runtime?.DeviceType.ToString() ?? "missing runtime metadata";
            throw new FoundryModelCompatibilityException(
                $"Model variant '{expectedId}' is not CPU-backed (reported '{actual}'). This application does not select accelerators.");
        }
    }

    private static async Task AppendIntervalAsync(
        LiveAudioTranscriptionSession session,
        PcmWaveFile waveFile,
        SpeechInterval interval,
        CancellationToken cancellationToken)
    {
        const int samplesPerAppend = 1_600; // 100 ms at 16 kHz
        var requestedBytes = checked(interval.LengthSamples * waveFile.Format.BlockAlign);
        var startByte = checked(waveFile.DataOffset + interval.StartSample * waveFile.Format.BlockAlign);
        var remaining = requestedBytes;
        var buffer = new byte[checked(samplesPerAppend * waveFile.Format.BlockAlign)];

        await using var stream = waveFile.OpenRead();
        stream.Seek(startByte, SeekOrigin.Begin);

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = 0;
            while (read < requested)
            {
                var received = await stream.ReadAsync(buffer.AsMemory(read, requested - read), cancellationToken).ConfigureAwait(false);
                if (received == 0)
                {
                    throw new EndOfStreamException("The temporary PCM data chunk ended before the requested speech interval.");
                }

                read += received;
            }

            await session.AppendAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static async Task<string> ReadResponseAsync(LiveAudioTranscriptionSession session, CancellationToken cancellationToken)
    {
        var text = new StreamingTranscriptAccumulator();
        await foreach (var response in session.GetStream(cancellationToken).ConfigureAwait(false))
        {
            foreach (var part in response.Content ?? [])
            {
                if (!string.IsNullOrWhiteSpace(part.Text))
                {
                    text.Append(part.Text);
                }
            }
        }

        return text.Text;
    }

    private static void ValidatePcmSource(PcmWaveFile waveFile, SpeechInterval interval)
    {
        if (!waveFile.Format.IsRequired)
        {
            throw new InvalidOperationException("Foundry transcription requires validated PCM16, 16 kHz, mono audio.");
        }

        if (!interval.IsValid || interval.EndSample > waveFile.SampleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "The requested speech interval is outside the PCM data chunk.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
    }
}

/// <summary>Core-port adapter that delegates interval recognition to the loaded Foundry host.</summary>
public sealed class NemotronSegmentRecognizer : ISpeechRecognizer
{
    private readonly FoundryLocalModelHost _host;

    public NemotronSegmentRecognizer(FoundryLocalModelHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public Task<string> RecognizeAsync(PcmWaveFile waveFile, SpeechInterval interval, CancellationToken cancellationToken)
        => _host.RecognizeAsync(waveFile, interval, cancellationToken);
}

public sealed class FoundryModelCompatibilityException : Exception
{
    public FoundryModelCompatibilityException(string message)
        : base(message)
    {
    }
}
