# Processing pipeline and WebVTT rules

## End-to-end flow

For each MP4 found recursively beneath the selected input folder:

1. Resolve a stable input list and input-relative output paths.
2. Transcode the file to a temporary 16 kHz, mono, PCM16 WAV.
3. Validate and locate the WAV data chunk.
4. Scan 20 ms frames and produce speech intervals as PCM sample indices.
5. For each interval, stream its bytes to a new Nemotron live-transcription session in 100 ms chunks.
6. Pair the recognized text with the VAD interval.
7. Write all non-empty cues to a temporary VTT in the output folder.
8. Commit the final relative-path `<input-stem>.vtt` according to the batch-wide collision policy.
9. Delete the PCM temporary file in `finally`.

The input list is snapshotted before processing and sorted by relative path using an ordinal, case-insensitive comparison. This makes the visible order deterministic. Each output path preserves the input-relative directory and replaces the MP4 extension with `.vtt`. Input and output may be the same directory because generated VTT files are never part of the MP4 snapshot. Directory traversal must avoid following reparse points so junctions cannot cause cycles or escape the selected tree.

## PCM contract

The internal audio contract is signed 16-bit little-endian PCM, 16,000 samples per second, one channel. A 20 ms VAD frame is 320 samples or 640 bytes. A 100 ms Foundry append is 1,600 samples or 3,200 bytes.

All timing is derived from integer sample indices:

```text
seconds = sampleIndex / 16000
```

Intervals are half-open `[startSample, endSample)`. Integer sample positions avoid accumulated floating-point or frame-clock drift. Formatting rounds once, at the VTT boundary, to milliseconds. A cue end is clamped to be strictly later than its start.

## VAD behavior

The proposed dependency-free VAD computes RMS energy for each 20 ms frame and converts it to dBFS. It maintains an adaptive noise estimate only while the state machine is confidently silent. Speech starts above a higher threshold for several consecutive frames and ends below a lower threshold after sustained silence.

Initial tuning ranges, to be selected with a representative corpus rather than treated as constants:

| Parameter | Initial range |
|---|---:|
| Frame size | 20 ms |
| Speech onset confirmation | 60-120 ms |
| End-of-speech silence | 350-700 ms |
| Pre-roll | 150-250 ms |
| Post-roll | 150-300 ms |
| Minimum speech | 200-400 ms |
| Merge gap | 150-350 ms |
| Maximum segment | 20-30 s |
| On threshold over noise floor | 9-15 dB |
| Off threshold over noise floor | 5-10 dB |

The absolute threshold must be clamped so an initially silent file does not make tiny codec noise look like speech. When maximum duration splits continuous speech, the split should prefer a recent low-energy boundary and overlap a small amount if necessary to avoid losing a word.

Energy VAD is a simplicity tradeoff, not equivalent to a trained speech detector. Music, applause, or machinery can be classified as speech. The Phase 2 quality gate must test clean speech, quiet, background music, continuous noise, clipped speech, and long pauses. A later neural VAD remains a replaceable adapter if the dependency-free result is not acceptable.

