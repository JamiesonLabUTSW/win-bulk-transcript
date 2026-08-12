# End-to-end workflow integration probe

This opt-in Windows 11 evidence tool runs supplied real MP4 inputs through the production `WindowsMediaAudioExtractor`, adaptive VAD, Foundry Local host and Nemotron recognizer, `BatchTranscriptionCoordinator`, and atomic `WebVttWriter`. It is deliberately outside the release solution: it needs a real .NET 10 Windows environment, rights-cleared media, and an available local Foundry model.

It writes no fixtures and never deletes an input or an existing final VTT. A JSON report must be outside both input and output roots, is committed with no-overwrite temp-and-move handling, and records snapshots, output bytes/header evidence, temporary-artifact snapshot errors, model variant, and scenario assertions. Missing model/media prerequisites are failures, not passing evidence.

## Successful workflow evidence

Use a disposable empty output folder and retain one report per architecture:

```powershell
dotnet run --project tools/WinBulkTranscript.WorkflowIntegrationProbe -- `
  --input-root C:\fixtures\win-bulk-transcript-media\valid-inputs `
  --output-root artifacts\workflow-probe\x64-output `
  --report artifacts\workflow-probe\x64-success.json
```

A successful report requires every discovered job to complete, every output to begin with a valid `WEBVTT` header, and no newly left-over extractor-owned temporary media artifact (WAV or long-path staging input) or VTT file.
## Repeatability evidence

Run the same input layout again into a separate empty output root and compare it with the retained successful output tree:

```powershell
dotnet run --project tools/WinBulkTranscript.WorkflowIntegrationProbe -- `
  --input-root test-assets\synthetic\flat `
  --output-root artifacts\workflow-probe\x64-repeatability-output `
  --compare-output-root artifacts\workflow-probe\x64-success-output `
  --report artifacts\workflow-probe\x64-repeatability.json
```

This normal-mode report fails if a corresponding VTT is missing, lacks a `WEBVTT` header, or differs byte-for-byte from the prior run.

## Expected corpus VTT comparison

The corpus expected VTTs are not a byte-for-byte end-to-end oracle: encoded AAC timing can alter VAD boundaries and an ASR result can split or merge cues. The plan calls for normalized-text comparison with tolerant cue boundaries, but it does not define a text score or timing tolerance. This probe therefore does not turn expected-VTT comparison into a pass/fail gate; adding one would create an acceptance threshold rather than execute an existing one. The repeatability mode above is retained locally runnable evidence without changing those criteria.


## Cancellation and preservation evidence

Cancellation timing depends on the input, media pipeline, and model state. The probe waits until it observes a per-file production stage before starting the requested delay. Use a sufficiently long valid input root and a short delay; if the run finishes before it can request cancellation, the report fails instead of claiming cancellation evidence.

```powershell
dotnet run --project tools/WinBulkTranscript.WorkflowIntegrationProbe -- `
  --input-root C:\fixtures\win-bulk-transcript-media\long-input `
  --output-root artifacts\workflow-probe\x64-cancel-output `
  --collision-policy overwrite `
  --preseed-existing-outputs `
  --cancel-after-ms 250 `
  --report artifacts\workflow-probe\x64-cancellation.json
