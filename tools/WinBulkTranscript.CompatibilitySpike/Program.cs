using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.AI.Foundry.Local;
using Microsoft.AI.Foundry.Local.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;
using WinBulkTranscript.App.Foundry;
using WinBulkTranscript.Core.Transcription;

namespace WinBulkTranscript.CompatibilitySpike;

/// <summary>Disposable Phase 0 proof process for one exact Foundry Local CPU variant.</summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ProbeOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(ProbeOptions.Usage);
                return 0;
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            var fixturePath = Path.GetFullPath(options.PcmPath!);
            var pcm = await File.ReadAllBytesAsync(fixturePath, cancellation.Token).ConfigureAwait(false);
            ValidateRawPcm(pcm, fixturePath);
            var fixtureHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pcm));
            Console.WriteLine($"Fixture: {fixturePath} ({pcm.Length / (double)BytesPerSecond:F2}s, SHA-256 {fixtureHash})");

            var report = await RunAsync(options, fixturePath, fixtureHash, pcm, cancellation.Token).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(report, ReportJsonOptions);
            Console.WriteLine(json);

            if (options.ReportPath is not null)
            {
                var reportPath = Path.GetFullPath(options.ReportPath);
                await WriteReportAsync(reportPath, json).ConfigureAwait(false);
                Console.WriteLine($"Evidence report: {reportPath}");
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Compatibility probe cancelled.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Compatibility probe failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task WriteReportAsync(string reportPath, string json)
    {
        if (File.Exists(reportPath) || Directory.Exists(reportPath))
        {
            throw new IOException($"Refusing to overwrite an existing evidence report path: '{reportPath}'.");
        }

        var directory = Path.GetDirectoryName(reportPath)
            ?? throw new InvalidOperationException("The evidence report path must have a containing directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(reportPath)}.{Guid.NewGuid():N}.tmp");
        var ownsTemporaryPath = false;
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                ownsTemporaryPath = true;
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
                await stream.WriteAsync(bytes.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

            File.Move(temporaryPath, reportPath, overwrite: false);
        }
        finally
        {
            if (ownsTemporaryPath)
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A failed evidence cleanup must not replace a completed report or primary probe error.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed evidence cleanup must not replace a completed report or primary probe error.
        }
    }

    private const int SampleRate = 16_000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const int BytesPerSample = BitsPerSample / 8;
    private const int BytesPerSecond = SampleRate * Channels * BytesPerSample;
    private const int BytesPerAppend = 3_200;
    private const int PushQueueCapacity = 2;
    private const int MinimumShortSessionCount = 20;
    private const string IsolatedCacheSentinelFileName = ".win-bulk-transcript-phase0-cache";
    private static readonly TimeSpan AbortCleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InFlightCancellationObservationTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions ReportJsonOptions = new() { WriteIndented = true };

    private static async Task<ProbeReport> RunAsync(
        ProbeOptions options,
        string fixturePath,
        string fixtureHash,
        byte[] pcm,
        CancellationToken cancellationToken)
    {
        var isolatedCache = PrepareIsolatedCache(options);
        if (FoundryLocalManager.IsInitialized && isolatedCache is not null)
        {
            throw new InvalidOperationException("The download-cancellation probe must run in a fresh process so its isolated Foundry configuration can be applied.");
        }

        if (!FoundryLocalManager.IsInitialized)
        {
            var configuration = isolatedCache is null
                ? new Configuration
                {
                    AppName = "WinBulkTranscriptPhase0CompatibilitySpike",
                }
                : new Configuration
                {
                    AppName = "WinBulkTranscriptPhase0CompatibilitySpike",
                    AppDataDir = isolatedCache.AppDataDirectory,
                    ModelCacheDir = isolatedCache.ModelCacheDirectory,
                };

            await FoundryLocalManager.CreateAsync(
                configuration,
                NullLogger.Instance,
                cancellationToken).ConfigureAwait(false);
        }

        var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        var model = await catalog.GetModelVariantAsync(options.ModelVariantId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Exact model variant '{options.ModelVariantId}' was not found.");
        ValidateExactCpuVariant(model, options.ModelVariantId);

        try
        {
            var loadTimer = Stopwatch.StartNew();
            var downloadCancellation = options.VerifyDownloadCancellation
                ? await VerifyDownloadCancellationAndRecoveryAsync(model, options, cancellationToken).ConfigureAwait(false)
                : await DownloadNormallyAsync(model, cancellationToken).ConfigureAwait(false);
            await model.LoadAsync(cancellationToken).ConfigureAwait(false);
            var client = await model.GetAudioClientAsync(cancellationToken).ConfigureAwait(false);
            client.Settings.Language = "en";
            client.Settings.Temperature = 0;
            loadTimer.Stop();

            var baseline = await TranscribeAsync(client, pcm, requireText: true, cancellationToken).ConfigureAwait(false);
            var shortSessions = new List<ShortSessionMeasurement>(options.ShortSessionCount);
            var shortPcm = pcm.AsMemory(0, Math.Min(pcm.Length, BytesPerSecond * 2));
            for (var index = 0; index < options.ShortSessionCount; index++)
            {
                var evidence = await TranscribeAsync(client, shortPcm, requireText: false, cancellationToken).ConfigureAwait(false);
                shortSessions.Add(new ShortSessionMeasurement(
                    index + 1,
                    evidence.StartAsyncMilliseconds,
                    evidence.SessionElapsedMilliseconds,
                    evidence.AppendWaitObserved,
                    !string.IsNullOrWhiteSpace(evidence.Transcript)));
            }

            var cancellationEvidence = await VerifyCancellationAsync(client, pcm, cancellationToken).ConfigureAwait(false);
            var recovery = await TranscribeAsync(client, pcm, requireText: true, cancellationToken).ConfigureAwait(false);

            return new ProbeReport(
                DateTimeOffset.UtcNow,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                fixturePath,
                fixtureHash,
                options.ModelVariantId,
                model.Id,
                model.Info.Runtime?.DeviceType.ToString() ?? "missing",
                loadTimer.Elapsed.TotalMilliseconds,
                PushQueueCapacity,
                baseline,
                shortSessions,
                new CacheIsolationEvidence(
                    isolatedCache is not null,
                    isolatedCache?.RootDirectory,
                    isolatedCache?.AppDataDirectory,
                    isolatedCache?.ModelCacheDirectory,
                    isolatedCache?.ResetRequested ?? false,
                    isolatedCache?.ModelCacheWasEmptyBeforeDownload ?? false),
                downloadCancellation,
                cancellationEvidence,
                recovery);
        }
        finally
        {
            await model.UnloadAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<DownloadCancellationEvidence> DownloadNormallyAsync(IModel model, CancellationToken cancellationToken)
    {
        Console.WriteLine("Downloading the model if it is not already cached.");
        await model.DownloadAsync(
            percent => Console.WriteLine($"Download: {percent:F0}%"),
            cancellationToken).ConfigureAwait(false);
        return new DownloadCancellationEvidence(false, null, false, false);
    }

    private static async Task<DownloadCancellationEvidence> VerifyDownloadCancellationAndRecoveryAsync(
        IModel model,
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cancellationRequested = false;
        var cancellationObserved = false;

        Console.WriteLine($"Starting an isolated cache-miss download and cancelling at {options.CancelDownloadAtPercent}%.");
        try
        {
            await model.DownloadAsync(percent =>
            {
                Console.WriteLine($"Download before cancellation: {percent:F0}%");
                if (!cancellationRequested && percent >= options.CancelDownloadAtPercent)
                {
                    cancellationRequested = true;
                    Console.WriteLine("Requesting download cancellation.");
                    downloadCancellation.Cancel();
                }
            }, downloadCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            cancellationObserved = true;
            Console.WriteLine("Download cancellation was observed. Retrying the same exact variant from the isolated cache.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!cancellationRequested)
        {
            throw new InvalidOperationException(
                $"The isolated download did not report progress reaching {options.CancelDownloadAtPercent}%. " +
                "It may have completed before cancellation could be requested; do not record this run as download-cancellation evidence.");
        }

        if (!cancellationObserved)
        {
            throw new InvalidOperationException("The download returned successfully after cancellation was requested; cancellation evidence was not observed.");
        }

        await model.DownloadAsync(
            percent => Console.WriteLine($"Recovery download: {percent:F0}%"),
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine("Recovery download completed.");
        return new DownloadCancellationEvidence(true, options.CancelDownloadAtPercent, true, true);
    }

    private static IsolatedCacheConfiguration? PrepareIsolatedCache(ProbeOptions options)
    {
        if (!options.VerifyDownloadCancellation)
        {
            return null;
        }

        var root = Path.GetFullPath(options.IsolatedCacheRoot!);
        ValidateIsolatedCacheRoot(root);
        if (Directory.Exists(root)
            && File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("The isolated cache root cannot be a reparse point.");
        }

        var sentinelPath = Path.Combine(root, IsolatedCacheSentinelFileName);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            if (!options.ResetIsolatedCache)
            {
                throw new InvalidOperationException(
                    $"The isolated cache root '{root}' is not empty. Use a new directory or explicitly pass --reset-isolated-cache.");
            }

            if (!File.Exists(sentinelPath))
            {
                throw new InvalidOperationException(
                    $"Refusing to reset '{root}' because it lacks the {IsolatedCacheSentinelFileName} sentinel created by this probe.");
            }

            DeleteOwnedIsolatedCache(root);
        }

        Directory.CreateDirectory(root);
        File.WriteAllText(sentinelPath, "WinBulkTranscript Phase 0 isolated cache.\n");
        var appDataDirectory = Path.Combine(root, "app-data");
        var modelCacheDirectory = Path.Combine(root, "model-cache");
        Directory.CreateDirectory(appDataDirectory);
        Directory.CreateDirectory(modelCacheDirectory);
        var modelCacheWasEmptyBeforeDownload = !Directory.EnumerateFileSystemEntries(modelCacheDirectory).Any();
        if (!modelCacheWasEmptyBeforeDownload)
        {
            throw new InvalidOperationException($"The isolated model cache '{modelCacheDirectory}' must be empty before the cancellation probe begins.");
        }

        return new IsolatedCacheConfiguration(
            root,
            appDataDirectory,
            modelCacheDirectory,
            options.ResetIsolatedCache,
            modelCacheWasEmptyBeforeDownload);
    }

    private static void ValidateIsolatedCacheRoot(string root)
    {
        var volumeRoot = Path.GetPathRoot(root);
        if (string.IsNullOrWhiteSpace(volumeRoot)
            || string.Equals(TrimPath(root), TrimPath(volumeRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The isolated cache root must be a dedicated subdirectory, not a drive root.", nameof(root));
        }

        var workingDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());
        if (IsSameOrDescendant(workingDirectory, root))
        {
            throw new ArgumentException("The isolated cache root must not contain the current working directory.", nameof(root));
        }
    }

    private static bool IsSameOrDescendant(string candidate, string ancestor)
    {
        var normalizedCandidate = TrimPath(candidate);
        var normalizedAncestor = TrimPath(ancestor);
        return string.Equals(normalizedCandidate, normalizedAncestor, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedAncestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedAncestor + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteOwnedIsolatedCache(string root)
    {
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("The isolated cache root became a reparse point before reset.");
        }

        DeleteOwnedDirectoryContents(root, root);

        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("The isolated cache root became a reparse point during reset.");
        }

        Directory.Delete(root, recursive: false);
    }

    private static void DeleteOwnedDirectoryContents(string directory, string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var fullEntry = Path.GetFullPath(entry);
            if (string.Equals(fullEntry, root, StringComparison.OrdinalIgnoreCase)
                || !IsSameOrDescendant(fullEntry, root))
            {
                throw new InvalidOperationException("The isolated cache reset encountered an entry outside its root.");
            }

            var attributes = File.GetAttributes(fullEntry);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // Delete the link itself; never recurse through a junction or symlink during reset.
                if (isDirectory)
                {
                    Directory.Delete(fullEntry, recursive: false);
                }
                else
                {
                    File.Delete(fullEntry);
                }

                continue;
            }

            if (isDirectory)
            {
                DeleteOwnedDirectoryContents(fullEntry, root);
                Directory.Delete(fullEntry, recursive: false);
            }
            else
            {
                File.Delete(fullEntry);
            }
        }
    }

    private static string TrimPath(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static async Task<TranscriptionEvidence> TranscribeAsync(
        OpenAIAudioClient client,
        ReadOnlyMemory<byte> pcm,
        bool requireText,
        CancellationToken cancellationToken)
    {
        var session = client.CreateLiveTranscriptionSession();
        Configure(session);
        using var appendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<IReadOnlyList<ResponseChunk>>? readerTask = null;
        Task<AppendEvidence>? appendTask = null;
        var started = false;
        var stopped = false;
        var sessionTimer = Stopwatch.StartNew();
        var startTimer = Stopwatch.StartNew();

        try
        {
            await session.StartAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            startTimer.Stop();
            started = true;
            // Start consuming before the bounded append queue can fill. No pacing delay is used.
            var activeReader = ReadResponsesAsync(session, readerCancellation.Token);
            readerTask = activeReader;
            var activeAppend = AppendAsync(session, pcm, appendCancellation.Token);
            appendTask = activeAppend;
            var append = await activeAppend.WaitAsync(cancellationToken).ConfigureAwait(false);
            await session.StopAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            stopped = true;
            var chunks = await activeReader
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var transcript = AssembleInEmissionOrder(chunks);
            if (requireText && string.IsNullOrWhiteSpace(transcript))
            {
                throw new InvalidOperationException("The known PCM fixture produced an empty transcript.");
            }

            sessionTimer.Stop();
            return new TranscriptionEvidence(
                transcript,
                chunks,
                append.Count,
                append.WaitObserved,
                append.MaximumMilliseconds,
                append.TotalMilliseconds,
                startTimer.Elapsed.TotalMilliseconds,
                sessionTimer.Elapsed.TotalMilliseconds);
        }
        finally
        {
            await CleanupSessionBestEffortAsync(
                session,
                started && !stopped,
                appendCancellation,
                appendTask,
                readerCancellation,
                readerTask,
                "transcription").ConfigureAwait(false);
        }
    }

    private static async Task<CancellationEvidence> VerifyCancellationAsync(
        OpenAIAudioClient client,
        ReadOnlyMemory<byte> pcm,
        CancellationToken cancellationToken)
    {
        if (pcm.Length < BytesPerSecond * 5)
        {
            throw new InvalidDataException("The PCM fixture must be at least five seconds for in-flight cancellation testing.");
        }

        var prompt = await VerifyPromptCancellationAsync(client).ConfigureAwait(false);
        var append = await VerifyAppendCancellationAsync(client, pcm, cancellationToken).ConfigureAwait(false);
        var response = await VerifyResponseCancellationAsync(client, cancellationToken).ConfigureAwait(false);
        var evidence = new CancellationEvidence(prompt, append, response);
        if (!evidence.AllCancellationObserved)
        {
            throw new InvalidOperationException("StartAsync, AppendAsync, or response-stream cancellation was not observed.");
        }

        if (!evidence.CleanupCompletedWithoutTimeoutOrFault)
        {
            throw new InvalidOperationException("Cancellation cleanup timed out or faulted; do not record this run as Phase 0 evidence.");
        }

        return evidence;
    }

    private static async Task<PromptCancellationEvidence> VerifyPromptCancellationAsync(OpenAIAudioClient client)
    {
        var session = client.CreateLiveTranscriptionSession();
        Configure(session);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancellationObserved = false;
        SessionCleanupEvidence? cleanup = null;
        try
        {
            await session.StartAsync(cancellation.Token).WaitAsync(AbortCleanupTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            cancellationObserved = true;
        }
        finally
        {
            cleanup = await CleanupSessionBestEffortAsync(
                session,
                stopRequired: false,
                appendCancellation: null,
                appendTask: null,
                readerCancellation: null,
                readerTask: null,
                activity: "prompt cancellation").ConfigureAwait(false);
        }

        if (!cancellationObserved)
        {
            throw new InvalidOperationException("StartAsync did not observe an already-cancelled token.");
        }

        return new PromptCancellationEvidence(
            cancellationObserved,
            cleanup ?? throw new InvalidOperationException("Prompt-cancellation cleanup did not produce evidence."));
    }

    private static async Task<AppendCancellationEvidence> VerifyAppendCancellationAsync(
        OpenAIAudioClient client,
        ReadOnlyMemory<byte> pcm,
        CancellationToken cancellationToken)
    {
        var session = client.CreateLiveTranscriptionSession();
        Configure(session);
        using var appendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<IReadOnlyList<ResponseChunk>>? readerTask = null;
        Task<AppendEvidence>? appendTask = null;
        var started = false;
        var cancellationObserved = false;
        var appendWaitObserved = false;
        SessionCleanupEvidence? cleanup = null;

        try
        {
            await session.StartAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            started = true;
            var activeReader = ReadResponsesAsync(session, readerCancellation.Token);
            readerTask = activeReader;
            var appendInFlight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var activeAppend = AppendAsync(session, pcm, appendCancellation.Token, appendInFlight);
            appendTask = activeAppend;
            await WaitForInFlightOperationAsync(
                appendInFlight.Task,
                activeAppend,
                "AppendAsync",
                cancellationToken).ConfigureAwait(false);

            appendWaitObserved = true;
            appendCancellation.Cancel();
            try
            {
                await activeAppend.WaitAsync(AbortCleanupTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (appendCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                cancellationObserved = true;
            }

            if (!cancellationObserved)
            {
                throw new InvalidOperationException("AppendAsync completed without observing the requested in-flight cancellation.");
            }
        }
        catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"AppendAsync did not complete within {AbortCleanupTimeout.TotalSeconds:F0} seconds after cancellation.");
        }
        finally
        {
            cleanup = await CleanupSessionBestEffortAsync(
                session,
                started,
                appendCancellation,
                appendTask,
                readerCancellation,
                readerTask,
                "append cancellation").ConfigureAwait(false);
        }

        return new AppendCancellationEvidence(
            cancellationObserved,
            appendWaitObserved,
            cleanup ?? throw new InvalidOperationException("Append-cancellation cleanup did not produce evidence."));
    }

    private static async Task<ResponseCancellationEvidence> VerifyResponseCancellationAsync(
        OpenAIAudioClient client,
        CancellationToken cancellationToken)
    {
        var session = client.CreateLiveTranscriptionSession();
        Configure(session);
        using var responseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<IReadOnlyList<ResponseChunk>>? readerTask = null;
        var started = false;
        var cancellationObserved = false;
        SessionCleanupEvidence? cleanup = null;

        try
        {
            await session.StartAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            started = true;
            var responseMoveNextInFlight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var activeReader = ReadResponsesAsync(session, responseCancellation.Token, responseMoveNextInFlight);
            readerTask = activeReader;
            await WaitForInFlightOperationAsync(
                responseMoveNextInFlight.Task,
                activeReader,
                "GetStream().MoveNextAsync",
                cancellationToken).ConfigureAwait(false);

            responseCancellation.Cancel();
            try
            {
                await activeReader.WaitAsync(AbortCleanupTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (responseCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                cancellationObserved = true;
            }

            if (!cancellationObserved)
            {
                throw new InvalidOperationException("GetStream().MoveNextAsync completed without observing the requested in-flight cancellation.");
            }
        }
        catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"GetStream().MoveNextAsync did not complete within {AbortCleanupTimeout.TotalSeconds:F0} seconds after cancellation.");
        }
        finally
        {
            cleanup = await CleanupSessionBestEffortAsync(
                session,
                started,
                appendCancellation: null,
                appendTask: null,
                responseCancellation,
                readerTask,
                "response cancellation").ConfigureAwait(false);
        }

        return new ResponseCancellationEvidence(
            cancellationObserved,
            cleanup ?? throw new InvalidOperationException("Response-cancellation cleanup did not produce evidence."));
    }

    private static async Task WaitForInFlightOperationAsync(
        Task operationBegan,
        Task operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            await operationBegan.WaitAsync(InFlightCancellationObservationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) when (operation.IsCompleted)
        {
            if (operation.IsCanceled || operation.IsFaulted)
            {
                await operation.ConfigureAwait(false);
            }

            throw new InvalidOperationException($"{operationName} completed without exposing an in-flight operation for cancellation evidence.");
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"{operationName} did not expose an in-flight operation within {InFlightCancellationObservationTimeout.TotalSeconds:F0} seconds.");
        }
    }

    private static async Task<AppendEvidence> AppendAsync(
        LiveAudioTranscriptionSession session,
        ReadOnlyMemory<byte> pcm,
        CancellationToken cancellationToken,
        TaskCompletionSource<bool>? inFlightAppendStarted = null)
    {
        var count = 0;
        var waitObserved = false;
        var maxMilliseconds = 0d;
        var totalMilliseconds = 0d;
        for (var offset = 0; offset < pcm.Length;)
        {
            var length = Math.Min(BytesPerAppend, pcm.Length - offset);
            var timer = Stopwatch.StartNew();
            var append = session.AppendAsync(pcm.Slice(offset, length), cancellationToken);
            if (!append.IsCompleted)
            {
                // Signal only after AppendAsync has begun and its awaited write is actually pending.
                inFlightAppendStarted?.TrySetResult(true);
                waitObserved = true;
            }

            await append.ConfigureAwait(false);
            timer.Stop();
            maxMilliseconds = Math.Max(maxMilliseconds, timer.Elapsed.TotalMilliseconds);
            totalMilliseconds += timer.Elapsed.TotalMilliseconds;
            count++;
            offset += length;
        }

        return new AppendEvidence(count, waitObserved, maxMilliseconds, totalMilliseconds);
    }

    private static async Task<IReadOnlyList<ResponseChunk>> ReadResponsesAsync(
        LiveAudioTranscriptionSession session,
        CancellationToken cancellationToken,
        TaskCompletionSource<bool>? inFlightMoveNextStarted = null)
    {
        var chunks = new List<ResponseChunk>();
        await using var enumerator = session.GetStream(cancellationToken).GetAsyncEnumerator();
        while (true)
        {
            var moveNext = enumerator.MoveNextAsync();
            if (!moveNext.IsCompleted)
            {
                // Signal only after MoveNextAsync has begun and is awaiting a response.
                inFlightMoveNextStarted?.TrySetResult(true);
            }

            if (!await moveNext.ConfigureAwait(false))
            {
                break;
            }

            var response = enumerator.Current;
            foreach (var part in response.Content ?? [])
            {
                if (!string.IsNullOrWhiteSpace(part.Text))
                {
                    chunks.Add(new ResponseChunk(chunks.Count, part.Text));
                }
            }
        }

        return chunks;
    }

    private static async Task<SessionCleanupEvidence> CleanupSessionBestEffortAsync(
        LiveAudioTranscriptionSession session,
        bool stopRequired,
        CancellationTokenSource? appendCancellation,
        Task? appendTask,
        CancellationTokenSource? readerCancellation,
        Task? readerTask,
        string activity)
    {
        // Cleanup cannot replace the caller's cancellation or model exception. Each wait is bounded
        // because StopAsync drains the push queue and a native reader may not promptly honor a token.
        CancelBestEffort(appendCancellation, activity + " append");
        CancelBestEffort(readerCancellation, activity + " reader");
        var append = await AwaitTaskBestEffortAsync(appendTask, activity + " append").ConfigureAwait(false);
        var stop = CleanupOperationEvidence.NotRequired;

        if (stopRequired)
        {
            stop = await StopSessionBestEffortAsync(session, activity).ConfigureAwait(false);
        }

        var reader = await AwaitTaskBestEffortAsync(readerTask, activity + " reader").ConfigureAwait(false);
        var dispose = await DisposeSessionBestEffortAsync(session, activity).ConfigureAwait(false);
        return new SessionCleanupEvidence(append, stop, reader, dispose);
    }

    private static async Task<CleanupOperationEvidence> DisposeSessionBestEffortAsync(LiveAudioTranscriptionSession session, string activity)
    {
        try
        {
            return await AwaitTaskBestEffortAsync(session.DisposeAsync().AsTask(), activity + " DisposeAsync").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Best-effort cleanup for {activity} DisposeAsync failed: {exception.Message}");
            return CleanupOperationEvidence.FailedToStart;
        }
    }

    private static async Task<CleanupOperationEvidence> StopSessionBestEffortAsync(LiveAudioTranscriptionSession session, string activity)
    {
        using var stopCancellation = new CancellationTokenSource(AbortCleanupTimeout);
        try
        {
            var evidence = await AwaitTaskBestEffortAsync(
                session.StopAsync(stopCancellation.Token),
                activity + " StopAsync").ConfigureAwait(false);
            return stopCancellation.IsCancellationRequested && evidence.Outcome == "Cancelled"
                ? CleanupOperationEvidence.TimedOut
                : evidence;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Best-effort cleanup for {activity} StopAsync failed: {exception.Message}");
            return CleanupOperationEvidence.FailedToStart;
        }
    }

    private static void CancelBestEffort(CancellationTokenSource? cancellation, string activity)
    {
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Best-effort cancellation for {activity} failed: {exception.Message}");
        }
    }

    private static async Task<CleanupOperationEvidence> AwaitTaskBestEffortAsync(Task? task, string activity)
    {
        if (task is null)
        {
            return CleanupOperationEvidence.NotRequired;
        }

        try
        {
            await task.WaitAsync(AbortCleanupTimeout).ConfigureAwait(false);
            return CleanupOperationEvidence.Completed;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"Best-effort cleanup for {activity} exceeded {AbortCleanupTimeout.TotalSeconds:F0} seconds.");
            ObserveLateFault(task);
            return CleanupOperationEvidence.TimedOut;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an expected cleanup result after the linked tokens were cancelled.
            return CleanupOperationEvidence.Cancelled;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Best-effort cleanup for {activity} failed: {exception.Message}");
            return CleanupOperationEvidence.Faulted;
        }
    }

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static string AssembleInEmissionOrder(IReadOnlyList<ResponseChunk> chunks)
    {
        var transcript = new StreamingTranscriptAccumulator();
        for (var index = 0; index < chunks.Count; index++)
        {
            if (chunks[index].Sequence != index)
            {
                throw new InvalidOperationException("Response chunks were not accumulated in emission order.");
            }

            transcript.Append(chunks[index].Text);
        }

        return transcript.Text;
    }

    private static void Configure(LiveAudioTranscriptionSession session)
    {
        session.Settings.SampleRate = SampleRate;
        session.Settings.Channels = Channels;
        session.Settings.BitsPerSample = BitsPerSample;
        session.Settings.Language = "en";
        session.Settings.PushQueueCapacity = PushQueueCapacity;
    }

    private static void ValidateExactCpuVariant(IModel model, string expectedId)
    {
        if (!string.Equals(model.Id, expectedId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected exact model '{expectedId}', but Foundry resolved '{model.Id}'.");
        }

        if (model.Info.Runtime?.DeviceType != DeviceType.CPU)
        {
            throw new InvalidOperationException($"Model '{expectedId}' is not CPU-backed.");
        }
    }

    private static void ValidateRawPcm(ReadOnlySpan<byte> pcm, string path)
    {
        if (pcm.Length < BytesPerSecond * 5 || pcm.Length % BytesPerSample != 0)
        {
            throw new InvalidDataException($"'{path}' must be at least five seconds of raw signed PCM16 at 16 kHz mono.");
        }
    }

    private sealed record ProbeOptions(
        string? PcmPath,
        string ModelVariantId,
        int ShortSessionCount,
        string? ReportPath,
        bool VerifyDownloadCancellation,
        string? IsolatedCacheRoot,
        bool ResetIsolatedCache,
        int CancelDownloadAtPercent,
        bool ShowHelp)
    {
        public const string Usage = """
            WinBulkTranscript.CompatibilitySpike

            --pcm <path>                  Known raw PCM16, 16 kHz, mono speech fixture (required).
            --model <exact-variant-id>    Exact CPU variant; default is the Phase 0 candidate.
            --short-sessions <count>      Repeat short-session startup measurements; minimum/default 20.
            --report <path>               Optional JSON evidence report path.
            --verify-download-cancellation Cancel an isolated cache-miss download, then recover it.
            --isolated-cache-root <path>  Dedicated cache root required by --verify-download-cancellation.
            --reset-isolated-cache         Explicitly delete a nonempty isolated cache root before the probe.
            --cancel-download-at-percent <1-99>
                                       Progress percentage that triggers cancellation; default 5.
            --help                        Show this help.
            """;

        public static ProbeOptions Parse(string[] args)
        {
            string? pcmPath = null;
            var model = FoundryModelContract.InitialCandidateModelVariant;
            var shortSessions = MinimumShortSessionCount;
            string? reportPath = null;
            var verifyDownloadCancellation = false;
            string? isolatedCacheRoot = null;
            var resetIsolatedCache = false;
            var cancelDownloadAtPercent = 5;
            var showHelp = false;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--pcm": pcmPath = Next(args, ref index, "--pcm"); break;
                    case "--model": model = Next(args, ref index, "--model"); break;
                    case "--short-sessions":
                        if (!int.TryParse(Next(args, ref index, "--short-sessions"), out shortSessions) || shortSessions < MinimumShortSessionCount)
                        {
                            throw new ArgumentException($"--short-sessions must be an integer of at least {MinimumShortSessionCount} for Phase 0 evidence.");
                        }

                        break;
                    case "--report": reportPath = Next(args, ref index, "--report"); break;
                    case "--verify-download-cancellation": verifyDownloadCancellation = true; break;
                    case "--isolated-cache-root": isolatedCacheRoot = Next(args, ref index, "--isolated-cache-root"); break;
                    case "--reset-isolated-cache": resetIsolatedCache = true; break;
                    case "--cancel-download-at-percent":
                        verifyDownloadCancellation = true;
                        if (!int.TryParse(Next(args, ref index, "--cancel-download-at-percent"), out cancelDownloadAtPercent)
                            || cancelDownloadAtPercent is < 1 or >= 100)
                        {
                            throw new ArgumentException("--cancel-download-at-percent must be an integer from 1 through 99.");
                        }

                        break;
                    case "--help": case "-h": showHelp = true; break;
                    default: throw new ArgumentException($"Unknown option '{args[index]}'.");
                }
            }

            if (!showHelp)
            {
                if (string.IsNullOrWhiteSpace(pcmPath))
                {
                    throw new ArgumentException("--pcm is required.");
                }

                if (verifyDownloadCancellation && string.IsNullOrWhiteSpace(isolatedCacheRoot))
                {
                    throw new ArgumentException("--isolated-cache-root is required with --verify-download-cancellation.");
                }

                if (!verifyDownloadCancellation && (!string.IsNullOrWhiteSpace(isolatedCacheRoot) || resetIsolatedCache))
                {
                    throw new ArgumentException("--isolated-cache-root and --reset-isolated-cache are only valid with --verify-download-cancellation.");
                }
            }

            return new ProbeOptions(
                pcmPath,
                model,
                shortSessions,
                reportPath,
                verifyDownloadCancellation,
                isolatedCacheRoot,
                resetIsolatedCache,
                cancelDownloadAtPercent,
                showHelp);
        }

        private static string Next(string[] args, ref int index, string option)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException($"{option} requires a value.");
            }

            return args[index];
        }
    }

    private sealed record ProbeReport(
        DateTimeOffset StartedUtc,
        string OperatingSystem,
        string ProcessArchitecture,
        string FixturePath,
        string FixtureSha256,
        string RequestedModelVariant,
        string ResolvedModelVariant,
        string RuntimeDeviceType,
        double DownloadAndLoadMilliseconds,
        int PushQueueCapacity,
        TranscriptionEvidence Baseline,
        IReadOnlyList<ShortSessionMeasurement> ShortSessions,
        CacheIsolationEvidence CacheIsolation,
        DownloadCancellationEvidence DownloadCancellation,
        CancellationEvidence Cancellation,
        TranscriptionEvidence Recovery);

    private sealed record TranscriptionEvidence(
        string Transcript,
        IReadOnlyList<ResponseChunk> ResponseChunks,
        int AppendCount,
        bool AppendWaitObserved,
        double MaximumAppendMilliseconds,
        double TotalAppendMilliseconds,
        double StartAsyncMilliseconds,
        double SessionElapsedMilliseconds);

    private sealed record ResponseChunk(int Sequence, string Text);

    private sealed record ShortSessionMeasurement(
        int Sequence,
        double StartAsyncMilliseconds,
        double SessionElapsedMilliseconds,
        bool AppendWaitObserved,
        bool ProducedText);

    private sealed record CacheIsolationEvidence(
        bool Enabled,
        string? RootDirectory,
        string? AppDataDirectory,
        string? ModelCacheDirectory,
        bool ResetRequested,
        bool ModelCacheWasEmptyBeforeDownload);

    private sealed record DownloadCancellationEvidence(
        bool Requested,
        int? CancelAtPercent,
        bool CancellationObserved,
        bool RecoveryDownloadCompleted);

    private sealed record IsolatedCacheConfiguration(
        string RootDirectory,
        string AppDataDirectory,
        string ModelCacheDirectory,
        bool ResetRequested,
        bool ModelCacheWasEmptyBeforeDownload);

    private sealed record CancellationEvidence(
        PromptCancellationEvidence Prompt,
        AppendCancellationEvidence Append,
        ResponseCancellationEvidence Response)
    {
        public bool AllCancellationObserved =>
            Prompt.CancellationObserved
            && Append.CancellationObserved
            && Response.CancellationObserved;

        public bool CleanupCompletedWithoutTimeoutOrFault =>
            Prompt.Cleanup.CompletedWithoutTimeoutOrFault
            && Append.Cleanup.CompletedWithoutTimeoutOrFault
            && Response.Cleanup.CompletedWithoutTimeoutOrFault;
    }

    private sealed record PromptCancellationEvidence(
        bool CancellationObserved,
        SessionCleanupEvidence Cleanup);

    private sealed record AppendCancellationEvidence(
        bool CancellationObserved,
        bool AppendWaitObserved,
        SessionCleanupEvidence Cleanup);

    private sealed record ResponseCancellationEvidence(
        bool CancellationObserved,
        SessionCleanupEvidence Cleanup);

    private sealed record SessionCleanupEvidence(
        CleanupOperationEvidence Append,
        CleanupOperationEvidence Stop,
        CleanupOperationEvidence Reader,
        CleanupOperationEvidence Dispose)
    {
        public bool CompletedWithoutTimeoutOrFault =>
            Append.CompletedWithoutTimeoutOrFault
            && Stop.CompletedWithoutTimeoutOrFault
            && Reader.CompletedWithoutTimeoutOrFault
            && Dispose.CompletedWithoutTimeoutOrFault;
    }

    private sealed record CleanupOperationEvidence(string Outcome, bool CompletedWithoutTimeoutOrFault)
    {
        public static CleanupOperationEvidence NotRequired { get; } = new("Not required", true);
        public static CleanupOperationEvidence Completed { get; } = new("Completed", true);
        public static CleanupOperationEvidence Cancelled { get; } = new("Cancelled", true);
        public static CleanupOperationEvidence TimedOut { get; } = new("Timed out", false);
        public static CleanupOperationEvidence Faulted { get; } = new("Faulted", false);
        public static CleanupOperationEvidence FailedToStart { get; } = new("Failed to start", false);
    }

    private sealed record AppendEvidence(int Count, bool WaitObserved, double MaximumMilliseconds, double TotalMilliseconds);
}
