# Windows media integration probe

This development-only Windows 11 tool runs real MP4s through the production [`WindowsMediaAudioExtractor`](../../src/WinBulkTranscript.App/Media/WindowsMediaAudioExtractor.cs). It is intentionally outside `WinBulkTranscript.sln`: a normal Core unit-test run cannot prove Windows codec availability, `MediaTranscoder`, temporary-file behavior, or architecture-specific media support.

The repository does **not** contain the binary MP4 fixtures. Generate the synthetic corpus with the companion tool or obtain the rights-cleared matrix artifacts under their documented provenance before running this probe. A missing matrix file is reported individually and returns exit code `3`; it is never treated as a passing test.

## What it verifies

- A successful extraction returns PCM16, 16 kHz, mono, block-aligned audio, and valid-corpus inputs contain decoded samples.
- The caller-owned temporary WAV is disposed, and the probe snapshots `%TEMP%\WinBulkTranscript` before and after each case to detect newly left-over WAVs or extractor-owned long-path input staging files. It does not delete unrelated temporary files. Inputs whose canonical path is at least 260 characters are staged privately only for the Windows media API, then the stage is included in the same cleanup assertion.
- Matrix cases are evaluated with the expectations in [`../../test-assets/media-fixture-matrix.md`](../../test-assets/media-fixture-matrix.md): failure cases require an actionable `MediaExtractionException` containing their case-specific terms (unreadable/corrupt, no usable audio, zero/audio, or unsupported/codec) rather than a raw native `Unknown` or merely nonempty error. The empty track may still produce header-only WAV; the valid control must succeed.
- `--cancel-input` requires an observed cancellation and applies the same temporary-media cleanup check. The production extractor explicitly cancels the WinRT transcode operation; if native completion outlives its bounded wait, deletion remains tied to that operation's eventual completion and the probe still fails any observed leftover.

The probe does not invoke `BatchTranscriptionCoordinator`, Foundry, or the WebVTT writer. It has no final-output path and creates no `.vtt` files, so it cannot itself prove that failure/cancellation leaves no final VTT. Keep that assertion in the batch/workflow integration evidence as required by the fixture matrix.

## Run against a generated corpus

First generate the corpus with an explicitly chosen installed voice (the generated corpus is intentionally ignored by Git):

```powershell
dotnet run --project tools/WinBulkTranscript.CorpusGenerator -- `
  --voice-id '<installed voice ID>' `
  --seed 20260806
```

Then exercise both the flat and nested input trees separately, retaining reports for each architecture:

```powershell
dotnet run --project tools/WinBulkTranscript.MediaIntegrationProbe -- `
  --corpus test-assets\synthetic\flat `
  --report artifacts\media-probe\x64-flat.json

dotnet run --project tools/WinBulkTranscript.MediaIntegrationProbe -- `
  --corpus test-assets\synthetic\nested `
  --report artifacts\media-probe\x64-nested.json
```

Use an ARM64 Windows machine and architecture-specific report names for the corresponding evidence. The commands run from the SDK; published-artifact validation remains a separate Phase 6 gate.

## Run the failure-fixture matrix

Place the five binary files named in [`../../test-assets/media-fixture-matrix.md`](../../test-assets/media-fixture-matrix.md) in a local directory that is not committed to this repository, then run:

```powershell
dotnet run --project tools/WinBulkTranscript.MediaIntegrationProbe -- `
  --matrix-root C:\fixtures\win-bulk-transcript-media `
  --report artifacts\media-probe\x64-matrix.json
```

The report includes each expected and observed outcome, required diagnostic terms, error type/message, PCM validation, progress, elapsed time, temporary-directory snapshot evidence, and an explicit `finalVttResponsibility` scope statement. Exit codes are:

| Code | Meaning |
|---:|---|
| `0` | Every supplied case met its extraction and cleanup expectation. |
| `1` | A supplied case failed its expectation or the probe could not run. |
| `2` | The operator cancelled the probe with Ctrl+C. |
| `3` | At least one requested fixture was missing. |

## Cancellation evidence

Use a sufficiently long valid file for delayed, true-in-flight cancellation. The delay is deliberately explicit because cancellation timing is controlled by the Windows media pipeline and source duration:

```powershell
dotnet run --project tools/WinBulkTranscript.MediaIntegrationProbe -- `
  --cancel-input C:\fixtures\win-bulk-transcript-media\long-cancellation-probe.mp4 `
  --cancel-after-ms 250 `
  --report artifacts\media-probe\x64-cancellation-delayed.json
```

If the file completes before the delay, the case is an unexpected success rather than evidence of cancellation. Use a longer valid fixture or shorten the delay, then retain the report that records an observed cancellation and no newly left-over temporary media artifact.

## Deterministic lifecycle-boundary cancellation

For a focused extractor cleanup test, the probe can cancel at one named production lifecycle boundary instead of using elapsed time. This is test-only probe instrumentation and does not replace the delayed, in-flight cancellation evidence above.

```powershell
# Prepare boundary
dotnet run --project tools/WinBulkTranscript.MediaIntegrationProbe -- `
  --cancel-input C:\fixtures\win-bulk-transcript-media\valid-short-control.mp4 `
  --cancel-at-boundary prepare `
  --report artifacts\media-probe\x64-prepare-boundary-cancellation.json

# Transcode boundary
dotnet run --project tools/WinBulkTranscript.MediaIntegrationProbe -- `
  --cancel-input C:\fixtures\win-bulk-transcript-media\valid-short-control.mp4 `
  --cancel-at-boundary transcode `
  --report artifacts\media-probe\x64-transcode-boundary-cancellation.json

# Validation boundary
dotnet run --project tools/WinBulkTranscript.MediaIntegrationProbe -- `
  --cancel-input C:\fixtures\win-bulk-transcript-media\valid-short-control.mp4 `
  --cancel-at-boundary validation `
  --report artifacts\media-probe\x64-validation-boundary-cancellation.json
```

Choose exactly one of `--cancel-after-ms` or `--cancel-at-boundary`; boundary values are `prepare`, `transcode`, and `validation`. The report records the requested and observed boundary and requires cancellation plus cleanup of both owned WAV and long-path staging artifacts. The lifecycle observer is available only through this evidence probe's internal test hook; it does not alter normal production extraction.

## Build/publish note

The tool references the production App project so it calls the same extractor implementation as the app. It needs .NET 10 and Windows 11 24H2 media APIs. It is a disposable development/evidence tool with no package lock of its own, and remains outside the release solution just like the Phase 0 compatibility spike.