```

`--preseed-existing-outputs` is intentionally guarded: it creates a sentinel VTT only where no output exists, and refuses to touch an existing output. The cancellation report requires that each sentinel is byte-for-byte unchanged and that no new extractor-owned temporary media artifact or VTT remains. Because a batch may legitimately complete earlier files before cancellation, inspect the recorded job/output states rather than assuming every output is absent.

At batch start, production preflight also performs best-effort recovery of only aged, recognized writer `.tmp` files. That recovery is not cancellation evidence: the report still fails if the selected run leaves any new temporary VTT behind.

## Existing-output collision evidence

Use a separate disposable output root for each response. `--preseed-existing-outputs` creates only probe-owned sentinel VTTs at otherwise absent mapped outputs, then the selected production collision policy handles them.

```powershell
# Skip existing output: no model load; each sentinel remains unchanged.
dotnet run --project tools/WinBulkTranscript.WorkflowIntegrationProbe -- `
  --input-root C:\fixtures\win-bulk-transcript-media\valid-inputs `
  --output-root artifacts\workflow-probe\x64-collision-skip-output `
  --collision-policy skip `
  --preseed-existing-outputs `
  --report artifacts\workflow-probe\x64-collision-skip.json

# Cancel at collision preflight: no model load; each sentinel remains unchanged.
dotnet run --project tools/WinBulkTranscript.WorkflowIntegrationProbe -- `
  --input-root C:\fixtures\win-bulk-transcript-media\valid-inputs `
  --output-root artifacts\workflow-probe\x64-collision-cancel-output `
  --collision-policy cancel `
  --preseed-existing-outputs `
  --report artifacts\workflow-probe\x64-collision-cancel.json

# Overwrite: each sentinel must be atomically replaced with a valid production VTT.
dotnet run --project tools/WinBulkTranscript.WorkflowIntegrationProbe -- `
  --input-root C:\fixtures\win-bulk-transcript-media\valid-inputs `
  --output-root artifacts\workflow-probe\x64-collision-overwrite-output `
  --collision-policy overwrite `
  --preseed-existing-outputs `
  --report artifacts\workflow-probe\x64-collision-overwrite.json
```

The report asserts the policy-specific job states, sentinel preservation or replacement, and temporary-media/VTT cleanup. These cases are meaningful only with the production preflight snapshots in the report; do not infer that a skipped batch tested the recognizer or writer.

## Read-only existing-output evidence

This disposable-root case marks the probe-created mapped VTTs with the Windows read-only file attribute before attempting the production overwrite path:

```powershell
dotnet run --project tools/WinBulkTranscript.WorkflowIntegrationProbe -- `
  --input-root C:\fixtures\win-bulk-transcript-media\valid-inputs `
  --output-root artifacts\workflow-probe\x64-read-only-output `
  --collision-policy overwrite `
  --preseed-existing-outputs `
  --read-only-preseeded-outputs `
  --report artifacts\workflow-probe\x64-read-only-output.json
```

It passes only if each write is isolated as a `Failed` job, the pre-existing sentinel is byte-identical and still read-only, and cleanup succeeds. This exercises a read-only existing final VTT; it does not substitute for a separate UAT case that denies writes to the output directory through ACLs.

## Denied output-directory ACL evidence

This production-path scenario creates a previously absent output root, adds a temporary deny rule for the current user that blocks file creation, and restores that exact rule before it writes its report. It refuses an existing output root so it cannot alter a user-owned ACL.

```powershell
dotnet run --project tools/WinBulkTranscript.WorkflowIntegrationProbe -- `
  --input-root C:\fixtures\win-bulk-transcript-media\valid-inputs `
  --output-root artifacts\workflow-probe\x64-denied-output-directory `
  --deny-output-directory-writes `
  --report artifacts\workflow-probe\x64-output-directory-acl.json
```

The report passes only if the production workflow reaches `WritingVtt`, every denied write becomes an isolated failed job, no final VTT or temporary media/VTT artifact remains, and the probe proves that write access was restored.

## Test-only simulated low-disk output evidence

`--simulate-output-disk-full` is a bounded diagnostic when it is unsafe or unavailable to exhaust a real filesystem. It requires `--preseed-existing-outputs` with `--collision-policy overwrite`, refuses ACL/read-only modes, recognizer injections, and timed cancellation, and preserves only probe-created sentinels.

```powershell
dotnet run --project tools/WinBulkTranscript.WorkflowIntegrationProbe -- `
  --input-root C:\fixtures\win-bulk-transcript-media\valid-inputs `
  --output-root artifacts\workflow-probe\x64-simulated-disk-full-output `
  --collision-policy overwrite `
  --preseed-existing-outputs `
  --simulate-output-disk-full `
  --report artifacts\workflow-probe\x64-simulated-output-disk-full.json
```

