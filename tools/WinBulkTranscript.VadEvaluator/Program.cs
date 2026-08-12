using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinBulkTranscript.App.Media;
using WinBulkTranscript.Core.Audio;
using WinBulkTranscript.Core.Domain;
using WinBulkTranscript.Core.Wave;

namespace WinBulkTranscript.VadEvaluator;

/// <summary>Runs opt-in corpus VAD measurements through the production implementation.</summary>
internal static class Program
{
    private const int ReportSchemaVersion = 3;
    private const int MinimumSupportedManifestSchemaVersion = 2;
    private const int MaximumSupportedManifestSchemaVersion = 3;
    private const string ToolVersion = "1.0";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>Runs the requested production VAD corpus evaluation.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>A process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = EvaluationOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(EvaluationOptions.Usage);
                return 0;
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            var manifestSnapshot = await ReadManifestSnapshotAsync(options.ManifestPath!, cancellation.Token).ConfigureAwait(false);
            var manifest = manifestSnapshot.Manifest;
            ValidateManifest(manifest);
            var inputs = await BuildFixtureInputsAsync(options, manifest, cancellation.Token).ConfigureAwait(false);
            EnsureReportDestination(options, manifest, inputs);

            var vadOptions = new AdaptiveEnergyVadOptions();
            var extractor = options.InputMode == EvaluationInputMode.EncodedMedia
                ? new WindowsMediaAudioExtractor()
                : null;
            if (extractor is not null)
            {
                WindowsMediaAudioExtractor.CleanupStaleTemporaryFiles();
            }

            var evaluations = new List<FixtureEvaluation>(inputs.Count);

