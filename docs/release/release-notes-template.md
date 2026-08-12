# WinBulkTranscript release notes — `<version>`

Release date: `<YYYY-MM-DD>`
Architecture artifact: `WinBulkTranscript-<version>-win-<x64|arm64>.zip`
Matrix source binding: `<this release-notes file name> sha256:<lowercase SHA-256>`
Release evidence: `<link or retained path to the completed release-test matrix and release record>`

## Included artifact

This release is an unpackaged, self-contained Windows folder distributed in a ZIP archive. Extract the ZIP before launching `WinBulkTranscript.exe`. The matching checksum sidecar is `WinBulkTranscript-<version>-win-<architecture>.zip.sha256`. The ZIP includes `PUBLISH-PAYLOAD.json`, `MODEL-PROVENANCE.json`, and the release-specific runtime/framework notices in addition to the app payload and other release evidence.

## First use and connectivity

The application downloads its configured local speech model on first use. A later cached launch can run offline. An uncached offline launch reports an actionable error; it does not require a separately installed .NET runtime, Windows App SDK runtime, or Foundry CLI.

## Unsigned download and SmartScreen warning

**This version is unsigned.** Windows SmartScreen may display a warning for a ZIP or executable downloaded from the internet, and enterprise policy can block an unsigned application. Obtain the archive from the trusted distribution channel and verify its published SHA-256 checksum. Do not disable Windows security controls or bypass an organization’s security policy to run this application.

## Validation summary

- Exact configured model variant: `<model variant>`
- Package-lock commit: `<full Git revision>`
- Matrix status for this architecture: `<Passed>`
- Model provenance input: `<file name and SHA-256>`
- Runtime/framework notice input: `<file name and SHA-256>`
- Known limitations or operator notes: `<none or concise text>`

## Version 1 deployment decision

Version 1 remains a self-contained folder/ZIP deployment. A literal single-file publish is an experiment only and is not this release artifact.

## Support information

`<support contact, issue tracker, or distribution contact>`

Before publishing, replace every placeholder. The publisher verifies that the completed notes name the requested version and exact ZIP artifact and retain the unsigned SmartScreen warning; the test matrix binds this specific file by filename and SHA-256.