Microsoft does expose VAD through its Voice Capture DSP, but it is designed around the capture/AEC media pipeline and represents detection through DSP-specific properties. The native documentation for [`MFPKEY_WMAAECMA_FEATR_VAD`](https://learn.microsoft.com/en-us/windows/win32/medfound/mfpkey-wmaaecma-featr-vadproperty) illustrates that coupling. It would add substantial COM and media-type complexity to an offline MP4 tool, so it is not recommended here.

## Nemotron session protocol

Each application release pins a catalog-tested CPU variant ID. The initial research candidate is `nemotron-speech-streaming-en-0.6b-generic-cpu:3`, but that version suffix is not a permanent product requirement. Resolve the release-pinned ID with exact-variant lookup, then validate the returned identity before download or load. CPU execution remains a product invariant, not a preference ranking, and the app never selects an arbitrary latest version at runtime.

For each VAD interval:

1. create a live transcription session;
2. configure 16,000 Hz, one channel, English;
3. start the response reader before producing audio;
4. call `StartAsync`;
5. append consecutive 100 ms raw PCM chunks, with a final shorter chunk allowed;
6. call `StopAsync` to flush;
7. await the response reader; and
8. dispose the session even after cancellation or failure.

`AppendAsync` provides a bounded internal queue and backpressure according to the [C# SDK documentation](https://github.com/microsoft/Foundry-Local/tree/main/sdk/cs). The implementation should await it and should not add real-time `Task.Delay` pacing unless Phase 0 proves the installed SDK/model requires pacing. Offline batch throughput should otherwise be allowed to run as fast as backpressure permits.

The model's response-relative start/end times are diagnostics only. Each session starts at zero and VAD is the requested segmentation authority, so the cue receives the VAD interval on the original file timeline.

## WebVTT contract

The output path is `<output-folder>\<input-relative-directory>\<input-file-name-without-extension>.vtt`. Required output subdirectories are created as each file begins. The file is UTF-8 and begins with:

```text
WEBVTT

```

Each non-empty recognition result becomes one cue:

```text
00:01:23.456 --> 00:01:27.890
recognized text

```

Always include hours for predictable formatting. Normalize CR/LF and runs of whitespace in cue text, replace the literal delimiter `-->` with a harmless Unicode arrow or spaced form, and skip empty text. Preserve chronological order and ensure each cue end is greater than its start. These rules follow the [W3C WebVTT specification](https://www.w3.org/TR/webvtt1/) and the concise [MDN WebVTT format guide](https://developer.mozilla.org/en-US/docs/Web/API/WebVTT_API/Web_Video_Text_Tracks_Format).

No cue identifiers, styling, regions, speaker labels, or model metadata are planned in version 1.

## Safe output commit

Create a uniquely named temporary file in the destination directory, write and flush the entire document, then move it into place. The selected overwrite policy determines whether the final step fails, skips, or replaces an existing VTT. Keeping the temporary file on the destination volume avoids a cross-volume “atomic move” assumption.

A failed or cancelled job must not publish a partial final VTT. If replacing an existing file is allowed, the old file must remain intact until the new temporary file is complete.

After recursive discovery and before model loading, preflight all destination paths. If any VTT already exists, show one batch-level prompt with Skip existing as the default, Overwrite all, and Cancel. The selected policy applies to the entire batch; never interrupt once per file. Skipped files retain a distinct non-error detail such as “Existing VTT skipped” and do not run extraction or ASR.

## Progress model

Progress is stage-based and monotonic, not an inference-time promise:

| Range | Stage | Basis |
|---|---|---|
| 0-30% | Extracting audio | `TranscodeAsync` progress |
| 30-45% | Detecting speech | PCM samples scanned / total data samples |
| 45-95% | Transcribing | completed speech samples / total speech samples |
| 95-100% | Writing output | commit milestones |

If no speech is found, the file moves from VAD to output and writes a valid header-only VTT unless product policy says that should be a failure. Model download/load is batch preflight and uses the same current-work area with its own label; load may be indeterminate if the SDK exposes no percentage.

The UI also shows `completed files / total files`, which is more useful than attempting to combine unlike per-stage costs into a precise batch percentage.

## Cancellation and errors

The Cancel action requests cooperative cancellation. Check it during enumeration preflight, media transcode, every VAD loop batch, between append calls, between segments, and before output commit. Dispose the current Foundry session and delete temporary PCM/output files.

Some native or SDK operations may not abort immediately. The UI should switch to “Cancelling…” and keep the action disabled until cleanup is complete rather than pretending cancellation was instantaneous.

Per-file decode, VAD, recognition, or write failures mark that job Failed and processing continues. Shared model initialization and invalid-directory failures stop the batch. User-facing rows show a short reason; diagnostic logs may keep the exception and operation context without storing transcript audio.
