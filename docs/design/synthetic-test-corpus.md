# Synthetic MP4 test corpus

Research snapshot: 2026-08-06.

## Purpose

Phase 0.5 creates a repeatable functional corpus before the production media, VAD, and ASR pipeline is built. It should exercise:

- audio-only MP4 input with no video stream;
- 30-120 second file durations;
- multiple 5-10 second speech utterances separated by known silence;
- VAD boundary recovery and no-speech-gap handling;
- deterministic expected text for ASR comparison;
- recursive input discovery and relative output-path mirroring; and
- identical media presented in flat and nested directory layouts.

This corpus is a controlled smoke/evaluation set. It does not replace rights-cleared real recordings, noisy-background tests, codec-error fixtures, or accessibility testing.

## Windows-native generation approach

Use a development-only .NET 10 Windows corpus generator with no cloud service and no third-party TTS runtime. `Windows.Media.SpeechSynthesis.SpeechSynthesizer` exposes installed Microsoft-signed voices and can synthesize a string into a `SpeechSynthesisStream`; see Microsoft's [`SpeechSynthesizer` documentation](https://learn.microsoft.com/en-us/uwp/api/windows.media.speechsynthesis.speechsynthesizer?view=winrt-26100) and [`SynthesizeTextToStreamAsync`](https://learn.microsoft.com/en-us/uwp/api/windows.media.speechsynthesis.speechsynthesizer.synthesizetexttostreamasync?view=winrt-26100).

Generate utterances individually rather than synthesizing an entire file in one call. Convert each result to the master PCM contract, concatenate it with explicitly generated silence, and record each boundary from the PCM sample count. This makes the intended speech intervals known independently of VAD.

