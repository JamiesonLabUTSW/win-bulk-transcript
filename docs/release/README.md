# Release evidence

This directory records the evidence needed to turn a successful build into a supported release. It intentionally contains templates and instructions rather than claims that unrun architecture and clean-machine gates have passed.

## Preflight and build

Complete a release test matrix before publishing an architecture. The publisher requires all of the following:

- The matrix release version, model variant, and package-lock commit match the command and current repository revision.
- The entire source working tree is clean, every release-owned source path is tracked at `HEAD`, and `HEAD` remains unchanged through restore and publish.
- Every row for the requested architecture says exactly `Passed` and has retained evidence or notes.
- The matrix has a receipt row for the exact output ZIP name. Its checksum column should say that the publisher writes the matching sidecar; a ZIP cannot contain its own final hash.
- Complete architecture-specific [release notes](release-notes-template.md), [model provenance](model-provenance-template.json), and [runtime/framework notices](runtime-framework-notices-template.txt). Their matrix headers must be the exact input filename plus SHA-256, not a generic template or an un-hashed path.
- The runtime/framework notice input must name every framework from the final `.runtimeconfig.json` and every non-lock framework package from the final `.deps.json`. It still requires legal review for completeness.
- Record the literal single-file experiment result in the matrix header or linked evidence. It is future information only; do not replace the version 1 folder/ZIP artifact with that experiment.

Set the matrix's Package lock commit header to the full output of `git rev-parse HEAD`.

The model argument is a validation assertion, not an override. It must equal the exact model configured in the application source. Update and test that configuration before changing the release command.

Use the [release-notes template](release-notes-template.md) for every architecture artifact. It contains the mandatory first-run connectivity explanation and unsigned SmartScreen warning. Use the model-provenance template to bind the exact model identifier, catalog/source URI, and supplied license hash. Complete the runtime/framework template from a retained preliminary raw publish; the final staged `PUBLISH-PAYLOAD.json` confirms that input rather than replacing it.

~~~powershell
$modelVariant = 'nemotron-speech-streaming-en-0.6b-generic-cpu:3'
$modelLicense = 'C:\release-inputs\model-license.txt'
$modelProvenance = 'C:\release-inputs\model-provenance.json'
$runtimeFrameworkNotices = 'C:\release-inputs\runtime-framework-notices.txt'
$releaseNotes = 'C:\release-inputs\release-notes-win-x64.md'
$testMatrix = 'C:\release-inputs\release-test-matrix.md'
$artifactsRoot = 'C:\release-artifacts'

# Checks the model/configuration, matrix, current package-lock commit, and inputs without writing artifacts.
.\scripts\Publish-Release.ps1 -Version 0.1.0 -Architecture x64 -ModelVariant $modelVariant -ModelLicensePath $modelLicense -ModelProvenancePath $modelProvenance -RuntimeFrameworkNoticesPath $runtimeFrameworkNotices -ReleaseNotesPath $releaseNotes -ReleaseTestMatrixPath $testMatrix -ArtifactsRoot $artifactsRoot -ValidateOnly

.\scripts\Publish-Release.ps1 -Version 0.1.0 -Architecture x64 -ModelVariant $modelVariant -ModelLicensePath $modelLicense -ModelProvenancePath $modelProvenance -RuntimeFrameworkNoticesPath $runtimeFrameworkNotices -ReleaseNotesPath $releaseNotes -ReleaseTestMatrixPath $testMatrix -ArtifactsRoot $artifactsRoot
.\scripts\Publish-Release.ps1 -Version 0.1.0 -Architecture arm64 -ModelVariant $modelVariant -ModelLicensePath $modelLicense -ModelProvenancePath $modelProvenance -RuntimeFrameworkNoticesPath $runtimeFrameworkNotices -ReleaseNotesPath 'C:\release-inputs\release-notes-win-arm64.md' -ReleaseTestMatrixPath 'C:\release-inputs\release-test-matrix-arm64.md' -ArtifactsRoot $artifactsRoot
~~~

The scripts support Windows PowerShell 5.1 and PowerShell 7. The release machine still needs the required .NET SDK and the locked package graph; end-user clean-machine requirements apply to the published artifact, not the build machine.

`ArtifactsRoot` is mandatory and must be outside the repository (for example `C:\release-artifacts`). This keeps build output outside the source tree, so provenance verification does not depend on ignored-output policy.

