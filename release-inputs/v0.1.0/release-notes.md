# WinBulkTranscript release notes - 0.1.0 (DRAFT)

Release date: `TBD`
Release policy: `preview`
Architecture artifacts: `WinBulkTranscript-0.1.0-win-x64.zip`, `WinBulkTranscript-0.1.0-win-arm64.zip`
Release evidence: draft matrices in `release-inputs/v0.1.0/win-x64/` and `release-inputs/v0.1.0/win-arm64/`; final release records will be attached to the GitHub Release.

> **Draft only:** v0.1.0 is not approved or supported yet. Both architecture matrices must be completed before these notes can be used to publish a release.

## Included artifacts

This release is planned to provide unpackaged, self-contained Windows folders in architecture-specific ZIP archives. Choose x64 for Intel/AMD Windows PCs or ARM64 for Windows on Arm. Extract the entire ZIP before launching `WinBulkTranscript.exe`; do not run inside the ZIP or copy the EXE by itself. Each ZIP will have a matching `.zip.sha256` sidecar, and `SHA256SUMS.txt` will cover both archives.

The published folder will include .NET and Windows App SDK dependencies plus release-specific provenance, notices, payload inventory, test matrix, and metadata. It will not contain the speech model.

## First use and connectivity

The application downloads its configured local speech model on first use. A later cached launch is intended to run offline. An uncached offline launch is intended to report an actionable error. These behaviors remain release gates and must be confirmed on clean x64 and ARM64 machines before this draft is approved.

## Unsigned download and SmartScreen warning

**This version is unsigned.** Windows SmartScreen may display a warning for a ZIP or executable downloaded from the internet, and enterprise policy can block an unsigned application. Obtain the archive only from the repository's GitHub Releases page and verify its published SHA-256 checksum. Do not disable Windows security controls or bypass an organization's security policy to run this application.

## Validation summary

- Exact configured model variant: `nemotron-speech-streaming-en-0.6b-generic-cpu:3`
- Release source: `v0.1.0`
- Release policy: `preview`
- Matrix status: `preview exceptions drafted for x64 and ARM64; explicit risk acceptance pending`
- Model provenance input: `model-provenance.json` - SHA-256 `TBD after finalization`
- x64 runtime/framework notice input: `win-x64/runtime-framework-notices.txt` - SHA-256 `TBD after legal review`
- ARM64 runtime/framework notice input: `win-arm64/runtime-framework-notices.txt` - SHA-256 `TBD after legal review`
- Known limitations or operator notes: no clean-machine, SmartScreen, offline, complete manual UAT, representative-acoustic, or ARM64 execution acceptance has been recorded; model and runtime notices remain under review.

## Deployment decision

Version 0.1.0 uses self-contained folder/ZIP deployment. The retained literal single-file x64 result is an informational compatibility experiment only and is not a release artifact.

## Support information

Draft contact: repository maintainers via `https://github.com/JamiesonLabUTSW/win-bulk-transcript/issues`.

Before publication, remove the draft warning, enter the actual release date, confirm the support channel, update the validation summary to show both matrices passed, and recompute this file's SHA-256 for both matrix headers.
