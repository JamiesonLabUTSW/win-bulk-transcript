# Construction and implementation plan

This plan intentionally delays broad UI work until the riskiest native/model assumptions are proven. Each phase ends with an acceptance gate; failure changes the plan before more code accumulates.

## Phase 0 — compatibility spikes

Create disposable proof code, not product architecture, to answer the unresolved platform questions.

1. Create minimal x64 and ARM64 .NET 10 processes using `Microsoft.AI.Foundry.Local` 1.2.4.
2. Start with `nemotron-speech-streaming-en-0.6b-generic-cpu:3`, identify a CPU variant version supported on both architectures, pin it for the application release, and verify CPU identity through exact-variant lookup.
3. Download/load the model and transcribe a known 16 kHz mono PCM fixture.
4. Stream audio without artificial delays; confirm bounded `AppendAsync` backpressure and complete output.
5. Measure per-segment session startup with many short segments.
6. Cancel during download, append, and stream reading; verify a later session still works.
7. Publish a tiny self-contained unpackaged x64 and ARM64 app and run it on clean machines.
8. Try a single-file publish only as a deployment experiment; confirm Foundry native probing after extraction.

Gate: both architectures load the same release-pinned CPU model version and complete repeatable live transcription from a self-contained published build. If the core Foundry package fails but `.WinML` succeeds, record the evidence and change only that package choice. If no common CPU version supports ARM64 and x64, stop and resolve the product/model constraint rather than silently using different models.

## Phase 0.5 — synthetic audio-only MP4 corpus

Create a development-only, Windows-native fixture generator and the reproducible corpus defined in [Synthetic MP4 test corpus](design/synthetic-test-corpus.md).

1. Define a fixed-seed semirandom English text generator using controlled sentence templates and vocabulary.
2. Select and record an installed Microsoft English TTS voice through `Windows.Media.SpeechSynthesis.SpeechSynthesizer`.
3. Generate individual 5-10 second utterances, convert them to PCM16/16 kHz/mono, and concatenate them with known seeded silence.
4. Build 30 master files whose decoded durations span 30-120 seconds, with short, medium, and long coverage.
5. Record sample-index speech boundaries, authored text, normalized expected text, voice metadata, and generation parameters.
6. Encode each master as an audio-only MPEG-4/AAC `.mp4` with one audio track and no video track using Windows `MediaTranscoder`.
7. Write all 30 unique files to one flat input directory.
8. Copy every file exactly once into a second input tree, using seeded random paths one to three directories deep and guaranteeing coverage at each depth.
9. Write the corpus manifest and expected VTT files outside the two input roots; verify hashes, tracks, durations, boundaries, and deterministic nested placement.

Gate: both input roots contain the same 30 byte-identical valid MP4 fixtures; every file has no video track, is 30-120 seconds long, contains only 5-10 second known-text utterances separated by known silence, and has complete ground truth. The nested tree contains files at depths one, two, and three and is reproducible from the recorded seed.

## Phase 1 — solution skeleton and media extraction

1. Create the App, Core, and Core.Tests projects with pinned package versions and architecture targets.
2. Establish warnings, nullable reference types, deterministic builds, and architecture-specific publish profiles.
3. Implement the minimal audio extraction port with `MediaTranscoder`.
4. Implement a robust RIFF/WAVE reader for the supported PCM form.
5. Add cancellation, progress, temporary-file ownership, and cleanup.
6. Consume the Phase 0.5 corpus and add the separate invalid and no-audio MP4 fixture matrix.

Gate: supported MP4 fixtures produce validated PCM16/16 kHz/mono on x64 and ARM64, cancellation cleans up, and unsupported media fails with a useful reason. Direct Media Foundation is considered only if this gate fails for a reason `MediaTranscoder` cannot address.

## Phase 2 — VAD and timestamp engine

1. Implement allocation-free frame energy calculation.
2. Implement adaptive noise-floor estimation and threshold clamps.
3. Implement the hysteresis state machine, padding, merging, minimum duration, EOF flush, and maximum-duration split.
4. Express all intervals in integer sample indices.
5. Add deterministic scorer/segmenter tests and corpus-level measurements.
6. Select and document initial tuning values from the representative corpus.