            foreach (var input in inputs)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Console.WriteLine($"[{evaluations.Count + 1}/{inputs.Count}] {input.Fixture.FixtureId}");
                evaluations.Add(await EvaluateFixtureAsync(
                    input,
                    manifest.VadTimingToleranceMilliseconds,
                    vadOptions,
                    extractor,
                    cancellation.Token).ConfigureAwait(false));
            }

            var report = CreateReport(options, manifest, manifestSnapshot.Sha256, vadOptions, evaluations);

            var json = JsonSerializer.Serialize(report, ReportJsonOptions);
            cancellation.Token.ThrowIfCancellationRequested();
            await WriteReportAsync(options.ReportPath!, json, options.Overwrite, cancellation.Token).ConfigureAwait(false);
            if (report.QualityMetricsAggregate is { } qualityMetrics)
            {
                Console.WriteLine(FormattableString.Invariant(
                    $"Synthesis-chunk quality evidence report written: {options.ReportPath} (missed speech {qualityMetrics.MissedSpeechPercent:F2}%, false positive {qualityMetrics.FalsePositivePercentOfDetected:F2}%, utterance recall {qualityMetrics.UtteranceRecallPercent:F2}%). Phase 2 quality gate remains pending."));
            }
            else
            {
                Console.WriteLine($"Encoded-media diagnostic report written: {options.ReportPath}. No cross-timeline quality metrics were calculated.");
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("VAD corpus evaluation cancelled.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"VAD corpus evaluation failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<ManifestSnapshot> ReadManifestSnapshotAsync(string manifestPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("The corpus manifest does not exist.", manifestPath);
        }

        await using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes, 32 * 1024, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var manifestBytes = bytes.ToArray();
        var manifest = JsonSerializer.Deserialize<CorpusManifest>(manifestBytes, ManifestJsonOptions)
            ?? throw new InvalidDataException("The corpus manifest was empty.");
        return new ManifestSnapshot(manifest, Convert.ToHexString(SHA256.HashData(manifestBytes)));
    }

    private static VadEvaluationReport CreateReport(
        EvaluationOptions options,
        CorpusManifest manifest,
        string manifestHash,
        AdaptiveEnergyVadOptions vadOptions,
        IReadOnlyList<FixtureEvaluation> evaluations)
    {
        var isQualityMeasurement = options.InputMode == EvaluationInputMode.RetainedMasterPcm;
        var qualityAggregate = isQualityMeasurement
            ? VadMetricCalculator.Aggregate(evaluations.Select(evaluation => evaluation.QualityMetrics
                ?? throw new InvalidOperationException("A retained-master evaluation did not produce quality metrics.")).ToArray())
            : null;
        var encodedMediaDiagnostics = isQualityMeasurement
            ? null
            : AggregateEncodedMediaDiagnostics(evaluations);
        var flatNestedComparison = options.CompareFlatAndNested
            ? CreateFlatNestedComparison(evaluations)
            : null;
        var sourceLayout = isQualityMeasurement
            ? null
            : options.CompareFlatAndNested
                ? "flat+nested"
                : options.SelectedCorpusLayout!.Value.ToReportValue();

        return new VadEvaluationReport(
            ReportSchemaVersion,
            $"WinBulkTranscript.VadEvaluator/{ToolVersion}",
            isQualityMeasurement
                ? "Development-only production adaptive-energy VAD measurement against hash-bound retained master PCM. Manifest utterance bounds are synthesized TTS chunk bounds, not independently derived acoustic speech boundaries; Phase 2 quality-gate status is pending."
                : "Development-only production media-extraction and VAD diagnostic. Master-PCM expectations and decoded PCM detections are reported separately; no cross-timeline quality metrics are calculated and Phase 2 quality-gate status is pending.",
            new RuntimeProvenance(
                RuntimeInformation.OSArchitecture.ToString(),
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.Is64BitProcess),
            new EvaluationLimitations(
                "synthesizedTtsChunkBounds",
                "pending",
                "Manifest utterance intervals delimit assembled TTS synthesis chunks, which can include SpeechAppendedSilence.Default. Hash binding makes retained-master interval coordinates identical, but does not independently label acoustic speech onset or offset; this report cannot complete the Phase 2 quality gate."),
            new EvaluationSource(
                isQualityMeasurement ? "retainedMasterPcmQualityMetrics" : "productionMediaExtractionDiagnostics",
                sourceLayout,
                isQualityMeasurement
                    ? "Expected and detected intervals use the same verified master PCM sample coordinates."
                    : "Expected intervals use manifest master PCM coordinates; detected intervals use production decoder PCM coordinates and are not intersected or scored against each other."),
            CreateManifestProvenance(manifest, manifestHash),
            ProductionVadDefaults.From(vadOptions),
            evaluations,
            qualityAggregate,
            encodedMediaDiagnostics,
            flatNestedComparison);
    }

    private static EncodedMediaDiagnosticAggregate AggregateEncodedMediaDiagnostics(IReadOnlyList<FixtureEvaluation> evaluations)
    {
        var totalMasterSamples = 0L;
        var totalDecodedSamples = 0L;
        var totalSampleCountDelta = 0L;
        var detectedIntervalCount = 0;
        var atMaximumCount = 0;

        foreach (var evaluation in evaluations)
        {
            var diagnostic = evaluation.EncodedMediaDiagnostics
                ?? throw new InvalidOperationException("An encoded-media evaluation did not produce diagnostics.");
            totalMasterSamples = checked(totalMasterSamples + diagnostic.ManifestMasterDurationSamples);
            totalDecodedSamples = checked(totalDecodedSamples + diagnostic.DecodedPcmSampleCount);
            totalSampleCountDelta = checked(totalSampleCountDelta + diagnostic.SampleCountDeltaFromManifestMaster);
            detectedIntervalCount = checked(detectedIntervalCount + diagnostic.DetectedIntervalCount);
            atMaximumCount = checked(atMaximumCount + diagnostic.DetectedIntervalsAtConfiguredMaximumLength);
        }

        return new EncodedMediaDiagnosticAggregate(
            evaluations.Count,
            totalMasterSamples,
            totalDecodedSamples,
            totalSampleCountDelta,
            detectedIntervalCount,
            atMaximumCount,
            "No missed-speech, false-positive, recall, or boundary aggregate is calculated because the master and decoded PCM timelines are not coordinate-aligned.");
    }

    private static FlatNestedComparison CreateFlatNestedComparison(IReadOnlyList<FixtureEvaluation> evaluations)
    {
        var comparisons = evaluations
            .GroupBy(evaluation => evaluation.FixtureId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var flat = group.SingleOrDefault(evaluation => string.Equals(evaluation.Input.CorpusLayout, "flat", StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"Fixture '{group.Key}' does not have exactly one flat evaluation for paired comparison.");
                var nested = group.SingleOrDefault(evaluation => string.Equals(evaluation.Input.CorpusLayout, "nested", StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"Fixture '{group.Key}' does not have exactly one nested evaluation for paired comparison.");
                if (group.Count() != 2)
                {
                    throw new InvalidOperationException($"Fixture '{group.Key}' has an unexpected number of evaluations for paired comparison.");
                }

                return new FlatNestedFixtureComparison(
                    group.Key,
                    string.Equals(flat.Input.Sha256, nested.Input.Sha256, StringComparison.OrdinalIgnoreCase),
                    string.Equals(flat.EvaluatedPcm.DataSha256, nested.EvaluatedPcm.DataSha256, StringComparison.OrdinalIgnoreCase),
                    flat.EvaluatedPcm.SampleCount == nested.EvaluatedPcm.SampleCount,
                    IntervalsEqual(flat.DetectedSpeechIntervals, nested.DetectedSpeechIntervals));
            })
            .ToArray();

        if (comparisons.Length == 0)
        {
            throw new InvalidOperationException("No encoded-media fixture pairs were available for flat/nested comparison.");
        }

        return new FlatNestedComparison(
            comparisons.Length,
            comparisons.Count(comparison => comparison.EncodedInputSha256Matches),
            comparisons.Count(comparison => comparison.DecodedPcmDataSha256Matches),
            comparisons.Count(comparison => comparison.DecodedPcmSampleCountMatches),
            comparisons.Count(comparison => comparison.DetectedIntervalSequenceMatches),
            comparisons.All(comparison => comparison.EncodedInputSha256Matches
                && comparison.DecodedPcmDataSha256Matches
                && comparison.DecodedPcmSampleCountMatches
                && comparison.DetectedIntervalSequenceMatches),
            comparisons);
    }

    private static bool IntervalsEqual(IReadOnlyList<SampleRange> left, IReadOnlyList<SampleRange> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].StartSample != right[index].StartSample || left[index].EndSample != right[index].EndSample)
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateManifest(CorpusManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion is < MinimumSupportedManifestSchemaVersion or > MaximumSupportedManifestSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported corpus manifest schema version {manifest.SchemaVersion}; expected {MinimumSupportedManifestSchemaVersion}-{MaximumSupportedManifestSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.GeneratorVersion))
        {
            throw new InvalidDataException("The corpus manifest has no generator version.");
        }

        var masterPcm = manifest.MasterPcm
            ?? throw new InvalidDataException("The corpus manifest has no master PCM contract.");
        if (masterPcm.SampleRate != PcmFormat.Required.SampleRate
            || masterPcm.Channels != PcmFormat.Required.Channels
            || masterPcm.BitsPerSample != PcmFormat.Required.BitsPerSample)
        {
            throw new InvalidDataException("The corpus manifest does not use the required PCM16, 16 kHz, mono sample coordinates.");
        }

        if (manifest.VadTimingToleranceMilliseconds < 0)
        {
            throw new InvalidDataException("The corpus manifest has a negative VAD timing tolerance.");
        }

        if (manifest.Fixtures is not { Count: > 0 })
        {
            throw new InvalidDataException("The corpus manifest contains no fixtures.");
        }

        var fixtureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fixture in manifest.Fixtures)
        {
            if (string.IsNullOrWhiteSpace(fixture.FixtureId) || !fixtureIds.Add(fixture.FixtureId))
            {
                throw new InvalidDataException("The corpus manifest contains an empty or duplicate fixture ID.");
            }

            if (fixture.MasterDurationSamples <= 0)
            {
                throw new InvalidDataException($"Fixture '{fixture.FixtureId}' has no positive master PCM duration.");
            }

            if (string.IsNullOrWhiteSpace(fixture.FlatPath)
                || string.IsNullOrWhiteSpace(fixture.NestedRelativePath)
                || !IsSha256(fixture.Sha256)
                || string.IsNullOrWhiteSpace(fixture.ExpectedVttPath)
                || !IsSha256(fixture.MasterPcmDataSha256))
            {
                throw new InvalidDataException($"Fixture '{fixture.FixtureId}' has incomplete media or master-PCM provenance.");
            }

            if (fixture.Utterances is not { Count: > 0 })
            {
                throw new InvalidDataException($"Fixture '{fixture.FixtureId}' has no manifest speech intervals.");
            }

            var expectedIntervals = fixture.Utterances
                .Select(utterance => new SampleRange(utterance.StartSample, utterance.EndSample))
                .ToArray();
            VadMetricCalculator.ValidateIntervals(expectedIntervals, fixture.MasterDurationSamples, $"Manifest fixture '{fixture.FixtureId}' speech");
        }
    }

    private static async Task<IReadOnlyList<FixtureInput>> BuildFixtureInputsAsync(
        EvaluationOptions options,
        CorpusManifest manifest,
        CancellationToken cancellationToken)
    {
        var fixtures = manifest.Fixtures!
            .OrderBy(fixture => fixture.FixtureId, StringComparer.Ordinal)
            .ToArray();
        var inputs = new List<FixtureInput>(fixtures.Length);

        if (options.InputMode == EvaluationInputMode.EncodedMedia)
        {
            var corpusRoot = options.CorpusRoot!;
            if (!Directory.Exists(corpusRoot))
            {
                throw new DirectoryNotFoundException($"The corpus root '{corpusRoot}' does not exist.");
            }

            var requestedLayouts = options.CompareFlatAndNested
                ? new[] { CorpusLayout.Flat, CorpusLayout.Nested }
                : new[] { options.SelectedCorpusLayout!.Value };
            foreach (var fixture in fixtures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var layout in requestedLayouts)
                {
                    var relativePath = layout == CorpusLayout.Flat
                        ? fixture.FlatPath
                        : fixture.NestedRelativePath;
                    var fullPath = ResolvePathUnderRoot(corpusRoot, relativePath, "corpus manifest");
                    if (!File.Exists(fullPath))
                    {
                        throw new FileNotFoundException($"Fixture '{fixture.FixtureId}' is missing its requested {layout.ToReportValue()} media input.", fullPath);
                    }

                    var hash = await ComputeSha256Async(fullPath, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(hash, fixture.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"Fixture '{fixture.FixtureId}' does not match the SHA-256 recorded in the corpus manifest.");
                    }

                    inputs.Add(new FixtureInput(
                        fixture,
                        EvaluationInputMode.EncodedMedia,
                        layout,
                        fullPath,
                        NormalizeRelativePath(relativePath),
                        hash));
                }
            }

            return inputs;
        }

        var pcmRoot = options.PcmRoot!;
        if (!Directory.Exists(pcmRoot))
        {
            throw new DirectoryNotFoundException($"The retained PCM root '{pcmRoot}' does not exist.");
        }

        foreach (var fixture in fixtures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = FindRetainedMasterPcmPath(pcmRoot, fixture.FixtureId);
            var hash = await ComputeSha256Async(fullPath, cancellationToken).ConfigureAwait(false);
            inputs.Add(new FixtureInput(
                fixture,
                EvaluationInputMode.RetainedMasterPcm,
                null,
                fullPath,
                NormalizeRelativePath(Path.GetRelativePath(pcmRoot, fullPath)),
                hash));
        }

        return inputs;
    }

    private static async Task<FixtureEvaluation> EvaluateFixtureAsync(
        FixtureInput input,
        int timingToleranceMilliseconds,
        AdaptiveEnergyVadOptions vadOptions,
        WindowsMediaAudioExtractor? extractor,
        CancellationToken cancellationToken)
    {
        FixtureEvaluation evaluation;
        if (input.Mode == EvaluationInputMode.EncodedMedia)
        {
            if (extractor is null)
            {
                throw new InvalidOperationException("The production extractor was not initialized for an encoded-media evaluation.");
            }

            await using var temporaryWave = await extractor
                .ExtractAsync(input.FullPath, progress: null, cancellationToken)
                .ConfigureAwait(false);
            evaluation = await EvaluateWaveAsync(input, temporaryWave.WaveFile, timingToleranceMilliseconds, vadOptions, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var retainedWave = WaveFileReader.Open(input.FullPath);
            evaluation = await EvaluateWaveAsync(input, retainedWave, timingToleranceMilliseconds, vadOptions, cancellationToken).ConfigureAwait(false);
        }

        var postEvaluationHash = await ComputeSha256Async(input.FullPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(postEvaluationHash, input.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Fixture '{input.Fixture.FixtureId}' changed while it was being evaluated.");
        }

        return evaluation;
    }

    private static async Task<FixtureEvaluation> EvaluateWaveAsync(
        FixtureInput input,
        PcmWaveFile waveFile,
        int timingToleranceMilliseconds,
        AdaptiveEnergyVadOptions vadOptions,
        CancellationToken cancellationToken)
    {
        if (!waveFile.Format.IsRequired)
        {
            throw new InvalidDataException($"Fixture '{input.Fixture.FixtureId}' did not yield PCM16, 16 kHz, mono audio.");
        }

        var pcmDataSha256 = await ComputePcmDataSha256Async(waveFile, cancellationToken).ConfigureAwait(false);

        var detector = new AdaptiveEnergyVoiceActivityDetector(vadOptions);
        var detectedIntervals = (await detector.DetectAsync(waveFile, progress: null, cancellationToken).ConfigureAwait(false))
            .Select(interval => new SampleRange(interval.StartSample, interval.EndSample))
            .ToArray();
        var expectedIntervals = input.Fixture.Utterances!
            .Select(utterance => new SampleRange(utterance.StartSample, utterance.EndSample))
            .ToArray();
        VadMetricCalculator.ValidateIntervals(detectedIntervals, waveFile.SampleCount, $"Production VAD fixture '{input.Fixture.FixtureId}'");

        VadMetricResult? qualityMetrics = null;
        EncodedMediaFixtureDiagnostics? encodedMediaDiagnostics = null;
        if (input.Mode == EvaluationInputMode.RetainedMasterPcm)
        {
            if (waveFile.SampleCount != input.Fixture.MasterDurationSamples)
            {
                throw new InvalidDataException($"Retained master PCM for fixture '{input.Fixture.FixtureId}' has {waveFile.SampleCount} samples; the manifest requires {input.Fixture.MasterDurationSamples}.");
            }

            if (!string.Equals(pcmDataSha256, input.Fixture.MasterPcmDataSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Retained master PCM for fixture '{input.Fixture.FixtureId}' does not match the manifest masterPcmDataSha256.");
            }

            qualityMetrics = VadMetricCalculator.Evaluate(
                expectedIntervals,
                detectedIntervals,
                input.Fixture.MasterDurationSamples,
                waveFile.SampleCount,
                waveFile.Format.SampleRate,
                timingToleranceMilliseconds,
                vadOptions.Segmenter.MaximumSegmentSamples);
        }
        else
        {
            encodedMediaDiagnostics = new EncodedMediaFixtureDiagnostics(
                input.Fixture.MasterDurationSamples,
                waveFile.SampleCount,
                waveFile.SampleCount - input.Fixture.MasterDurationSamples,
                detectedIntervals.Length,
                detectedIntervals.Count(interval => interval.LengthSamples == vadOptions.Segmenter.MaximumSegmentSamples),
                "Master and decoded PCM timelines are reported separately; no cross-timeline quality metrics were calculated.");
        }

        return new FixtureEvaluation(
            input.Fixture.FixtureId,
            new InputEvidence(
                input.Mode == EvaluationInputMode.EncodedMedia ? "encodedMedia" : "retainedMasterPcm",
                input.CorpusLayout?.ToReportValue(),
                input.RelativePath,
                input.Sha256),
            new EvaluatedPcmEvidence(
                waveFile.Format.SampleRate,
                waveFile.Format.Channels,
                waveFile.Format.BitsPerSample,
                waveFile.Format.BlockAlign,
                waveFile.DataLength,
                pcmDataSha256,
                waveFile.SampleCount,
                waveFile.SampleCount - input.Fixture.MasterDurationSamples,
                waveFile.SampleCount * 1_000d / waveFile.Format.SampleRate),
            input.Fixture.MasterDurationSamples,
            input.Fixture.MasterPcmDataSha256,
            expectedIntervals,
            detectedIntervals,
            true,
            qualityMetrics,
            encodedMediaDiagnostics);
    }

    private static ManifestProvenance CreateManifestProvenance(CorpusManifest manifest, string manifestHash)
    {
        var voice = manifest.Voice is null
            ? null
            : new VoiceProvenance(
                NullIfWhiteSpace(manifest.Voice.Id),
                NullIfWhiteSpace(manifest.Voice.DisplayName),
                NullIfWhiteSpace(manifest.Voice.Language),
                NullIfWhiteSpace(manifest.Voice.Gender),
                manifest.Voice.FallbackUsed);
        var masterPcm = manifest.MasterPcm!;
        return new ManifestProvenance(
            manifest.SchemaVersion,
            manifest.GeneratorVersion,
            manifest.RandomSeed,
            NullIfWhiteSpace(manifest.GenerationTimestampUtc),
            manifestHash,
            new MasterPcmProvenance(
                NullIfWhiteSpace(masterPcm.Encoding),
                masterPcm.SampleRate,
                masterPcm.Channels,
                masterPcm.BitsPerSample),
            manifest.VadTimingToleranceMilliseconds,
            voice);
    }

    private static string FindRetainedMasterPcmPath(string pcmRoot, string fixtureId)
    {
        var expectedFileName = $"{fixtureId}.wav";
        var matches = Directory
            .EnumerateFiles(pcmRoot, "*.wav", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MatchCasing = MatchCasing.CaseInsensitive,
            })
            .Where(path => string.Equals(Path.GetFileName(path), expectedFileName, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .OrderBy(path => NormalizeRelativePath(Path.GetRelativePath(pcmRoot, path)), StringComparer.Ordinal)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new FileNotFoundException($"Retained master PCM for fixture '{fixtureId}' was not found. Expected exactly one file named '{expectedFileName}' below '{pcmRoot}'."),
            _ => throw new InvalidDataException($"Retained master PCM for fixture '{fixtureId}' is ambiguous. Expected exactly one file named '{expectedFileName}' below '{pcmRoot}'."),
        };
    }

    private static string ResolvePathUnderRoot(string root, string relativePath, string sourceDescription)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"A {sourceDescription} path must be nonempty and relative.");
        }

        var normalizedRoot = Path.GetFullPath(root);
        var rootWithSeparator = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(rootWithSeparator, normalizedRelativePath));
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The {sourceDescription} path '{relativePath}' escapes its configured root.");
        }

        return fullPath;
    }

    private static void EnsureReportDestination(EvaluationOptions options, CorpusManifest manifest, IReadOnlyList<FixtureInput> inputs)
    {
        var reportPath = options.ReportPath!;
        if (Directory.Exists(reportPath))
        {
            throw new InvalidOperationException($"The report path '{reportPath}' is an existing directory.");
        }

        if (string.Equals(reportPath, options.ManifestPath, StringComparison.OrdinalIgnoreCase)
            || inputs.Any(input => string.Equals(reportPath, input.FullPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The evidence report path must not overwrite the manifest or an evaluated input file.");
        }

        if (options.CorpusRoot is not null && IsPathWithinDirectory(reportPath, options.CorpusRoot))
        {
            throw new InvalidOperationException("The evidence report path must be outside the corpus root so it cannot replace an MP4 or expected VTT artifact.");
        }

        if (options.PcmRoot is not null && IsPathWithinDirectory(reportPath, options.PcmRoot))
        {
            throw new InvalidOperationException("The evidence report path must be outside the retained master PCM root.");
        }

        var manifestDirectory = Path.GetDirectoryName(options.ManifestPath!)
            ?? throw new InvalidOperationException("The corpus manifest must have a parent directory.");
        foreach (var fixture in manifest.Fixtures!)
        {
            var protectedArtifactPaths = new[]
            {
                ResolvePathUnderRoot(manifestDirectory, fixture.FlatPath, "corpus manifest"),
                ResolvePathUnderRoot(manifestDirectory, fixture.NestedRelativePath, "corpus manifest"),
                ResolvePathUnderRoot(manifestDirectory, fixture.ExpectedVttPath, "corpus manifest"),
            };
            if (protectedArtifactPaths.Any(path => string.Equals(path, reportPath, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("The evidence report path must not overwrite a manifest-referenced corpus artifact.");
            }
        }
    }

    private static async Task WriteReportAsync(string reportPath, string json, bool overwrite, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(reportPath) && !overwrite)
        {
            throw new IOException($"The evidence report '{reportPath}' already exists. Re-run with --overwrite to replace it intentionally.");
        }

        var directory = Path.GetDirectoryName(reportPath)
            ?? throw new InvalidOperationException("The evidence report must have a parent directory.");
        Directory.CreateDirectory(directory);
        cancellationToken.ThrowIfCancellationRequested();
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(reportPath)}.{Guid.NewGuid():N}.tmp");
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
                await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 32 * 1024, leaveOpen: true))
                {
                    await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, reportPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // A failed cleanup only affects the evaluator-owned temporary report file.
                }
                catch (UnauthorizedAccessException)
                {
                    // See the cleanup comment above.
                }
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task<string> ComputePcmDataSha256Async(PcmWaveFile waveFile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(waveFile);

        await using var stream = waveFile.OpenRead();
        var dataEnd = checked(waveFile.DataOffset + waveFile.DataLength);
        if (waveFile.DataOffset < 0 || waveFile.DataLength < 0 || dataEnd > stream.Length)
        {
            throw new InvalidDataException("The PCM WAVE data range is invalid while calculating evidence provenance.");
        }

        stream.Position = waveFile.DataOffset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var remaining = waveFile.DataLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min((long)buffer.Length, remaining);
                var read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The PCM WAVE data ended before its declared data length while calculating evidence provenance.");
                }

                hash.AppendData(buffer.AsSpan(0, read));
                remaining -= read;
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
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

    private static bool IsSha256(string? value)
    {
        return value is { Length: 64 } && value.All(Uri.IsHexDigit);
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        return relativePath.Replace('\\', '/');
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

/// <summary>Parsed command-line options for the development-only VAD evidence evaluator.</summary>
internal sealed record EvaluationOptions(
    string? ManifestPath,
    string? CorpusRoot,
    string? PcmRoot,
    CorpusLayout? SelectedCorpusLayout,
    bool CompareFlatAndNested,
    EvaluationInputMode InputMode,
    string? ReportPath,
    bool Overwrite,
    bool ShowHelp)
{
    public const string Usage = """
        WinBulkTranscript VAD corpus evaluator

        Runs the production default AdaptiveEnergyVoiceActivityDetector in one of two explicit
        modes: hash-bound retained master PCM quality measurement, or production encoded-media
        extraction/VAD diagnostics. This development-only Windows tool is deliberately outside
        the product solution.

        Usage:
          dotnet run --project tools/WinBulkTranscript.VadEvaluator -- [options]

        Required options:
          --manifest <path>              Synthetic corpus corpus-manifest.json
          --corpus-root <directory>      Generated corpus root; produces encoded-media diagnostics
            or
          --pcm-root <directory>         Retained master PCM root with hash-matching fixture-###.wav files
          --report <path>                JSON evidence report to create

        Other options:
          --source <flat|nested|both>    Encoded-media layout (default: flat; corpus-root only)
          --overwrite                    Intentionally replace an existing report
          --help                         Show this help

        Corpus mode hashes each manifest-selected MP4, calls the production Windows media
        extractor, then calls the production VAD. It reports decoded PCM hashes, sample counts,
        and raw VAD intervals but never intersects decoded intervals with master-PCM expectations.
        With --source both, it also produces an automatic per-fixture flat/nested comparison of
        encoded input hashes, decoded PCM hashes/sample counts, and returned interval sequences.
        Retained master PCM mode verifies each WAV data hash and sample count against the manifest
        before calculating missed-speech, false-positive, recall, and boundary metrics. Generate
        retained evidence with the corpus generator's --retain-master-pcm option. Its expected
        bounds are synthesized chunks, not independently labeled acoustic speech bounds; the
        Phase 2 quality gate remains pending. Reports must be written outside the corpus and
        retained-PCM roots.
        """;

    /// <summary>Parses supported arguments without silently accepting unknown options.</summary>
    public static EvaluationOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? manifestPath = null;
        string? corpusRoot = null;
        string? pcmRoot = null;
        CorpusLayout? corpusLayout = CorpusLayout.Flat;
        var compareFlatAndNested = false;
        var sourceSpecified = false;
        string? reportPath = null;
        var overwrite = false;
        var showHelp = false;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--manifest":
                    manifestPath = SetOnce(manifestPath, ReadValue(args, ref index, "--manifest"), "--manifest");
                    break;
                case "--corpus-root":
                    corpusRoot = SetOnce(corpusRoot, ReadValue(args, ref index, "--corpus-root"), "--corpus-root");
                    break;
                case "--pcm-root":
                    pcmRoot = SetOnce(pcmRoot, ReadValue(args, ref index, "--pcm-root"), "--pcm-root");
                    break;
                case "--source":
                    if (sourceSpecified)
                    {
                        throw new ArgumentException("Option '--source' can be supplied only once.");
                    }

                    var source = ReadValue(args, ref index, "--source");
                    if (string.Equals(source, "both", StringComparison.OrdinalIgnoreCase))
                    {
                        compareFlatAndNested = true;
                    }
                    else
                    {
                        corpusLayout = ParseCorpusLayout(source);
                    }

                    sourceSpecified = true;
                    break;
                case "--report":
                    reportPath = SetOnce(reportPath, ReadValue(args, ref index, "--report"), "--report");
                    break;
                case "--overwrite":
                    overwrite = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[index]}'.{Environment.NewLine}{Usage}");
            }
        }

        if (showHelp)
        {
            return new EvaluationOptions(null, null, null, null, false, EvaluationInputMode.EncodedMedia, null, false, true);
        }

        if (string.IsNullOrWhiteSpace(manifestPath) || string.IsNullOrWhiteSpace(reportPath))
        {
            throw new ArgumentException($"Options '--manifest' and '--report' are required.{Environment.NewLine}{Usage}");
        }

        if ((corpusRoot is null) == (pcmRoot is null))
        {
            throw new ArgumentException("Specify exactly one of '--corpus-root' or '--pcm-root'.");
        }

        if (pcmRoot is not null && sourceSpecified)
        {
            throw new ArgumentException("Option '--source' is valid only with '--corpus-root'.");
        }

        return new EvaluationOptions(
            Path.GetFullPath(manifestPath),
            corpusRoot is null ? null : Path.GetFullPath(corpusRoot),
            pcmRoot is null ? null : Path.GetFullPath(pcmRoot),
            corpusRoot is null ? null : corpusLayout,
            corpusRoot is not null && compareFlatAndNested,
            corpusRoot is null ? EvaluationInputMode.RetainedMasterPcm : EvaluationInputMode.EncodedMedia,
            Path.GetFullPath(reportPath),
            overwrite,
            false);
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"Option '{option}' requires a value.");
        }

        return args[index];
    }

    private static string SetOnce(string? currentValue, string newValue, string option)
    {
        if (currentValue is not null)
        {
            throw new ArgumentException($"Option '{option}' can be supplied only once.");
        }

        return newValue;
    }

    private static CorpusLayout ParseCorpusLayout(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "flat" => CorpusLayout.Flat,
            "nested" => CorpusLayout.Nested,
            _ => throw new ArgumentException("Option '--source' must be 'flat', 'nested', or 'both'."),
        };
    }
}

