# Win Bulk Transcript planning index

Research snapshot: 2026-08-06.

This directory is the design record for a small Windows 11 desktop utility that:

1. accepts an input folder containing MP4 files and an output folder;
2. extracts 16 kHz, mono, 16-bit PCM with Windows media APIs;
3. identifies speech intervals with voice activity detection (VAD);
4. transcribes each interval with Foundry Local and a release-tested CPU variant of Nemotron Streaming ASR;
5. writes one WebVTT file per input file; and
6. shows a sequential job list with Pending, Transcribing, Complete, Failed, and Cancelled states.

## Fixed decisions

- Windows 11 only.
- WinUI 3 on .NET 10.
- Separate x64 and ARM64 builds.
- Unpackaged, self-contained deployment, initially without Authenticode signing.
- Windows-native media decoding/transcoding.
- Very few direct runtime dependencies.
- CPU execution only in the first version.
- Recursive input discovery, preserving the input directory structure beneath the output directory.
- A batch-level choice when existing VTT files are found: Skip existing, Overwrite all, or Cancel.
- Header-only VTT output is a successful result when no speech is detected.
- Cancelled is a distinct visible job state.
- Windows 11 24H2 is the minimum supported release.
- Initial distribution uses architecture-specific self-contained ZIP files, not a literal single-file executable.
- Adaptive energy VAD is the accepted version 1 detector.
- Documentation and planning only at this stage; no application implementation has been added.

## Recommended baseline

Use only two direct runtime packages: the stable Windows App SDK selected during implementation and `Microsoft.AI.Foundry.Local` 1.2.4. The latter is the CPU-capable core package; the separate `.WinML` package is not the default because this product does not need dynamic GPU/NPU execution-provider discovery. The official SDK reference says the core package can be used on Windows without WinML and is forward-compatible with .NET 10. Each application release pins a CPU model variant proven on x64 and ARM64; `:3` is the initial research candidate, not a permanent product requirement. This choice must pass the Phase 0 model-loading spike before it is locked. See [Foundry Local and Nemotron research](research/foundry-local-nemotron.md).

The proposed application has two production projects rather than reproducing the much larger layering in `win-audio-grader`:

- `WinBulkTranscript.Core`: workflow, domain state, VAD, timestamping, and WebVTT formatting;
- `WinBulkTranscript.App`: WinUI 3, Windows media, Foundry Local adapter, file pickers, and composition.

There is also one test project for deterministic core tests. No DI container, MVVM framework, FFmpeg, NAudio, direct ONNX Runtime package, neural VAD model, database, or CLI dependency is planned.

## Documents

- [Existing implementation review](research/existing-implementation-review.md) — reusable lessons and complexity to leave behind.
- [Foundry Local and Nemotron](research/foundry-local-nemotron.md) — release-pinned CPU model selection, live-transcription lifecycle, and validation risks.
- [Windows media and VAD](research/windows-media-vad.md) — MP4-to-PCM strategy and dependency-free VAD design.
- [WinUI and deployment](research/winui-deployment.md) — screen idioms, controls, x64/ARM64 publishing, and unsigned distribution.
- [Architecture](design/architecture.md) — boundaries, responsibilities, state, concurrency, and failure behavior.
- [Processing and WebVTT](design/processing-and-vtt.md) — end-to-end data flow, timestamps, progress, cancellation, and output rules.
- [Synthetic MP4 test corpus](design/synthetic-test-corpus.md) — deterministic TTS fixtures, ground truth, and flat/nested directory layouts.
- [Implementation plan](implementation-plan.md) — ordered construction phases and acceptance gates.
- [Resolved decisions and validation questions](open-questions.md) — confirmed product choices and the empirical questions remaining for Phase 0/1.

## Product boundary for version 1

Version 1 is intentionally a batch tool, not a general audio workbench. It does not include microphone capture, playback, transcript editing, session history, SQLite, multiple model choices, GPU/NPU selection, or concurrent file transcription. Those can be reconsidered only after the basic CPU path is correct and useful.
