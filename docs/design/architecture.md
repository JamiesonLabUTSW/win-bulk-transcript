# Proposed architecture

## Design goals

The structure should make the Windows and Foundry integrations replaceable without turning a single-screen batch utility into a framework. The core processing rules must be deterministic and testable without a GPU, audio device, UI thread, or downloaded model.

The application processes one file at a time. That is a deliberate CPU policy: it limits memory, produces predictable thermals, makes cancellation understandable, and avoids competing Foundry sessions.

## Solution shape

```text
WinBulkTranscript.App (WinUI 3, Windows only)
  MainPage + MainViewModel
  WindowsMediaAudioExtractor
  FoundryLocalModelHost
  NemotronSegmentRecognizer
  App composition
                 |
                 v
WinBulkTranscript.Core (.NET 10)
  BatchTranscriptionCoordinator
  AdaptiveEnergyVoiceActivityDetector
  HysteresisSpeechSegmenter
  WebVttWriter
  domain records + small ports

WinBulkTranscript.Core.Tests
  VAD, timestamp, VTT, workflow, cancellation tests
```

Two production projects are enough. The Windows project owns all WinRT and Foundry types. The core project owns plain .NET types and rules. Manual composition in `App.xaml.cs` or one composition-root class replaces a dependency-injection package.

## Small ports at the boundary

The core coordinator needs four narrow capabilities:

- `IAudioExtractor`: turn one MP4 into a temporary PCM WAV and report extraction progress;
- `IVoiceActivityDetector`: scan PCM and return sample-accurate speech intervals;
- `ISpeechRecognizer`: transcribe one speech interval from the PCM source;
- `ITranscriptWriter`: commit completed cues to the final VTT path.

These are architectural descriptions, not prescribed signatures. Interfaces should stay task-shaped and accept `CancellationToken`. Do not create a generic media framework or repository layer.

## Primary components

### `BatchTranscriptionCoordinator`

Owns the sequential workflow and is the only component allowed to move a job through states. It performs batch preflight, asks the model host to load once, processes each file independently, and continues after a per-file failure. A model or invalid-folder failure is batch-fatal because no file can proceed.

The coordinator should expose immutable progress snapshots rather than UI types. The UI maps those snapshots onto observable job rows on the dispatcher thread.

### `WindowsMediaAudioExtractor`

Uses `Windows.Media.Transcoding.MediaTranscoder` to create a temporary WAV with PCM audio properties of 16,000 Hz, one channel, and 16 bits per sample. `PrepareFileTranscodeAsync` must succeed and report `CanTranscode` before work begins. Microsoft documents the prepare/transcode pattern, cancellation, and progress for this API in its [media transcoding guidance](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/transcode-media-files), while [`AudioEncodingProperties.CreatePcm`](https://learn.microsoft.com/en-us/uwp/api/windows.media.mediaproperties.audioencodingproperties.createpcm?view=winrt-26100) supplies the desired PCM properties.

The adapter validates the resulting WAV rather than assuming a fixed 44-byte header. It walks RIFF chunks, finds `fmt ` and `data`, and rejects output that is not PCM16/16 kHz/mono.

### `AdaptiveEnergyVoiceActivityDetector`

Uses only managed arithmetic over PCM16 frames. It estimates a noise floor, applies distinct speech-on and speech-off thresholds, and delegates state transitions to a small hysteresis segmenter. It produces sample-index intervals, not copied audio arrays.

The VAD is intentionally behind an interface. If energy VAD is inadequate for noisy or music-heavy material, a neural implementation can be added later without altering the batch workflow or output writer.

### `FoundryLocalModelHost`

Creates one `FoundryLocalManager`, resolves the CPU model variant pinned for this application release with `GetModelVariantAsync`, downloads it if necessary, validates that the selected variant is CPU-backed, and loads it once for the batch. It unloads during orderly shutdown. The initial candidate is `nemotron-speech-streaming-en-0.6b-generic-cpu:3`, but later releases may intentionally pin a newer tested CPU version. The official C# SDK distinguishes alias lookup from exact-variant lookup in the [Foundry Local C# SDK documentation](https://github.com/microsoft/Foundry-Local/tree/main/sdk/cs).

There is no execution-provider ranking, accelerator registration, model chooser, or runtime fallback to an untested variant. A failure to obtain the release-pinned CPU variant is explicit and actionable.

### `NemotronSegmentRecognizer`

Creates a live-transcription session for one VAD interval, starts a reader for the result stream, appends PCM in bounded 100 ms chunks, stops to flush, awaits the reader, and disposes the session. It joins non-empty response text in emission order. The Foundry guide requires raw 16 kHz mono PCM and demonstrates the live-session lifecycle in [Live transcribe audio with Foundry Local](https://learn.microsoft.com/en-us/azure/foundry-local/how-to/how-to-live-transcribe-audio).

Do not implement token-prefix de-duplication without a proven SDK need. The reviewed application had already encountered text regression from such logic; plain ordered accumulation is the safer baseline.

### `WebVttWriter`

Formats cues from VAD timestamps plus recognized text, writes a same-directory temporary file, flushes it, and then commits it to the requested final name. A failure never leaves a partial file under the final `.vtt` name.

## UI boundary

`MainPage` should stay thin. It owns the AppWindow-aware folder-picker calls and forwards Start/Cancel actions. `MainViewModel` owns bindable paths, validation, the job collection, current-stage text, progress, and command availability. Manual `INotifyPropertyChanged` and `ObservableCollection<T>` are sufficient; adding an MVVM toolkit only to avoid a small amount of boilerplate conflicts with the dependency goal.

The UI job states are:

- Pending
- Transcribing
- Complete
- Failed
- Cancelled

Internally, Transcribing has stage detail such as Extracting audio, Detecting speech, Transcribing 4 of 12, and Writing VTT. This preserves the requested simple state vocabulary without hiding current work.

## State and concurrency rules

- Exactly one batch may run.
- Exactly one file may be active.
- Exactly one segment-recognition session may append audio at a time.
- Folder controls and Start are disabled while running; Cancel remains available.
- All I/O and model work runs asynchronously off the UI path.
- Progress callbacks are coalesced before UI dispatch so a 20 ms VAD loop cannot flood the dispatcher.
- Job transitions are one-way: Pending -> Transcribing -> Complete, Failed, or Cancelled. Pending jobs become Cancelled when the user cancels the batch.
- Failed files do not abort later files unless the error indicates the shared model host is unusable.

## Data and memory behavior

The decoded WAV is temporary disk-backed data. VAD scans it once and stores only intervals. Recognition reopens or seeks within the data chunk and streams one interval in small chunks. No complete decoded file and no collection of segment byte arrays is retained in memory. A maximum segment duration also bounds one live session.

Temporary files should live in an application-specific subdirectory under the user's local temporary area, use unpredictable names, and be deleted in `finally`. Startup may best-effort remove stale files older than a conservative threshold. Final VTT temporary files live in the output directory so the commit stays on the same volume.

## Dependency budget

Expected direct runtime package references:

1. `Microsoft.WindowsAppSDK` — WinUI 3 and Windows desktop APIs.
2. `Microsoft.AI.Foundry.Local` — in-process model management and live transcription.

Test packages are development-only. Do not add direct FFmpeg, NAudio, ONNX Runtime, Silero, SQLite, logging, DI, or MVVM packages in the first implementation. The Foundry package has its own transitive dependencies; “minimal dependencies” means minimizing direct product choices, not claiming a zero-dependency executable.
