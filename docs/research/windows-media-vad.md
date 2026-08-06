# Windows media and VAD research

Research snapshot: 2026-08-06.

## Recommended extraction path

Use `Windows.Media.Transcoding.MediaTranscoder` to convert each MP4 to a temporary WAV described by a custom PCM profile: 16,000 Hz, one channel, 16 bits per sample.

The Windows API follows a two-step contract: call [`PrepareFileTranscodeAsync`](https://learn.microsoft.com/en-us/uwp/api/windows.media.transcoding.mediatranscoder.preparefiletranscodeasync?view=winrt-26100), verify `CanTranscode`, and then call `TranscodeAsync`. Microsoft's [transcoding guide](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/transcode-media-files) documents progress reporting and cancellation. [`AudioEncodingProperties.CreatePcm`](https://learn.microsoft.com/en-us/uwp/api/windows.media.mediaproperties.audioencodingproperties.createpcm?view=winrt-26100) supplies the target sample rate, channel count, and bit depth.

Why this is the default:

- It is a Windows-native API available to a desktop WinUI app.
- It handles MP4 demuxing, source codec decode, remixing, and resampling.
- It exposes asynchronous preparation, progress, failure reasons, and cancellation.
- It avoids shipping FFmpeg, NAudio, or hundreds of lines of custom Media Foundation COM declarations.

The tradeoff is temporary disk I/O and a second read during VAD/ASR. For a sequential CPU application, that cost is acceptable until measurement says otherwise.

## Validation requirements

Do not assume that requesting PCM guarantees every source produces the exact desired layout. After transcoding:

- parse RIFF/WAVE chunks rather than skipping 44 bytes;
- require a supported PCM `fmt ` chunk;
- require 16 kHz, one channel, and 16 bits;
- find the `data` chunk and honor its declared length;
- reject truncated, RF64, compressed, or unexpected extensible forms unless explicitly implemented;
- calculate total samples from validated block alignment.

The Phase 1 fixture set should include H.264/AAC MP4, HEVC/AAC where the OS codec is present, different source sample rates, stereo, long files, no-audio MP4, corrupt/truncated input, Unicode paths, and a read-only source. Codec availability can vary with Windows edition and optional media components, so `PrepareFileTranscodeAsync` failure must become a useful per-file message.

## Why not `AudioGraph`

[`AudioGraph`](https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audiograph?view=winrt-26100) is excellent for real-time routing, effects, capture, and playback. Its file-input graph is oriented toward a running audio graph and floating-point audio. That introduces clocked playback semantics and more nodes/events than an offline conversion needs. It is not the cleanest batch decoder.

## Why not direct Media Foundation first

An `IMFSourceReader` pipeline can decode without writing a temporary WAV and may eventually improve throughput. The reviewed `win-audio-grader` proves it is viable. It also proves the ownership burden: COM interfaces, attributes, stream selection, native buffers, media-type changes, HRESULT mapping, and precise disposal.

Keep direct Source Reader decoding as a fallback with a specific trigger: `MediaTranscoder` cannot reliably generate PCM16/16 kHz/mono for the supported fixture corpus or its disk overhead is demonstrated to be unacceptable. Do not build both paths speculatively.

## VAD options considered

| Option | Dependencies | Offline fit | Decision |
|---|---|---|---|
| Adaptive RMS energy + hysteresis | None beyond .NET | Good for controlled speech; weak with music/noise | Version 1 default |
| Voice Capture DSP VAD | Native Windows COM/DSP | Capture/AEC-oriented and awkward for files | Do not use |
| Silero VAD via ONNX Runtime | Native runtime + model asset | Stronger classification | Deferred quality fallback |
| Use ASR model only, no VAD | No extra model | Violates requested segmentation and wastes CPU on silence | Reject |

The Voice Capture DSP VAD modes are documented in the native [`AEC_VAD_MODE` enumeration](https://learn.microsoft.com/en-us/windows/win32/api/wmcodecdsp/ne-wmcodecdsp-aec_vad_mode). They are tied to the Voice Capture DSP pipeline, not a simple “classify this PCM frame” Windows API.

## Adaptive energy detector

The detector should compute RMS over 20 ms PCM frames, convert to dBFS, and feed a hysteresis state machine. Its noise floor adapts slowly during confident silence and freezes during possible speech. Separate enter/exit margins prevent rapid toggling. Padding preserves consonants at speech boundaries; minimum duration rejects clicks; maximum duration bounds recognition sessions.

Use saturating or sufficiently wide accumulators when squaring PCM16 values. Handle the all-zero frame without `log10(0)`. Avoid allocating per frame: consume spans and update scalar state. Report progress every several hundred milliseconds of media, not every frame.

Test the scorer separately from segmentation. Scripted score tests should cover onset confirmation, short blips, padding clamp at file edges, silence-based close, flush at EOF, merge gap, maximum-duration split, and monotonically increasing non-overlapping output intervals.

## Quality boundary

An energy detector answers “is this acoustically active?” more reliably than “is this human speech?” It may pass music or sustained noise to ASR. That creates extra CPU and possibly junk cues, but it should not corrupt timing or crash the batch.

Before declaring version 1 quality acceptable, assemble a small rights-cleared corpus representative of the real inputs and label speech intervals. Measure missed speech, false-positive duration, segment fragmentation, and end-to-end VTT usefulness. If the results fail agreed thresholds, add a neural VAD behind the existing interface as a deliberate dependency decision.

