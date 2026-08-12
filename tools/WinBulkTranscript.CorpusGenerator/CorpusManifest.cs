namespace WinBulkTranscript.CorpusGenerator;

/// <summary>
/// JSON-serializable ground truth for a generated corpus.
/// </summary>
internal sealed class CorpusManifest
{
    /// <summary>Gets or sets the manifest schema version.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Gets or sets the generator version.</summary>
    public required string GeneratorVersion { get; set; }

    /// <summary>Gets or sets the deterministic corpus seed.</summary>
    public ulong RandomSeed { get; set; }

    /// <summary>Gets or sets the UTC generation timestamp.</summary>
    public required string GenerationTimestampUtc { get; set; }

    /// <summary>Gets or sets the host metadata.</summary>
    public required HostManifest Host { get; set; }

    /// <summary>Gets or sets the selected TTS voice metadata.</summary>
    public required VoiceManifest Voice { get; set; }

    /// <summary>Gets or sets the master PCM contract.</summary>
    public required PcmManifest MasterPcm { get; set; }

    /// <summary>Gets or sets the encoded output properties.</summary>
    public required OutputMediaManifest OutputMedia { get; set; }

    /// <summary>Gets or sets the expected-text normalization contract.</summary>
    public required NormalizationManifest Normalization { get; set; }

    /// <summary>Gets or sets the initial VAD timing tolerance for encoded-media round trips.</summary>
    public int VadTimingToleranceMilliseconds { get; set; }

    /// <summary>Gets or sets the complete fixture list.</summary>
    public List<FixtureManifest> Fixtures { get; set; } = [];
}

/// <summary>
/// Records the Windows host used for a waveform-producing run.
/// </summary>
internal sealed class HostManifest
{
    /// <summary>Gets or sets the Windows version/build string.</summary>
    public required string WindowsBuild { get; set; }

    /// <summary>Gets or sets the process architecture.</summary>
    public required string Architecture { get; set; }
}

/// <summary>
/// Records the installed voice and synthesis choices that influence generated waveforms.
/// </summary>
internal sealed class VoiceManifest
{
    /// <summary>Gets or sets the configured voice ID, when one was supplied.</summary>
    public string? RequestedVoiceId { get; set; }

    /// <summary>Gets or sets a value indicating whether stable English fallback was used.</summary>
    public bool FallbackUsed { get; set; }

    /// <summary>Gets or sets the installed voice ID.</summary>
    public required string Id { get; set; }

    /// <summary>Gets or sets the installed voice display name.</summary>
    public required string DisplayName { get; set; }

    /// <summary>Gets or sets the installed voice language.</summary>
    public required string Language { get; set; }

    /// <summary>Gets or sets the installed voice gender.</summary>
    public required string Gender { get; set; }

    /// <summary>Gets or sets the speech synthesis options.</summary>
    public required SynthesisOptionsManifest Options { get; set; }
}

/// <summary>
/// Records the stable speech-synthesis option values used by the generator.
/// </summary>
internal sealed class SynthesisOptionsManifest
{
    /// <summary>Gets or sets the speaking rate.</summary>
    public double SpeakingRate { get; set; }

    /// <summary>Gets or sets the audio pitch.</summary>
    public double AudioPitch { get; set; }

    /// <summary>Gets or sets the audio volume.</summary>
    public double AudioVolume { get; set; }
    public required string AppendedSilence { get; set; }
    public bool IncludeSentenceBoundaryMetadata { get; set; }
    public bool IncludeWordBoundaryMetadata { get; set; }
    public required string PunctuationSilence { get; set; }
}

/// <summary>
/// Records the PCM assembly format.
/// </summary>
internal sealed class PcmManifest
{
    /// <summary>Gets or sets the sample encoding name.</summary>
    public required string Encoding { get; set; }

    /// <summary>Gets or sets the sample rate.</summary>
    public int SampleRate { get; set; }

    /// <summary>Gets or sets the channel count.</summary>
    public int Channels { get; set; }

    /// <summary>Gets or sets the bits per sample.</summary>
    public int BitsPerSample { get; set; }
}

/// <summary>
/// Records the requested audio-only encoded media contract.
/// </summary>
internal sealed class OutputMediaManifest
{
    /// <summary>Gets or sets the media container.</summary>
    public required string Container { get; set; }

    /// <summary>Gets or sets the audio codec.</summary>
    public required string AudioCodec { get; set; }

    /// <summary>Gets or sets the file extension.</summary>
    public required string FileExtension { get; set; }

    /// <summary>Gets or sets the target audio sample rate.</summary>
    public int SampleRate { get; set; }

