using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.SpeechSynthesis;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WinBulkTranscript.CorpusGenerator;

/// <summary>
/// Wraps the Windows-native voice, conversion, encoding, and media-inspection APIs used only by this tool.
/// </summary>
internal sealed class WindowsTtsAndMedia : IDisposable
{
    private const uint AacBitrate = 96_000;
    private readonly SpeechSynthesizer synthesizer;

    private WindowsTtsAndMedia(SpeechSynthesizer synthesizer, VoiceManifest voiceManifest)
    {
        this.synthesizer = synthesizer;
        VoiceManifest = voiceManifest;
    }

    /// <summary>
    /// Gets the selected voice metadata for the corpus manifest.
    /// </summary>
    public VoiceManifest VoiceManifest { get; }

    /// <summary>
    /// Lists the installed English voices in stable ID order.
    /// </summary>
    /// <returns>Installed English voice descriptors.</returns>
    public static IReadOnlyList<InstalledVoice> ListEnglishVoices()
    {
        return GetEnglishVoices()
            .Select(voice => new InstalledVoice(voice.Id, voice.DisplayName, voice.Language, voice.Gender.ToString()))
            .ToArray();
    }

    /// <summary>
    /// Selects a configured installed English voice, or an explicit deterministic fallback.
    /// </summary>
    /// <param name="options">The current generator options.</param>
    /// <returns>A configured media service.</returns>
    public static WindowsTtsAndMedia Create(GeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var englishVoices = GetEnglishVoices();
        if (englishVoices.Length == 0)
        {
            throw new InvalidOperationException("No installed English Windows TTS voice is available. Install an English Microsoft-signed voice and run --list-voices.");
        }

        VoiceInformation? selectedVoice = null;
        var fallbackUsed = false;
        if (!string.IsNullOrWhiteSpace(options.VoiceId))
        {
            selectedVoice = englishVoices.FirstOrDefault(voice => string.Equals(voice.Id, options.VoiceId, StringComparison.Ordinal));
            if (selectedVoice is null && !options.AllowFirstEnglishVoice)
            {
                throw new InvalidOperationException(BuildVoiceSelectionFailure(options.VoiceId, englishVoices));
            }
        }
        else if (!options.AllowFirstEnglishVoice)
        {
            throw new InvalidOperationException(BuildVoiceSelectionFailure(null, englishVoices));
        }

        if (selectedVoice is null)
        {
            selectedVoice = englishVoices[0];
            fallbackUsed = true;
        }

        var synthesizer = new SpeechSynthesizer
        {
            Voice = selectedVoice,
        };
        synthesizer.Options.SpeakingRate = 1.0;
        synthesizer.Options.AudioPitch = 1.0;
        synthesizer.Options.AudioVolume = 1.0;
        synthesizer.Options.AppendedSilence = SpeechAppendedSilence.Default;
        synthesizer.Options.PunctuationSilence = SpeechPunctuationSilence.Default;
        synthesizer.Options.IncludeSentenceBoundaryMetadata = false;
        synthesizer.Options.IncludeWordBoundaryMetadata = false;

        return new WindowsTtsAndMedia(
            synthesizer,
            new VoiceManifest
            {
                RequestedVoiceId = options.VoiceId,
                FallbackUsed = fallbackUsed,
                Id = selectedVoice.Id,
                DisplayName = selectedVoice.DisplayName,
                Language = selectedVoice.Language,
                Gender = selectedVoice.Gender.ToString(),
                Options = new SynthesisOptionsManifest
                {
                    AppendedSilence = synthesizer.Options.AppendedSilence.ToString(),
                    SpeakingRate = synthesizer.Options.SpeakingRate,
                    AudioPitch = synthesizer.Options.AudioPitch,
                    AudioVolume = synthesizer.Options.AudioVolume,
                    IncludeSentenceBoundaryMetadata = synthesizer.Options.IncludeSentenceBoundaryMetadata,
                    IncludeWordBoundaryMetadata = synthesizer.Options.IncludeWordBoundaryMetadata,
                    PunctuationSilence = synthesizer.Options.PunctuationSilence.ToString(),
                },
            });
    }

    /// <summary>
    /// Synthesizes one utterance and converts it to the master PCM format with MediaTranscoder.
    /// </summary>
    /// <param name="text">The utterance to synthesize.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>PCM16/16 kHz/mono utterance audio.</returns>
    public async Task<Pcm16Audio> SynthesizeMasterPcmAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();

        using var speechStream = await synthesizer.SynthesizeTextToStreamAsync(text);
        cancellationToken.ThrowIfCancellationRequested();

        using var waveStream = new InMemoryRandomAccessStream();
        var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
        profile.Audio = AudioEncodingProperties.CreatePcm(Pcm16Audio.SampleRate, Pcm16Audio.Channels, Pcm16Audio.BitsPerSample);
        profile.Video = null;

        var transcoder = new MediaTranscoder
        {
            AlwaysReencode = true,
        };
        speechStream.Seek(0);
        var preparation = await transcoder.PrepareStreamTranscodeAsync(speechStream, waveStream, profile);
        if (!preparation.CanTranscode)
        {
            throw new InvalidOperationException($"Windows could not convert the synthesized utterance to master PCM: {preparation.FailureReason}.");
        }

