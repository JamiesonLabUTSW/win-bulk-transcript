using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinBulkTranscript.App.Foundry;
using WinBulkTranscript.App.Media;
using WinBulkTranscript.Core.Audio;
using WinBulkTranscript.Core.Batch;
using WinBulkTranscript.Core.Domain;
using WinBulkTranscript.Core.Output;
using WinBulkTranscript.Core.Ports;

namespace WinBulkTranscript.WorkflowIntegrationProbe;

/// <summary>
/// Runs opt-in end-to-end evidence through the production media, VAD, Foundry, coordinator, and VTT paths.
/// </summary>
internal static class Program
{
    private const string TemporaryDirectoryName = "WinBulkTranscript";
    private const string PreseededVttContent = "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nPre-existing output sentinel\n";
    private static readonly TimeSpan DeferredMediaCleanupSettlementTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeferredMediaCleanupPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DeferredFoundryCleanupSettlementTimeout = TimeSpan.FromSeconds(30);
    private static readonly byte[] PreseededVttBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        .GetBytes(PreseededVttContent);
    private static readonly byte[] HeaderOnlyVttBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        .GetBytes("WEBVTT\n\n");
    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Runs the selected workflow evidence scenario.</summary>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = WorkflowProbeOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(WorkflowProbeOptions.Usage);
                return 0;
            }

            return await RunAsync(options).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Workflow integration probe cancelled before it could write evidence.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Workflow integration probe failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(WorkflowProbeOptions options)
    {
        var request = BatchRequest.Create(options.InputRoot!, options.OutputRoot!);
        var discovery = Mp4Discovery.Discover(request.InputRoot, CancellationToken.None);
        if (discovery.Issues.Count > 0)
        {
            throw new BatchPreflightException(string.Join(
                Environment.NewLine,
                discovery.Issues.Select(static issue => $"{issue.Path}: {issue.Reason}")));
        }

        if (discovery.Files.Count == 0)
        {
            throw new BatchPreflightException("The supplied input root contains no MP4 files.");
        }

        var preflight = BatchPreflight.Create(request, discovery.Files);
        var reportPath = ValidateReportPath(options.ReportPath, request, preflight);
        var preseededOutputs = options.PreseedExistingOutputs
            ? PreseedOutputs(preflight)
            : new Dictionary<string, PreseededOutput>(StringComparer.OrdinalIgnoreCase);
        var readOnlyPreseededOutputs = options.ReadOnlyPreseededOutputs
            ? MarkPreseededOutputsReadOnly(preseededOutputs)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        OutputDirectoryWriteRestriction? outputDirectoryWriteRestriction = null;
        DiskFullSimulationTranscriptWriter? diskFullSimulation = null;

        var temporaryMediaArtifactsBefore = TemporaryFileSnapshot.CaptureTemporaryMediaArtifacts();
        var temporaryVttsBefore = TemporaryFileSnapshot.CaptureTemporaryVtts(request.OutputRoot);
        var snapshots = new SnapshotCollector();
        var stopwatch = Stopwatch.StartNew();
        var runCancelled = false;
        var ctrlCRequested = false;
        var automaticCancellationRequested = false;
        ProcessingStage? cancellationTriggerStage = null;
        Exception? runException = null;
        IRecognizerInjection? recognizerInjection = null;

        FoundryLocalModelHost? modelHost = null;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            ctrlCRequested = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            if (options.DenyOutputDirectoryWrites)
            {
                outputDirectoryWriteRestriction = OutputDirectoryWriteRestriction.Apply(request.OutputRoot);
            }

            modelHost = new FoundryLocalModelHost(options.ModelVariant);
            ISpeechRecognizer recognizer;
            if (options.InjectRecognizerFailure)
            {
                var failingRecognizer = new FailingRecognizer();
                recognizerInjection = failingRecognizer;
                recognizer = failingRecognizer;
            }
            else if (options.InjectEmptyRecognizerResponse)
            {
                var emptyRecognizer = new EmptyRecognizer();
                recognizerInjection = emptyRecognizer;
                recognizer = emptyRecognizer;
            }
            else
            {
                var productionRecognizer = new NemotronSegmentRecognizer(modelHost);
                if (options.InjectRecognizerFailureOnCall is { } failureOnCall)
                {
                    var mixedRecognizer = new FailOnCallRecognizer(productionRecognizer, failureOnCall);
                    recognizerInjection = mixedRecognizer;
                    recognizer = mixedRecognizer;
                }
                else
                {
                    recognizer = productionRecognizer;
                }
            }
            ITranscriptWriter transcriptWriter;
            if (options.SimulateOutputDiskFull)
            {
                diskFullSimulation = new DiskFullSimulationTranscriptWriter();
                transcriptWriter = diskFullSimulation;
            }
            else
            {
                transcriptWriter = new WebVttWriter();
            }

            var coordinator = new BatchTranscriptionCoordinator(
                new WindowsMediaAudioExtractor(),
                new AdaptiveEnergyVoiceActivityDetector(),
                recognizer,
                transcriptWriter,
                modelHost,
                new FixedExistingOutputPolicyResolver(options.CollisionPolicy));

            try
            {
                var runTask = coordinator.RunAsync(request, snapshots, cancellation.Token);
                if (options.CancelAfterMilliseconds is not null)
                {
                    var cancellationTargetTask = snapshots.WaitForCancellationTargetAsync(options.CancellationTarget);
                    var firstCompletedTask = await Task.WhenAny(runTask, cancellationTargetTask).ConfigureAwait(false);
                    if (firstCompletedTask == cancellationTargetTask)
                    {
                        cancellationTriggerStage = await cancellationTargetTask.ConfigureAwait(false);
                        try
                        {
                            await Task.Delay(options.CancelAfterMilliseconds.Value, cancellation.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                        {
                            // Ctrl+C requested cancellation while the probe was waiting to arm its timed cancellation.
                        }

                        if (!runTask.IsCompleted && !cancellation.IsCancellationRequested)
                        {
                            automaticCancellationRequested = true;
                            cancellation.Cancel();
                        }
                    }
                }

                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                runCancelled = true;
            }
            catch (Exception exception)
            {
                runException = exception;
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            stopwatch.Stop();
            outputDirectoryWriteRestriction?.Restore();
        }

        var foundryCleanupSettlement = await AwaitFoundryCleanupToSettleAsync(modelHost)
            .ConfigureAwait(false);
        var temporaryMediaArtifactSettlement = await TemporaryFileSnapshot
            .WaitForNewTemporaryMediaArtifactsToSettleAsync(temporaryMediaArtifactsBefore).ConfigureAwait(false);
        var temporaryMediaArtifactsAfter = temporaryMediaArtifactSettlement.Snapshot;
        var temporaryVttsAfter = TemporaryFileSnapshot.CaptureTemporaryVtts(request.OutputRoot);
        var newTemporaryMediaArtifacts = TemporaryFileSnapshot.GetNewPaths(temporaryMediaArtifactsBefore.Paths, temporaryMediaArtifactsAfter.Paths);
        var newTemporaryVtts = TemporaryFileSnapshot.GetNewPaths(temporaryVttsBefore.Paths, temporaryVttsAfter.Paths);
        var temporarySnapshotErrors = TemporaryFileSnapshot.GetErrors(
            ("before temporary media-artifact snapshot", temporaryMediaArtifactsBefore),
            ("after temporary media-artifact snapshot", temporaryMediaArtifactsAfter),
            ("before temporary VTT snapshot", temporaryVttsBefore),
            ("after temporary VTT snapshot", temporaryVttsAfter));
        var outputEvidence = BuildOutputEvidence(preflight, preseededOutputs, readOnlyPreseededOutputs);
        var repeatabilityEvidence = options.CompareOutputRoot is null
            ? null
            : BuildRepeatabilityEvidence(preflight, options.CompareOutputRoot);
        var snapshotEvidence = snapshots.ToEvidence();
        var finalSnapshot = snapshotEvidence.LastOrDefault();
        var summary = Evaluate(
            options,
            preflight,
            snapshotEvidence,
            finalSnapshot,
            outputEvidence,
            newTemporaryMediaArtifacts,
            temporaryMediaArtifactSettlement,
            newTemporaryVtts,
            foundryCleanupSettlement,
            temporarySnapshotErrors,
            runCancelled,
            cancellation.IsCancellationRequested,
            automaticCancellationRequested,
            cancellationTriggerStage,
            recognizerInjection?.InvocationCount ?? 0,
            recognizerInjection?.FailureCount ?? 0,
            recognizerInjection?.DelegatedInvocationCount ?? 0,
            repeatabilityEvidence,
            outputDirectoryWriteRestriction?.ToEvidence(),
            diskFullSimulation?.ToEvidence(),
            runException);

        var report = new WorkflowIntegrationReport(
            DateTimeOffset.UtcNow,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            options.ModelVariant,
            request.InputRoot,
            request.OutputRoot,
            options.Scenario.ToString(),
            options.CollisionPolicy,
            options.PreseedExistingOutputs,
            options.ReadOnlyPreseededOutputs,
            options.DenyOutputDirectoryWrites,
            outputDirectoryWriteRestriction?.ToEvidence(),
            options.SimulateOutputDiskFull,
            diskFullSimulation?.ToEvidence(),
            stopwatch.Elapsed,
            GetProbeScope(options),
            snapshotEvidence,
            finalSnapshot,
            outputEvidence,
            repeatabilityEvidence,
            newTemporaryMediaArtifacts,
            temporaryMediaArtifactSettlement,
            newTemporaryVtts,
            foundryCleanupSettlement,
            temporarySnapshotErrors,
            options.CancellationTarget,
            cancellationTriggerStage,
            automaticCancellationRequested,
            recognizerInjection?.InvocationCount ?? 0,
            recognizerInjection?.FailureCount ?? 0,
            recognizerInjection?.DelegatedInvocationCount ?? 0,
            runException?.ToString(),
            summary);

        var json = JsonSerializer.Serialize(report, ReportJsonOptions);
        Console.WriteLine(json);
        if (reportPath is not null)
        {
            await WriteReportAsync(reportPath, json).ConfigureAwait(false);
            Console.WriteLine($"Evidence report: {reportPath}");
        }

        return ctrlCRequested
            ? 2
            : summary.Passed
                ? 0
                : 1;
    }

    private static string? ValidateReportPath(
        string? configuredPath,
        BatchRequest request,
        PreflightResult preflight)
    {
        if (configuredPath is null)
        {
            return null;
        }

        var reportPath = Path.GetFullPath(configuredPath);
        if (IsPathWithinRoot(request.InputRoot, reportPath)
            || IsPathWithinRoot(request.OutputRoot, reportPath)
            || preflight.Items.Any(item => string.Equals(
                item.OutputPath,
                reportPath,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "The evidence report must be outside the input and output roots so it cannot replace media or a final VTT.");
        }

        if (File.Exists(reportPath) || Directory.Exists(reportPath))
        {
            throw new IOException($"Refusing to overwrite an existing evidence report path: '{reportPath}'.");
        }

        return reportPath;
    }

    private static bool IsPathWithinRoot(string root, string path)
    {
        var relativePath = Path.GetRelativePath(root, path);
        return string.Equals(relativePath, ".", StringComparison.Ordinal)
            || (!string.Equals(relativePath, "..", StringComparison.Ordinal)
                && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
                && !Path.IsPathRooted(relativePath));
    }

    private static async Task WriteReportAsync(string reportPath, string json)
    {
        var reportDirectory = Path.GetDirectoryName(reportPath)
            ?? throw new InvalidOperationException("The report path must have a containing directory.");
        Directory.CreateDirectory(reportDirectory);

        var temporaryPath = Path.Combine(
            reportDirectory,
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
            if (ownsTemporaryPath && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string GetProbeScope(WorkflowProbeOptions options)
        => options.SimulateOutputDiskFull
            ? "This opt-in test-only mode calls the production extractor, VAD, Foundry host/recognizer, and Core coordinator, then replaces ITranscriptWriter at the output boundary with an exact Windows ERROR_DISK_FULL (112 / HRESULT 0x80070070) throw. It does not exhaust a filesystem, invoke the production WebVttWriter for the failing write, or replace literal low-disk clean-machine release acceptance."
            : "This opt-in tool calls the production extractor, VAD, Foundry host/recognizer (unless an explicit test-only recognizer injection is selected), Core coordinator, and WebVTT writer. A passing report is machine-specific runtime evidence; it does not replace x64/ARM64 clean-machine release acceptance.";

    private static Dictionary<string, PreseededOutput> PreseedOutputs(PreflightResult preflight)
    {
        var seeded = new Dictionary<string, PreseededOutput>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in preflight.Items)
        {
            if (File.Exists(item.OutputPath))
            {
                throw new IOException(
                    $"Refusing to preseed an existing output path: '{item.OutputPath}'. Supply an empty output root for this evidence scenario.");
            }
        }

        try
        {
            foreach (var item in preflight.Items)
            {
                CreatePreseededOutput(item.OutputPath);
                seeded.Add(item.OutputPath, new PreseededOutput(PreseededVttBytes));
            }

            return seeded;
        }
        catch
        {
            RemovePreseededOutputs(seeded);
            throw;
        }
    }

    private static void CreatePreseededOutput(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("An output path must have a containing directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        var ownsTemporaryPath = false;
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                ownsTemporaryPath = true;
                stream.Write(PreseededVttBytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, outputPath);
        }
        finally
        {
            if (ownsTemporaryPath && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void RemovePreseededOutputs(IReadOnlyDictionary<string, PreseededOutput> seeded)
    {
        foreach (var (path, output) in seeded)
        {
            try
            {
                if (File.Exists(path)
                    && File.ReadAllBytes(path).AsSpan().SequenceEqual(output.OriginalBytes))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Preserve the original pre-seeding exception without deleting a file that changed after creation.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the original pre-seeding exception without deleting a file that changed after creation.
            }
        }
    }

    private static HashSet<string> MarkPreseededOutputsReadOnly(
        IReadOnlyDictionary<string, PreseededOutput> preseededOutputs)
    {
        if (preseededOutputs.Count == 0)
        {
            throw new InvalidOperationException("Read-only output evidence requires one or more probe-created output sentinels.");
        }

        var markedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in preseededOutputs.Keys)
            {
                if (!File.Exists(path))
                {
                    throw new IOException($"The probe-created output sentinel is missing before it can be made read-only: '{path}'.");
                }

                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                if ((File.GetAttributes(path) & FileAttributes.ReadOnly) == 0)
                {
                    throw new IOException($"The output sentinel could not be marked read-only: '{path}'.");
                }

                markedPaths.Add(path);
            }

            return markedPaths;
        }
        catch
        {
            foreach (var path in markedPaths)
            {
                try
                {
                    File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                }
                catch (IOException)
                {
                    // Preserve the original setup failure. The sentinels live only in an explicitly
                    // empty, probe-owned output root and remain readable if attribute restoration fails.
                }
                catch (UnauthorizedAccessException)
                {
                    // Preserve the original setup failure. See the matching IOException case above.
                }
            }

            throw;
        }
    }

    private sealed class DiskFullSimulationTranscriptWriter : ITranscriptWriter
    {
        private const int WindowsErrorDiskFull = 112;
        private const int DiskFullHResult = unchecked((int)0x80070070);
        private readonly List<DiskFullWriteAttemptEvidence> attempts = new();

        public Task<TranscriptWriteResult> WriteAsync(
            string outputPath,
            IReadOnlyList<TranscriptCue> cues,
            TranscriptCommitMode commitMode,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            ArgumentNullException.ThrowIfNull(cues);
            cancellationToken.ThrowIfCancellationRequested();

            var document = WebVttFormatter.Format(cues);
            attempts.Add(new DiskFullWriteAttemptEvidence(
                Path.GetFullPath(outputPath),
                commitMode,
                cues.Count,
                document.CueCount,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetByteCount(document.Text)));

            throw new IOException(
                "Simulated output write failure at the ITranscriptWriter boundary: Windows ERROR_DISK_FULL (112, HRESULT 0x80070070).",
                DiskFullHResult);
        }

        public DiskFullSimulationEvidence ToEvidence()
            => new(
                true,
                "ITranscriptWriter.WriteAsync before production WebVttWriter file creation",
                WindowsErrorDiskFull,
                $"0x{DiskFullHResult:X8}",
                attempts.Count,
                attempts.ToArray());
    }

    private sealed class OutputDirectoryWriteRestriction
    {
        private readonly string outputRoot;
        private readonly FileSystemAccessRule writeDenyRule;
        private bool restoreAttempted;

        private OutputDirectoryWriteRestriction(string outputRoot, FileSystemAccessRule writeDenyRule)
        {
            this.outputRoot = outputRoot;
            this.writeDenyRule = writeDenyRule;
        }

        public bool WriteDeniedConfirmed { get; private set; }

        public bool Restored { get; private set; }

        public string? RestorationError { get; private set; }

        public static OutputDirectoryWriteRestriction Apply(string outputRoot)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
            if (Directory.Exists(outputRoot))
            {
                throw new ArgumentException(
                    "--deny-output-directory-writes requires an output root that does not already exist so the probe owns the temporary ACL change.");
            }

            Directory.CreateDirectory(outputRoot);
            var directory = new DirectoryInfo(outputRoot);
            var restrictedSecurity = directory.GetAccessControl(AccessControlSections.Access);
            using var identity = WindowsIdentity.GetCurrent();
            var currentUser = identity.User
                ?? throw new InvalidOperationException("The current Windows identity has no security identifier for ACL evidence.");
            var writeDenyRule = new FileSystemAccessRule(
                currentUser,
                FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Deny);
            restrictedSecurity.AddAccessRule(writeDenyRule);

            var restriction = new OutputDirectoryWriteRestriction(outputRoot, writeDenyRule);
            try
            {
                directory.SetAccessControl(restrictedSecurity);
                restriction.VerifyWriteIsDenied();
                return restriction;
            }
            catch
            {
                restriction.Restore();
                throw;
            }
        }

        public void Restore()
        {
            if (restoreAttempted)
            {
                return;
            }

            restoreAttempted = true;
            try
            {
                var directory = new DirectoryInfo(outputRoot);
                var security = directory.GetAccessControl(AccessControlSections.Access);
                security.RemoveAccessRuleSpecific(writeDenyRule);
                directory.SetAccessControl(security);
                VerifyWriteIsRestored();
                Restored = true;
            }
            catch (Exception exception)
            {
                RestorationError = exception.Message;
            }
        }

        public OutputDirectoryWriteRestrictionEvidence ToEvidence()
            => new(true, WriteDeniedConfirmed, Restored, RestorationError);

        private void VerifyWriteIsDenied()
        {
            var verificationPath = Path.Combine(outputRoot, $".wbt-acl-verification-{Guid.NewGuid():N}.tmp");
            try
            {
                using (new FileStream(verificationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }
            }
            catch (UnauthorizedAccessException)
            {
                WriteDeniedConfirmed = true;
                return;
            }
            catch (IOException exception)
            {
                throw new IOException("The probe could not verify that the output-directory ACL denies file creation.", exception);
            }

            try
            {
                File.Delete(verificationPath);
            }
            catch (Exception exception)
            {
                throw new IOException("The output-directory ACL unexpectedly allowed a probe file and that file could not be removed.", exception);
            }

            throw new InvalidOperationException("The output-directory ACL did not deny creation of a probe file.");
        }

        private void VerifyWriteIsRestored()
        {
            var verificationPath = Path.Combine(outputRoot, $".wbt-acl-restoration-{Guid.NewGuid():N}.tmp");
            try
            {
                using (new FileStream(verificationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }
            }
            catch (Exception exception)
            {
                throw new IOException("The probe could not verify that output-directory write access was restored.", exception);
            }

            try
            {
                File.Delete(verificationPath);
            }
            catch (Exception exception)
            {
                throw new IOException("The ACL restoration verification file could not be removed.", exception);
            }
        }
    }

    private static async Task<FoundryCleanupSettlement> AwaitFoundryCleanupToSettleAsync(FoundryLocalModelHost? modelHost)
    {
        if (modelHost is null)
        {
            return new FoundryCleanupSettlement(false, true, 0, null);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await modelHost.WaitForQuiescenceAsync()
                .WaitAsync(DeferredFoundryCleanupSettlementTimeout)
                .ConfigureAwait(false);
            return new FoundryCleanupSettlement(true, true, stopwatch.ElapsedMilliseconds, null);
        }
        catch (TimeoutException)
        {
            return new FoundryCleanupSettlement(true, false, stopwatch.ElapsedMilliseconds, "Foundry host cleanup did not settle before the evidence timeout.");
        }
        catch (Exception exception)
        {
            return new FoundryCleanupSettlement(true, false, stopwatch.ElapsedMilliseconds, exception.Message);
        }
    }

    private static List<OutputEvidence> BuildOutputEvidence(
        PreflightResult preflight,
        IReadOnlyDictionary<string, PreseededOutput> preseededOutputs,
        HashSet<string> readOnlyPreseededOutputs)
    {
        var results = new List<OutputEvidence>(preflight.Items.Count);
        foreach (var item in preflight.Items)
        {
            var exists = File.Exists(item.OutputPath);
            var wasPreseeded = preseededOutputs.TryGetValue(item.OutputPath, out var preseededOutput);
            var wasMarkedReadOnly = readOnlyPreseededOutputs.Contains(item.OutputPath);
            var preseededContentPreserved = !wasPreseeded;
            var hasHeader = false;
            var isHeaderOnly = false;
            var readOnlyAttributePresent = false;
            long? length = null;
            string? evidenceError = null;
            string? sha256 = null;

            if (!exists)
            {
                if (wasPreseeded)
                {
                    preseededContentPreserved = false;
                    evidenceError = "The pre-seeded output sentinel is missing.";
                }
            }
            else
            {
                try
                {
                    readOnlyAttributePresent = (File.GetAttributes(item.OutputPath) & FileAttributes.ReadOnly) != 0;
                    var bytes = File.ReadAllBytes(item.OutputPath);
                    length = bytes.LongLength;
                    hasHeader = HasWebVttHeader(bytes);
                    isHeaderOnly = bytes.AsSpan().SequenceEqual(HeaderOnlyVttBytes);
                    sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                    if (wasPreseeded)
                    {
                        preseededContentPreserved = bytes.AsSpan().SequenceEqual(preseededOutput!.OriginalBytes);
                    }
                }
                catch (IOException exception)
                {
                    preseededContentPreserved = !wasPreseeded;
                    evidenceError = $"Could not read output evidence: {exception.Message}";
                }
                catch (UnauthorizedAccessException exception)
                {
                    preseededContentPreserved = !wasPreseeded;
                    evidenceError = $"Could not read output evidence: {exception.Message}";
                }
            }

            results.Add(new OutputEvidence(
                item.RelativePath,
                item.OutputPath,
                exists,
                hasHeader,
                isHeaderOnly,
                wasPreseeded,
                preseededContentPreserved,
                wasMarkedReadOnly,
                readOnlyAttributePresent,
                length,
                sha256,
                evidenceError));
        }

        return results;
    }
    private static List<RepeatabilityEvidence> BuildRepeatabilityEvidence(
        PreflightResult preflight,
        string comparisonRoot)
    {
        var results = new List<RepeatabilityEvidence>(preflight.Items.Count);
        foreach (var item in preflight.Items)
        {
            var relativeOutputPath = Path.ChangeExtension(item.RelativePath, ".vtt");
            var comparisonPath = Path.GetFullPath(Path.Combine(comparisonRoot, relativeOutputPath));
            if (!IsPathWithinRoot(comparisonRoot, comparisonPath))
            {
                results.Add(new RepeatabilityEvidence(
                    item.RelativePath,
                    item.OutputPath,
                    comparisonPath,
                    CurrentOutputExists: File.Exists(item.OutputPath),
                    ComparisonOutputExists: false,
                    ComparisonHasWebVttHeader: false,
                    ByteIdentical: false,
                    ComparisonSha256: null,
                    EvidenceError: "The comparison output mapping escaped the supplied comparison root."));
                continue;
            }

            var currentOutputExists = File.Exists(item.OutputPath);
            var comparisonOutputExists = File.Exists(comparisonPath);
            var comparisonHasHeader = false;
            var byteIdentical = false;
            string? comparisonSha256 = null;
            string? evidenceError = null;

            if (comparisonOutputExists && currentOutputExists)
            {
                try
                {
                    var comparisonBytes = File.ReadAllBytes(comparisonPath);
                    comparisonHasHeader = HasWebVttHeader(comparisonBytes);
                    comparisonSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(comparisonBytes));
                    var currentBytes = File.ReadAllBytes(item.OutputPath);
                    byteIdentical = currentBytes.AsSpan().SequenceEqual(comparisonBytes);
                }
                catch (IOException exception)
                {
                    evidenceError = $"Could not read repeatability evidence: {exception.Message}";
                }
                catch (UnauthorizedAccessException exception)
                {
                    evidenceError = $"Could not read repeatability evidence: {exception.Message}";
                }
            }

            results.Add(new RepeatabilityEvidence(
                item.RelativePath,
                item.OutputPath,
                comparisonPath,
                currentOutputExists,
                comparisonOutputExists,
                comparisonHasHeader,
                byteIdentical,
                comparisonSha256,
                evidenceError));
        }

        return results;
    }

    private static bool HasWebVttHeader(ReadOnlySpan<byte> content)
    {
        var offset = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF
            ? 3
            : 0;
        return content.Length >= offset + 6
            && content.Slice(offset, 6).SequenceEqual("WEBVTT"u8);
    }

    private static ProbeSummary Evaluate(
        WorkflowProbeOptions options,
        PreflightResult preflight,
        IReadOnlyList<SnapshotEvidence> snapshots,
        SnapshotEvidence? finalSnapshot,
        IReadOnlyList<OutputEvidence> outputEvidence,
        List<string> newTemporaryMediaArtifacts,
        TemporaryMediaArtifactCleanupSettlement temporaryMediaArtifactSettlement,
        List<string> newTemporaryVtts,
        FoundryCleanupSettlement foundryCleanupSettlement,
        IReadOnlyList<string> temporarySnapshotErrors,
        bool runCancelled,
        bool cancellationRequested,
        bool automaticCancellationRequested,
        ProcessingStage? cancellationTriggerStage,
        int injectedRecognizerInvocationCount,
        int injectedRecognizerFailureCount,
        int delegatedRecognizerInvocationCount,
        IReadOnlyList<RepeatabilityEvidence>? repeatabilityEvidence,
        OutputDirectoryWriteRestrictionEvidence? outputDirectoryWriteRestriction,
        DiskFullSimulationEvidence? diskFullSimulation,
        Exception? runException)
    {
        var failures = new List<string>();
        if (runException is not null)
        {
            failures.Add($"The coordinator escaped an exception: {runException.Message}");
        }

        foreach (var temporarySnapshotError in temporarySnapshotErrors)
        {
            failures.Add($"Temporary-artifact evidence could not be captured: {temporarySnapshotError}");
        }

        if (!temporaryMediaArtifactSettlement.Settled)
        {
            failures.Add($"Extractor-owned temporary media-artifact cleanup did not settle within {DeferredMediaCleanupSettlementTimeout.TotalSeconds:F0} seconds.");
        }

        if (!foundryCleanupSettlement.Settled)
        {
            failures.Add($"Foundry host cleanup did not settle within {DeferredFoundryCleanupSettlementTimeout.TotalSeconds:F0} seconds: {foundryCleanupSettlement.Error ?? "no additional diagnostics"}");
        }

        if (newTemporaryMediaArtifacts.Count > 0)
        {
            failures.Add($"New extractor-owned temporary media artifacts remained: {string.Join(", ", newTemporaryMediaArtifacts)}");
        }

        if (newTemporaryVtts.Count > 0)
        {
            failures.Add($"New temporary VTT files remained: {string.Join(", ", newTemporaryVtts)}");
        }

        if (!string.IsNullOrWhiteSpace(finalSnapshot?.FatalError))
        {
            failures.Add($"The coordinator reported a fatal batch error: {finalSnapshot.FatalError}");
        }

        foreach (var output in outputEvidence.Where(static output => output.EvidenceError is not null))
        {
            failures.Add($"Could not validate final-output evidence for '{output.OutputPath}': {output.EvidenceError}");
        }

        foreach (var output in outputEvidence.Where(static output => output.Exists && !output.HasWebVttHeader))
        {
            failures.Add($"Final VTT is not a valid header-bearing WebVTT file: {output.OutputPath}");
        }

        switch (options.Scenario)
        {
            case WorkflowScenario.Success:
                if (cancellationRequested || runCancelled)
                {
                    failures.Add("The success scenario was cancelled.");
                }

                if (finalSnapshot?.CurrentStage != ProcessingStage.Complete)
                {
                    failures.Add("The success scenario did not reach the Complete stage.");
                }

                if (finalSnapshot is null || finalSnapshot.Jobs.Count != preflight.Items.Count
                    || finalSnapshot.Jobs.Any(static job => job.State != JobState.Complete))
                {
                    failures.Add("The success scenario did not complete every discovered job.");
                }

                foreach (var output in outputEvidence.Where(static output => !output.Exists || !output.HasWebVttHeader))
                {
                    failures.Add($"Expected a committed, valid VTT output: {output.OutputPath}");
                }


                if (repeatabilityEvidence is not null)
                {
                    EnsureRepeatabilityMatches(repeatabilityEvidence, preflight, failures);
                }
                break;

            case WorkflowScenario.Cancellation:
                if (cancellationTriggerStage is null)
                {
                    failures.Add($"The cancellation scenario never observed its {options.CancellationTarget} target before the batch ended.");
                }

                if (!automaticCancellationRequested)
                {
                    failures.Add("The cancellation scenario completed before the probe could request its timed cancellation.");
                }

                var cancellationObserved = automaticCancellationRequested
                    && cancellationRequested
                    && (runCancelled || finalSnapshot?.CurrentStage == ProcessingStage.Cancelled);
                if (!cancellationObserved)
                {
                    failures.Add("The timed cancellation did not produce a cancelled batch.");
                }

                EnsurePreseededOutputsArePreserved(outputEvidence, failures);
                break;

            case WorkflowScenario.CollisionSkip:
                EnsureCollisionPreflightObserved(snapshots, failures);
                EnsureNoModelLoadObserved(snapshots, failures);
                if (cancellationRequested || runCancelled)
                {
                    failures.Add("The collision-skip scenario was cancelled.");
                }

                if (finalSnapshot?.CurrentStage != ProcessingStage.Complete
                    || finalSnapshot.Jobs.Count != preflight.Items.Count
                    || finalSnapshot.Jobs.Any(static job => job.State != JobState.Complete
                        || !string.Equals(job.Detail, "Existing VTT skipped", StringComparison.Ordinal)))
                {
                    failures.Add("The collision-skip scenario did not complete every pre-existing output as skipped.");
                }

                EnsurePreseededOutputsArePreserved(outputEvidence, failures);
                break;

            case WorkflowScenario.CollisionCancel:
                EnsureCollisionPreflightObserved(snapshots, failures);
                EnsureNoModelLoadObserved(snapshots, failures);
                if (cancellationRequested || runCancelled)
                {
                    failures.Add("The collision-cancel scenario was externally cancelled instead of taking the collision decision.");
                }

                if (finalSnapshot?.CurrentStage != ProcessingStage.Cancelled
                    || finalSnapshot.Jobs.Count != preflight.Items.Count
                    || finalSnapshot.Jobs.Any(static job => job.State != JobState.Cancelled))
                {
                    failures.Add("The collision-cancel scenario did not cancel every preflight job before processing.");
                }

                EnsurePreseededOutputsArePreserved(outputEvidence, failures);
                break;

            case WorkflowScenario.CollisionOverwrite:
                EnsureCollisionPreflightObserved(snapshots, failures);
                if (cancellationRequested || runCancelled)
                {
                    failures.Add("The collision-overwrite scenario was cancelled.");
                }

                if (finalSnapshot?.CurrentStage != ProcessingStage.Complete
                    || finalSnapshot.Jobs.Count != preflight.Items.Count
                    || finalSnapshot.Jobs.Any(static job => job.State != JobState.Complete))
                {
                    failures.Add("The collision-overwrite scenario did not complete every preflight job.");
                }

                if (outputEvidence.Any(static output => !output.WasPreseeded || output.PreseededContentPreserved))
                {
                    failures.Add("The collision-overwrite scenario did not replace every probe-created existing VTT.");
                }

                break;

            case WorkflowScenario.ReadOnlyOutput:
                EnsureCollisionPreflightObserved(snapshots, failures);
                if (cancellationRequested || runCancelled)
                {
                    failures.Add("The read-only-output scenario was cancelled.");
                }

                if (finalSnapshot?.CurrentStage != ProcessingStage.Complete
                    || finalSnapshot.Jobs.Count != preflight.Items.Count
                    || finalSnapshot.Jobs.Any(static job => job.State != JobState.Failed))
                {
                    failures.Add("The read-only-output scenario did not report every write as an isolated failed job.");
                }

                if (outputEvidence.Any(static output => !output.WasMarkedReadOnly || !output.ReadOnlyAttributePresent))
                {
                    failures.Add("The read-only-output scenario could not prove that every existing VTT retained its read-only attribute.");
                }

                EnsurePreseededOutputsArePreserved(outputEvidence, failures);
                break;

            case WorkflowScenario.OutputDirectoryWriteDenied:
                if (cancellationRequested || runCancelled)
                {
                    failures.Add("The output-directory ACL scenario was cancelled.");
                }

                if (outputDirectoryWriteRestriction is not
                    {
                        Applied: true,
                        WriteDeniedConfirmed: true,
                        Restored: true,
                        RestorationError: null,
                    })
                {
                    failures.Add("The output-directory ACL scenario could not prove a probe-owned denied-write ACL was applied and restored.");
                }

                if (!snapshots.Any(static snapshot => snapshot.Jobs.Any(static job => job.Stage == ProcessingStage.WritingVtt)))
                {
                    failures.Add("The output-directory ACL scenario never reached the production VTT writer.");
                }

                if (finalSnapshot?.CurrentStage != ProcessingStage.Complete
                    || finalSnapshot.Jobs.Count != preflight.Items.Count
                    || finalSnapshot.Jobs.Any(static job => job.State != JobState.Failed))
                {
                    failures.Add("The output-directory ACL scenario did not isolate every denied write as a failed job.");
                }

                if (outputEvidence.Any(static output => output.Exists))
                {
                    failures.Add("The output-directory ACL scenario left a final VTT despite denied output-directory writes.");
                }

                break;

            case WorkflowScenario.SimulatedOutputDiskFull:
                if (cancellationRequested || runCancelled)
                {
                    failures.Add("The simulated-disk-full scenario was cancelled.");
                }

                if (!snapshots.Any(static snapshot => snapshot.Jobs.Any(static job => job.Stage == ProcessingStage.WritingVtt)))
                {
                    failures.Add("The simulated-disk-full scenario never reached the output-write stage after production extraction, VAD, and recognition.");
                }

                if (diskFullSimulation is not
                    {
                        Enabled: true,
                        InjectionBoundary: "ITranscriptWriter.WriteAsync before production WebVttWriter file creation",
                        WindowsErrorCode: 112,
                        HResult: "0x80070070",
                    })
                {
                    failures.Add("The simulated-disk-full scenario could not prove its exact test-only ERROR_DISK_FULL injection contract.");
                }
                else
                {
                    var expectedOutputPaths = preflight.Items
                        .Select(static item => Path.GetFullPath(item.OutputPath))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var attemptedOutputPaths = diskFullSimulation.Attempts
                        .Select(static attempt => attempt.OutputPath)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    if (diskFullSimulation.AttemptCount != preflight.Items.Count
                        || diskFullSimulation.Attempts.Count != preflight.Items.Count
                        || !expectedOutputPaths.SetEquals(attemptedOutputPaths))
                    {
                        failures.Add("The simulated-disk-full scenario did not inject exactly one ERROR_DISK_FULL write failure for every mapped output.");
                    }

                    if (diskFullSimulation.Attempts.Any(static attempt =>
                        attempt.CommitMode != TranscriptCommitMode.Overwrite
                        || attempt.FormattedCueCount <= 0
                        || attempt.FormattedUtf8ByteCount <= 8
                        || attempt.InputCueCount < attempt.FormattedCueCount))
                    {
                        failures.Add("The simulated-disk-full scenario did not prove a non-empty real-recognizer transcript reached the output-write boundary for every job.");
                    }
                }

                if (finalSnapshot?.CurrentStage != ProcessingStage.Complete
                    || finalSnapshot.Jobs.Count != preflight.Items.Count
                    || finalSnapshot.Jobs.Any(static job => job.State != JobState.Failed))
                {
                    failures.Add("The simulated-disk-full scenario did not isolate every simulated output write failure as a failed job.");
                }

                if (finalSnapshot?.Jobs.Any(static job => !job.Detail.Contains("ERROR_DISK_FULL", StringComparison.Ordinal)) == true)
                {
                    failures.Add("The simulated-disk-full job diagnostics did not retain the ERROR_DISK_FULL reason.");
                }

                EnsurePreseededOutputsArePreserved(outputEvidence, failures);
                break;

            case WorkflowScenario.InjectedRecognizerFailure:
                if (cancellationRequested || runCancelled)
                {
                    failures.Add("The injected-recognizer-failure scenario was cancelled instead of completing its failure path.");
                }

                if (injectedRecognizerInvocationCount == 0)
                {
                    failures.Add("The injected recognizer was never invoked; use an input with detected speech.");
                }

                if (finalSnapshot is null || !finalSnapshot.Jobs.Any(static job => job.State == JobState.Failed))
                {
                    failures.Add("The injected-recognizer-failure scenario did not produce a failed job. Use an input with detected speech.");
                }

                if (!string.IsNullOrWhiteSpace(finalSnapshot?.FatalError))
                {
                    failures.Add("The injected-recognizer-failure scenario reported a fatal batch error instead of an isolated failed job.");
                }

                EnsurePreseededOutputsArePreserved(outputEvidence, failures);
                break;
            case WorkflowScenario.EmptyRecognizerResponse:
                if (cancellationRequested || runCancelled)
                {
                    failures.Add("The empty-recognizer-response scenario was cancelled.");
                }

                if (injectedRecognizerInvocationCount == 0)
                {
                    failures.Add("The empty recognizer was never invoked; use an input with detected speech.");
                }

                if (injectedRecognizerFailureCount != 0)
                {
                    failures.Add("The empty recognizer unexpectedly reported an injected failure.");
                }

                if (finalSnapshot?.CurrentStage != ProcessingStage.Complete)
                {
                    failures.Add("The empty-recognizer-response scenario did not reach the Complete stage.");
                }

                if (finalSnapshot is null || finalSnapshot.Jobs.Count != preflight.Items.Count
                    || finalSnapshot.Jobs.Any(static job => job.State != JobState.Complete))
                {
                    failures.Add("The empty-recognizer-response scenario did not complete every discovered job.");
                }

                if (finalSnapshot is not null && finalSnapshot.Jobs.Any(static job => job.CueCount != 0))
                {
                    failures.Add("An empty recognizer response produced one or more VTT cues.");
                }

                foreach (var output in outputEvidence.Where(static output => !output.Exists || !output.IsHeaderOnly))
                {
                    failures.Add($"Expected a committed header-only VTT after empty recognition: {output.OutputPath}");
                }

                break;

            case WorkflowScenario.MixedRecognizerFailure:
                if (cancellationRequested || runCancelled)
                {
                    failures.Add("The mixed-recognizer-failure scenario was cancelled instead of completing its isolated failure path.");
                }

                if (injectedRecognizerFailureCount != 1)
                {
                    failures.Add($"Expected exactly one injected recognizer failure, observed {injectedRecognizerFailureCount}.");
                }

                if (delegatedRecognizerInvocationCount == 0)
                {
                    failures.Add("The mixed-recognizer-failure scenario never reached the real recognizer after its injected failure.");
                }

                if (finalSnapshot?.CurrentStage != ProcessingStage.Complete)
                {
                    failures.Add("The mixed-recognizer-failure scenario did not reach the Complete stage.");
                }

                if (finalSnapshot is null || finalSnapshot.Jobs.Count != preflight.Items.Count
                    || !finalSnapshot.Jobs.Any(static job => job.State == JobState.Failed)
                    || !finalSnapshot.Jobs.Any(static job => job.State == JobState.Complete)
                    || finalSnapshot.Jobs.Any(static job => job.State is not (JobState.Complete or JobState.Failed)))
                {
                    failures.Add("The mixed-recognizer-failure scenario did not leave a mix of only failed and completed jobs.");
                }

                EnsureMixedRecognizerFailureOutputContract(finalSnapshot, outputEvidence, failures);
                break;

            default:
                throw new InvalidOperationException($"Unknown workflow scenario '{options.Scenario}'.");
        }

        return new ProbeSummary(
            failures.Count == 0,
            preflight.Items.Count,
            finalSnapshot?.Jobs.Count(static job => job.State == JobState.Complete) ?? 0,
            finalSnapshot?.Jobs.Count(static job => job.State == JobState.Failed) ?? 0,
            finalSnapshot?.Jobs.Count(static job => job.State == JobState.Cancelled) ?? 0,
            failures);
    }

    private static void EnsurePreseededOutputsArePreserved(
        IReadOnlyList<OutputEvidence> outputEvidence,
        List<string> failures)
    {
        foreach (var output in outputEvidence)
        {
            if (!output.WasPreseeded)
            {
                failures.Add($"The expected preservation sentinel was not created: {output.OutputPath}");
            }
            else if (!output.PreseededContentPreserved)
            {
                failures.Add($"An existing final VTT changed during a failed/cancelled scenario: {output.OutputPath}");
            }
        }
    }
    private static void EnsureCollisionPreflightObserved(
        IReadOnlyList<SnapshotEvidence> snapshots,
        List<string> failures)
    {
        if (!snapshots.Any(static snapshot => snapshot.CurrentStage == ProcessingStage.Preflight
            && snapshot.StageText.Contains("existing VTT", StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add("The scenario did not record the existing-output preflight stage.");
        }
    }

    private static void EnsureNoModelLoadObserved(
        IReadOnlyList<SnapshotEvidence> snapshots,
        List<string> failures)
    {
        if (snapshots.Any(static snapshot => snapshot.CurrentStage is ProcessingStage.LoadingModel or ProcessingStage.DownloadingModel))
        {
            failures.Add("The collision decision entered model loading even though no file should have been processed.");
        }
    }

    private static void EnsureRepeatabilityMatches(
        IReadOnlyList<RepeatabilityEvidence> repeatabilityEvidence,
        PreflightResult preflight,
        List<string> failures)
    {
        if (repeatabilityEvidence.Count != preflight.Items.Count)
        {
            failures.Add("The repeatability comparison did not contain every discovered output.");
        }

        foreach (var comparison in repeatabilityEvidence)
        {
            if (comparison.EvidenceError is not null)
            {
                failures.Add($"Could not validate repeatability for '{comparison.RelativeInputPath}': {comparison.EvidenceError}");
                continue;
            }

            if (!comparison.CurrentOutputExists)
            {
                failures.Add($"The repeatability run did not produce a current VTT: {comparison.CurrentOutputPath}");
            }
            else if (!comparison.ComparisonOutputExists)
            {
                failures.Add($"The repeatability baseline VTT is missing: {comparison.ComparisonOutputPath}");
            }
            else if (!comparison.ComparisonHasWebVttHeader)
            {
                failures.Add($"The repeatability baseline is not a header-bearing WebVTT: {comparison.ComparisonOutputPath}");
            }
            else if (!comparison.ByteIdentical)
            {
                failures.Add($"The repeatability output differs byte-for-byte: {comparison.RelativeInputPath}");
            }
        }
    }
    private static void EnsureMixedRecognizerFailureOutputContract(
        SnapshotEvidence? finalSnapshot,
        IReadOnlyList<OutputEvidence> outputEvidence,
        List<string> failures)
    {
        if (finalSnapshot is null)
        {
            return;
        }

        var outputsByPath = outputEvidence.ToDictionary(static output => output.OutputPath, StringComparer.OrdinalIgnoreCase);

        foreach (var job in finalSnapshot.Jobs)
        {
            if (!outputsByPath.TryGetValue(job.OutputPath, out var output))
            {
                failures.Add($"The mixed-recognizer-failure scenario has no output evidence for '{job.OutputPath}'.");
                continue;
            }

            switch (job.State)
            {
                case JobState.Failed:
                    if (!output.WasPreseeded)
                    {
                        failures.Add($"The failed job was not protected by a pre-seeded sentinel: {output.OutputPath}");
                    }
                    else if (!output.PreseededContentPreserved)
                    {
                        failures.Add($"The failed job changed its existing final VTT: {output.OutputPath}");
                    }

                    break;

                case JobState.Complete:
                    if (!output.WasPreseeded)
                    {
                        failures.Add($"The completed job was not covered by the mixed-scenario output contract: {output.OutputPath}");
                    }
                    else if (output.PreseededContentPreserved)
                    {
                        failures.Add($"The completed job left its sentinel unchanged instead of committing a replacement VTT: {output.OutputPath}");
                    }
                    else if (!output.Exists || !output.HasWebVttHeader)
                    {
                        failures.Add($"The completed job did not commit a valid final VTT: {output.OutputPath}");
                    }

                    break;

                default:
                    failures.Add($"The mixed-recognizer-failure scenario ended with an unexpected job state '{job.State}' for '{job.RelativePath}'.");
                    break;
            }
        }
    }


    private sealed class FixedExistingOutputPolicyResolver : IExistingOutputPolicyResolver
    {
        private readonly ExistingOutputPolicy _policy;

        public FixedExistingOutputPolicyResolver(ExistingOutputPolicy policy)
        {
            _policy = policy;
        }

        public Task<ExistingOutputPolicy> ResolveAsync(
            IReadOnlyList<BatchItem> existingOutputs,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(existingOutputs);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_policy);
        }
    }
    private interface IRecognizerInjection
    {
        int InvocationCount { get; }
        int FailureCount { get; }
        int DelegatedInvocationCount { get; }
    }

    private sealed class FailingRecognizer : ISpeechRecognizer, IRecognizerInjection
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public int FailureCount => InvocationCount;
        public int DelegatedInvocationCount => 0;

        public Task<string> RecognizeAsync(
            PcmWaveFile waveFile,
            SpeechInterval interval,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(waveFile);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _invocationCount);
            return Task.FromException<string>(
                new InvalidOperationException("Workflow probe injected a recognizer failure before any final VTT write."));
        }
    }
    private sealed class EmptyRecognizer : ISpeechRecognizer, IRecognizerInjection
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public int FailureCount => 0;
        public int DelegatedInvocationCount => 0;

        public Task<string> RecognizeAsync(
            PcmWaveFile waveFile,
            SpeechInterval interval,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(waveFile);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _invocationCount);
            return Task.FromResult(string.Empty);
        }
    }

    private sealed class FailOnCallRecognizer : ISpeechRecognizer, IRecognizerInjection
    {
        private readonly ISpeechRecognizer _inner;
        private readonly int _failureOnCall;
        private int _invocationCount;
        private int _failureCount;
        private int _delegatedInvocationCount;

        public FailOnCallRecognizer(ISpeechRecognizer inner, int failureOnCall)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(failureOnCall);
            _inner = inner;
            _failureOnCall = failureOnCall;
        }

        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public int FailureCount => Volatile.Read(ref _failureCount);
        public int DelegatedInvocationCount => Volatile.Read(ref _delegatedInvocationCount);

        public async Task<string> RecognizeAsync(
            PcmWaveFile waveFile,
            SpeechInterval interval,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(waveFile);
            cancellationToken.ThrowIfCancellationRequested();
            var invocation = Interlocked.Increment(ref _invocationCount);
            if (invocation == _failureOnCall)
            {
                Interlocked.Increment(ref _failureCount);
                throw new InvalidOperationException("Workflow probe injected one recognizer failure before delegating all later calls to the production recognizer.");
            }

            Interlocked.Increment(ref _delegatedInvocationCount);
            return await _inner.RecognizeAsync(waveFile, interval, cancellationToken).ConfigureAwait(false);
        }
    }


    private sealed class SnapshotCollector : IProgress<BatchProgressSnapshot>
    {
        private readonly object _syncRoot = new();
        private readonly List<BatchProgressSnapshot> _snapshots = [];
        private readonly TaskCompletionSource<ProcessingStage> _detectingSpeechStage = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ProcessingStage> _transcribingStage = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProcessingStage> WaitForCancellationTargetAsync(WorkflowCancellationTarget target)
            => target switch
            {
                WorkflowCancellationTarget.DetectingSpeech => _detectingSpeechStage.Task,
                WorkflowCancellationTarget.Transcribing => _transcribingStage.Task,
                _ => throw new ArgumentOutOfRangeException(nameof(target)),
            };

        public void Report(BatchProgressSnapshot value)
        {
            ArgumentNullException.ThrowIfNull(value);

            var detectingSpeechStage = FindObservedStage(value, IsDetectingSpeechOrLater);
            if (detectingSpeechStage is not null)
            {
                _detectingSpeechStage.TrySetResult(detectingSpeechStage.Value);
            }

            var transcribingStage = FindObservedStage(value, static stage => stage == ProcessingStage.Transcribing);
            if (transcribingStage is not null)
            {
                _transcribingStage.TrySetResult(transcribingStage.Value);
            }

            lock (_syncRoot)
            {
                _snapshots.Add(value);
            }
        }

        public List<SnapshotEvidence> ToEvidence()
        {
            lock (_syncRoot)
            {
                var evidence = new List<SnapshotEvidence>(_snapshots.Count);
                for (var index = 0; index < _snapshots.Count; index++)
                {
                    var snapshot = _snapshots[index];
                    evidence.Add(new SnapshotEvidence(
                        index + 1,
                        snapshot.CurrentStage,
                        snapshot.StageText,
                        snapshot.CurrentFileName,
                        snapshot.CurrentFileProgress,
                        snapshot.IsRunning,
                        snapshot.IsCancelling,
                        snapshot.CompletedFileCount,
                        snapshot.TotalFileCount,
                        snapshot.FatalError,
                        snapshot.Jobs
                            .Select(static job => new JobEvidence(
                                job.RelativePath,
                                job.OutputPath,
                                job.State,
                                job.Stage,
                                job.Progress,
                                job.Detail,
                                job.CueCount))
                            .ToArray()));
                }

                return evidence;
            }
        }

        private static ProcessingStage? FindObservedStage(
            BatchProgressSnapshot snapshot,
            Func<ProcessingStage, bool> predicate)
        {
            if (predicate(snapshot.CurrentStage))
            {
                return snapshot.CurrentStage;
            }

            foreach (var job in snapshot.Jobs)
            {
                if (predicate(job.Stage))
                {
                    return job.Stage;
                }
            }

            return null;
        }

        private static bool IsDetectingSpeechOrLater(ProcessingStage stage)
            => stage is ProcessingStage.DetectingSpeech
                or ProcessingStage.Transcribing
                or ProcessingStage.WritingVtt;
    }

    private static class TemporaryFileSnapshot
    {
        public static TemporaryFileSnapshotResult CaptureTemporaryMediaArtifacts()
            => Capture(
                Path.Combine(Path.GetTempPath(), TemporaryDirectoryName),
                "*.*",
                static path => WindowsMediaAudioExtractor.IsOwnedTemporaryWavePath(path)
                    || WindowsMediaAudioExtractor.IsOwnedTemporaryInputPath(path));

        public static async Task<TemporaryMediaArtifactCleanupSettlement> WaitForNewTemporaryMediaArtifactsToSettleAsync(TemporaryFileSnapshotResult before)
        {
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                var snapshot = CaptureTemporaryMediaArtifacts();
                if (before.Error is not null || snapshot.Error is not null)
                {
                    return new TemporaryMediaArtifactCleanupSettlement(snapshot, false, stopwatch.ElapsedMilliseconds);
                }

                if (GetNewPaths(before.Paths, snapshot.Paths).Count == 0)
                {
                    return new TemporaryMediaArtifactCleanupSettlement(snapshot, true, stopwatch.ElapsedMilliseconds);
                }

                var remaining = DeferredMediaCleanupSettlementTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return new TemporaryMediaArtifactCleanupSettlement(snapshot, false, stopwatch.ElapsedMilliseconds);
                }

                await Task.Delay(
                    remaining < DeferredMediaCleanupPollInterval ? remaining : DeferredMediaCleanupPollInterval,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        public static TemporaryFileSnapshotResult CaptureTemporaryVtts(string outputRoot)
            => Capture(outputRoot, "*.tmp");

        public static List<string> GetNewPaths(
            IReadOnlyCollection<string> before,
            IReadOnlyCollection<string> after)
            => after
                .Except(before, StringComparer.OrdinalIgnoreCase)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

        public static List<string> GetErrors(
            params (string Description, TemporaryFileSnapshotResult Snapshot)[] snapshots)
            => snapshots
                .Where(static snapshot => snapshot.Snapshot.Error is not null)
                .Select(static snapshot => $"{snapshot.Description}: {snapshot.Snapshot.Error}")
                .ToList();

        private static TemporaryFileSnapshotResult Capture(string root, string searchPattern, Func<string, bool>? pathFilter = null)
        {
            try
            {
                var paths = Directory
                    .EnumerateFiles(
                        root,
                        searchPattern,
                        new EnumerationOptions
                        {
                            RecurseSubdirectories = true,
                            IgnoreInaccessible = false,
                            AttributesToSkip = FileAttributes.ReparsePoint,
                        })
                    .Select(Path.GetFullPath)
                    .Where(path => pathFilter is null || pathFilter(path))
                    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new TemporaryFileSnapshotResult(paths, null);
            }
            catch (DirectoryNotFoundException)
            {
                return new TemporaryFileSnapshotResult([], null);
            }
            catch (Exception exception)
            {
                return new TemporaryFileSnapshotResult(
                    [],
                    $"Could not enumerate '{root}' for '{searchPattern}': {exception.Message}");
            }
        }
    }

    private sealed record TemporaryFileSnapshotResult(
        IReadOnlyList<string> Paths,
        string? Error);

    private sealed record TemporaryMediaArtifactCleanupSettlement(TemporaryFileSnapshotResult Snapshot, bool Settled, long WaitMilliseconds);

    private sealed record FoundryCleanupSettlement(
        bool HostCreated,
        bool Settled,
        long WaitMilliseconds,
        string? Error);

    private enum WorkflowCancellationTarget
    {
        DetectingSpeech,
        Transcribing,
    }

    private enum WorkflowScenario
    {
        Success,
        Cancellation,
        CollisionSkip,
        CollisionCancel,
        CollisionOverwrite,
        ReadOnlyOutput,
        SimulatedOutputDiskFull,
        OutputDirectoryWriteDenied,
        InjectedRecognizerFailure,
        EmptyRecognizerResponse,
        MixedRecognizerFailure,
    }

    private sealed record WorkflowProbeOptions(
        bool ShowHelp,
        string? InputRoot,
        string? OutputRoot,
        string? CompareOutputRoot,
        string? ReportPath,
        string ModelVariant,
        ExistingOutputPolicy CollisionPolicy,
        int? CancelAfterMilliseconds,
        WorkflowCancellationTarget CancellationTarget,
        bool PreseedExistingOutputs,
        bool ReadOnlyPreseededOutputs,
        bool DenyOutputDirectoryWrites,
        bool SimulateOutputDiskFull,
        bool InjectRecognizerFailure,
        bool InjectEmptyRecognizerResponse,
        int? InjectRecognizerFailureOnCall,
        WorkflowScenario Scenario)
    {
        public const string Usage = """
            Usage:
              dotnet run --project tools/WinBulkTranscript.WorkflowIntegrationProbe -- --input-root <MP4 directory> --output-root <empty output directory> [options]

            Required:
              --input-root <directory>           Root containing one or more real MP4 inputs.
              --output-root <directory>          Destination root for mirrored final VTT files.

            Options:
              --report <path>                    Write JSON evidence outside the input/output roots; never overwrites.
              --model <exact variant>             Candidate Foundry variant (default: nemotron-speech-streaming-en-0.6b-generic-cpu:3).
              --collision-policy <skip|overwrite|cancel>
                                                 Batch-wide existing-output response (default: skip).
              --cancel-after-ms <positive int>    After a per-file stage is observed, request cancellation after this delay.
              --preseed-existing-outputs          Create sentinel VTT files only at otherwise absent mapped outputs.
              --read-only-preseeded-outputs        Mark probe-created sentinels read-only and assert isolated write failures.
              --deny-output-directory-writes       Require a new probe-owned output directory, deny file creation temporarily,
                                                 and assert isolated production writer failures plus ACL restoration.
              --simulate-output-disk-full          Test-only ERROR_DISK_FULL output-boundary simulation; requires preseed + overwrite.
                                                 Does not exhaust a filesystem or replace literal low-disk UAT.
              --compare-output-root <directory>   Require byte-for-byte equality with a prior normal-run VTT tree.
              --inject-recognizer-failure         Fail every injected recognizer call; requires preseed + overwrite.
              --inject-empty-recognizer-response  Return empty text after detected speech and require header-only VTTs.
              --inject-recognizer-failure-on-call <positive int>
                                                 Fail one call, then delegate later calls to the production recognizer.
              --help                              Show this help.

            Scenarios:
              Normal options verify a completed production workflow. --compare-output-root is normal-mode
              repeatability evidence and must use a prior VTT tree with the same input-relative layout.
              --cancel-after-ms requires preseed + overwrite and verifies cancellation after per-file work plus cleanup.
              --inject-empty-recognizer-response verifies that detected speech with empty recognition commits only
              header-only VTTs. --inject-recognizer-failure verifies all-failure sentinel preservation.
              --inject-recognizer-failure-on-call requires preseed + overwrite, proves one failed job preserves
              its sentinel, and requires later jobs to reach the real recognizer and commit valid replacements.
              With --preseed-existing-outputs alone, skip, overwrite, and cancel each produce a collision-policy
              evidence scenario. --read-only-preseeded-outputs requires preseed + overwrite and leaves only
              probe-created sentinels marked read-only in the supplied disposable output root.
              --deny-output-directory-writes is a production-recognizer scenario with the default collision policy.
              It requires an output root that does not exist, creates and owns that root, proves file creation is
              denied during the run, asserts failed writer jobs with no final VTT, and restores the original ACL
              before emitting its report.
              --simulate-output-disk-full requires preseed + overwrite and retains the production extractor, VAD,
              Foundry recognizer, and coordinator before injecting Windows ERROR_DISK_FULL at ITranscriptWriter.
              It is deterministic simulation evidence only, never literal low-disk or release-matrix acceptance.
            """;

        public static WorkflowProbeOptions Parse(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);

            string? inputRoot = null;
            string? outputRoot = null;
            string? compareOutputRoot = null;
            string? reportPath = null;
            var modelVariant = FoundryLocalModelHost.InitialCandidateModelVariant;
            var collisionPolicy = ExistingOutputPolicy.SkipExisting;
            int? cancelAfterMilliseconds = null;
            var cancellationTarget = WorkflowCancellationTarget.DetectingSpeech;
            var cancellationTargetSpecified = false;
            var preseedExistingOutputs = false;
            var simulateOutputDiskFull = false;
            var readOnlyPreseededOutputs = false;
            var denyOutputDirectoryWrites = false;
            var injectRecognizerFailure = false;
            var injectEmptyRecognizerResponse = false;
            int? injectRecognizerFailureOnCall = null;
            var showHelp = false;

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--help":
                    case "-h":
                        showHelp = true;
                        break;
                    case "--input-root":
                        inputRoot = ReadValue(args, ref index, "--input-root");
                        break;
                    case "--output-root":
                        outputRoot = ReadValue(args, ref index, "--output-root");
                        break;
                    case "--compare-output-root":
                        compareOutputRoot = ReadValue(args, ref index, "--compare-output-root");
                        break;
                    case "--report":
                        reportPath = ReadValue(args, ref index, "--report");
                        break;
                    case "--model":
                        modelVariant = ReadValue(args, ref index, "--model");
                        break;
                    case "--collision-policy":
                        collisionPolicy = ParseCollisionPolicy(ReadValue(args, ref index, "--collision-policy"));
                        break;
                    case "--cancel-after-ms":
                        cancelAfterMilliseconds = ParsePositiveInt(ReadValue(args, ref index, "--cancel-after-ms"), "--cancel-after-ms");
                        break;
                    case "--cancel-at-stage":
                        if (cancellationTargetSpecified)
                        {
                            throw new ArgumentException("Option '--cancel-at-stage' can be supplied only once.");
                        }

                        cancellationTarget = ParseCancellationTarget(ReadValue(args, ref index, "--cancel-at-stage"));
                        cancellationTargetSpecified = true;
                        break;
                    case "--preseed-existing-outputs":
                        preseedExistingOutputs = true;
                        break;
                    case "--read-only-preseeded-outputs":
                        readOnlyPreseededOutputs = true;
                        break;
                    case "--deny-output-directory-writes":
                        denyOutputDirectoryWrites = true;
                        break;
                    case "--simulate-output-disk-full":
                        simulateOutputDiskFull = true;
                        break;
                    case "--inject-recognizer-failure":
                        injectRecognizerFailure = true;
                        break;
                    case "--inject-empty-recognizer-response":
                        injectEmptyRecognizerResponse = true;
                        break;
                    case "--inject-recognizer-failure-on-call":
                        if (injectRecognizerFailureOnCall is not null)
                        {
                            throw new ArgumentException("Option '--inject-recognizer-failure-on-call' can be supplied only once.");
                        }

                        injectRecognizerFailureOnCall = ParsePositiveInt(ReadValue(args, ref index, "--inject-recognizer-failure-on-call"), "--inject-recognizer-failure-on-call");
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{args[index]}'. Use --help for usage.");
                }
            }

            if (showHelp)
            {
                return new WorkflowProbeOptions(
                    true,
                    null,
                    null,
                    compareOutputRoot,
                    reportPath,
                    modelVariant,
                    collisionPolicy,
                    cancelAfterMilliseconds,
                    cancellationTarget,
                    preseedExistingOutputs,
                    readOnlyPreseededOutputs,
                    denyOutputDirectoryWrites,
                    simulateOutputDiskFull,
                    injectRecognizerFailure,
                    injectEmptyRecognizerResponse,
                    injectRecognizerFailureOnCall,
                    WorkflowScenario.Success);
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(inputRoot);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
            ArgumentException.ThrowIfNullOrWhiteSpace(modelVariant);

            var recognizerInjectionCount = (injectRecognizerFailure ? 1 : 0)
                + (injectEmptyRecognizerResponse ? 1 : 0)
                + (injectRecognizerFailureOnCall is null ? 0 : 1);

            if (recognizerInjectionCount > 1)
            {
                throw new ArgumentException("Choose at most one recognizer injection option.");
            }

            if (recognizerInjectionCount > 0 && cancelAfterMilliseconds is not null)
            {
                throw new ArgumentException("Choose either a recognizer injection option or --cancel-after-ms, not both.");
            }

            if ((injectRecognizerFailure || injectRecognizerFailureOnCall is not null)
                && (!preseedExistingOutputs || collisionPolicy != ExistingOutputPolicy.OverwriteAll))
            {
                throw new ArgumentException("Recognizer failure injection requires --preseed-existing-outputs with --collision-policy overwrite to prove final-output preservation.");
            }

            if (cancelAfterMilliseconds is not null
                && (!preseedExistingOutputs || collisionPolicy != ExistingOutputPolicy.OverwriteAll))
            {
                throw new ArgumentException("--cancel-after-ms requires --preseed-existing-outputs with --collision-policy overwrite to prove final-output preservation.");
            }

            if (readOnlyPreseededOutputs
                && (!preseedExistingOutputs || collisionPolicy != ExistingOutputPolicy.OverwriteAll))
            {
                throw new ArgumentException("--read-only-preseeded-outputs requires --preseed-existing-outputs with --collision-policy overwrite.");
            }

            if (readOnlyPreseededOutputs && (recognizerInjectionCount > 0 || cancelAfterMilliseconds is not null))
            {
                throw new ArgumentException("--read-only-preseeded-outputs uses the production recognizer and cannot be combined with a recognizer injection or timed cancellation.");
            }

            if (denyOutputDirectoryWrites
                && (preseedExistingOutputs
                    || readOnlyPreseededOutputs
                    || recognizerInjectionCount > 0
                    || cancelAfterMilliseconds is not null
                    || collisionPolicy != ExistingOutputPolicy.SkipExisting))
            {
                throw new ArgumentException("--deny-output-directory-writes is an isolated production scenario and cannot be combined with sentinels, injections, timed cancellation, or a collision policy override.");
            }

            if (simulateOutputDiskFull
                && (!preseedExistingOutputs
                    || collisionPolicy != ExistingOutputPolicy.OverwriteAll
                    || readOnlyPreseededOutputs
                    || denyOutputDirectoryWrites
                    || recognizerInjectionCount > 0
                    || cancelAfterMilliseconds is not null))
            {
                throw new ArgumentException("--simulate-output-disk-full is an isolated test-only output-boundary scenario that requires --preseed-existing-outputs with --collision-policy overwrite and cannot be combined with ACL/read-only modes, recognizer injections, or timed cancellation.");
            }


            if (cancellationTargetSpecified && cancelAfterMilliseconds is null)
            {
                throw new ArgumentException("Option '--cancel-at-stage' requires --cancel-after-ms.");
            }

            var scenario = simulateOutputDiskFull
                ? WorkflowScenario.SimulatedOutputDiskFull
                : denyOutputDirectoryWrites
                ? WorkflowScenario.OutputDirectoryWriteDenied
                : readOnlyPreseededOutputs
                ? WorkflowScenario.ReadOnlyOutput
                : injectRecognizerFailure
                    ? WorkflowScenario.InjectedRecognizerFailure
                    : injectEmptyRecognizerResponse
                        ? WorkflowScenario.EmptyRecognizerResponse
                        : injectRecognizerFailureOnCall is not null
                            ? WorkflowScenario.MixedRecognizerFailure
                            : cancelAfterMilliseconds is not null
                                ? WorkflowScenario.Cancellation
                                : preseedExistingOutputs
                                    ? collisionPolicy switch
                                    {
                                        ExistingOutputPolicy.SkipExisting => WorkflowScenario.CollisionSkip,
                                        ExistingOutputPolicy.OverwriteAll => WorkflowScenario.CollisionOverwrite,
                                        ExistingOutputPolicy.Cancel => WorkflowScenario.CollisionCancel,
                                        _ => throw new InvalidOperationException($"Unknown collision policy '{collisionPolicy}'."),
                                    }
                                    : WorkflowScenario.Success;

            var fullInputRoot = Path.GetFullPath(inputRoot);
            var fullOutputRoot = Path.GetFullPath(outputRoot);
            var fullCompareOutputRoot = compareOutputRoot is null
                ? null
                : Path.GetFullPath(compareOutputRoot);

            if (fullCompareOutputRoot is not null && scenario != WorkflowScenario.Success)
            {
                throw new ArgumentException("--compare-output-root is allowed only for the normal success scenario.");
            }

            if (fullCompareOutputRoot is not null
                && (Program.IsPathWithinRoot(fullOutputRoot, fullCompareOutputRoot)
                    || Program.IsPathWithinRoot(fullCompareOutputRoot, fullOutputRoot)))
            {
                throw new ArgumentException("--compare-output-root must be a separate prior-run VTT tree, not the current output root or one of its parents/children.");
            }

            if (fullCompareOutputRoot is not null && !Directory.Exists(fullCompareOutputRoot))
            {
                throw new DirectoryNotFoundException($"The repeatability comparison root does not exist: '{fullCompareOutputRoot}'.");
            }

            return new WorkflowProbeOptions(
                false,
                fullInputRoot,
                fullOutputRoot,
                fullCompareOutputRoot,
                reportPath,
                modelVariant,
                collisionPolicy,
                cancelAfterMilliseconds,
                cancellationTarget,
                preseedExistingOutputs,
                readOnlyPreseededOutputs,
                denyOutputDirectoryWrites,
                simulateOutputDiskFull,
                injectRecognizerFailure,
                injectEmptyRecognizerResponse,
                injectRecognizerFailureOnCall,
                scenario);
        }

        private static string ReadValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException($"Option '{option}' requires a value.");
            }

            index++;
            return args[index];
        }

        private static int ParsePositiveInt(string value, string option)
        {
            if (!int.TryParse(value, out var parsed) || parsed <= 0)
            {
                throw new ArgumentException($"Option '{option}' requires a positive integer.");
            }

            return parsed;
        }

        private static ExistingOutputPolicy ParseCollisionPolicy(string value)
            => value.ToLowerInvariant() switch
            {
                "skip" => ExistingOutputPolicy.SkipExisting,
                "overwrite" => ExistingOutputPolicy.OverwriteAll,
                "cancel" => ExistingOutputPolicy.Cancel,
                _ => throw new ArgumentException("Collision policy must be skip, overwrite, or cancel."),
            };

        private static WorkflowCancellationTarget ParseCancellationTarget(string value)
            => value.ToLowerInvariant() switch
            {
                "detecting-speech" => WorkflowCancellationTarget.DetectingSpeech,
                "transcribing" => WorkflowCancellationTarget.Transcribing,
                _ => throw new ArgumentException("Cancellation target must be detecting-speech or transcribing."),
            };
    }

    private sealed record WorkflowIntegrationReport(
        DateTimeOffset GeneratedAtUtc,
        string OperatingSystem,
        string ProcessArchitecture,
        string ModelVariant,
        string InputRoot,
        string OutputRoot,
        string Scenario,
        ExistingOutputPolicy CollisionPolicy,
        bool PreseedExistingOutputs,
        bool ReadOnlyPreseededOutputs,
        bool DenyOutputDirectoryWrites,
        OutputDirectoryWriteRestrictionEvidence? OutputDirectoryWriteRestriction,
        bool SimulateOutputDiskFull,
        DiskFullSimulationEvidence? DiskFullSimulation,
        TimeSpan Elapsed,
        string Scope,
        IReadOnlyList<SnapshotEvidence> Snapshots,
        SnapshotEvidence? FinalSnapshot,
        IReadOnlyList<OutputEvidence> Outputs,
        IReadOnlyList<RepeatabilityEvidence>? RepeatabilityComparison,
        IReadOnlyList<string> NewTemporaryMediaArtifacts,
        TemporaryMediaArtifactCleanupSettlement TemporaryMediaArtifactCleanupSettlement,
        IReadOnlyList<string> NewTemporaryVtts,
        FoundryCleanupSettlement FoundryCleanupSettlement,
        IReadOnlyList<string> TemporarySnapshotErrors,
        WorkflowCancellationTarget CancellationTarget,
        ProcessingStage? CancellationTriggerStage,
        bool AutomaticCancellationRequested,
        int InjectedRecognizerInvocationCount,
        int InjectedRecognizerFailureCount,
        int DelegatedRecognizerInvocationCount,
        string? EscapedException,
        ProbeSummary Summary);

    private sealed record SnapshotEvidence(
        int Sequence,
        ProcessingStage CurrentStage,
        string StageText,
        string CurrentFileName,
        double CurrentFileProgress,
        bool IsRunning,
        bool IsCancelling,
        int CompletedFileCount,
        int TotalFileCount,
        string? FatalError,
        IReadOnlyList<JobEvidence> Jobs);

    private sealed record JobEvidence(
        string RelativePath,
        string OutputPath,
        JobState State,
        ProcessingStage Stage,
        double Progress,
        string Detail,
        int CueCount);

    private sealed record PreseededOutput(byte[] OriginalBytes);

    private sealed record OutputDirectoryWriteRestrictionEvidence(
        bool Applied,

        bool WriteDeniedConfirmed,
        bool Restored,
        string? RestorationError);

    private sealed record DiskFullSimulationEvidence(
        bool Enabled,
        string InjectionBoundary,
        int WindowsErrorCode,
        string HResult,
        int AttemptCount,
        IReadOnlyList<DiskFullWriteAttemptEvidence> Attempts);

    private sealed record DiskFullWriteAttemptEvidence(
        string OutputPath,
        TranscriptCommitMode CommitMode,
        int InputCueCount,
        int FormattedCueCount,
        int FormattedUtf8ByteCount);

    private sealed record OutputEvidence(
        string RelativeInputPath,
        string OutputPath,
        bool Exists,
        bool HasWebVttHeader,
        bool IsHeaderOnly,
        bool WasPreseeded,
        bool PreseededContentPreserved,
        bool WasMarkedReadOnly,
        bool ReadOnlyAttributePresent,
        long? Length,
        string? Sha256,
        string? EvidenceError);
    private sealed record RepeatabilityEvidence(
        string RelativeInputPath,
        string CurrentOutputPath,
        string ComparisonOutputPath,
        bool CurrentOutputExists,
        bool ComparisonOutputExists,
        bool ComparisonHasWebVttHeader,
        bool ByteIdentical,
        string? ComparisonSha256,
        string? EvidenceError);

    private sealed record ProbeSummary(
        bool Passed,
        int DiscoveredFiles,
        int CompletedJobs,
        int FailedJobs,
        int CancelledJobs,
        IReadOnlyList<string> Failures);
}