The mode retains the production extractor, VAD, Foundry model/recognizer, and batch coordinator. After production recognition reaches `WritingVtt`, it replaces `ITranscriptWriter` before any production `WebVttWriter` file creation and throws an `IOException` with Windows `ERROR_DISK_FULL` (112 / `0x80070070`). A passing report requires a non-empty formatted cue at that boundary, exactly one injected failure per mapped output, isolated failed jobs, unchanged sentinels, and no new temporary media/VTT artifact.

This is intentionally **not** literal low-disk evidence: it does not fill or quota a volume and does not exercise `WebVttWriter`'s file-write failure path. It does not close the Phase 6 low-disk or clean-machine release-matrix requirement. Use an isolated real low-space volume for that acceptance case.

## Empty-recognition evidence

This test-only recognizer returns an empty response after production extraction and VAD have detected speech. It verifies that the coordinator writes valid header-only VTTs instead of malformed or partial files.

```powershell
dotnet run --project tools/WinBulkTranscript.WorkflowIntegrationProbe -- `
  --input-root test-assets\synthetic\flat `
  --output-root artifacts\workflow-probe\x64-empty-response-output `
  --inject-empty-recognizer-response `
  --report artifacts\workflow-probe\x64-empty-response.json
```

The report requires at least one injected-recognizer invocation, every job to complete with zero cues, committed `WEBVTT\n\n` outputs, and no new temporary media-artifact/VTT files.


## Per-file failure preservation evidence

This mode keeps the production media extractor, VAD, coordinator, and Foundry-host lifetime wiring, but deliberately substitutes the recognizer and prevents VTT writing. It is designed to prove that an existing final VTT is never replaced by a failed job; use an input that contains detected speech.

```powershell
dotnet run --project tools/WinBulkTranscript.WorkflowIntegrationProbe -- `
  --input-root C:\fixtures\win-bulk-transcript-media\valid-inputs `
  --output-root artifacts\workflow-probe\x64-failure-output `
  --collision-policy overwrite `
  --preseed-existing-outputs `
  --inject-recognizer-failure `
  --report artifacts\workflow-probe\x64-recognizer-failure.json
```

The report passes only when the injected recognizer was actually invoked, at least one job is `Failed` without a fatal batch error, all pre-seeded final VTTs are byte-for-byte unchanged, and no new temporary media-artifact/VTT remains.
## Mixed per-file failure and real-recognizer evidence

This scenario injects one deterministic failure at the selected recognizer invocation, then delegates every later recognizer call to the production Foundry recognizer. It demonstrates that the batch continues after an isolated per-file failure.

```powershell
dotnet run --project tools/WinBulkTranscript.WorkflowIntegrationProbe -- `
  --input-root test-assets\synthetic\flat `
  --output-root artifacts\workflow-probe\x64-mixed-output `
  --collision-policy overwrite `
  --preseed-existing-outputs `
  --inject-recognizer-failure-on-call 1 `
  --report artifacts\workflow-probe\x64-mixed-recognizer-failure.json
```

The report requires exactly one injected failure, at least one later delegated real-recognizer call and completed job, preservation of each failed job's sentinel, valid replacement VTTs for completed jobs, and no new temporary media-artifact/VTT files.


## Options and exit codes

Run with `--help` for the full option list. The default model candidate is `nemotron-speech-streaming-en-0.6b-generic-cpu:3`; use `--model` only to intentionally test an exact replacement variant.

| Code | Meaning |
|---:|---|
| `0` | The selected scenario met its runtime, output, and cleanup assertions. |
| `1` | A prerequisite, workflow result, output-preservation check, or cleanup assertion failed. |
| `2` | The operator pressed Ctrl+C; the tool attempted to write the report before returning. |

Run the success, cancellation, and failure-preservation scenarios on both x64 and ARM64. This tool provides Phase 1/3/4 evidence, but it does not replace the separate Phase 0 compatibility spike or Phase 6 clean-machine/publish matrix.