/// <summary>Describes the source representation that is evaluated.</summary>
internal enum EvaluationInputMode
{
    EncodedMedia,
    RetainedMasterPcm,
}

/// <summary>Describes a manifest-selected encoded-media layout.</summary>
internal enum CorpusLayout
{
    Flat,
    Nested,
}

internal static class CorpusLayoutExtensions
{
    public static string ToReportValue(this CorpusLayout layout)
    {
        return layout == CorpusLayout.Flat ? "flat" : "nested";
    }
}

internal sealed record FixtureInput(
    FixtureManifest Fixture,
    EvaluationInputMode Mode,
    CorpusLayout? CorpusLayout,
    string FullPath,
    string RelativePath,
    string Sha256);

internal sealed record VadEvaluationReport(
    int ReportSchemaVersion,
    string Tool,
    string EvaluationScope,
    RuntimeProvenance Runtime,
    EvaluationLimitations Limitations,
    EvaluationSource Source,
    ManifestProvenance Manifest,
    ProductionVadDefaults ProductionDefaults,
    IReadOnlyList<FixtureEvaluation> Fixtures,
    AggregateVadMetricResult? QualityMetricsAggregate,
    EncodedMediaDiagnosticAggregate? EncodedMediaDiagnostics,
    FlatNestedComparison? FlatNestedComparison);

