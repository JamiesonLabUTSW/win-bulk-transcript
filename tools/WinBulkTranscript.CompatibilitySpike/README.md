# Foundry Local compatibility spike

This disposable Phase 0 process is not part of the product application. It proves one exact CPU model variant before a release pins it. It takes a known raw speech fixture, validates the exact catalog ID and CPU runtime metadata, downloads/loads the model, streams unpaced 16 kHz PCM in awaited bounded writes with `PushQueueCapacity` fixed at `2`, records response chunks in emission order, and requires nonempty baseline and recovery transcripts.

It measures `StartAsync` separately from full session elapsed time for at least 20 short sessions, and records `AppendWaitObserved` whenever an awaited append is actually pending. That is explicit runtime evidence of an observed queue wait under the recorded capacity; it is not an inference from append timing alone. The spike also verifies cancellation at `StartAsync`, `AppendAsync`, and an active response reader, retaining the append/stop/reader/dispose cleanup outcome for each path. The optional download-cancellation mode uses an isolated Foundry `Configuration.AppDataDir` and `Configuration.ModelCacheDir`, cancels the first cache-miss download from its progress callback, redownloads the same exact variant, and then proves a later transcription session works. This makes the download-cancel/recovery evidence reproducible without changing the normal Foundry cache.

## Fixture contract

Supply a retained, rights-cleared raw fixture containing a known English phrase in signed little-endian PCM16 at 16,000 Hz, mono. Record its source, phrase, selected Windows TTS voice (if used), and SHA-256. Use exactly the same fixture bytes for x64 and ARM64 comparison. The tool requires at least five seconds of speech so its bounded-queue cancellation check can observe an active append and response reader.

The companion corpus tool can produce this once with an explicit installed voice: `dotnet run --project tools/WinBulkTranscript.CorpusGenerator -- --voice-id '<installed voice ID>' --phase0-fixture C:\fixtures\phase0-known-en.pcm`. Keep its `.json` provenance sidecar beside the PCM file.

## Run

```powershell
dotnet run --project tools/WinBulkTranscript.CompatibilitySpike -- `
  --pcm C:\fixtures\phase0-known-en.pcm `
  --report artifacts\phase0\x64-report.json
```

The default candidate is defined once in `FoundryModelContract` and shared with the application host: `nemotron-speech-streaming-en-0.6b-generic-cpu:3`. Use `--model` only to deliberately test a replacement exact variant. The default and minimum `--short-sessions` value is `20`; a smaller run is refused because it cannot serve as the Phase 0 many-short-sessions evidence.

Use a fresh `--report` path for every retained run. The spike refuses to overwrite any pre-existing file or directory and writes a same-directory temporary file before atomically committing it without overwrite.

## Evidence interpretation

Inspect the report for the resolved ID, `CPU` device type, fixture hash, and nonempty baseline/recovery transcripts. `PushQueueCapacity` records the configured bound. `Baseline.AppendWaitObserved` and each `ShortSessions[*].AppendWaitObserved` show whether an append actually waited; `ShortSessions[*].StartAsyncMilliseconds` and `SessionElapsedMilliseconds` are deliberately separate measurements.

Every `Cancellation.Prompt`, `Cancellation.Append`, and `Cancellation.Response` object must have `CancellationObserved: true`. For append, `AppendWaitObserved: true` additionally proves the cancellation was requested while an append was pending. All `Cleanup` operation outcomes must be completed without timeout or fault; a completed report also has `Cancellation.CleanupCompletedWithoutTimeoutOrFault: true`.

## Reproducible download-cancellation evidence

Run this separately for each architecture with a dedicated cache directory. The mode refuses a nonempty root unless `--reset-isolated-cache` is explicitly supplied. On first use it writes a tool-owned `.win-bulk-transcript-phase0-cache` sentinel; reset additionally refuses a root without that sentinel or a reparse-point root. The reset still deletes the supplied isolated root, so do not point it at a shared Foundry cache or a source directory.

```powershell
$cacheRoot = Join-Path $PWD 'artifacts\phase0\download-cancel-cache-x64'
dotnet run --project tools/WinBulkTranscript.CompatibilitySpike -- `
  --pcm C:\fixtures\phase0-known-en.pcm `
  --verify-download-cancellation `
  --isolated-cache-root $cacheRoot `
  --reset-isolated-cache `
  --cancel-download-at-percent 5 `
  --report artifacts\phase0\x64-download-cancel-report.json
```

The tool creates `<cacheRoot>\app-data` and `<cacheRoot>\model-cache`, passes those exact locations to Foundry Local configuration, requires the model cache to be empty before the first download, and records them in the JSON report. A successful report has `DownloadCancellation.Requested`, `DownloadCancellation.CancellationObserved`, and `DownloadCancellation.RecoveryDownloadCompleted` all set to `true`; the normal baseline and recovery transcripts are further proof that the later session worked. Repeat with an ARM64-specific cache root and report.

## Architecture and publish gate

Run the same raw fixture on clean Windows 11 24H2 x64 and ARM64 machines. Run once from the SDK, then publish and run a self-contained unpackaged folder on each machine:

```powershell
dotnet publish tools/WinBulkTranscript.CompatibilitySpike -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
dotnet publish tools/WinBulkTranscript.CompatibilitySpike -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=false
```

Record first-run download, cached offline use, uncached-offline error, per-session startup and elapsed measurements, observed append waits, isolated download cancellation/recovery, cancellation cleanup outcomes, and successful recovery. Ctrl+C remains available for manual cancellation of normal operations, but it is not the evidence path for the download-cancellation requirement. A literal single-file build is only an experiment: publish it separately with `-p:PublishSingleFile=true` and record whether Foundry native probing still works. Do not promote a model version until both architecture reports pass.

This project deliberately stays outside `WinBulkTranscript.sln`: the product CI must not pretend that model download, installed voices, clean-machine testing, or cross-architecture proof can be replaced by a normal unit-test run.
