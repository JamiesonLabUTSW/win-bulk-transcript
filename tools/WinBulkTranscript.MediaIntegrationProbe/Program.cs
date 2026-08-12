using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinBulkTranscript.App.Media;
using WinBulkTranscript.Core.Domain;

namespace WinBulkTranscript.MediaIntegrationProbe;

/// <summary>
/// Runs opt-in Windows media integration evidence through the production extractor.
/// </summary>
internal static class Program
{
    private const string TemporaryDirectoryName = "WinBulkTranscript";
    private static readonly TimeSpan DeferredCleanupSettlementTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeferredCleanupPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Runs one or more real-MP4 extraction cases and writes optional machine-readable evidence.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>A process exit code.</returns>
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

            var cases = BuildCases(options);
            var reportPath = ValidateReportPath(options, cases);
            var results = new List<ExtractionCaseResult>(cases.Count);
            foreach (var probeCase in cases)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Console.WriteLine($"[{results.Count + 1}/{cases.Count}] {probeCase.Id}");
                results.Add(await RunCaseAsync(probeCase, cancellation.Token).ConfigureAwait(false));
            }

            var report = new MediaIntegrationReport(
                DateTimeOffset.UtcNow,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                GetTemporaryDirectory(),
                "This probe calls WindowsMediaAudioExtractor only. It creates no final .vtt files and cannot prove coordinator/WebVTT commit behavior.",
                results,
                CreateSummary(results));
            var json = JsonSerializer.Serialize(report, ReportJsonOptions);
            Console.WriteLine(json);

            if (reportPath is not null)
            {
                await WriteReportAsync(reportPath, json, cancellation.Token).ConfigureAwait(false);
                Console.WriteLine($"Evidence report: {reportPath}");
            }

            return report.Summary.Failed == 0
                ? 0
                : report.Summary.Missing > 0
                    ? 3
                    : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Windows media integration probe cancelled.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Windows media integration probe failed: {exception.Message}");
            return 1;
        }
    }

    private static List<ProbeCase> BuildCases(ProbeOptions options)
    {
        var cases = new List<ProbeCase>();
        var sequence = 0;

        foreach (var inputPath in options.InputPaths)
        {
            sequence++;
            cases.Add(new ProbeCase(
                $"input-{sequence:D2}:{Path.GetFileName(inputPath)}",
                inputPath,
                ExpectedExtractionOutcome.Success,
                RequireAudioData: true,
                "explicit input"));
        }

        if (options.CorpusRoot is not null)
        {
            var corpusRoot = Path.GetFullPath(options.CorpusRoot);
            if (!Directory.Exists(corpusRoot))
            {
                throw new DirectoryNotFoundException($"The corpus directory '{corpusRoot}' does not exist.");
            }

            var corpusFiles = Directory
                .EnumerateFiles(corpusRoot, "*.mp4", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = false,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MatchCasing = MatchCasing.CaseInsensitive,
                })
                .OrderBy(path => Path.GetRelativePath(corpusRoot, path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (corpusFiles.Length == 0)
            {
                throw new InvalidOperationException($"The corpus directory '{corpusRoot}' contains no MP4 files.");
            }

            foreach (var inputPath in corpusFiles)
            {
                cases.Add(new ProbeCase(
                    $"corpus:{Path.GetRelativePath(corpusRoot, inputPath)}",
                    inputPath,
                    ExpectedExtractionOutcome.Success,
                    RequireAudioData: true,
                    "valid corpus"));
            }
        }

        if (options.MatrixRoot is not null)
        {
            var matrixRoot = Path.GetFullPath(options.MatrixRoot);
            foreach (var fixture in MatrixFixtures)
            {
                cases.Add(new ProbeCase(
                    $"matrix:{fixture.FileName}",
                    Path.Combine(matrixRoot, fixture.FileName),
                    fixture.ExpectedOutcome,
                    fixture.RequireAudioData,
                    "media failure-fixture matrix")
                {
                    RequiredDiagnosticTerms = fixture.RequiredDiagnosticTerms,
                });
            }
        }

        if (options.CancellationInputPath is not null)
        {
            var cancellationSource = options.CancellationBoundary is { } boundary
                ? $"test-only lifecycle cancellation at {boundary}"
                : "delayed true-in-flight cancellation after extraction progress";
            cases.Add(new ProbeCase(
                $"cancellation:{Path.GetFileName(options.CancellationInputPath)}",
                options.CancellationInputPath,
                ExpectedExtractionOutcome.Cancellation,
                RequireAudioData: false,
                cancellationSource,
                options.CancellationAfterMilliseconds)
            {
                CancellationBoundary = options.CancellationBoundary,
            });
        }

        if (cases.Count == 0)
        {
            throw new InvalidOperationException("Specify --input, --corpus, --matrix-root, or --cancel-input.");
        }

        return cases;
    }

    private static string? ValidateReportPath(ProbeOptions options, IReadOnlyList<ProbeCase> cases)
    {
        if (options.ReportPath is null)
        {
            return null;
        }

        var reportPath = options.ReportPath;
        if (Directory.Exists(reportPath))
        {
            throw new IOException($"The evidence report path '{reportPath}' is an existing directory.");
        }

        if (File.Exists(reportPath))
        {
            throw new IOException($"Refusing to overwrite an existing evidence report path: '{reportPath}'.");
        }

        if (cases.Any(probeCase => string.Equals(probeCase.InputPath, reportPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The evidence report path must not replace a requested media input.");
        }

        if ((options.CorpusRoot is not null && IsPathWithinDirectory(reportPath, options.CorpusRoot))
            || (options.MatrixRoot is not null && IsPathWithinDirectory(reportPath, options.MatrixRoot)))
        {
            throw new ArgumentException("The evidence report path must be outside supplied corpus and matrix roots.");
        }

        return reportPath;
    }

    private static bool IsPathWithinDirectory(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path);
        var normalizedRoot = Path.GetFullPath(root);
        var rootWithSeparator = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteReportAsync(string reportPath, string json, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(reportPath)
            ?? throw new InvalidOperationException("The evidence report path must have a containing directory.");
        Directory.CreateDirectory(directory);
        cancellationToken.ThrowIfCancellationRequested();

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(reportPath)}.{Guid.NewGuid():N}.tmp");
        var ownsTemporaryPath = false;
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                32 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                ownsTemporaryPath = true;
                await using var writer = new StreamWriter(
                    stream,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    32 * 1024,
                    leaveOpen: true);
                await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, reportPath, overwrite: false);
        }
        finally
        {
            if (ownsTemporaryPath && File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // A failed cleanup affects only the probe-owned report temporary file.
                }
                catch (UnauthorizedAccessException)
                {
                    // See the cleanup comment above.
                }
            }
        }
    }

    private static async Task<ExtractionCaseResult> RunCaseAsync(ProbeCase probeCase, CancellationToken processCancellationToken)
    {
        var temporaryFilesBefore = TemporaryFileSnapshot.Capture();
        if (!File.Exists(probeCase.InputPath))
        {
            var missingSettlement = await TemporaryFileSnapshot
                .WaitForNewOwnedArtifactsToSettleAsync(temporaryFilesBefore).ConfigureAwait(false);
            return new ExtractionCaseResult(
                probeCase.Id,
                probeCase.Source,
                probeCase.InputPath,
                probeCase.ExpectedOutcome,
                ObservedExtractionOutcome.Missing,
                false,
                null,
                null,
                null,
                CreateCleanupEvidence(temporaryFilesBefore, missingSettlement, null),
                false,
                false,
                false,
                null,
                0)
            {
                RequiredDiagnosticTerms = probeCase.RequiredDiagnosticTerms,
                RequestedCancellationBoundary = probeCase.CancellationBoundary,
            };
        }

        using var caseCancellation = CancellationTokenSource.CreateLinkedTokenSource(processCancellationToken);
        var stopwatch = Stopwatch.StartNew();
        double? lastProgress = null;
        var extractionProgressObserved = false;
        var cancellationArmedAfterProgress = false;
        var cancellationRequestedAfterProgress = false;
        var extractionProgressSignal = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundaryObserver = probeCase.CancellationBoundary is { } cancellationBoundary
            ? new BoundaryCancellationObserver(cancellationBoundary)
            : null;
        MediaExtractionLifecycleCheckpoint? observedCancellationBoundary = null;
        var cancellationRequestedAtBoundary = false;
        PcmValidation? pcm = null;
        string? ownedTemporaryPath = null;
        Exception? failure = null;
        var observedOutcome = ObservedExtractionOutcome.Failed;

        try
        {
            var progress = new InlineProgress(fraction =>
            {
                if (double.IsFinite(fraction))
                {
                    lastProgress = Math.Clamp(fraction, 0, 1);
                    extractionProgressObserved = true;
                    extractionProgressSignal.TrySetResult(lastProgress.Value);
                }
            });
            var extractor = boundaryObserver is null
                ? new WindowsMediaAudioExtractor()
                : new WindowsMediaAudioExtractor(boundaryObserver);
            var extractionTask = extractor.ExtractAsync(probeCase.InputPath, progress, caseCancellation.Token);
            if (probeCase.ExpectedOutcome == ExpectedExtractionOutcome.Cancellation)
            {
                if (boundaryObserver is not null)
                {
                    var firstCompletedTask = await Task.WhenAny(extractionTask, boundaryObserver.Reached).ConfigureAwait(false);
                    if (firstCompletedTask == boundaryObserver.Reached && !extractionTask.IsCompleted)
                    {
                        observedCancellationBoundary = await boundaryObserver.Reached.ConfigureAwait(false);
                        if (!caseCancellation.IsCancellationRequested)
                        {
                            cancellationRequestedAtBoundary = true;
                            caseCancellation.Cancel();
                        }
                    }
                }
                else
                {
                    var firstCompletedTask = await Task.WhenAny(extractionTask, extractionProgressSignal.Task).ConfigureAwait(false);
                    if (firstCompletedTask == extractionProgressSignal.Task && !extractionTask.IsCompleted)
                    {
                        cancellationArmedAfterProgress = true;
                        await Task.Delay(probeCase.CancellationAfterMilliseconds!.Value, processCancellationToken).ConfigureAwait(false);
                        if (!extractionTask.IsCompleted && !caseCancellation.IsCancellationRequested)
                        {
                            cancellationRequestedAfterProgress = true;
                            caseCancellation.Cancel();
                        }
                    }
                }
            }

            await using (var temporaryWave = await extractionTask.ConfigureAwait(false))
            {
                ownedTemporaryPath = temporaryWave.WaveFile.Path;
                pcm = ValidatePcm(temporaryWave.WaveFile);
            }

            observedOutcome = ObservedExtractionOutcome.Succeeded;
        }
        catch (OperationCanceledException exception) when (processCancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(exception.Message, exception, processCancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            failure = exception;
            observedOutcome = ObservedExtractionOutcome.Cancelled;
        }
        catch (Exception exception)
        {
            failure = exception;
            observedOutcome = ObservedExtractionOutcome.Failed;
        }
        finally
        {
            stopwatch.Stop();
        }

        var temporarySettlement = await TemporaryFileSnapshot.WaitForNewOwnedArtifactsToSettleAsync(temporaryFilesBefore).ConfigureAwait(false);
        var cleanup = CreateCleanupEvidence(temporaryFilesBefore, temporarySettlement, ownedTemporaryPath);
        var passed = IsPassing(
            probeCase,
            observedOutcome,
            failure,
            pcm,
            cleanup,
            extractionProgressObserved,
            cancellationArmedAfterProgress,
            cancellationRequestedAfterProgress,
            observedCancellationBoundary,
            cancellationRequestedAtBoundary);

        return new ExtractionCaseResult(
            probeCase.Id,
            probeCase.Source,
            probeCase.InputPath,
            probeCase.ExpectedOutcome,
            observedOutcome,
            passed,
            failure?.GetType().FullName,
            failure?.Message,
            pcm,
            cleanup,
            extractionProgressObserved,
            cancellationArmedAfterProgress,
            cancellationRequestedAfterProgress,
            lastProgress,
            stopwatch.ElapsedMilliseconds)
        {
            RequiredDiagnosticTerms = probeCase.RequiredDiagnosticTerms,
            RequestedCancellationBoundary = probeCase.CancellationBoundary,
            ObservedCancellationBoundary = observedCancellationBoundary,
            CancellationRequestedAtBoundary = cancellationRequestedAtBoundary,
        };
    }

    private static PcmValidation ValidatePcm(PcmWaveFile waveFile)
    {
        ArgumentNullException.ThrowIfNull(waveFile);
        var format = waveFile.Format;
        return new PcmValidation(
            format.SampleRate,
            format.Channels,
            format.BitsPerSample,
            format.BlockAlign,
            waveFile.DataLength,
            waveFile.SampleCount,
            format.IsRequired,
            waveFile.DataLength > 0,
            waveFile.DataLength % format.BlockAlign == 0);
    }

    private static bool IsPassing(
        ProbeCase probeCase,
        ObservedExtractionOutcome observedOutcome,
        Exception? failure,
        PcmValidation? pcm,
        TemporaryCleanupEvidence cleanup,
        bool extractionProgressObserved,
        bool cancellationArmedAfterProgress,
        bool cancellationRequestedAfterProgress,
        MediaExtractionLifecycleCheckpoint? observedCancellationBoundary,
        bool cancellationRequestedAtBoundary)
    {
        if (!cleanup.Verified
            || !cleanup.DeferredCleanupSettled
            || !cleanup.NoNewTemporaryWaveFiles
            || !cleanup.NoNewTemporaryInputStageFiles
            || !cleanup.OwnedTemporaryFileDeleted)
        {
            return false;
        }

        return probeCase.ExpectedOutcome switch
        {
            ExpectedExtractionOutcome.Success => observedOutcome == ObservedExtractionOutcome.Succeeded
                && IsValidPcm(pcm, probeCase.RequireAudioData),
            ExpectedExtractionOutcome.Failure => observedOutcome == ObservedExtractionOutcome.Failed
                && IsActionableMediaFailure(failure, probeCase.RequiredDiagnosticTerms),
            ExpectedExtractionOutcome.FailureOrHeaderOnlySuccess =>
                (observedOutcome == ObservedExtractionOutcome.Failed
                    && IsActionableMediaFailure(failure, probeCase.RequiredDiagnosticTerms))
                || (observedOutcome == ObservedExtractionOutcome.Succeeded && IsValidPcm(pcm, requireAudioData: false) && !pcm!.HasAudioData),
            ExpectedExtractionOutcome.Cancellation => IsCancellationPassing(
                probeCase,
                observedOutcome,
                extractionProgressObserved,
                cancellationArmedAfterProgress,
                cancellationRequestedAfterProgress,
                observedCancellationBoundary,
                cancellationRequestedAtBoundary),
            _ => false,
        };
    }

    private static bool IsCancellationPassing(
        ProbeCase probeCase,
        ObservedExtractionOutcome observedOutcome,
        bool extractionProgressObserved,
        bool cancellationArmedAfterProgress,
        bool cancellationRequestedAfterProgress,
        MediaExtractionLifecycleCheckpoint? observedCancellationBoundary,
        bool cancellationRequestedAtBoundary)
    {
        if (probeCase.CancellationBoundary is { } requestedBoundary)
        {
            return observedOutcome == ObservedExtractionOutcome.Cancelled
                && observedCancellationBoundary == requestedBoundary
                && cancellationRequestedAtBoundary;
        }

        return observedOutcome == ObservedExtractionOutcome.Cancelled
            && extractionProgressObserved
            && cancellationArmedAfterProgress
            && cancellationRequestedAfterProgress;
    }

    private static bool IsValidPcm(PcmValidation? pcm, bool requireAudioData)
        => pcm is not null
            && pcm.IsRequiredFormat
            && pcm.IsDataBlockAligned
            && (!requireAudioData || pcm.HasAudioData);

    private static bool IsActionableMediaFailure(
        Exception? failure,
        IReadOnlyList<string> requiredDiagnosticTerms)
    {
        ArgumentNullException.ThrowIfNull(requiredDiagnosticTerms);
        if (failure is not MediaExtractionException || string.IsNullOrWhiteSpace(failure.Message))
        {
            return false;
        }

        return requiredDiagnosticTerms.All(term =>
            failure.Message.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static TemporaryCleanupEvidence CreateCleanupEvidence(
        TemporaryFileSnapshot before,
        TemporaryWaveCleanupSettlement settlement,
        string? ownedTemporaryPath)
    {
        var after = settlement.Snapshot;
        var snapshotsAvailable = before.IsAvailable && after.IsAvailable;
        var newWaveFiles = snapshotsAvailable
            ? after.WavePaths.Except(before.WavePaths, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();
        var newInputStageFiles = snapshotsAvailable
            ? after.InputStagePaths.Except(before.InputStagePaths, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();
        var ownedTemporaryFileDeleted = ownedTemporaryPath is null || !File.Exists(ownedTemporaryPath);
        return new TemporaryCleanupEvidence(
            GetTemporaryDirectory(),
            snapshotsAvailable,
            before.WavePaths.Count,
            after.WavePaths.Count,
            newWaveFiles,
            newWaveFiles.Length == 0,
            before.InputStagePaths.Count,
            after.InputStagePaths.Count,
            newInputStageFiles,
            newInputStageFiles.Length == 0,
            settlement.Settled,
            settlement.WaitMilliseconds,
            ownedTemporaryPath,
            ownedTemporaryFileDeleted,
            before.Error ?? after.Error);
    }

    private static ProbeSummary CreateSummary(IReadOnlyList<ExtractionCaseResult> results)
    {
        var passed = results.Count(result => result.Passed);
        var missing = results.Count(result => result.ObservedOutcome == ObservedExtractionOutcome.Missing);
        return new ProbeSummary(results.Count, passed, results.Count - passed, missing);
    }

    private static string GetTemporaryDirectory()
        => Path.Combine(Path.GetTempPath(), TemporaryDirectoryName);

    private static readonly MatrixFixture[] MatrixFixtures =
    [
        new("malformed-truncated.mp4", ExpectedExtractionOutcome.Failure, RequireAudioData: false, ["unreadable", "corrupt"]),
        new("no-audio-video-only.mp4", ExpectedExtractionOutcome.Failure, RequireAudioData: false, ["no usable audio"]),
        new("empty-audio-track.mp4", ExpectedExtractionOutcome.FailureOrHeaderOnlySuccess, RequireAudioData: false, ["zero", "audio"]),
        new("unsupported-audio-codec.mp4", ExpectedExtractionOutcome.Failure, RequireAudioData: false, ["unsupported", "codec"]),
        new("valid-short-control.mp4", ExpectedExtractionOutcome.Success, RequireAudioData: true, []),
    ];

    private sealed record ProbeCase(
        string Id,
        string InputPath,
        ExpectedExtractionOutcome ExpectedOutcome,
        bool RequireAudioData,
        string Source,
        int? CancellationAfterMilliseconds = null)
    {
        public IReadOnlyList<string> RequiredDiagnosticTerms { get; init; } = Array.Empty<string>();

        public MediaExtractionLifecycleCheckpoint? CancellationBoundary { get; init; }
    }

    private sealed record MatrixFixture(
        string FileName,
        ExpectedExtractionOutcome ExpectedOutcome,
        bool RequireAudioData,
        IReadOnlyList<string> RequiredDiagnosticTerms);

    private sealed record MediaIntegrationReport(
        DateTimeOffset GeneratedAtUtc,
        string OperatingSystem,
        string ProcessArchitecture,
        string TemporaryWaveDirectory,
        string FinalVttResponsibility,
        IReadOnlyList<ExtractionCaseResult> Cases,
        ProbeSummary Summary);

    private sealed record ExtractionCaseResult(
        string Id,
        string Source,
        string InputPath,
        ExpectedExtractionOutcome ExpectedOutcome,
        ObservedExtractionOutcome ObservedOutcome,
        bool Passed,
        string? ErrorType,
        string? ErrorMessage,
        PcmValidation? Pcm,
        TemporaryCleanupEvidence Cleanup,
        bool ExtractionProgressObserved,
        bool CancellationArmedAfterProgress,
        bool CancellationRequestedAfterProgress,
        double? LastProgress,
        long ElapsedMilliseconds)
    {
        public IReadOnlyList<string> RequiredDiagnosticTerms { get; init; } = Array.Empty<string>();

        public MediaExtractionLifecycleCheckpoint? RequestedCancellationBoundary { get; init; }

        public MediaExtractionLifecycleCheckpoint? ObservedCancellationBoundary { get; init; }

        public bool CancellationRequestedAtBoundary { get; init; }
    }

    private sealed record PcmValidation(
        int SampleRate,
        short Channels,
        short BitsPerSample,
        short BlockAlign,
        long DataLengthBytes,
        long SampleCount,
        bool IsRequiredFormat,
        bool HasAudioData,
        bool IsDataBlockAligned);

    private sealed record TemporaryCleanupEvidence(
        string Directory,
        bool Verified,
        int TemporaryWaveCountBefore,
        int TemporaryWaveCountAfter,
        IReadOnlyList<string> NewTemporaryWavePathsAfter,
        bool NoNewTemporaryWaveFiles,
        int TemporaryInputStageCountBefore,
        int TemporaryInputStageCountAfter,
        IReadOnlyList<string> NewTemporaryInputStagePathsAfter,
        bool NoNewTemporaryInputStageFiles,
        bool DeferredCleanupSettled,
        long DeferredCleanupWaitMilliseconds,
        string? OwnedTemporaryWavePath,
        bool OwnedTemporaryFileDeleted,
        string? SnapshotError);

    private sealed record ProbeSummary(int Total, int Passed, int Failed, int Missing);

    private sealed record TemporaryFileSnapshot(
        IReadOnlySet<string> WavePaths,
        IReadOnlySet<string> InputStagePaths,
        bool IsAvailable,
        string? Error)
    {
        public static async Task<TemporaryWaveCleanupSettlement> WaitForNewOwnedArtifactsToSettleAsync(TemporaryFileSnapshot before)
        {
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                var snapshot = Capture();
                if (!before.IsAvailable || !snapshot.IsAvailable)
                {
                    return new TemporaryWaveCleanupSettlement(snapshot, false, stopwatch.ElapsedMilliseconds);
                }

                var hasNewWave = snapshot.WavePaths.Except(before.WavePaths, StringComparer.OrdinalIgnoreCase).Any();
                var hasNewInputStage = snapshot.InputStagePaths.Except(before.InputStagePaths, StringComparer.OrdinalIgnoreCase).Any();
                if (!hasNewWave && !hasNewInputStage)
                {
                    return new TemporaryWaveCleanupSettlement(snapshot, true, stopwatch.ElapsedMilliseconds);
                }

                var remaining = DeferredCleanupSettlementTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return new TemporaryWaveCleanupSettlement(snapshot, false, stopwatch.ElapsedMilliseconds);
                }

                await Task.Delay(
                    remaining < DeferredCleanupPollInterval ? remaining : DeferredCleanupPollInterval,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        public static TemporaryFileSnapshot Capture()
        {
            try
            {
                var directory = GetTemporaryDirectory();
                if (!Directory.Exists(directory))
                {
                    return new TemporaryFileSnapshot(
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        true,
                        null);
                }

                var paths = Directory
                    .EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFullPath)
                    .ToArray();
                var wavePaths = paths
                    .Where(WindowsMediaAudioExtractor.IsOwnedTemporaryWavePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var inputStagePaths = paths
                    .Where(WindowsMediaAudioExtractor.IsOwnedTemporaryInputPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return new TemporaryFileSnapshot(wavePaths, inputStagePaths, true, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new TemporaryFileSnapshot(
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    false,
                    exception.Message);
            }
        }
    }

    private sealed record TemporaryWaveCleanupSettlement(TemporaryFileSnapshot Snapshot, bool Settled, long WaitMilliseconds);

    private sealed class BoundaryCancellationObserver(
        MediaExtractionLifecycleCheckpoint expectedBoundary) : IWindowsMediaAudioExtractorTestLifecycleObserver
    {
        private readonly TaskCompletionSource<MediaExtractionLifecycleCheckpoint> reached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MediaExtractionLifecycleCheckpoint> Reached => reached.Task;

        public Task OnCheckpointAsync(
            MediaExtractionLifecycleCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            if (checkpoint != expectedBoundary)
            {
                return Task.CompletedTask;
            }

            reached.TrySetResult(checkpoint);
            // Hold exactly at the named production lifecycle boundary until the probe cancels.
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private enum ExpectedExtractionOutcome
    {
        Success,
        Failure,
        FailureOrHeaderOnlySuccess,
        Cancellation,
    }

    private enum ObservedExtractionOutcome
    {
        Missing,
        Succeeded,
        Failed,
        Cancelled,
    }
}

/// <summary>Parsed command-line options for the Windows media integration probe.</summary>
internal sealed record ProbeOptions(
    IReadOnlyList<string> InputPaths,
    string? CorpusRoot,
    string? MatrixRoot,
    string? CancellationInputPath,
    int? CancellationAfterMilliseconds,
    string? ReportPath,
    bool ShowHelp)
{
    public MediaExtractionLifecycleCheckpoint? CancellationBoundary { get; init; }

    public const string Usage = """
        WinBulkTranscript.MediaIntegrationProbe

        Calls the production WindowsMediaAudioExtractor against real MP4 fixtures. This is opt-in
        evidence code for Windows 11 24H2; it is deliberately outside the product solution.

        Usage:
          dotnet run --project tools/WinBulkTranscript.MediaIntegrationProbe -- [options]

        Options:
          --input <path>                 Validate one expected-success MP4 (repeatable)
          --corpus <directory>           Recursively validate expected-success MP4 fixtures
          --matrix-root <directory>      Exercise the five named files in media-fixture-matrix.md
          --cancel-input <path>          Require cancellation while extracting this valid MP4
          --cancel-after-ms <positive>   Cancel after extraction progress; mutually exclusive with --cancel-at-boundary
          --cancel-at-boundary <point>   Cancel --cancel-input at prepare, transcode, or validation (test-only)
          --report <path>                Atomically create a JSON evidence report; never overwrites
          --help                         Show this help

        Matrix files are not included in this repository. Missing files are reported individually
        and produce exit code 3. The probe verifies temporary WAV ownership/cleanup only; it never
        invokes the batch coordinator or creates a final WebVTT file.
        """;

    public static ProbeOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var inputs = new List<string>();
        string? corpusRoot = null;
        string? matrixRoot = null;
        string? cancellationInputPath = null;
        int? cancellationAfterMilliseconds = null;
        MediaExtractionLifecycleCheckpoint? cancellationBoundary = null;
        string? reportPath = null;
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--help" or "-h" or "/?":
                    showHelp = true;
                    break;
                case "--input":
                    inputs.Add(ReadValue(args, ref index, "--input"));
                    break;
                case "--corpus":
                    corpusRoot = ReadValue(args, ref index, "--corpus");
                    break;
                case "--matrix-root":
                    matrixRoot = ReadValue(args, ref index, "--matrix-root");
                    break;
                case "--cancel-input":
                    cancellationInputPath = ReadValue(args, ref index, "--cancel-input");
                    break;
                case "--cancel-after-ms":
                    cancellationAfterMilliseconds = ParsePositiveInteger(ReadValue(args, ref index, "--cancel-after-ms"), "--cancel-after-ms");
                    break;
                case "--cancel-at-boundary":
                    cancellationBoundary = ParseCancellationBoundary(ReadValue(args, ref index, "--cancel-at-boundary"));
                    break;
                case "--report":
                    reportPath = ReadValue(args, ref index, "--report");
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[index]}'.{Environment.NewLine}{Usage}");
            }
        }

        if (!showHelp)
        {
            var hasCancellationInput = cancellationInputPath is not null;
            var hasDelayedCancellation = cancellationAfterMilliseconds is not null;
            var hasBoundaryCancellation = cancellationBoundary is not null;

            if (!hasCancellationInput && (hasDelayedCancellation || hasBoundaryCancellation))
            {
                throw new ArgumentException("--cancel-after-ms and --cancel-at-boundary both require --cancel-input.");
            }

            if (hasCancellationInput && hasDelayedCancellation == hasBoundaryCancellation)
            {
                throw new ArgumentException("--cancel-input requires exactly one of --cancel-after-ms or --cancel-at-boundary.");
            }
        }

        return new ProbeOptions(
            inputs.Select(Path.GetFullPath).ToArray(),
            corpusRoot is null ? null : Path.GetFullPath(corpusRoot),
            matrixRoot is null ? null : Path.GetFullPath(matrixRoot),
            cancellationInputPath is null ? null : Path.GetFullPath(cancellationInputPath),
            cancellationAfterMilliseconds,
            reportPath is null ? null : Path.GetFullPath(reportPath),
            showHelp)
        {
            CancellationBoundary = cancellationBoundary,
        };
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return args[index];
    }

    private static MediaExtractionLifecycleCheckpoint ParseCancellationBoundary(string value)
        => value.ToLowerInvariant() switch
        {
            "prepare" => MediaExtractionLifecycleCheckpoint.Prepare,
            "transcode" => MediaExtractionLifecycleCheckpoint.Transcode,
            "validation" => MediaExtractionLifecycleCheckpoint.Validation,
            _ => throw new ArgumentException("--cancel-at-boundary must be prepare, transcode, or validation."),
        };

    private static int ParsePositiveInteger(string value, string option)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"{option} must be a positive integer number of milliseconds.");
        }

        return parsed;
    }
}
