using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinBulkTranscript.Core.Output;

namespace WinBulkTranscript.CorpusOutputDiagnostics;

/// <summary>
/// Produces provenance-bound, informational text and cue-overlap diagnostics for an existing
/// synthetic-corpus workflow output tree. It deliberately does not apply a quality threshold.
/// </summary>
internal static class Program
{
    private const int ReportSchemaVersion = 1;
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

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Runs the corpus-output diagnostic.
    /// </summary>
    /// <param name="args">Command-line options.</param>
    /// <returns>A process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = DiagnosticsOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(DiagnosticsOptions.Usage);
                return 0;
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            var manifestSnapshot = await ReadManifestSnapshotAsync(options.ManifestPath!, cancellation.Token).ConfigureAwait(false);
            ValidateManifest(manifestSnapshot.Manifest);
            EnsureReportDestination(options, manifestSnapshot.Manifest);

            var report = await CreateReportAsync(options, manifestSnapshot, cancellation.Token).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(report, ReportJsonOptions);
            cancellation.Token.ThrowIfCancellationRequested();
            await WriteReportAsync(options.ReportPath!, json, options.Overwrite, cancellation.Token).ConfigureAwait(false);

            Console.WriteLine(FormattableString.Invariant(
                $"Informational corpus-output diagnostic written: {options.ReportPath} ({report.Aggregate.WordEditDistance}/{report.Aggregate.ExpectedWordCount} word edits; {report.Aggregate.ProducedCuesWithoutExpectedOverlap} produced cues and {report.Aggregate.ExpectedUtterancesWithoutProducedOverlap} expected utterances without overlap). No text or cue-timing threshold was applied."));
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Corpus-output diagnostic cancelled.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Corpus-output diagnostic failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<ManifestSnapshot> ReadManifestSnapshotAsync(string manifestPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("The corpus manifest does not exist.", manifestPath);
        }

        var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<CorpusManifest>(bytes, ManifestJsonOptions)
            ?? throw new InvalidDataException("The corpus manifest was empty.");
        return new ManifestSnapshot(manifest, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static void ValidateManifest(CorpusManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.SchemaVersion < MinimumSupportedManifestSchemaVersion
            || manifest.SchemaVersion > MaximumSupportedManifestSchemaVersion)
        {
            throw new InvalidDataException(
                $"Corpus manifest schema version {manifest.SchemaVersion} is not supported by this diagnostic.");
        }

        if (string.IsNullOrWhiteSpace(manifest.GeneratorVersion))
        {
            throw new InvalidDataException("The corpus manifest has no generator version.");
        }

        var masterPcm = manifest.MasterPcm
            ?? throw new InvalidDataException("The corpus manifest has no master PCM contract.");
        if (masterPcm.SampleRate != WebVttFormatter.SamplesPerSecond
            || masterPcm.Channels != 1
            || masterPcm.BitsPerSample != 16)
        {
            throw new InvalidDataException("The corpus manifest does not use PCM16, 16 kHz, mono sample coordinates.");
        }

        var normalization = manifest.Normalization
            ?? throw new InvalidDataException("The corpus manifest has no transcript-normalization contract.");
        if (!string.Equals(normalization.UnicodeNormalization, "FormKC", StringComparison.Ordinal)
            || !string.Equals(normalization.Casing, "Invariant lowercase", StringComparison.Ordinal)
            || !string.Equals(normalization.Punctuation, "Removed", StringComparison.Ordinal)
            || !string.Equals(normalization.Whitespace, "Collapsed to single spaces and trimmed", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The corpus manifest's transcript-normalization contract is not supported by this diagnostic.");
        }

        if (manifest.Fixtures is not { Count: > 0 })
        {
            throw new InvalidDataException("The corpus manifest contains no fixtures.");
        }

        var fixtureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var flatPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nestedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            if (!IsRelativePath(fixture.FlatPath)
                || !IsRelativePath(fixture.NestedRelativePath)
                || !IsRelativePath(fixture.ExpectedVttPath)
                || !IsSha256(fixture.Sha256)
                || !flatPaths.Add(fixture.FlatPath)
                || !nestedPaths.Add(fixture.NestedRelativePath))
            {
                throw new InvalidDataException($"Fixture '{fixture.FixtureId}' has incomplete or duplicate provenance paths.");
            }

            if (fixture.Utterances is not { Count: > 0 })
            {
                throw new InvalidDataException($"Fixture '{fixture.FixtureId}' has no expected utterances.");
            }

            var previousEnd = 0L;
            foreach (var utterance in fixture.Utterances)
            {
                if (utterance.StartSample < 0
                    || utterance.EndSample <= utterance.StartSample
                    || utterance.EndSample > fixture.MasterDurationSamples
                    || utterance.StartSample < previousEnd
                    || string.IsNullOrWhiteSpace(utterance.AuthoredText)
                    || string.IsNullOrWhiteSpace(utterance.NormalizedExpectedText))
                {
                    throw new InvalidDataException($"Fixture '{fixture.FixtureId}' contains an invalid expected utterance.");
                }

                var normalizedAuthoredText = CorpusTranscriptNormalizer.Normalize(utterance.AuthoredText);
                if (!string.Equals(normalizedAuthoredText, utterance.NormalizedExpectedText, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Fixture '{fixture.FixtureId}' has expected text that does not match its declared normalization contract.");
                }

                previousEnd = utterance.EndSample;
            }
        }
    }

    private static async Task<CorpusOutputDiagnosticsReport> CreateReportAsync(
        DiagnosticsOptions options,
        ManifestSnapshot manifestSnapshot,
        CancellationToken cancellationToken)
    {
        var manifest = manifestSnapshot.Manifest;
        var corpusRoot = options.CorpusRoot!;
        var sourceRoot = ResolvePathUnderRoot(corpusRoot, options.SourceLayout.ToDirectoryName(), "selected corpus source root");
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"The selected corpus source root '{sourceRoot}' does not exist.");
        }