internal sealed record RuntimeProvenance(
    string OperatingSystemArchitecture,
    string ProcessArchitecture,
    bool Is64BitProcess);

internal sealed record EvaluationLimitations(
    string GroundTruthBoundaryBasis,
    string Phase2QualityGateStatus,
    string Detail);

internal sealed record EvaluationSource(string Mode, string? CorpusLayout, string CoordinateBasis);

internal sealed record ManifestProvenance(
    int SchemaVersion,
    string GeneratorVersion,
    ulong RandomSeed,
    string? GenerationTimestampUtc,
    string Sha256,
    MasterPcmProvenance MasterPcm,
    int VadTimingToleranceMilliseconds,
    VoiceProvenance? Voice);

internal sealed record MasterPcmProvenance(string? Encoding, int SampleRate, int Channels, int BitsPerSample);

internal sealed record VoiceProvenance(string? Id, string? DisplayName, string? Language, string? Gender, bool FallbackUsed);

internal sealed record ProductionVadDefaults(
    int FrameSamples,
    double InitialNoiseFloorDbfs,
    double MinimumNoiseFloorDbfs,
    double MaximumNoiseFloorDbfs,
    double NoiseFloorAdaptation,
    double OnThresholdAboveNoiseFloorDb,
    double OffThresholdAboveNoiseFloorDb,
    double MinimumOnThresholdDbfs,
    double MinimumOffThresholdDbfs,
    ProductionSegmenterDefaults Segmenter)
{
    public static ProductionVadDefaults From(AdaptiveEnergyVadOptions options)
    {
        var segmenter = options.Segmenter;
        return new ProductionVadDefaults(
            options.FrameSamples,
            options.InitialNoiseFloorDbfs,
            options.MinimumNoiseFloorDbfs,
            options.MaximumNoiseFloorDbfs,
            options.NoiseFloorAdaptation,
            options.OnThresholdAboveNoiseFloorDb,
            options.OffThresholdAboveNoiseFloorDb,
            options.MinimumOnThresholdDbfs,
            options.MinimumOffThresholdDbfs,
            new ProductionSegmenterDefaults(
                segmenter.OnsetFrames,
                segmenter.SilenceFrames,
                segmenter.PreRollSamples,
                segmenter.PostRollSamples,
                segmenter.MinimumSpeechSamples,
                segmenter.MergeGapSamples,
                segmenter.MaximumSegmentSamples,
                segmenter.SplitSearchSamples,
                segmenter.SplitOverlapSamples));
    }
}