        await preparation.TranscodeAsync();
        cancellationToken.ThrowIfCancellationRequested();
        waveStream.Seek(0);
        var waveBytes = await ReadAllBytesAsync(waveStream, cancellationToken);
        return PcmWaveFile.ReadMasterPcm16Mono(waveBytes);
    }

    /// <summary>
    /// Encodes a closed master WAVE file as an audio-only MPEG-4/AAC file with an MP4 extension.
    /// </summary>
    /// <param name="masterWavePath">The closed source WAVE file path.</param>
    /// <param name="outputMp4Path">The destination MP4 path.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An asynchronous task.</returns>
    public static async Task EncodeAudioOnlyMp4Async(string masterWavePath, string outputMp4Path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(masterWavePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputMp4Path);
        cancellationToken.ThrowIfCancellationRequested();

        var outputDirectory = Path.GetDirectoryName(outputMp4Path);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new InvalidOperationException("The MP4 destination must have a directory.");
        }

        var sourceFile = await StorageFile.GetFileFromPathAsync(masterWavePath);
        var destinationFolder = await StorageFolder.GetFolderFromPathAsync(outputDirectory);
        var destinationFile = await destinationFolder.CreateFileAsync(
            Path.GetFileName(outputMp4Path),
            CreationCollisionOption.ReplaceExisting);

        // The source is a closed and re-opened read-only stream, as required by PrepareStreamTranscodeAsync.
        using var sourceStream = await sourceFile.OpenReadAsync();
        using var destinationStream = await destinationFile.OpenAsync(FileAccessMode.ReadWrite);
        var profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High);
        profile.Audio = AudioEncodingProperties.CreateAac(Pcm16Audio.SampleRate, Pcm16Audio.Channels, AacBitrate);
        profile.Video = null;

        var transcoder = new MediaTranscoder
        {
            AlwaysReencode = true,
        };
        var preparation = await transcoder.PrepareStreamTranscodeAsync(sourceStream, destinationStream, profile);
        if (!preparation.CanTranscode)
        {
            throw new InvalidOperationException($"Windows could not encode '{Path.GetFileName(outputMp4Path)}' as audio-only AAC/MP4: {preparation.FailureReason}.");
        }

        await preparation.TranscodeAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Inspects a completed MP4 using Windows media APIs.
    /// </summary>
    /// <param name="mp4Path">The MP4 file path.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The decoded duration and media track counts.</returns>
    public static async Task<MediaInspection> InspectAsync(string mp4Path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mp4Path);
        cancellationToken.ThrowIfCancellationRequested();

        var file = await StorageFile.GetFileFromPathAsync(mp4Path);
        using var mediaSource = MediaSource.CreateFromStorageFile(file);
        await mediaSource.OpenAsync();
        var duration = mediaSource.Duration ?? throw new InvalidDataException("Windows media inspection did not report an MP4 duration.");
        var profile = await MediaEncodingProfile.CreateFromFileAsync(file);
        cancellationToken.ThrowIfCancellationRequested();
        return new MediaInspection(
            profile.GetAudioTracks().Count,
            profile.GetVideoTracks().Count,
            duration.TotalSeconds,
            profile.Container?.Subtype,
            profile.Audio?.Subtype);
    }

    /// <summary>
    /// Releases the selected Windows speech synthesizer.
    /// </summary>
    public void Dispose()
    {
        synthesizer.Dispose();
    }

    private static VoiceInformation[] GetEnglishVoices()
    {
        return SpeechSynthesizer.AllVoices
            .Where(voice => IsEnglish(voice.Language))
            .OrderBy(voice => voice.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsEnglish(string language)
    {
        return string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("en-", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildVoiceSelectionFailure(string? requestedVoiceId, IReadOnlyList<VoiceInformation> englishVoices)
    {
        var requested = requestedVoiceId is null
            ? "No --voice-id (or WIN_BULK_TRANSCRIPT_TTS_VOICE_ID) was configured"
            : $"Configured voice '{requestedVoiceId}' is not an installed English voice";
        var available = string.Join(
            Environment.NewLine,
            englishVoices.Select(voice => $"  {voice.Id} | {voice.DisplayName} | {voice.Language} | {voice.Gender}"));
        return $"{requested}. Supply a listed --voice-id, or deliberately pass --allow-first-english-voice.{Environment.NewLine}Installed English voices:{Environment.NewLine}{available}";
    }

    private static async Task<byte[]> ReadAllBytesAsync(InMemoryRandomAccessStream stream, CancellationToken cancellationToken)
    {
        if (stream.Size > int.MaxValue)
        {
            throw new InvalidDataException("The transcoded utterance is too large to read into memory.");
        }

        using var input = stream.GetInputStreamAt(0);
        using var reader = new DataReader(input);
        var requested = checked((uint)stream.Size);
        var read = await reader.LoadAsync(requested);
        cancellationToken.ThrowIfCancellationRequested();
        if (read != requested)
        {
            throw new EndOfStreamException($"Only {read} of {requested} bytes were read from the transcoded utterance.");
        }

        var bytes = new byte[checked((int)stream.Size)];
        reader.ReadBytes(bytes);
        return bytes;
    }
}

/// <summary>
/// An installed Windows voice suitable for console display.
/// </summary>
/// <param name="Id">The installed voice ID.</param>
/// <param name="DisplayName">The voice display name.</param>
/// <param name="Language">The BCP-47 language tag.</param>
/// <param name="Gender">The reported voice gender.</param>
internal sealed record InstalledVoice(string Id, string DisplayName, string Language, string Gender);

/// <summary>
/// A decoded MP4 inspection result.
/// </summary>
/// <param name="AudioTrackCount">The number of audio tracks.</param>
/// <param name="VideoTrackCount">The number of video tracks.</param>
/// <param name="DurationSeconds">The decoded duration in seconds.</param>
/// <param name="ContainerSubtype">The Windows media container subtype reported from the encoded file.</param>
/// <param name="AudioSubtype">The Windows media audio codec subtype reported from the encoded file.</param>
internal sealed record MediaInspection(
    int AudioTrackCount, int VideoTrackCount, double DurationSeconds, string? ContainerSubtype, string? AudioSubtype);