For each architecture, the publisher snapshots the test matrix, App package lock, model license, model provenance, runtime/framework notice, release notes, and both release scripts before restore. It invokes the third-party-notices generator from its captured byte snapshot rather than rereading its mutable source path. It refuses to finalize if any snapshot, the canonical model contract, the host alias, `HEAD`, or the clean tracked source state changes during the build; it repeats this check after notices generation and immediately before finalization. It performs a locked restore, publishes to a unique staging directory, inventories the raw output before evidence files are copied, and only then moves artifacts to their final names.

The folder and ZIP include:

- `PACKAGE-LOCK.json`, `MODEL-LICENSE.txt`, `MODEL-PROVENANCE.json`, `DOTNET-RUNTIME-NOTICES.txt`, `RELEASE-NOTES.md`, `RELEASE-TEST-MATRIX.md`, `PUBLISH-PAYLOAD.json`, `THIRD-PARTY-NOTICES.md`, and `RELEASE-METADATA.json`.

The artifact root also receives:

- `WinBulkTranscript-<version>-win-<architecture>.zip.sha256`, the canonical ZIP checksum sidecar.
- `WinBulkTranscript-<version>-win-<architecture>.release-record.json`, a portable receipt that binds the ZIP hash, committed source revision, exact inputs, model provenance, actual raw payload inventory, package lock, matrix, SDK version, and recorded build commands.

The metadata and notices use artifact names, repository-relative paths, and hashes rather than the release machine's absolute paths. The Foundry model is downloaded separately on first use and is not embedded in either artifact.

## Preparing runtime/framework notices

The final `PUBLISH-PAYLOAD.json` is deliberately created from the final staged output, before any release-evidence files are added. Complete the runtime/framework notice input from a retained preliminary raw folder publish in an external scratch directory, then let the final publisher reject drift between that input and the actual staged `.deps.json`/`.runtimeconfig.json`.

~~~powershell
$preflightPublish = 'C:\release-preflight\WinBulkTranscript-0.1.0-win-x64'
# The project declares both supported RIDs; restore that locked graph before selecting one for publish.
dotnet restore .\src\WinBulkTranscript.App\WinBulkTranscript.App.csproj --locked-mode
dotnet publish .\src\WinBulkTranscript.App\WinBulkTranscript.App.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore --property:Version=0.1.0 --property:WindowsAppSDKSelfContained=true --property:PublishSingleFile=false --output $preflightPublish
Get-FileHash "$preflightPublish\WinBulkTranscript.deps.json", "$preflightPublish\WinBulkTranscript.runtimeconfig.json" -Algorithm SHA256
~~~

From those two retained raw files, copy every runtime framework name/version and every package library that is absent from the App lock but belongs to the self-contained runtime into [the runtime/framework notices template](runtime-framework-notices-template.txt), together with reviewed legal text. Use an external scratch directory and never reuse it as `ArtifactsRoot` for the final release.

The final publisher recreates a deterministic inventory of every raw output file, every `.deps.json` package library, and every declared runtime framework. It refuses notice generation if the final staged framework/package names or manifest hashes no longer agree. This is a drift check and provenance record; it does not replace legal review of framework/native file attribution.

## Safety and recovery

The publisher refuses all existing final paths and atomically reserves an artifact name before it starts. It uses unique staging paths and non-overwriting moves for the final folder, ZIP, checksum, and release record.

If publishing fails before finalization, the reservation is released and any staging output is retained for inspection. If an interruption occurs during finalization, the `.publish-reservation` file remains and some final paths may exist. Do not rerun the same version/architecture or delete that reservation until the partial output has been inspected and deliberately recovered or discarded.

## Notices and legal review

The publisher invokes `New-ThirdPartyNotices.ps1` only after it has generated a staged `.deps.json`-bound `PUBLISH-PAYLOAD.json`. The generator refuses to overwrite an existing notice, validates that each actual lock-backed `.deps.json` package matches the App lock content hash and restored NuGet `.nupkg.metadata` content hash, and embeds available package license files plus the supplied model license/provenance. It separately lists declared runtime frameworks and non-lock runtime package libraries, then embeds the reviewed runtime/framework notice input.

This is deliberately not a claim that the package lock alone covers a self-contained release. Automation can prove filename/hash/manifest relationships and that the supplied runtime text names the discovered framework/package records. It cannot decide whether copied framework, native, or runtime files are legally complete, so legal review remains a release gate. SPDX expressions and license URLs remain references that must be reviewed against the actual payload. The checked-in [template](THIRD-PARTY-NOTICES.md) is not a shipping notice.

## Unsigned artifacts

Version 1 artifacts are intentionally unsigned. A downloaded ZIP or executable can trigger Windows SmartScreen and enterprise policy can block it. Distribute through a trusted channel and do not tell users to disable Windows security controls.