internal sealed record ProductionSegmenterDefaults(
    int OnsetFrames,
    int SilenceFrames,
    long PreRollSamples,
    long PostRollSamples,
    long MinimumSpeechSamples,
    long MergeGapSamples,
    long MaximumSegmentSamples,
    long SplitSearchSamples,
    long SplitOverlapSamples);

internal sealed record FixtureEvaluation(
    string FixtureId,
    InputEvidence Input,
    EvaluatedPcmEvidence EvaluatedPcm,
    long ManifestMasterDurationSamples,
    string ManifestMasterPcmDataSha256,
    IReadOnlyList<SampleRange> MasterPcmExpectedSpeechIntervals,
    IReadOnlyList<SampleRange> DetectedSpeechIntervals,
    bool ReturnedIntervalsValidated,
    VadMetricResult? QualityMetrics,
    EncodedMediaFixtureDiagnostics? EncodedMediaDiagnostics);

internal sealed record InputEvidence(string Kind, string? CorpusLayout, string RelativePath, string Sha256);

internal sealed record EvaluatedPcmEvidence(
    int SampleRate,
    short Channels,
    short BitsPerSample,
    short BlockAlign,
    long DataLengthBytes,
    string DataSha256,
    long SampleCount,
    long SampleCountDeltaFromManifestMaster,
    double DurationMilliseconds);

