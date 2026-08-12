# WinBulkTranscript 0.1.0 preview

Release date: `2026-08-12`
Release policy: `preview`

## Downloads

- `WinBulkTranscript-0.1.0-win-x64.zip` for Intel and AMD Windows PCs
- `WinBulkTranscript-0.1.0-win-arm64.zip` for Windows on Arm

Extract the entire matching ZIP, then run `WinBulkTranscript.exe`. Do not run the application inside the ZIP or copy the EXE by itself. Each ZIP has a `.zip.sha256` sidecar, and `SHA256SUMS.txt` covers both archives.

The archives are self-contained and include the required .NET and Windows App SDK files. The speech model is not included; Foundry Local downloads `nemotron-speech-streaming-en-0.6b-generic-cpu:3` on first use. A cached model can be used offline.

## Preview status

This is an early version-zero preview. Mike Holcomb approved the documented x64 and ARM64 validation limitations on 2026-08-12. Those limitations include incomplete clean-machine, SmartScreen, offline, full manual UI, representative-acoustic, resilience, and ARM64 execution coverage. They are accepted for v0.1.0 and are recorded in the release matrices included with each archive.

## Unsigned download and SmartScreen warning

**This version is unsigned.** Windows SmartScreen may warn about a ZIP or executable downloaded from the internet, and enterprise policy can block unsigned applications. Obtain the archive from this repository's GitHub Releases page and verify its SHA-256 checksum. Do not disable Windows security controls or bypass an organization's security policy.

## Release details

- Source: `v0.1.0`
- Model: `nemotron-speech-streaming-en-0.6b-generic-cpu:3`
- Deployment: self-contained folder/ZIP
- Support and feedback: `https://github.com/JamiesonLabUTSW/win-bulk-transcript/issues`

The retained x64 single-file experiment is informational. It is not a v0.1.0 release artifact.