    /// <summary>Gets or sets the target audio channel count.</summary>
    public int Channels { get; set; }

    /// <summary>Gets or sets the target encoded bitrate.</summary>
    public int Bitrate { get; set; }
}

/// <summary>
/// Records how authored text becomes ASR comparison text.
/// </summary>
internal sealed class NormalizationManifest
{
    /// <summary>Gets or sets the Unicode normalization form.</summary>
    public required string UnicodeNormalization { get; set; }

    /// <summary>Gets or sets the casing rule.</summary>
    public required string Casing { get; set; }

    /// <summary>Gets or sets the punctuation rule.</summary>
    public required string Punctuation { get; set; }

    /// <summary>Gets or sets the whitespace rule.</summary>
    public required string Whitespace { get; set; }
}

/// <summary>
/// Records one source MP4, its nested copy, and all sample-index ground truth.
/// </summary>
internal sealed class FixtureManifest
{
    /// <summary>Gets or sets the stable fixture ID.</summary>
    public required string FixtureId { get; set; }

    /// <summary>Gets or sets the media file name.</summary>
    public required string FileName { get; set; }

    /// <summary>Gets or sets the flat corpus-relative media path.</summary>
    public required string FlatPath { get; set; }

    /// <summary>Gets or sets the nested corpus-relative media path.</summary>
    public required string NestedRelativePath { get; set; }

    /// <summary>Gets or sets the common flat/nested SHA-256 hash.</summary>
    public required string Sha256 { get; set; }

    /// <summary>Gets or sets the SHA-256 of master PCM16 data bytes used by the ground-truth sample coordinates.</summary>
    public required string MasterPcmDataSha256 { get; set; }

    /// <summary>Gets or sets the planned short, medium, or long duration-coverage band.</summary>
    public required string DurationCoverageBand { get; set; }

    /// <summary>Gets or sets the pre-assembly target duration selected by the seeded fixture plan.</summary>
    public double RequestedTargetDurationSeconds { get; set; }

    /// <summary>Gets or sets the final master-PCM duration after deterministic silence allocation.</summary>
    public double TargetDurationSeconds { get; set; }

    /// <summary>Gets or sets the measured decoded media duration in seconds.</summary>
    public double DecodedDurationSeconds { get; set; }

    /// <summary>Gets or sets the master PCM duration in samples.</summary>
    public long MasterDurationSamples { get; set; }

    /// <summary>Gets or sets the master PCM duration in seconds.</summary>
    public double MasterDurationSeconds { get; set; }

    /// <summary>Gets or sets the detected audio-track count.</summary>
    public int AudioTrackCount { get; set; }

    /// <summary>Gets or sets the detected video-track count.</summary>
    public int VideoTrackCount { get; set; }

    /// <summary>Gets or sets the leading silence details.</summary>
    public required SilenceManifest LeadingSilence { get; set; }

    /// <summary>Gets or sets the inter-utterance silence details in utterance order.</summary>
    public List<SilenceManifest> InterUtteranceSilences { get; set; } = [];

    /// <summary>Gets or sets the trailing silence details.</summary>
    public required SilenceManifest TrailingSilence { get; set; }

    /// <summary>Gets or sets the ordered utterances.</summary>
    public List<UtteranceManifest> Utterances { get; set; } = [];

    /// <summary>Gets or sets the expected VTT corpus-relative path.</summary>
    public required string ExpectedVttPath { get; set; }
}

/// <summary>
/// Records a known silent sample range length.
/// </summary>
internal sealed class SilenceManifest
{
    /// <summary>Gets or sets the silence duration in samples.</summary>
    public long Samples { get; set; }

    /// <summary>Gets or sets the silence duration in seconds.</summary>
    public double Seconds { get; set; }
}

/// <summary>
/// Records one authored speech interval in master sample coordinates.
/// </summary>
internal sealed class UtteranceManifest
{
    /// <summary>Gets or sets the human-readable authored utterance.</summary>
    public required string AuthoredText { get; set; }

    /// <summary>Gets or sets the normalized expected ASR comparison text.</summary>
    public required string NormalizedExpectedText { get; set; }

    /// <summary>Gets or sets the inclusive start sample index.</summary>
    public long StartSample { get; set; }

    /// <summary>Gets or sets the exclusive end sample index.</summary>
    public long EndSample { get; set; }

    /// <summary>Gets or sets the start time in seconds.</summary>
    public double StartSeconds { get; set; }

    /// <summary>Gets or sets the end time in seconds.</summary>
    public double EndSeconds { get; set; }
}