        if (!Directory.Exists(options.OutputRoot))
        {
            throw new DirectoryNotFoundException($"The workflow output root '{options.OutputRoot}' does not exist.");
        }

        var fixtureReports = new List<FixtureDiagnostics>(manifest.Fixtures!.Count);
        var outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fixture in manifest.Fixtures.OrderBy(static fixture => fixture.FixtureId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"[{fixtureReports.Count + 1}/{manifest.Fixtures.Count}] {fixture.FixtureId}");
            fixtureReports.Add(await CreateFixtureDiagnosticsAsync(
                fixture,
                options,
                sourceRoot,
                outputPaths,
                cancellationToken).ConfigureAwait(false));
        }

        var aggregate = Aggregate(fixtureReports);
        return new CorpusOutputDiagnosticsReport(
            ReportSchemaVersion,
            $"WinBulkTranscript.CorpusOutputDiagnostics/{ToolVersion}",
            DateTimeOffset.UtcNow,
            "Development-only provenance-bound comparison of synthetic corpus expectations and existing workflow VTT outputs. It reports normalized token-edit and nonzero cue-overlap measurements without defining, applying, or passing any text-score or cue-timing threshold.",
            new RuntimeProvenance(
                RuntimeInformation.OSArchitecture.ToString(),
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.Is64BitProcess),
            new DiagnosticLimitations(
                "syntheticMasterPcmUtteranceBounds",
                "No text score or cue-timing tolerance is defined by the implementation plan or applied by this tool.",
                "The overlap measurement records only whether intervals overlap after emitted VTT milliseconds are converted to 16 kHz samples. It is diagnostic evidence, not an acoustic-boundary or ASR acceptance result."),
            CreateManifestProvenance(manifest, manifestSnapshot.Sha256),
            options.SourceLayout.ToReportValue(),
            fixtureReports,
            aggregate,
            new DiagnosticSummary(
                "complete",
                QualityThresholdApplied: false,
                "All selected manifest inputs and mapped VTT outputs were structurally validated and measured. The aggregate values are informational only.",
                []));
    }

    private static async Task<FixtureDiagnostics> CreateFixtureDiagnosticsAsync(
        FixtureManifest fixture,
        DiagnosticsOptions options,
        string sourceRoot,
        HashSet<string> outputPaths,
        CancellationToken cancellationToken)
    {
        var selectedManifestPath = options.SourceLayout == CorpusSourceLayout.Flat
            ? fixture.FlatPath
            : fixture.NestedRelativePath;
        var sourcePath = ResolvePathUnderRoot(options.CorpusRoot!, selectedManifestPath, "corpus manifest");
        if (!IsPathWithinDirectory(sourcePath, sourceRoot))
        {
            throw new InvalidDataException(
                $"Fixture '{fixture.FixtureId}' selected {options.SourceLayout.ToReportValue()} path is outside the selected source root.");
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Fixture '{fixture.FixtureId}' input is missing.", sourcePath);
        }

        var relativeInputPath = NormalizeRelativePath(Path.GetRelativePath(sourceRoot, sourcePath));
        if (!IsRelativePath(relativeInputPath))
        {
            throw new InvalidDataException($"Fixture '{fixture.FixtureId}' input mapping escaped the selected source root.");
        }

        var relativeOutputPath = NormalizeRelativePath(Path.ChangeExtension(relativeInputPath, ".vtt"));
        var outputPath = ResolvePathUnderRoot(options.OutputRoot!, relativeOutputPath, "workflow output");
        if (!outputPaths.Add(outputPath))
        {
            throw new InvalidDataException($"Two fixture inputs map to the same output VTT path: '{relativeOutputPath}'.");
        }

        if (!File.Exists(outputPath))
        {
            throw new FileNotFoundException($"Fixture '{fixture.FixtureId}' mapped VTT output is missing.", outputPath);
        }

        var inputSha256 = await ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(inputSha256, fixture.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Fixture '{fixture.FixtureId}' input does not match the SHA-256 recorded in the corpus manifest.");
        }

        var expectedVttPath = ResolvePathUnderRoot(options.CorpusRoot!, fixture.ExpectedVttPath, "corpus manifest");
        if (!File.Exists(expectedVttPath))
        {
            throw new FileNotFoundException($"Fixture '{fixture.FixtureId}' expected VTT is missing.", expectedVttPath);
        }

        var utterances = fixture.Utterances
            ?? throw new InvalidDataException($"Fixture '{fixture.FixtureId}' has no expected utterances.");
        var expectedVttBytes = await File.ReadAllBytesAsync(expectedVttPath, cancellationToken).ConfigureAwait(false);
        var expectedVttSha256 = Convert.ToHexString(SHA256.HashData(expectedVttBytes));
        var expectedVttCues = WebVttParser.Parse(
            DecodeUtf8(expectedVttBytes, $"Expected VTT for '{fixture.FixtureId}'"),
            $"expected VTT for '{fixture.FixtureId}'");
        ValidateExpectedVtt(fixture, utterances, expectedVttCues);
        var outputBytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
        var outputSha256 = Convert.ToHexString(SHA256.HashData(outputBytes));
        var outputCues = WebVttParser.Parse(DecodeUtf8(outputBytes, $"Output VTT for '{fixture.FixtureId}'"), fixture.FixtureId);
        var expectedIntervals = utterances
            .Select(static utterance => new SampleRange(utterance.StartSample, utterance.EndSample))
            .ToArray();
        var expectedText = string.Join(' ', utterances.Select(static utterance => utterance.NormalizedExpectedText));
        var actualText = string.Join(' ', outputCues.Select(static cue => cue.Text));
        var expectedWords = CorpusTranscriptNormalizer.Tokenize(expectedText);
        var actualWords = CorpusTranscriptNormalizer.Tokenize(actualText);
        var wordEditDistance = WordEditDistance.Compute(expectedWords, actualWords);
        var producedCuesWithoutExpectedOverlap = CountProducedCuesWithoutExpectedOverlap(outputCues, expectedIntervals);
        var expectedUtterancesWithoutProducedOverlap = CountExpectedUtterancesWithoutProducedOverlap(expectedIntervals, outputCues);
        var wordEditPercent = Percentage(wordEditDistance, expectedWords.Count);

        return new FixtureDiagnostics(
            fixture.FixtureId,
            new InputProvenance(relativeInputPath, inputSha256, fixture.Sha256),
            new ExpectedVttProvenance(NormalizeRelativePath(fixture.ExpectedVttPath), expectedVttSha256, expectedVttCues.Count, MatchesManifest: true),
            new OutputVttProvenance(relativeOutputPath, outputSha256, outputCues.Count),
            expectedIntervals.Length,
            outputCues.Count,
            producedCuesWithoutExpectedOverlap,
            expectedUtterancesWithoutProducedOverlap,
            expectedWords.Count,
            actualWords.Count,
            wordEditDistance,
            wordEditPercent);
    }

    private static AggregateDiagnostics Aggregate(List<FixtureDiagnostics> fixtures)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        if (fixtures.Count == 0)
        {
            throw new InvalidDataException("No fixture diagnostics were available to aggregate.");
        }

        var producedCueCount = 0;
        var expectedUtteranceCount = 0;
        var producedCuesWithoutExpectedOverlap = 0;
        var expectedUtterancesWithoutProducedOverlap = 0;
        var expectedWordCount = 0;
        var actualWordCount = 0;
        var wordEditDistance = 0;
        foreach (var fixture in fixtures)
        {
            producedCueCount = checked(producedCueCount + fixture.ProducedCueCount);
            expectedUtteranceCount = checked(expectedUtteranceCount + fixture.ExpectedUtteranceCount);
            producedCuesWithoutExpectedOverlap = checked(producedCuesWithoutExpectedOverlap + fixture.ProducedCuesWithoutExpectedOverlap);
            expectedUtterancesWithoutProducedOverlap = checked(expectedUtterancesWithoutProducedOverlap + fixture.ExpectedUtterancesWithoutProducedOverlap);
            expectedWordCount = checked(expectedWordCount + fixture.ExpectedWordCount);
            actualWordCount = checked(actualWordCount + fixture.ActualWordCount);
            wordEditDistance = checked(wordEditDistance + fixture.WordEditDistance);
        }

        var worstFixture = fixtures
            .OrderByDescending(static fixture => fixture.WordEditPercent)
            .ThenBy(static fixture => fixture.FixtureId, StringComparer.Ordinal)
            .First();
        return new AggregateDiagnostics(
            fixtures.Count,
            producedCueCount,
            expectedUtteranceCount,
            producedCuesWithoutExpectedOverlap,
            expectedUtterancesWithoutProducedOverlap,
            expectedWordCount,
            actualWordCount,
            wordEditDistance,
            Percentage(wordEditDistance, expectedWordCount),
            new WorstFixtureDiagnostics(
                worstFixture.FixtureId,
                worstFixture.WordEditDistance,
                worstFixture.ExpectedWordCount,
                worstFixture.WordEditPercent));
    }

    private static void ValidateExpectedVtt(
        FixtureManifest fixture,
        List<UtteranceManifest> utterances,
        IReadOnlyList<ParsedVttCue> expectedVttCues)
    {
        if (expectedVttCues.Count != utterances.Count)
        {
            throw new InvalidDataException(
                $"Expected VTT for '{fixture.FixtureId}' has {expectedVttCues.Count} cues; the manifest has {utterances.Count} utterances.");
        }

        for (var index = 0; index < utterances.Count; index++)
        {
            var utterance = utterances[index];
            var cue = expectedVttCues[index];
            var expectedStart = WebVttFormatter.RoundSamplesToMilliseconds(utterance.StartSample);
            var expectedEnd = WebVttFormatter.RoundSamplesToMilliseconds(utterance.EndSample);
            if (cue.StartMilliseconds != expectedStart
                || cue.EndMilliseconds != expectedEnd
                || !string.Equals(CorpusTranscriptNormalizer.Normalize(cue.Text), utterance.NormalizedExpectedText, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Expected VTT for '{fixture.FixtureId}' does not agree with manifest utterance {index + 1}.");
            }
        }
    }

    private static int CountProducedCuesWithoutExpectedOverlap(
        IReadOnlyList<ParsedVttCue> outputCues,
        IReadOnlyList<SampleRange> expectedIntervals)
    {
        var count = 0;
        foreach (var outputCue in outputCues)
        {
            var outputRange = outputCue.ToSampleRange();
            if (!expectedIntervals.Any(expected => RangesOverlap(expected, outputRange)))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountExpectedUtterancesWithoutProducedOverlap(
        IReadOnlyList<SampleRange> expectedIntervals,
        IReadOnlyList<ParsedVttCue> outputCues)
    {
        var count = 0;
        foreach (var expected in expectedIntervals)
        {
            if (!outputCues.Any(outputCue => RangesOverlap(expected, outputCue.ToSampleRange())))
            {
                count++;
            }
        }

        return count;
    }

    private static bool RangesOverlap(SampleRange left, SampleRange right)
        => left.StartSample < right.EndSample && left.EndSample > right.StartSample;

    private static ManifestProvenance CreateManifestProvenance(CorpusManifest manifest, string manifestSha256)
    {
        var masterPcm = manifest.MasterPcm!;
        return new ManifestProvenance(
            manifest.SchemaVersion,
            manifest.GeneratorVersion,
            manifest.RandomSeed,
            manifestSha256,
            new MasterPcmProvenance(
                masterPcm.Encoding,
                masterPcm.SampleRate,
                masterPcm.Channels,
                masterPcm.BitsPerSample),
            manifest.Voice is null
                ? null
                : new VoiceProvenance(
                    manifest.Voice.Id,
                    manifest.Voice.DisplayName,
                    manifest.Voice.Language,
                    manifest.Voice.Gender,
                    manifest.Voice.FallbackUsed));
    }

    private static void EnsureReportDestination(DiagnosticsOptions options, CorpusManifest manifest)
    {
        var reportPath = options.ReportPath!;
        if (Directory.Exists(reportPath))
        {
            throw new InvalidOperationException($"The report path '{reportPath}' is an existing directory.");
        }

        if (string.Equals(reportPath, options.ManifestPath, StringComparison.OrdinalIgnoreCase)
            || IsPathWithinDirectory(reportPath, options.CorpusRoot!)
            || IsPathWithinDirectory(reportPath, options.OutputRoot!))
        {
            throw new InvalidOperationException("The evidence report must be outside the corpus and workflow output roots and must not replace the manifest.");
        }

        foreach (var fixture in manifest.Fixtures!)
        {
            foreach (var relativePath in new[] { fixture.FlatPath, fixture.NestedRelativePath, fixture.ExpectedVttPath })
            {
                var protectedPath = ResolvePathUnderRoot(options.CorpusRoot!, relativePath, "corpus manifest");
                if (string.Equals(reportPath, protectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The evidence report must not replace a manifest-referenced corpus artifact.");
                }
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
                    // The temporary file is owned solely by this diagnostic.
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

    private static string DecodeUtf8(byte[] bytes, string description)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"{description} is not valid UTF-8.", exception);
        }
    }

    private static string ResolvePathUnderRoot(string root, string relativePath, string description)
    {
        if (!IsRelativePath(relativePath))
        {
            throw new InvalidDataException($"A {description} path must be nonempty and relative.");
        }

        var normalizedRoot = Path.GetFullPath(root);
        var rootWithSeparator = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(rootWithSeparator, normalizedRelativePath));
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The {description} path '{relativePath}' escapes its configured root.");
        }

        return fullPath;
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

    private static bool IsRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return false;
        }

        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        var relative = Path.GetRelativePath(".", normalized);
        return !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/');

    private static double Percentage(int portion, int total)
        => total == 0 ? 0d : portion * 100d / total;
}

/// <summary>
/// Parses command-line options for the development-only corpus-output diagnostic.
/// </summary>
internal sealed record DiagnosticsOptions(
    string? ManifestPath,
    string? CorpusRoot,
    CorpusSourceLayout SourceLayout,
    string? OutputRoot,
    string? ReportPath,
    bool Overwrite,
    bool ShowHelp)
{
    /// <summary>Gets the usage text.</summary>
    public const string Usage = """
        WinBulkTranscript corpus-output diagnostic

        Produces a provenance-bound, informational comparison of a synthetic corpus manifest and
        an existing workflow VTT output tree. It never runs Foundry or changes media/VTT inputs.

        Usage:
          dotnet run --project tools/WinBulkTranscript.CorpusOutputDiagnostics -- [options]

        Required options:
          --manifest <path>             Synthetic corpus corpus-manifest.json
          --corpus-root <directory>     Generated synthetic corpus root
          --source <flat|nested>        Corpus input layout that produced the VTT tree
          --output-root <directory>     Existing workflow VTT output root
          --report <path>               JSON report to create outside corpus/output roots

        Other options:
          --overwrite                   Intentionally replace an existing report
          --help                        Show this help

        The report verifies the selected manifest media SHA-256 values, expected-VTT provenance,
        output mapping, UTF-8/WebVTT structure, and output SHA-256 values. It reports normalized
        token-edit and nonzero cue-overlap measurements, but deliberately applies no text score or
        cue-timing acceptance threshold.
        """;

    /// <summary>
    /// Parses supported options without silently accepting unknown arguments.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Validated option values.</returns>
    public static DiagnosticsOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? manifestPath = null;
        string? corpusRoot = null;
        CorpusSourceLayout? sourceLayout = null;
        string? outputRoot = null;
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
                case "--source":
                    if (sourceLayout is not null)
                    {
                        throw new ArgumentException("Option '--source' can be supplied only once.");
                    }

                    sourceLayout = ParseSourceLayout(ReadValue(args, ref index, "--source"));
                    break;
                case "--output-root":
                    outputRoot = SetOnce(outputRoot, ReadValue(args, ref index, "--output-root"), "--output-root");
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
            return new DiagnosticsOptions(null, null, CorpusSourceLayout.Flat, null, null, false, true);
        }

        if (string.IsNullOrWhiteSpace(manifestPath)
            || string.IsNullOrWhiteSpace(corpusRoot)
            || sourceLayout is null
            || string.IsNullOrWhiteSpace(outputRoot)
            || string.IsNullOrWhiteSpace(reportPath))
        {
            throw new ArgumentException($"Options '--manifest', '--corpus-root', '--source', '--output-root', and '--report' are required.{Environment.NewLine}{Usage}");
        }

        return new DiagnosticsOptions(
            Path.GetFullPath(manifestPath),
            Path.GetFullPath(corpusRoot),
            sourceLayout.Value,
            Path.GetFullPath(outputRoot),
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

    private static string SetOnce(string? currentValue, string value, string option)
    {
        if (currentValue is not null)
        {
            throw new ArgumentException($"Option '{option}' can be supplied only once.");
        }

        return value;
    }

    private static CorpusSourceLayout ParseSourceLayout(string value)
        => value.ToLowerInvariant() switch
        {
            "flat" => CorpusSourceLayout.Flat,
            "nested" => CorpusSourceLayout.Nested,
            _ => throw new ArgumentException("Option '--source' must be 'flat' or 'nested'."),
        };
}

/// <summary>Identifies the corpus layout that produced a VTT output tree.</summary>
internal enum CorpusSourceLayout
{
    /// <summary>The flat corpus layout.</summary>
    Flat,

    /// <summary>The nested corpus layout.</summary>
    Nested,
}

/// <summary>Provides stable report names for corpus source layouts.</summary>
internal static class CorpusSourceLayoutExtensions
{
    /// <summary>Gets the corpus directory name.</summary>
    /// <param name="layout">The source layout.</param>
    /// <returns>The corresponding corpus directory name.</returns>
    public static string ToDirectoryName(this CorpusSourceLayout layout)
        => layout == CorpusSourceLayout.Flat ? "flat" : "nested";

    /// <summary>Gets the stable JSON report value.</summary>
    /// <param name="layout">The source layout.</param>
    /// <returns>The corresponding report value.</returns>
    public static string ToReportValue(this CorpusSourceLayout layout)
        => layout == CorpusSourceLayout.Flat ? "flat" : "nested";
}

/// <summary>Normalizes corpus text using the checked-in synthetic corpus contract.</summary>
internal static class CorpusTranscriptNormalizer
{
    /// <summary>Normalizes text with FormKC, invariant lowercase, punctuation removal, and collapsed whitespace.</summary>
    /// <param name="text">Text to normalize.</param>
    /// <returns>Normalized text.</returns>
    public static string Normalize(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var character in normalized)
        {
            if (char.IsPunctuation(character))
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString().Trim();
    }

    /// <summary>Splits normalized text into deterministic tokens.</summary>
    /// <param name="text">Text to normalize and split.</param>
    /// <returns>Ordered normalized tokens.</returns>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var normalized = Normalize(text);
        return normalized.Length == 0
            ? []
            : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

/// <summary>Computes deterministic token edit distances without a third-party dependency.</summary>
internal static class WordEditDistance
{
    /// <summary>Computes a Levenshtein edit distance over ordered tokens.</summary>
    /// <param name="expected">Expected tokens.</param>
    /// <param name="actual">Actual tokens.</param>
    /// <returns>The minimum insertion/deletion/substitution count.</returns>
    public static int Compute(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var previous = new int[actual.Count + 1];
        for (var index = 0; index <= actual.Count; index++)
        {
            previous[index] = index;
        }

        for (var expectedIndex = 1; expectedIndex <= expected.Count; expectedIndex++)
        {
            var current = new int[actual.Count + 1];
            current[0] = expectedIndex;
            for (var actualIndex = 1; actualIndex <= actual.Count; actualIndex++)
            {
                var substitution = previous[actualIndex - 1]
                    + (string.Equals(expected[expectedIndex - 1], actual[actualIndex - 1], StringComparison.Ordinal) ? 0 : 1);
                var deletion = previous[actualIndex] + 1;
                var insertion = current[actualIndex - 1] + 1;
                current[actualIndex] = Math.Min(substitution, Math.Min(deletion, insertion));
            }

            previous = current;
        }

        return previous[actual.Count];
    }
}

/// <summary>Parses the strict UTF-8 WebVTT subset written by the production formatter.</summary>
internal static class WebVttParser
{
    /// <summary>Parses header-only or textual WebVTT cues, rejecting malformed or regressing cues.</summary>
    /// <param name="content">UTF-8-decoded VTT text.</param>
    /// <param name="description">Source description for errors.</param>
    /// <returns>Ordered parsed cues.</returns>
    public static IReadOnlyList<ParsedVttCue> Parse(string content, string description)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        if (content.Length > 0 && content[0] == '\uFEFF')
        {
            content = content[1..];
        }

        var lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        if (lines.Length == 0 || !HasWebVttHeader(lines[0]))
        {
            throw new InvalidDataException($"Output for '{description}' does not begin with a valid WEBVTT header.");
        }

        var cues = new List<ParsedVttCue>();
        var index = 1;
        var previousEndMilliseconds = 0L;
        while (index < lines.Length)
        {
            while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
            }

            if (index >= lines.Length)
            {
                break;
            }

            var timingLineIndex = index;
            if (!TryParseTimingLine(lines[index], out var startMilliseconds, out var endMilliseconds))
            {
                index++;
                if (index >= lines.Length || !TryParseTimingLine(lines[index], out startMilliseconds, out endMilliseconds))
                {
                    throw new InvalidDataException($"Output for '{description}' contains a cue block without a valid timing line at line {timingLineIndex + 1}.");
                }
            }

            if (startMilliseconds < previousEndMilliseconds)
            {
                throw new InvalidDataException($"Output for '{description}' has regressing or overlapping cue timestamps at line {index + 1}.");
            }

            index++;
            var textLines = new List<string>();
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
            {
                textLines.Add(lines[index]);
                index++;
            }

            var text = string.Join(' ', textLines);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidDataException($"Output for '{description}' has an empty textual cue at line {timingLineIndex + 1}.");
            }

            cues.Add(new ParsedVttCue(startMilliseconds, endMilliseconds, text));
            previousEndMilliseconds = endMilliseconds;
        }

        return cues;
    }

    private static bool HasWebVttHeader(string line)
    {
        const string header = "WEBVTT";
        return line.StartsWith(header, StringComparison.Ordinal)
            && (line.Length == header.Length || char.IsWhiteSpace(line[header.Length]));
    }

    private static bool TryParseTimingLine(string line, out long startMilliseconds, out long endMilliseconds)
    {
        startMilliseconds = 0;
        endMilliseconds = 0;
        var arrowIndex = line.IndexOf("-->", StringComparison.Ordinal);
        if (arrowIndex <= 0)
        {
            return false;
        }

        var startToken = line[..arrowIndex].Trim();
        var endAndSettings = line[(arrowIndex + 3)..].TrimStart();
        var settingsIndex = IndexOfWhitespace(endAndSettings);
        var endToken = settingsIndex < 0 ? endAndSettings : endAndSettings[..settingsIndex];
        return TryParseTimestamp(startToken, out startMilliseconds)
            && TryParseTimestamp(endToken, out endMilliseconds)
            && endMilliseconds > startMilliseconds;
    }

    private static int IndexOfWhitespace(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryParseTimestamp(string value, out long milliseconds)
    {
        milliseconds = 0;
        var parts = value.Split(':');
        if (parts.Length != 3
            || parts[0].Length < 2
            || parts[1].Length != 2
            || parts[2].Length != 6
            || parts[2][2] != '.')
        {
            return false;
        }

        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(parts[2][..2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || !int.TryParse(parts[2][3..], NumberStyles.None, CultureInfo.InvariantCulture, out var fraction))
        {
            return false;
        }

        if (hours < 0 || minutes is > 59 or < 0 || seconds is > 59 or < 0 || fraction is > 999 or < 0)
        {
            return false;
        }

        try
        {
            milliseconds = checked(hours * 3_600_000L + minutes * 60_000L + seconds * 1_000L + fraction);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}

/// <summary>Records a half-open interval in the synthetic master PCM coordinate system.</summary>
internal readonly record struct SampleRange(long StartSample, long EndSample);

/// <summary>Represents a parsed output VTT cue.</summary>
internal sealed record ParsedVttCue(long StartMilliseconds, long EndMilliseconds, string Text)
{
    /// <summary>Converts millisecond VTT positions to 16 kHz sample coordinates.</summary>
    /// <returns>The corresponding half-open sample range.</returns>
    public SampleRange ToSampleRange()
    {
        const int MillisecondsPerSecond = 1_000;
        if (WebVttFormatter.SamplesPerSecond % MillisecondsPerSecond != 0)
        {
            throw new InvalidOperationException("The VTT diagnostic requires an integral samples-per-millisecond conversion.");
        }

        var samplesPerMillisecond = WebVttFormatter.SamplesPerSecond / MillisecondsPerSecond;
        return new SampleRange(
            checked(StartMilliseconds * samplesPerMillisecond),
            checked(EndMilliseconds * samplesPerMillisecond));
    }
}

internal sealed record CorpusOutputDiagnosticsReport(
    int ReportSchemaVersion,
    string Tool,
    DateTimeOffset GeneratedAtUtc,
    string Scope,
    RuntimeProvenance Runtime,
    DiagnosticLimitations Limitations,
    ManifestProvenance Manifest,
    string SourceLayout,
    IReadOnlyList<FixtureDiagnostics> Fixtures,
    AggregateDiagnostics Aggregate,
    DiagnosticSummary Summary);

internal sealed record RuntimeProvenance(string OperatingSystemArchitecture, string ProcessArchitecture, bool Is64BitProcess);

internal sealed record DiagnosticLimitations(
    string ExpectedTimingBasis,
    string ThresholdStatus,
    string Detail);

internal sealed record ManifestProvenance(
    int SchemaVersion,
    string GeneratorVersion,
    ulong RandomSeed,
    string Sha256,
    MasterPcmProvenance MasterPcm,
    VoiceProvenance? Voice);

internal sealed record MasterPcmProvenance(string Encoding, int SampleRate, int Channels, int BitsPerSample);

internal sealed record VoiceProvenance(string Id, string DisplayName, string Language, string Gender, bool FallbackUsed);

internal sealed record FixtureDiagnostics(
    string FixtureId,
    InputProvenance Input,
    ExpectedVttProvenance ExpectedVtt,
    OutputVttProvenance OutputVtt,
    int ExpectedUtteranceCount,
    int ProducedCueCount,
    int ProducedCuesWithoutExpectedOverlap,
    int ExpectedUtterancesWithoutProducedOverlap,
    int ExpectedWordCount,
    int ActualWordCount,
    int WordEditDistance,
    double WordEditPercent);

internal sealed record InputProvenance(string RelativePath, string Sha256, string ManifestSha256);

internal sealed record ExpectedVttProvenance(string RelativePath, string Sha256, int ParsedCueCount, bool MatchesManifest);

internal sealed record OutputVttProvenance(string RelativePath, string Sha256, int ParsedCueCount);

internal sealed record AggregateDiagnostics(
    int FixtureCount,
    int ProducedCueCount,
    int ExpectedUtteranceCount,
    int ProducedCuesWithoutExpectedOverlap,
    int ExpectedUtterancesWithoutProducedOverlap,
    int ExpectedWordCount,
    int ActualWordCount,
    int WordEditDistance,
    double WordEditPercent,
    WorstFixtureDiagnostics WorstFixture);

internal sealed record WorstFixtureDiagnostics(
    string FixtureId,
    int WordEditDistance,
    int ExpectedWordCount,
    double WordEditPercent);

internal sealed record DiagnosticSummary(
    string Status,
    bool QualityThresholdApplied,
    string Detail,
    IReadOnlyList<string> Failures);

internal sealed record ManifestSnapshot(CorpusManifest Manifest, string Sha256);

/// <summary>Subset of the synthetic-corpus manifest contract needed for output diagnostics.</summary>
internal sealed class CorpusManifest
{
    public int SchemaVersion { get; set; }

    public string GeneratorVersion { get; set; } = string.Empty;

    public ulong RandomSeed { get; set; }

    public VoiceManifest? Voice { get; set; }

    public PcmManifest? MasterPcm { get; set; }

    public NormalizationManifest? Normalization { get; set; }

    public List<FixtureManifest>? Fixtures { get; set; }
}

internal sealed class VoiceManifest
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    public string Gender { get; set; } = string.Empty;

    public bool FallbackUsed { get; set; }
}

internal sealed class PcmManifest
{
    public string Encoding { get; set; } = string.Empty;

    public int SampleRate { get; set; }

    public int Channels { get; set; }

    public int BitsPerSample { get; set; }
}

internal sealed class NormalizationManifest
{
    public string UnicodeNormalization { get; set; } = string.Empty;

    public string Casing { get; set; } = string.Empty;

    public string Punctuation { get; set; } = string.Empty;

    public string Whitespace { get; set; } = string.Empty;
}

internal sealed class FixtureManifest
{
    public string FixtureId { get; set; } = string.Empty;

    public string FlatPath { get; set; } = string.Empty;

    public string NestedRelativePath { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public long MasterDurationSamples { get; set; }

    public string ExpectedVttPath { get; set; } = string.Empty;

    public List<UtteranceManifest>? Utterances { get; set; }
}

internal sealed class UtteranceManifest
{
    public string AuthoredText { get; set; } = string.Empty;

    public string NormalizedExpectedText { get; set; } = string.Empty;

    public long StartSample { get; set; }

    public long EndSample { get; set; }
}