internal sealed record EncodedMediaFixtureDiagnostics(
    long ManifestMasterDurationSamples,
    long DecodedPcmSampleCount,
    long SampleCountDeltaFromManifestMaster,
    int DetectedIntervalCount,
    int DetectedIntervalsAtConfiguredMaximumLength,
    string QualityMetricStatus);

internal sealed record EncodedMediaDiagnosticAggregate(
    int FixtureCount,
    long TotalManifestMasterDurationSamples,
    long TotalDecodedPcmSampleCount,
    long TotalSampleCountDeltaFromManifestMaster,
    int DetectedIntervalCount,
    int DetectedIntervalsAtConfiguredMaximumLength,
    string QualityMetricStatus);

internal sealed record FlatNestedComparison(
    int ComparedFixtureCount,
    int MatchingEncodedInputSha256Count,
    int MatchingDecodedPcmDataSha256Count,
    int MatchingDecodedPcmSampleCountCount,
    int MatchingDetectedIntervalSequenceCount,
    bool AllPairsMatch,
    IReadOnlyList<FlatNestedFixtureComparison> Fixtures);

internal sealed record FlatNestedFixtureComparison(
    string FixtureId,
    bool EncodedInputSha256Matches,
    bool DecodedPcmDataSha256Matches,
    bool DecodedPcmSampleCountMatches,
    bool DetectedIntervalSequenceMatches);

