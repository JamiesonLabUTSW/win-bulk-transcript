# Resolved decisions and remaining validation questions

The owner-facing design choices are settled. The remaining questions require evidence from the planned compatibility and media spikes rather than further product preference.

## Resolved product decisions

- Discover MP4 files recursively and mirror each input-relative directory beneath the output root. Do not follow reparse points.
- If preflight finds existing VTT files, ask once for the batch: Skip existing (default), Overwrite all, or Cancel.
- A file with no detected speech produces a valid header-only VTT and completes with detail “No speech detected.”
- Cancelled is a distinct visible job state.
- Windows 11 24H2/build 26100 is the minimum supported release.
- Ship separate unsigned, unpackaged, self-contained x64 and ARM64 ZIP/folder artifacts. A literal single-file EXE is not required for version 1.
- Use dependency-free adaptive energy VAD for version 1 and tune it against the representative corpus.
- English Nemotron CPU execution remains fixed. The `:3` model version is not permanent; each app release pins one CPU variant version that has passed x64 and ARM64 validation.

## Questions to settle with Phase 0/1 evidence

1. Does `Microsoft.AI.Foundry.Local` 1.2.4, without `.WinML`, load a common live-transcription CPU variant version on both x64 and ARM64?

2. Which current CPU variant version should the first app release pin after testing both architectures? Start the investigation with `:3`, but do not require it if a newer common version is better supported.

3. Can offline PCM be appended as fast as SDK backpressure allows, or does this model/runtime require real-time pacing?

4. Is one live session per VAD interval fast enough for files with many short utterances? What measured session count or overhead would justify switching to one session per file?

5. Does `MediaTranscoder` reliably produce PCM16/16 kHz/mono from the real MP4 corpus on clean Windows 11 systems, including editions with different optional codecs?

6. How promptly can an active Nemotron live session be cancelled, and is “cancel after the current append/session” the honest UI contract?

7. What energy-VAD thresholds, padding, and silence duration best match the real recordings?

8. What directory-enumeration behavior is fastest and safest for very large trees while still avoiding reparse-point cycles and reporting inaccessible subdirectories usefully?
