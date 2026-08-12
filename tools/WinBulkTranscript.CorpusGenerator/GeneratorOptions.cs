using System.Globalization;

namespace WinBulkTranscript.CorpusGenerator;

/// <summary>
/// Immutable command-line settings for a corpus-generation run.
/// </summary>
internal sealed record GeneratorOptions(
    string OutputRoot,
    ulong Seed,
    string? VoiceId,
    string? Phase0FixturePath,
    string? CancellationFixturePath,
    string? MediaFixtureMatrixRoot,
    bool RetainMasterPcm,
    bool AllowFirstEnglishVoice,
    bool Overwrite,
    bool ListVoices,
    bool ShowHelp)
{
    /// <summary>
    /// The explicit default used whenever a seed is not supplied.
    /// </summary>
    public const ulong DefaultSeed = 20_260_806UL;

    /// <summary>
    /// Command-line help text.
    /// </summary>
    public const string Usage = """
        WinBulkTranscript synthetic MP4 corpus generator

        Usage:
          dotnet run --project tools/WinBulkTranscript.CorpusGenerator -- [options]

        Options:
          --output <path>                 Corpus root (default: test-assets/synthetic beneath the repository root)
          --seed <unsigned-integer>       Stable corpus seed (default: 20260806)
          --voice-id <installed-voice-id> Required English TTS voice, unless --allow-first-english-voice is used
          --phase0-fixture <path>          Write a retained known raw PCM16 fixture and JSON provenance sidecar
          --cancellation-fixture <path>    Write a disposable 20-minute AAC/MP4 and JSON provenance sidecar for the media cancellation probe
          --media-fixture-matrix <path>    Create the five opt-in media failure-matrix MP4s plus provenance
          --retain-master-pcm              Publish hash-bound master PCM WAV evidence under retained-master-pcm
          --allow-first-english-voice      Deliberately fall back to the first English voice by stable ID order
          --overwrite                      Replace an existing output corpus only after a complete staged build succeeds
          --list-voices                    Print installed English voices and exit
          --help                           Print this help text

        The WIN_BULK_TRANSCRIPT_TTS_VOICE_ID environment variable can supply the configured voice ID.
        """;

    /// <summary>
    /// Parses supported command-line arguments without silently accepting unknown options.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The parsed options.</returns>
    /// <exception cref="ArgumentException">Thrown when an option is invalid or incomplete.</exception>
    public static GeneratorOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var outputRoot = Path.Combine(FindRepositoryRoot(), "test-assets", "synthetic");
        var seed = DefaultSeed;
        string? voiceId = Environment.GetEnvironmentVariable("WIN_BULK_TRANSCRIPT_TTS_VOICE_ID");
        string? phase0FixturePath = null;
        string? cancellationFixturePath = null;
        string? mediaFixtureMatrixRoot = null;
        var retainMasterPcm = false;
        var allowFirstEnglishVoice = false;
        var overwrite = false;
        var listVoices = false;
        var showHelp = false;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--output":
                    outputRoot = RequireValue(args, ref index, argument);
                    break;
                case "--seed":
                    var seedText = RequireValue(args, ref index, argument);
                    if (!ulong.TryParse(seedText, NumberStyles.None, CultureInfo.InvariantCulture, out seed))
                    {
                        throw new ArgumentException($"'{seedText}' is not a valid unsigned seed.");
                    }

                    break;
                case "--voice-id":
                    voiceId = RequireValue(args, ref index, argument);
                    break;
                case "--phase0-fixture":
                    phase0FixturePath = RequireValue(args, ref index, argument);
                    break;
                case "--cancellation-fixture":
                    cancellationFixturePath = RequireValue(args, ref index, argument);
                    break;
                case "--media-fixture-matrix":
                    mediaFixtureMatrixRoot = RequireValue(args, ref index, argument);
                    break;
                case "--retain-master-pcm":
                    retainMasterPcm = true;
                    break;
                case "--allow-first-english-voice":
                    allowFirstEnglishVoice = true;
                    break;
                case "--overwrite":
                    overwrite = true;
                    break;
                case "--list-voices":
                    listVoices = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{argument}'.{Environment.NewLine}{Usage}");
            }
        }

        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new ArgumentException("The output path cannot be empty.");
        }

        var specializedFixtureModeCount = new[] { phase0FixturePath, cancellationFixturePath, mediaFixtureMatrixRoot }
            .Count(value => value is not null);
        if (specializedFixtureModeCount > 1)
        {
            throw new ArgumentException("Only one of --phase0-fixture, --cancellation-fixture, or --media-fixture-matrix may be used at a time.");
        }

        if (cancellationFixturePath is not null
            && !string.Equals(Path.GetExtension(cancellationFixturePath), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--cancellation-fixture must name an .mp4 output file.");
        }

        var normalizedMediaFixtureMatrixRoot = string.IsNullOrWhiteSpace(mediaFixtureMatrixRoot)
            ? null
            : Path.GetFullPath(mediaFixtureMatrixRoot);
        if (normalizedMediaFixtureMatrixRoot is not null
            && IsPathWithinDirectory(normalizedMediaFixtureMatrixRoot, Path.Combine(FindRepositoryRoot(), "test-assets")))
        {
            throw new ArgumentException(
                "--media-fixture-matrix must write outside the repository's test-assets directory. " +
                "The matrix is an opt-in evidence artifact and must not replace or masquerade as shipped test assets.");
        }

        return new GeneratorOptions(
            Path.GetFullPath(outputRoot),
            seed,
            string.IsNullOrWhiteSpace(voiceId) ? null : voiceId,
            string.IsNullOrWhiteSpace(phase0FixturePath) ? null : Path.GetFullPath(phase0FixturePath),
            string.IsNullOrWhiteSpace(cancellationFixturePath) ? null : Path.GetFullPath(cancellationFixturePath),
            normalizedMediaFixtureMatrixRoot,
            retainMasterPcm,
            allowFirstEnglishVoice,
            overwrite,
            listVoices,
            showHelp);
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"Option '{option}' requires a value.");
        }

        return args[index];
    }

    internal static string FindRepositoryRoot()
    {
        var candidate = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "WinBulkTranscript.sln")))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    internal static bool IsPathWithinDirectory(string path, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var normalizedPath = Path.GetFullPath(path);
        var normalizedRoot = Path.GetFullPath(root);
        var rootWithSeparator = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
