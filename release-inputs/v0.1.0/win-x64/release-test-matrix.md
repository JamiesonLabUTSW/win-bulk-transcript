# WinBulkTranscript v0.1.0 preview release test matrix - x64

Status: **Approved preview - documented x64 limitations accepted by Mike Holcomb on 2026-08-12.**

Release version: `0.1.0`
Release date: `2026-08-12`
Release Foundry model variant: `nemotron-speech-streaming-en-0.6b-generic-cpu:3`
Release source: `v0.1.0`
Release policy: `preview`
Preview risk acceptance: `Approver: Mike Holcomb; Date: 2026-08-12; Decision: Approve the documented v0.1.0 x64 preview limitations and Accepted risk rows.`
Release notes source: `release-notes.md sha256:7c14fde3962190d5b03a691b5c6f371e0373848ce1a76ab0a44a0cabb0f03fac`
Model provenance source: `model-provenance.json sha256:31e7bc929380f60d8939b81fce0700265034073aa78a13619c77b63b341a3e0e`
Runtime framework notices source: `runtime-framework-notices.txt sha256:75843646aaa9ff45eaf8633a8d41cdefb24aabc8c81a2d6e71ca1dde8c721b0a`
Single-file experiment record: `docs/validation/single-file-experiment-x64.md` (x64 informational only; folder/ZIP deployment remains the release target)

`Accepted risk` records a preview limitation accepted for v0.1.0. These rows are not blockers under the version-zero preview policy.

| Area | x64 | ARM64 | Evidence / notes |
|---|---|---|---|
| Self-contained unpackaged folder launch on clean Windows 11 24H2 | Accepted risk | See ARM64 matrix | Local non-clean x64 startup smoke: `docs/validation/uat-evidence-status.md`; clean-machine launch is not recorded for this preview. |
| ZIP extracted from a Mark-of-the-Web download | Accepted risk | See ARM64 matrix | SmartScreen behavior is not recorded for this preview; do not bypass organizational policy. |
| No .NET SDK, Windows App SDK runtime, or Foundry CLI installed | Accepted risk | See ARM64 matrix | Clean-machine dependency isolation is not recorded for this preview. |
| Exact CPU model variant resolves, downloads, and loads | Accepted risk | See ARM64 matrix | Retained non-clean x64 Phase 0 reports under `artifacts/phase0/`; clean-machine coverage is deferred to the supported policy. |
| Known 16 kHz PCM transcription completes without pacing | Accepted risk | See ARM64 matrix | `artifacts/phase0/x64-report.json` and `artifacts/phase0/x64-accumulator-report.json`; not clean-machine release evidence. |
| Cancel download, append, and response read; later session recovers | Accepted risk | See ARM64 matrix | Retained x64 Phase 0 cancellation/recovery reports; broader architecture coverage is deferred to the supported policy. |
| First-run online model download | Accepted risk | See ARM64 matrix | Cache-isolated x64 download recovery was exercised; packaged first-run coverage is an accepted preview limitation. |
| Cached launch while offline | Accepted risk | See ARM64 matrix | No qualifying clean-machine offline evidence recorded. |
| Uncached launch while offline returns actionable error | Accepted risk | See ARM64 matrix | No qualifying clean-machine offline evidence recorded. |
| Synthetic flat corpus: 30 valid audio-only MP4s | Accepted risk | See ARM64 matrix | `artifacts/workflow-probe/x64-success.json`; release-candidate/clean-machine rerun is deferred to the supported policy. |
| Synthetic nested corpus: same 30 mirrored VTTs | Accepted risk | See ARM64 matrix | `artifacts/workflow-probe/x64-nested-success.json` and repeatability diagnostics; release-candidate rerun is deferred to the supported policy. |
| Media fixture matrix: valid control plus malformed/no-audio/empty/unsupported cases | Accepted risk | See ARM64 matrix | `docs/validation/media-fixture-matrix-x64.md` and retained extractor/workflow reports; production provenance and ARM64 coverage are deferred to the supported policy. |
| Media extraction cancellation and temporary-WAV cleanup | Accepted risk | See ARM64 matrix | Retained x64 media cancellation and long-path boundary reports; release-candidate rerun is deferred to the supported policy. |
| End-to-end media/VAD/Foundry/coordinator/VTT success, cancellation, and failure preservation | Accepted risk | See ARM64 matrix | Retained x64 success, cancellation, failure, empty-response, mixed-failure, and repeatability reports under `artifacts/workflow-probe/`. |
| Long input, low disk, read-only output, Unicode path | Accepted risk | See ARM64 matrix | Retained x64 long/Unicode, read-only, ACL-denial, and simulated disk-full reports; literal low-disk coverage is deferred to the supported policy. |
| Cancellation, sleep/resume, and window-close coordination | Accepted risk | See ARM64 matrix | Local x64 cancellation and structural window-close evidence exists; literal sleep/resume and manual release UAT are deferred to the supported policy. |
| High contrast, 100-200% scale, keyboard-only, long strings | Accepted risk | See ARM64 matrix | Basic x64 UI Automation exists; complete manual UI coverage is deferred to the supported policy. |

## Future-information single-file experiment

| Future-information check | x64 | ARM64 | Evidence / notes |
|---|---|---|---|
| Literal single-file publish and Foundry native probing after extraction | Recorded | See ARM64 matrix | `docs/validation/single-file-experiment-x64.md`; this does not change the folder/ZIP release decision. |

## Artifact checksums

| Artifact | SHA-256 | Generated by |
|---|---|---|
| `WinBulkTranscript-0.1.0-win-x64.zip` | Publisher writes the matching `.zip.sha256` sidecar and `.release-record.json`. | `scripts/Publish-Release.ps1` |
| `WinBulkTranscript-0.1.0-win-arm64.zip` | Publisher writes the matching `.zip.sha256` sidecar and `.release-record.json`. | `scripts/Publish-Release.ps1` |
