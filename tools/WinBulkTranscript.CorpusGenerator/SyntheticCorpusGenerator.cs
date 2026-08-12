using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinBulkTranscript.CorpusGenerator;

/// <summary>
/// Builds, validates, and atomically stages the complete synthetic MP4 functional corpus.
/// </summary>
internal sealed class SyntheticCorpusGenerator
{
    /// <summary>The current manifest schema version.</summary>
    public const int ManifestSchemaVersion = 3;

    private const int FixtureCount = 30;
    private const int MinimumInterUtteranceSilenceSamples = Pcm16Audio.SampleRate * 3 / 4;
    private const int MaximumInterUtteranceSilenceSamples = Pcm16Audio.SampleRate * 3;
    private const int MaximumLeadingOrTrailingSilenceSamples = Pcm16Audio.SampleRate * 2;
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly GeneratorOptions options;

    /// <summary>
    /// Initializes a generator for one requested corpus artifact.
    /// </summary>
    /// <param name="options">The requested generation options.</param>
    public SyntheticCorpusGenerator(GeneratorOptions options)
    {
        this.options = options;
    }

    /// <summary>
    /// Generates a complete staged corpus and publishes it only after every acceptance check passes.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An asynchronous task.</returns>
    public async Task GenerateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOutputCanBeCreated();

