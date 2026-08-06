# Review of `win-audio-grader`

Reviewed read-only from `C:\Users\holcm\work\win-audio-grader` on 2026-08-06. This is a local implementation review, so file references below are evidence locations rather than web citations.

## What the existing application solves

`win-audio-grader` is a broader application than the proposed batch utility. It handles microphone and file workflows, sessions, persistence, playback-oriented state, more than one inference path, model/provider policy, and a much larger UI. Its layered solution separates Core, Audio, Inference, Persistence, and UI.

That structure is understandable for its scope but should not become the template for this narrower product.

## Useful lessons to carry forward

### Direct Media Foundation decoding works but is expensive to own

`src/WinAudioGrader.Audio/Decode/MfSourceReaderMediaFileDecoder.cs` proves that a Source Reader can deselect video, request PCM, handle media-type changes, stream through a bounded channel, and cancel cleanly. `MfNative.cs` shows the cost: a large hand-authored COM surface and careful lifetime management. The tests cover a real MP4 smoke fixture, prompt disposal, and cancellation.

For the new application, Windows `MediaTranscoder` plus a temporary PCM WAV gives up some I/O efficiency in exchange for dramatically less interop code. A direct Source Reader remains a measured fallback only if the Phase 1 transcoding spike cannot reliably produce the required PCM format.

### The hysteresis segmenter is the right abstraction

`Core/Pipeline/HysteresisSpeechSegmenter.cs` separates frame scoring from segment-state decisions. It handles onset confirmation, different positive/negative thresholds, minimum speech and silence, padding, maximum length, and flush behavior. That separation is worth retaining conceptually.

The new implementation should pair a smaller, pure managed energy scorer with a similarly deterministic segmenter. Its tests should use scripted scores, as the existing VAD tests do.

### Silero VAD conflicts with the new dependency budget

`Inference/Vad/SileroOnnxFrameScorer.cs` requires an explicit ONNX Runtime package and a shipped `silero_vad.onnx`. It may produce better classification in difficult audio, but it adds a native runtime, a model asset, architecture-specific validation, and another licensing/update surface.

The first release should start with adaptive energy VAD and preserve an interface seam. Silero or another neural VAD becomes a data-driven fallback if corpus testing proves the simple detector inadequate.

### Exact variant selection should replace provider policy

`FoundryLocalHost.cs` and `FoundryEpPolicy.cs` contain extensive execution-provider discovery, ranking, diagnostics, and CUDA handling. None of that is needed when each app release specifies one tested CPU variant. The new host should resolve the release-pinned ID (`nemotron-speech-streaming-en-0.6b-generic-cpu:3` is the initial candidate), verify it, load it, and fail clearly if it is unavailable.

The local repository guidance also distinguishes `GetModelAsync` for aliases from `GetModelVariantAsync` for full package IDs. That agrees with the current official SDK documentation and prevents accidental hardware-driven variant selection.

### Streaming text should be accumulated conservatively

`Inference/Asr/FoundryLocalStreamingAsr.cs` records a regression caused by trying to merge text using token-prefix assumptions. The new recognizer should append non-empty emitted text in order. Nemotron responses are currently described as final by the SDK, but the adapter should still keep SDK-specific response interpretation localized.

### WebVTT rules are small and testable

`WebVttTranscriptFormatter.cs` and its tests demonstrate a useful narrow boundary: skip empty text, sanitize the cue delimiter, format timestamps consistently, and force end after start. The new writer should preserve those behaviors and add safe temporary-file commit semantics.

## Complexity to leave behind

- The multi-project Core/Audio/Inference/Persistence/UI split.
- Session database and history management.
- Microphone capture and audio-device handling.
- Playback and transcript-detail views.
- Multiple inference providers and EP ranking.
- GPU/NPU/CUDA probing and fallback policy.
- Per-session/model configuration UI.
- The large session manager view model.
- Neural VAD and its direct ONNX Runtime dependency in version 1.

## Recommended reuse policy

Reuse behavior and tests, not source files wholesale. The Media Foundation implementation is a fallback reference; the VAD state-machine cases and WebVTT edge cases can inspire new focused tests. Avoid copying namespace structure, general-purpose abstractions, or provider-policy code into the clean workspace.