internal sealed record ManifestSnapshot(CorpusManifest Manifest, string Sha256);

/// <summary>Subset of the corpus-manifest contract needed to evaluate speech boundaries.</summary>
internal sealed class CorpusManifest
{
    public int SchemaVersion { get; set; }

    public string GeneratorVersion { get; set; } = string.Empty;

    public ulong RandomSeed { get; set; }

    public string? GenerationTimestampUtc { get; set; }

    public VoiceManifest? Voice { get; set; }

    public PcmManifest? MasterPcm { get; set; }

    public int VadTimingToleranceMilliseconds { get; set; }

    public List<FixtureManifest>? Fixtures { get; set; }
}

internal sealed class VoiceManifest
{
    public string? Id { get; set; }

    public string? DisplayName { get; set; }

    public string? Language { get; set; }

    public string? Gender { get; set; }

    public bool FallbackUsed { get; set; }
}

internal sealed class PcmManifest
{
    public string? Encoding { get; set; }

    public int SampleRate { get; set; }

    public int Channels { get; set; }

    public int BitsPerSample { get; set; }
}

internal sealed class FixtureManifest
{
    public string FixtureId { get; set; } = string.Empty;

    public string FlatPath { get; set; } = string.Empty;

    public string NestedRelativePath { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;
    public string MasterPcmDataSha256 { get; set; } = string.Empty;


    public long MasterDurationSamples { get; set; }

    public List<UtteranceManifest>? Utterances { get; set; }
    public string ExpectedVttPath { get; set; } = string.Empty;

}

internal sealed class UtteranceManifest
{
    public long StartSample { get; set; }

    public long EndSample { get; set; }
}