        var outputParent = Path.GetDirectoryName(options.OutputRoot)
            ?? throw new InvalidOperationException("The corpus output must have a parent directory.");
        Directory.CreateDirectory(outputParent);
        var outputName = Path.GetFileName(options.OutputRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var stagingRoot = Path.Combine(outputParent, $".{outputName}.staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);

        var stagedSuccessfully = false;
        try
        {
            var flatRoot = Path.Combine(stagingRoot, "flat");
            var nestedRoot = Path.Combine(stagingRoot, "nested");
            var vttRoot = Path.Combine(stagingRoot, "expected-vtt");
            var workRoot = Path.Combine(stagingRoot, ".working");
            var retainedMasterPcmRoot = options.RetainMasterPcm
                ? Path.Combine(stagingRoot, "retained-master-pcm")
                : null;
            Directory.CreateDirectory(flatRoot);
            Directory.CreateDirectory(nestedRoot);
            Directory.CreateDirectory(vttRoot);
            Directory.CreateDirectory(workRoot);

            if (retainedMasterPcmRoot is not null)
            {
                Directory.CreateDirectory(retainedMasterPcmRoot);
            }
            var fixtureIds = Enumerable.Range(1, FixtureCount).Select(index => $"fixture-{index:D3}").ToArray();
            var layout = NestedLayoutPlanner.Create(options.Seed, fixtureIds);
            var textFactory = new CorpusTextFactory(options.Seed);
            using var media = WindowsTtsAndMedia.Create(options);
            var fixtures = new List<FixtureManifest>(FixtureCount);

            for (var fixtureIndex = 0; fixtureIndex < FixtureCount; fixtureIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fixtureId = fixtureIds[fixtureIndex];
                Console.WriteLine($"[{fixtureIndex + 1:D2}/{FixtureCount}] {fixtureId}");
                fixtures.Add(await GenerateFixtureAsync(
                    fixtureIndex,
                    fixtureId,
                    layout[fixtureId],
                    textFactory,
                    media,
                    flatRoot,
                    nestedRoot,
                    workRoot,
                    retainedMasterPcmRoot,
                    cancellationToken).ConfigureAwait(false));
            }

            var manifest = CreateManifest(media.VoiceManifest, fixtures);
            await WriteExpectedVttsAsync(stagingRoot, manifest.Fixtures, cancellationToken).ConfigureAwait(false);
            var manifestPath = Path.Combine(stagingRoot, "corpus-manifest.json");
            await WriteManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            var persistedManifest = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);

            await CorpusValidator.ValidateAsync(stagingRoot, persistedManifest, options.RetainMasterPcm, cancellationToken).ConfigureAwait(false);
            Directory.Delete(workRoot, recursive: true);
            await CommitStagingAsync(stagingRoot, cancellationToken).ConfigureAwait(false);
            stagedSuccessfully = true;
        }
        finally
        {
            if (!stagedSuccessfully && Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    private async Task<FixtureManifest> GenerateFixtureAsync(
        int fixtureIndex,
        string fixtureId,
        NestedPlacement placement,
        CorpusTextFactory textFactory,
        WindowsTtsAndMedia media,
        string flatRoot,
        string nestedRoot,
        string workRoot,
        string? retainedMasterPcmRoot,
        CancellationToken cancellationToken)
    {
        var plan = CreateFixturePlan(fixtureIndex, fixtureId);
        var utterances = new List<SynthesizedUtterance>(plan.UtteranceCount);
        for (var utteranceIndex = 0; utteranceIndex < plan.UtteranceCount; utteranceIndex++)
        {
            utterances.Add(await SynthesizeValidUtteranceAsync(
                fixtureIndex,
                utteranceIndex,
                textFactory,
                media,
                cancellationToken).ConfigureAwait(false));
        }

        var speechSamples = utterances.Sum(utterance => utterance.Audio.SampleCount);
        var silencePlan = CreateSilencePlan(fixtureId, plan.RawTargetDurationSamples, utterances.Count, speechSamples);
        var masterParts = new List<Pcm16Audio>(utterances.Count * 2 + 2)
        {
            Pcm16Audio.CreateSilence(silencePlan.LeadingSamples),
        };
        var manifestUtterances = new List<UtteranceManifest>(utterances.Count);
        var cursor = silencePlan.LeadingSamples;
        for (var utteranceIndex = 0; utteranceIndex < utterances.Count; utteranceIndex++)
        {
            var utterance = utterances[utteranceIndex];
            var startSample = cursor;
            cursor += utterance.Audio.SampleCount;
            masterParts.Add(utterance.Audio);
            manifestUtterances.Add(new UtteranceManifest
            {
                AuthoredText = utterance.AuthoredText,
                NormalizedExpectedText = TranscriptNormalizer.Normalize(utterance.AuthoredText),
                StartSample = startSample,
                EndSample = cursor,
                StartSeconds = startSample / (double)Pcm16Audio.SampleRate,
                EndSeconds = cursor / (double)Pcm16Audio.SampleRate,
            });

            if (utteranceIndex < silencePlan.InterUtteranceSamples.Count)
            {
                var silence = silencePlan.InterUtteranceSamples[utteranceIndex];
                masterParts.Add(Pcm16Audio.CreateSilence(silence));
                cursor += silence;
            }
        }

        masterParts.Add(Pcm16Audio.CreateSilence(silencePlan.TrailingSamples));
        cursor += silencePlan.TrailingSamples;
        var masterAudio = Pcm16Audio.Concatenate(masterParts);
        if (masterAudio.SampleCount != cursor || masterAudio.SampleCount != silencePlan.TargetDurationSamples)
        {
            throw new InvalidOperationException($"Master assembly mismatch for '{fixtureId}'.");
        }

        var fileName = $"{fixtureId}.mp4";
        var flatPath = Path.Combine(flatRoot, fileName);
        var masterPcmDataSha256 = Convert.ToHexString(SHA256.HashData(masterAudio.Samples));

        var masterWavePath = Path.Combine(workRoot, $"{fixtureId}.wav");
        try
        {
            await PcmWaveFile.WriteAsync(masterWavePath, masterAudio, cancellationToken).ConfigureAwait(false);
            if (retainedMasterPcmRoot is not null)
            {
                File.Copy(masterWavePath, Path.Combine(retainedMasterPcmRoot, $"{fixtureId}.wav"), overwrite: false);
            }

            await WindowsTtsAndMedia.EncodeAudioOnlyMp4Async(masterWavePath, flatPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(masterWavePath))
            {
                File.Delete(masterWavePath);
            }
        }

        var nestedRelativePath = $"nested/{placement.RelativePath}";
        var nestedPath = Path.Combine(nestedRoot, placement.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(nestedPath)!);
        File.Copy(flatPath, nestedPath, overwrite: false);

        var flatHash = await ComputeSha256Async(flatPath, cancellationToken).ConfigureAwait(false);
        var nestedHash = await ComputeSha256Async(nestedPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(flatHash, nestedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Nested copy for '{fixtureId}' is not byte-identical to its flat source.");
        }

        var inspection = await WindowsTtsAndMedia.InspectAsync(flatPath, cancellationToken).ConfigureAwait(false);
        return new FixtureManifest
        {
            FixtureId = fixtureId,
            FileName = fileName,
            FlatPath = $"flat/{fileName}",
            NestedRelativePath = nestedRelativePath,
            Sha256 = flatHash,
            DurationCoverageBand = plan.DurationCoverageBand,
            RequestedTargetDurationSeconds = plan.RawTargetDurationSamples / (double)Pcm16Audio.SampleRate,
            TargetDurationSeconds = masterAudio.DurationSeconds,
            DecodedDurationSeconds = inspection.DurationSeconds,
            MasterDurationSamples = masterAudio.SampleCount,
            MasterDurationSeconds = masterAudio.DurationSeconds,
            MasterPcmDataSha256 = masterPcmDataSha256,
            AudioTrackCount = inspection.AudioTrackCount,
            VideoTrackCount = inspection.VideoTrackCount,
            LeadingSilence = ToSilenceManifest(silencePlan.LeadingSamples),
            InterUtteranceSilences = silencePlan.InterUtteranceSamples.Select(ToSilenceManifest).ToList(),
            TrailingSilence = ToSilenceManifest(silencePlan.TrailingSamples),
            Utterances = manifestUtterances,
            ExpectedVttPath = $"expected-vtt/{fixtureId}.vtt",
        };
    }

    private async Task<SynthesizedUtterance> SynthesizeValidUtteranceAsync(
        int fixtureIndex,
        int utteranceIndex,
        CorpusTextFactory textFactory,
        WindowsTtsAndMedia media,
        CancellationToken cancellationToken)
    {
        var lengthRandom = new StableRandom(StableRandom.DeriveSeed(options.Seed, $"utterance-word-count/{fixtureIndex}/{utteranceIndex}"));
        var targetWordCount = lengthRandom.NextInt(19, 28);
        const int maximumAttempts = 14;

        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authoredText = textFactory.BuildCandidate(fixtureIndex, utteranceIndex, attempt, targetWordCount);
            var audio = await media.SynthesizeMasterPcmAsync(authoredText, cancellationToken).ConfigureAwait(false);
            var durationSamples = audio.SampleCount;
            if (durationSamples is >= (5 * Pcm16Audio.SampleRate) and <= (10 * Pcm16Audio.SampleRate))
            {
                return new SynthesizedUtterance(authoredText, audio);
            }

            targetWordCount = AdjustWordCount(targetWordCount, durationSamples);
        }

        throw new InvalidOperationException(
            $"Unable to synthesize a 5-10 second utterance for fixture {fixtureIndex + 1}, utterance {utteranceIndex + 1} after {maximumAttempts} deterministic adjustments.");
    }

    private static int AdjustWordCount(int currentWordCount, long measuredSamples)
    {
        if (measuredSamples <= 0)
        {
            return Math.Min(72, currentWordCount + 6);
        }

        const double desiredSamples = 7.5 * Pcm16Audio.SampleRate;
        var scaled = (int)Math.Round(currentWordCount * (desiredSamples / measuredSamples), MidpointRounding.AwayFromZero);
        scaled = Math.Clamp(scaled, 6, 72);
        if (measuredSamples < 5 * Pcm16Audio.SampleRate && scaled <= currentWordCount)
        {
            return Math.Min(72, currentWordCount + 3);
        }

        if (measuredSamples > 10 * Pcm16Audio.SampleRate && scaled >= currentWordCount)
        {
            return Math.Max(6, currentWordCount - 3);
        }

        return scaled;
    }

    private FixturePlan CreateFixturePlan(int fixtureIndex, string fixtureId)
    {
        var random = new StableRandom(StableRandom.DeriveSeed(options.Seed, $"fixture-plan/{fixtureId}"));
        if (fixtureIndex < 10)
        {
            return new FixturePlan(
                "short",
                random.NextInt(4, 6),
                SecondsToSamples(random.NextInt64(34, 55)));
        }

        if (fixtureIndex < 20)
        {
            var utteranceCount = random.NextInt(7, 9);
            var targetSeconds = utteranceCount == 7
                ? random.NextInt64(58, 79)
                : random.NextInt64(65, 86);
            return new FixturePlan("medium", utteranceCount, SecondsToSamples(targetSeconds));
        }

        var longUtteranceCount = random.NextInt(10, 12);
        var longTargetSeconds = longUtteranceCount == 10
            ? random.NextInt64(82, 109)
            : random.NextInt64(90, 116);
        return new FixturePlan("long", longUtteranceCount, SecondsToSamples(longTargetSeconds));
    }

    private SilencePlan CreateSilencePlan(string fixtureId, long rawTargetDurationSamples, int utteranceCount, long speechSamples)
    {
        var minimumGapSamples = checked((utteranceCount - 1L) * MinimumInterUtteranceSilenceSamples);
        var maximumGapSamples = checked(((utteranceCount - 1L) * MaximumInterUtteranceSilenceSamples) + (2L * MaximumLeadingOrTrailingSilenceSamples));
        var minimumTotal = checked(speechSamples + minimumGapSamples);
        var maximumTotal = checked(speechSamples + maximumGapSamples);
        var targetSamples = Math.Clamp(rawTargetDurationSamples, minimumTotal, maximumTotal);

        var values = new long[utteranceCount + 1];
        var capacities = new long[utteranceCount + 1];
        values[0] = 0;
        capacities[0] = MaximumLeadingOrTrailingSilenceSamples;
        for (var index = 1; index < values.Length - 1; index++)
        {
            values[index] = MinimumInterUtteranceSilenceSamples;
            capacities[index] = MaximumInterUtteranceSilenceSamples - MinimumInterUtteranceSilenceSamples;
        }

        values[^1] = 0;
        capacities[^1] = MaximumLeadingOrTrailingSilenceSamples;
        var remainingExtra = checked(targetSamples - speechSamples - values.Sum());
        var random = new StableRandom(StableRandom.DeriveSeed(options.Seed, $"silence/{fixtureId}"));

        for (var index = 0; index < values.Length; index++)
        {
            var capacityAfterCurrent = capacities.Skip(index + 1).Sum();
            var minimumExtraHere = Math.Max(0, remainingExtra - capacityAfterCurrent);
            var maximumExtraHere = Math.Min(capacities[index], remainingExtra);
            var extra = random.NextInt64(minimumExtraHere, maximumExtraHere);
            values[index] += extra;
            remainingExtra -= extra;
        }

        if (remainingExtra != 0)
        {
            throw new InvalidOperationException($"Silence allocation failed for '{fixtureId}'.");
        }

        return new SilencePlan(targetSamples, values[0], values.Skip(1).Take(utteranceCount - 1).ToArray(), values[^1]);
    }

    private CorpusManifest CreateManifest(VoiceManifest voiceManifest, List<FixtureManifest> fixtures)
    {
        return new CorpusManifest
        {
            SchemaVersion = ManifestSchemaVersion,
            GeneratorVersion = GetGeneratorVersion(),
            RandomSeed = options.Seed,
            GenerationTimestampUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Host = new HostManifest
            {
                WindowsBuild = Environment.OSVersion.Version.ToString(),
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
            },
            Voice = voiceManifest,
            MasterPcm = new PcmManifest
            {
                Encoding = "PCM signed little-endian",
                SampleRate = Pcm16Audio.SampleRate,
                Channels = Pcm16Audio.Channels,
                BitsPerSample = Pcm16Audio.BitsPerSample,
            },
            OutputMedia = new OutputMediaManifest
            {
                Container = "MPEG-4 audio-only",
                AudioCodec = "AAC",
                FileExtension = ".mp4",
                SampleRate = Pcm16Audio.SampleRate,
                Channels = Pcm16Audio.Channels,
                Bitrate = 96_000,
            },
            Normalization = new NormalizationManifest
            {
                UnicodeNormalization = "FormKC",
                Casing = "Invariant lowercase",
                Punctuation = "Removed",
                Whitespace = "Collapsed to single spaces and trimmed",
            },
            VadTimingToleranceMilliseconds = 250,
            Fixtures = fixtures,
        };
    }

    private static async Task WriteExpectedVttsAsync(string corpusRoot, IEnumerable<FixtureManifest> fixtures, CancellationToken cancellationToken)
    {
        foreach (var fixture in fixtures)
        {
            var path = Path.Combine(corpusRoot, fixture.ExpectedVttPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, VttWriter.Render(fixture), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteManifestAsync(string path, CorpusManifest manifest, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, manifest, ManifestJsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CorpusManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, useAsync: true);
        return await JsonSerializer.DeserializeAsync<CorpusManifest>(stream, ManifestJsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The serialized corpus manifest was empty.");
    }

    private Task CommitStagingAsync(string stagingRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(options.OutputRoot))
        {
            Directory.Move(stagingRoot, options.OutputRoot);
            return Task.CompletedTask;
        }

        if (!options.Overwrite)
        {
            throw new InvalidOperationException($"The output directory '{options.OutputRoot}' already exists. Re-run with --overwrite to intentionally replace it.");
        }

        var outputParent = Path.GetDirectoryName(options.OutputRoot)!;
        var backupPath = Path.Combine(outputParent, $".{Path.GetFileName(options.OutputRoot)}.backup-{Guid.NewGuid():N}");
        Directory.Move(options.OutputRoot, backupPath);
        try
        {
            Directory.Move(stagingRoot, options.OutputRoot);
        }
        catch
        {
            if (!Directory.Exists(options.OutputRoot) && Directory.Exists(backupPath))
            {
                Directory.Move(backupPath, options.OutputRoot);
            }

            throw;
        }

        Directory.Delete(backupPath, recursive: true);
        return Task.CompletedTask;
    }

    private void EnsureOutputCanBeCreated()
    {
        if (File.Exists(options.OutputRoot))
        {
            throw new InvalidOperationException($"The requested corpus output '{options.OutputRoot}' is an existing file, not a directory.");
        }

        if (Directory.Exists(options.OutputRoot) && !options.Overwrite)
        {
            throw new InvalidOperationException($"The output directory '{options.OutputRoot}' already exists. Re-run with --overwrite to intentionally replace it.");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static SilenceManifest ToSilenceManifest(long samples)
    {
        return new SilenceManifest
        {
            Samples = samples,
            Seconds = samples / (double)Pcm16Audio.SampleRate,
        };
    }

    private static long SecondsToSamples(long seconds)
    {
        return checked(seconds * Pcm16Audio.SampleRate);
    }

    private static string GetGeneratorVersion()
    {
        var assembly = typeof(SyntheticCorpusGenerator).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}

/// <summary>
/// Describes a fixture's seeded utterance-count and initial duration target.
/// </summary>
/// <param name="DurationCoverageBand">The required short, medium, or long coverage band.</param>
/// <param name="UtteranceCount">The number of individually synthesized utterances.</param>
/// <param name="RawTargetDurationSamples">The pre-clamp target duration in master samples.</param>
internal sealed record FixturePlan(string DurationCoverageBand, int UtteranceCount, long RawTargetDurationSamples);

/// <summary>
/// Holds one accepted synthesized utterance before master assembly.
/// </summary>
/// <param name="AuthoredText">The generated source text.</param>
/// <param name="Audio">The accepted master-format PCM audio.</param>
internal sealed record SynthesizedUtterance(string AuthoredText, Pcm16Audio Audio);

/// <summary>
/// Holds allocated silence sample counts for one master assembly.
/// </summary>
/// <param name="TargetDurationSamples">The resulting total master sample count.</param>
/// <param name="LeadingSamples">The leading silence length.</param>
/// <param name="InterUtteranceSamples">The silences between utterances.</param>
/// <param name="TrailingSamples">The trailing silence length.</param>
internal sealed record SilencePlan(
    long TargetDurationSamples,
    long LeadingSamples,
    IReadOnlyList<long> InterUtteranceSamples,
    long TrailingSamples);
