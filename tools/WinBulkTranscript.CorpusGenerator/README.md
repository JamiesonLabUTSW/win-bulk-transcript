# Synthetic corpus generator

This development-only Windows tool creates the fixed-seed audio-only MP4 corpus described in [`../../docs/design/synthetic-test-corpus.md`](../../docs/design/synthetic-test-corpus.md). It uses installed Windows speech voices and Windows `MediaTranscoder`; it has no cloud dependency and does not use FFmpeg.

First list the installed English voice IDs:

```powershell
dotnet run --project tools/WinBulkTranscript.CorpusGenerator -- --list-voices
```

Generate the default `test-assets/synthetic` artifact using one explicitly chosen English voice:

```powershell
dotnet run --project tools/WinBulkTranscript.CorpusGenerator -- --voice-id '<installed voice ID>' --seed 20260806
```

To deliberately allow the stable first-English-voice fallback, add `--allow-first-english-voice`. The manifest records that substitution, along with the selected voice, options, Windows build, seed, track counts, durations, boundaries, hashes, paths, and expected VTT references.

The generator refuses to replace an existing corpus unless `--overwrite` is supplied. It builds in a staging directory, validates all 30 flat and 30 nested files, then publishes the staged corpus. TTS waveform bytes can still change when the installed Windows voice changes; regeneration is intentional and normal tests should consume the checked artifact rather than silently running this tool.

## Retained master PCM evidence

For VAD evidence that must use the manifest's master-PCM sample coordinates, add `--retain-master-pcm` during corpus generation:

```powershell
dotnet run --project tools/WinBulkTranscript.CorpusGenerator -- `
  --voice-id '<installed voice ID>' `
  --seed 20260806 `
  --retain-master-pcm
```

This publishes `retained-master-pcm/fixture-###.wav` beside the MP4 layouts. Each fixture manifest record contains `masterPcmDataSha256`, the SHA-256 of its raw PCM16 data bytes; staged validation verifies every retained WAV's format, data hash, and sample count before publishing. The [VAD evaluator](../WinBulkTranscript.VadEvaluator/README.md) accepts these WAVs for coordinate-valid synthesis-chunk measurements only. They are not independently labeled acoustic speech boundaries, so their report keeps the Phase 2 quality gate pending.


## Retained Phase 0 PCM fixture

Generate the known raw fixture used by the Foundry compatibility spike with the same explicitly selected voice:

```powershell
dotnet run --project tools/WinBulkTranscript.CorpusGenerator -- `
  --voice-id '<installed voice ID>' `
  --phase0-fixture C:\fixtures\phase0-known-en.pcm
```

This mode creates the PCM file once (`CreateNew`) plus a `<file>.json` provenance sidecar containing the fixed phrase, voice metadata, PCM format, duration, byte length, and SHA-256. Retain both files unchanged and use the same bytes for x64 and ARM64 Phase 0 runs. It does not generate or replace the MP4 corpus.

## Long cancellation-probe MP4

Generate a disposable, audio-only AAC/MP4 fixture for the real media-extraction cancellation probe:

```powershell
dotnet run --project tools/WinBulkTranscript.CorpusGenerator -- `
  --voice-id '<installed voice ID>' `
  --cancellation-fixture C:\fixtures\win-bulk-transcript-media\long-cancellation-probe.mp4
```

The opt-in fixture is exactly 20 minutes of PCM before Windows `MediaTranscoder` encodes it as AAC/MP4. It is intended to provide an in-flight native progress point for `WinBulkTranscript.MediaIntegrationProbe --cancel-input`; it is not part of the corpus or acceptance fixture matrix. The generator refuses to replace either the MP4 or its `<file>.json` provenance sidecar. The sidecar records the source text, selected voice, Windows host, PCM and encoded durations, media tracks/codecs, and SHA-256 hashes for both the master PCM data and output MP4. Delete the disposable pair after retaining the probe evidence.

## Media failure-fixture matrix

Create the five separately named, opt-in failure-path MP4s plus one provenance sidecar with an explicit installed voice:

```powershell
dotnet run --project tools/WinBulkTranscript.CorpusGenerator -- `
  --voice-id '<installed voice ID>' `
  --media-fixture-matrix C:\fixtures\win-bulk-transcript-media
```

The matrix root must be outside the repository's `test-assets` directory: these are opt-in evidence artifacts, not shipped test assets. The writer stages and CreateNew-publishes `malformed-truncated.mp4`, `no-audio-video-only.mp4`, `empty-audio-track.mp4`, `unsupported-audio-codec.mp4`, `valid-short-control.mp4`, and `media-fixture-matrix.provenance.json`. It validates native track layout for the valid control, video-only, and the pre-transformation empty-track source; it records hashes and construction details for every final binary.

The empty-track fixture starts from a native AAC source and retains exactly one audio sample description while zeroing movie, track, and media durations; `stts`, `stsc`, `stsz`/`stz2`, and chunk-offset entry counts; and any present optional sample-reference tables. After writing, the writer rereads the ISO-BMFF tree and refuses publication unless it proves one audio-only track, zero duration, and no sample references. Its provenance sidecar retains those post-mutation values and the source hash. Windows may reject it during extraction or produce header-only PCM; the matrix explicitly records either observed result through `MediaIntegrationProbe`. The unsupported-codec fixture preserves a native AAC file while replacing only the audio sample-entry type with an intentionally unknown four-character code.

This tooling establishes reproducible technical provenance without external media. It does not independently establish redistribution rights for generated Windows TTS output or replace the required internal artifact-store approval, architecture-specific reports, or clean-machine evidence. See the [matrix specification](../../test-assets/media-fixture-matrix.md) for those remaining requirements.