Gate: no timestamp regressions, boundary/EOF cases pass, and corpus results meet an agreed missed-speech/false-positive threshold. If quality is insufficient, make a conscious neural-VAD dependency decision before integrating ASR.

## Phase 3 — Foundry adapter and WebVTT

1. Implement the exact-variant model host with download/load progress and explicit validation.
2. Implement one live session per VAD segment with concurrent response consumption and bounded PCM appends.
3. Read segment ranges directly from the temporary PCM data chunk.
4. Implement conservative text accumulation and useful segment errors.
5. Implement WebVTT formatting, sanitization, monotonic timestamp guards, UTF-8 output, and header-only output.
6. Implement same-directory temporary write and final commit.
7. Add fake-recognizer workflow tests plus opt-in end-to-end model tests.

Gate: a fixture MP4 produces deterministic cue timing and valid VTT; empty and failed recognition never corrupt an existing final file; cancellation disposes the session and removes temporary files.

## Phase 4 — sequential batch coordinator

1. Recursively snapshot MP4 files without following reparse points, and sort them by relative path.
2. Map relative input paths beneath the output root and preflight all collisions.
3. If collisions exist, show one Skip existing / Overwrite all / Cancel prompt and apply the answer batch-wide.
4. Load the model once, then process files sequentially.
5. Implement state transitions, including Cancelled, and weighted stage progress.
6. Continue after per-file failures; stop on shared preflight/model failure.
7. Add batch cancellation and close-request coordination.
8. Coalesce progress events to a UI-friendly rate.

Gate: mixed success/failure batches yield correct row states, never process two files concurrently, and leave no partial outputs or PCM temporary files after cancel/failure.

## Phase 5 — WinUI 3 screen

1. Build the single-page layout with two folder-picker rows.
2. Add inline validation and MP4 count.
3. Add Start, Cancel/Cancelling, current-stage text, one progress bar, and batch count.
4. Add a virtualized `ListView` with accessible icon+text states.
5. Add `InfoBar` for fatal errors and final summary.
6. Implement dispatcher-safe snapshot mapping and focus/accessibility behavior.
7. Test window resizing, 100-200% display scale, high contrast, keyboard-only use, long paths, and long localized strings.

Gate: the workflow is understandable without documentation, invalid submission is impossible, the UI remains responsive during decode/model work, and status is not dependent on color.

## Phase 6 — publishing and release hardening

1. Produce self-contained unpackaged x64 and ARM64 folder builds and ZIP artifacts.
2. Verify clean-machine launch with no developer runtime or Foundry CLI.
3. Verify first-run download, offline cached launch, and offline uncached error.
4. Test SmartScreen/Mark-of-the-Web behavior and document the unsigned warning.
5. Run long-file, low-disk-space, read-only output, Unicode path, cancellation, sleep/resume, and window-close cases.
6. Generate third-party notices from the packages and model version actually shipped.
7. Retain ZIP/folder deployment for version 1; record single-file experiment results only as future information.
8. Record build commands, artifact checksums, model ID, package lock, and test matrix for each release.

Gate: both architecture artifacts pass clean-machine acceptance, no unsupported runtime prerequisite is hidden, model/license notices are present, and release notes state the unsigned SmartScreen implications.

## Testing layers

- Fast core tests: frame math, segment state machine, timestamps, VTT, job transitions, progress monotonicity.
- Adapter tests: RIFF parsing, file cleanup, exact model lookup behavior using fakes around the boundary where possible.
- Windows media integration tests: real rights-cleared MP4 fixtures.
- Foundry integration tests: opt-in because model download/runtime cost is high.
- Published-artifact smoke tests: clean x64 and ARM64 machines.
- Corpus evaluation: speech detection and transcript usefulness, tracked separately from deterministic unit correctness.

## Explicit non-goals during construction

Do not add microphone capture, playback, transcript editing, SQLite, multi-window navigation, plugin architecture, multiple models, GPU/NPU probing, file-level parallelism, installer/updater infrastructure, or a general-purpose logging framework. A simple rolling text log may be added only if diagnostics during Phase 0 show it is necessary.
