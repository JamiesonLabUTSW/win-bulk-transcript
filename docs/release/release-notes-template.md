# WinBulkTranscript release notes — `<version>`

Release date: `<YYYY-MM-DD>`
Architecture artifacts: `WinBulkTranscript-<version>-win-x64.zip`, `WinBulkTranscript-<version>-win-arm64.zip`
Release evidence: `<links or retained paths to the completed architecture matrices and release records>`

## Included artifact

This release provides unpackaged, self-contained Windows folders in architecture-specific ZIP archives. Choose x64 for Intel/AMD Windows PCs or ARM64 for Windows on Arm. Extract the ZIP before launching `WinBulkTranscript.exe`. Each ZIP has a matching `.zip.sha256` sidecar, and `SHA256SUMS.txt` covers both. The ZIP includes `PUBLISH-PAYLOAD.json`, `MODEL-PROVENANCE.json`, and release-specific runtime/framework notices in addition to the app payload and other release evidence.

## First use and connectivity

The application downloads its configured local speech model on first use. A later cached launch can run offline. An uncached offline launch reports an actionable error; it does not require a separately installed .NET runtime, Windows App SDK runtime, or Foundry CLI.

## Unsigned download and SmartScreen warning

**This version is unsigned.** Windows SmartScreen may display a warning for a ZIP or executable downloaded from the internet, and enterprise policy can block an unsigned application. Obtain the archive from the trusted distribution channel and verify its published SHA-256 checksum. Do not disable Windows security controls or bypass an organization’s security policy to run this application.

## Validation summary

- Exact configured model variant: `<model variant>`
- Release source: `<v-prefixed release tag>`
- Matrix status: `<x64 Passed; ARM64 Passed>`
- Model provenance input: `<file name and SHA-256>`
- x64 runtime/framework notice input: `<file name and SHA-256>`
- ARM64 runtime/framework notice input: `<file name and SHA-256>`
- Known limitations or operator notes: `<none or concise text>`

## Version 1 deployment decision

Version 1 remains a self-contained folder/ZIP deployment. A literal single-file publish is an experiment only and is not this release artifact.

## Support information

`<support contact, issue tracker, or distribution contact>`

Before publishing, replace every placeholder, then compute this completed file's SHA-256 and put that filename/hash in each test matrix's `Release notes source` header. The publisher verifies that the notes name the requested version and exact ZIP artifact for each architecture and retain the unsigned SmartScreen warning.