The master assembly format is PCM16, 16 kHz, mono. Write the completed master WAV to a temporary file and use Windows `MediaTranscoder` to encode an audio-only MPEG-4/AAC file with an `.mp4` extension. [`PrepareStreamTranscodeAsync`](https://learn.microsoft.com/en-us/uwp/api/windows.media.transcoding.mediatranscoder.preparestreamtranscodeasync?view=winrt-26100) supports stream-to-stream transcoding, although its source must be read-only rather than a writable in-memory stream; using a closed-and-reopened temporary WAV satisfies that contract.

The generated MP4 must have exactly one audio track and zero video tracks. Validate this after encoding with Windows media inspection APIs. Do not depend on FFmpeg or `ffprobe` for generation or acceptance.

## Determinism

Use one explicit corpus seed, stored in the manifest and accepted as a generator option. Every semirandom choice must come from that seeded generator:

- target file duration;
- sentence templates and vocabulary;
- utterance count and text length;
- inter-utterance silence;
- optional leading/trailing silence; and
- nested destination depth and folder names.

Given the same generated flat media set and seed, nested placement must be identical. TTS waveforms may differ if the installed Windows voice changes, so the manifest records the actual voice ID, display name, language, gender, synthesis options, OS build, and generator version. Prefer a configured English voice ID; if unavailable, fail with the installed English voice list rather than silently choosing a different language. A deliberately enabled fallback may select the first English voice in stable ID order and must record that substitution.

The generated MP4 files and their manifest become the test artifact. Regeneration is an intentional operation; normal tests consume the existing artifact rather than silently recreating it.

## Text construction

Build semirandom but natural English from a checked-in sentence-template and vocabulary set. Keep the baseline text friendly to deterministic comparison:

- common English words;
- complete declarative or interrogative sentences;
- no names whose pronunciation varies by voice;
- no abbreviations, symbols, dates, currency, or digit normalization in the baseline set;
- no profanity or copyrighted passages; and
- enough lexical variety to avoid repeating one memorized phrase.

Synthesize one candidate utterance, measure its actual PCM duration, and accept it only when it is between 5.0 and 10.0 seconds. If it is too short or long, adjust its word count deterministically and resynthesize. Store both the authored text and a normalized expected text: Unicode-normalized, invariant lowercase, punctuation removed, and whitespace collapsed. ASR assertions use the normalized form while retaining the authored form for inspection.

Use deterministic silence ranges that exercise VAD without dominating the files:

- leading silence: 0.0-2.0 seconds;
- between utterances: 0.75-3.0 seconds; and
- trailing silence: 0.0-2.0 seconds.

The final decoded duration of each MP4 must be between 30 and 120 seconds. Distribute targets across the range instead of relying on 30 independent uniform samples: ten short files, ten medium files, and ten long files. This prevents an unlucky seed from clustering durations.

## Directory layout

The generated artifact uses this shape:

```text
test-assets/synthetic/
  corpus-manifest.json
  expected-vtt/
    fixture-001.vtt
    ...
    fixture-030.vtt
  flat/
    fixture-001.mp4
    ...
    fixture-030.mp4
  nested/
    <random-folder>/fixture-....mp4
    <random-folder>/<random-folder>/fixture-....mp4
    <random-folder>/<random-folder>/<random-folder>/fixture-....mp4
  retained-master-pcm/                 (only with --retain-master-pcm)
    fixture-001.wav
    ...
    fixture-030.wav
```

Create exactly 30 uniquely named MP4 files in `flat`. Copy every flat file exactly once into `nested`; do not regenerate or select only a subset. Choose a deterministic random depth of one, two, or three directories and deterministic filesystem-safe folder names. Force coverage so at least several files occur at every depth, regardless of the seed.

The nested copy must be byte-identical to its flat source. Record its relative path and SHA-256, then verify the copy hash. The expected VTT is stored outside both input directories so it cannot be mistaken for an existing output during collision-policy tests.

When `--retain-master-pcm` is requested, publish one master PCM16/16 kHz/mono WAV per fixture in `retained-master-pcm`. This optional evidence root is outside the MP4 input layouts. It preserves the exact byte stream whose sample coordinates were used while assembling the manifest; it is not a decoded AAC artifact.

## Manifest contract

`corpus-manifest.json` should contain corpus-level metadata plus one record per fixture.

Corpus metadata:

- schema version and generator version;
- random seed;
- generation timestamp;
- Windows build and architecture;
- selected TTS voice and options;
- master PCM and output codec/container properties; and
- normalization rules.

Fixture metadata:

- stable fixture ID and file name;
- flat path, nested relative path, and SHA-256;
- `masterPcmDataSha256`, the SHA-256 of raw master PCM16 data bytes;
- short, medium, or long target-duration coverage band (ten fixtures each);
- requested target, final master-PCM, and measured encoded/decoded durations;
- audio-track count and video-track count;
- ordered utterances with authored and normalized text;
- utterance start/end master sample indices and seconds;
- leading, inter-utterance, and trailing silence durations; and
- expected VTT relative path.

Expected VTT timestamps come from master PCM sample indices. AAC encoder delay and decode rounding can shift recovered boundaries slightly, so encoded-media VAD diagnostics retain decoded intervals separately rather than score them against master intervals. Hash-bound retained-master WAVs can support same-coordinate measurements, but manifest intervals still delimit synthesized TTS chunks (including configured `SpeechAppendedSilence.Default`) rather than independently labeled acoustic speech onset/offset. Therefore the Phase 2 VAD quality gate remains pending acoustic ground truth. Text and file-placement assertions do not need timing tolerance.

## Corpus acceptance checks

The generator succeeds only when all of these checks pass:

1. Exactly 30 MP4 files exist in `flat` and exactly 30 beneath `nested`.
2. Every flat file has one and only one nested copy with the same SHA-256.
3. Every nested relative path is one to three directories deep, with coverage at all three depths.
4. Every MP4 has exactly one audio track and no video track; Windows media inspection reports MPEG-4 container and AAC audio subtypes; and recorded track metadata agrees with that inspection.
5. The manifest records exactly ten short, ten medium, and ten long target-duration fixtures.
6. Every decoded duration is between 30 and 120 seconds.
7. Every speech utterance is between 5 and 10 seconds in the master PCM.
8. Speech intervals are ordered, non-overlapping, and within the file duration.
9. Each authored/normalized transcript and expected VTT agrees with the manifest.
10. File and directory names are valid on Windows and exercise some spaces and Unicode without exceeding conservative path lengths.
11. A second nested-layout run with the same seed produces the same placement map.
12. When retained master PCM is requested, every expected retained WAV has the manifest sample count and raw PCM data SHA-256.

## How the corpus is used

- Run the flat directory through the application and compare 30 output VTTs with normalized expected text and tolerant cue boundaries.
- Run the nested directory and confirm the 30 output VTTs reproduce the same relative tree.
- Compare each flat/nested pair's transcription result to catch traversal or path-mapping differences.
- Run the VAD evaluator's `--source both` encoded-media diagnostic to compare the flat/nested decoded PCM and returned interval sequences automatically; use hash-bound retained master PCM only for synthesis-chunk measurements, not Phase 2 gate completion.
- Prepopulate selected output VTTs to exercise the batch-wide Skip existing, Overwrite all, and Cancel choices.
- Keep the separate [malformed, no-audio, empty-audio, and unsupported-codec fixture matrix](../../test-assets/media-fixture-matrix.md) for failure-path tests; they do not count toward these 30 valid files.

