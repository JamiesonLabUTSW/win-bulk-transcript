# Release evidence

This directory records the evidence used to turn a successful build into a release. Version-zero releases use the preview policy; version 1 and later use the supported-release policy. A tag release stores its inputs in a committed [`release-inputs/v<version>` dossier](../../release-inputs/README.md).

## Distribution format

Version 1 ships as separate unpackaged, self-contained x64 and ARM64 ZIPs. This is the Windows App SDK deployment shape for xcopy/ZIP distribution: users extract the matching archive and run the application without registering a package or installing .NET/Windows App SDK separately.

MSIX remains a future signed-distribution option. Windows 11 supports `Add-AppxPackage -AllowUnsigned` for an unsigned MSIX, but Microsoft describes it as a testing feature rather than broad distribution; executable packages normally require elevated, all-user installation and a special unsigned identity. A self-signed MSIX instead requires every target machine to trust the certificate first. Move to MSIX when the project has a Store, enterprise-trusted, or Artifact Signing identity and has validated package install/update behavior; do not publish a testing-only unsigned MSIX as an end-user installer.

## Preflight and build

Complete a release test matrix before publishing an architecture. The publisher requires all of the following:

- The matrix release version, model variant, and release source match the command and verified repository revision/tag.
- The entire source working tree is clean, every release-owned source path is tracked at `HEAD`, and `HEAD` remains unchanged through restore and publish.
- The version-derived release policy is recorded in the matrix. Preview 0.x releases permit exactly `Passed`, `Accepted risk`, or `Not applicable`; non-passing rows require concrete rationale, and `Accepted risk` requires `Approver: <name>; Date: <YYYY-MM-DD>; Decision: <explicit decision>`. Supported 1.x and later releases require exactly `Passed` for every applicable architecture row.
- The matrix has a receipt row for the exact output ZIP name. Its checksum column should say that the publisher writes the matching sidecar; a ZIP cannot contain its own final hash.
- Complete the [release notes](release-notes-template.md), [model provenance](model-provenance-template.json), and architecture-specific [runtime/framework information](runtime-framework-notices-template.txt). Their matrix headers must be the exact input filename plus SHA-256, not a generic template or an un-hashed path.
- The runtime/framework input must name every framework from the final `.runtimeconfig.json` and every non-lock framework package from the final `.deps.json`. A preview records the current terms sources; a supported 1.x release requires the stricter reviewed notice set.
- Record the literal single-file experiment result in the matrix header or linked evidence. It is future information only; do not replace the version 1 folder/ZIP artifact with that experiment.

For a tag release, set each matrix's Release source header to the exact tag, such as `v1.2.3`. The publisher verifies that the tag resolves to the checked-out revision. For an explicitly local/manual package without `-ReleaseSourceRef`, use the full output of `git rev-parse HEAD`.

The model argument is a validation assertion, not an override. It must equal the exact model configured in the application source. Update and test that configuration before changing the release command.

Use the [release-notes template](release-notes-template.md) for every architecture artifact. It contains the mandatory first-run connectivity explanation and unsigned SmartScreen warning. Use the model-provenance template to bind the exact model identifier, catalog/source URI, and supplied license hash. Complete the runtime/framework template from a retained preliminary raw publish; the final staged `PUBLISH-PAYLOAD.json` confirms that input rather than replacing it.

~~~powershell
$modelVariant = 'nemotron-speech-streaming-en-0.6b-generic-cpu:3'
$modelLicense = 'C:\release-inputs\model-license.txt'
$modelProvenance = 'C:\release-inputs\model-provenance.json'
$x64RuntimeFrameworkNotices = 'C:\release-inputs\runtime-framework-notices-win-x64.txt'
$arm64RuntimeFrameworkNotices = 'C:\release-inputs\runtime-framework-notices-win-arm64.txt'
$releaseNotes = 'C:\release-inputs\release-notes.md'
$x64TestMatrix = 'C:\release-inputs\release-test-matrix-win-x64.md'
$arm64TestMatrix = 'C:\release-inputs\release-test-matrix-win-arm64.md'
$artifactsRoot = 'C:\release-artifacts'

# Checks the model/configuration, matrix, current source revision, and inputs without writing artifacts.
.\scripts\Publish-Release.ps1 -Version 0.1.0 -Architecture x64 -ModelVariant $modelVariant -ModelLicensePath $modelLicense -ModelProvenancePath $modelProvenance -RuntimeFrameworkNoticesPath $x64RuntimeFrameworkNotices -ReleaseNotesPath $releaseNotes -ReleaseTestMatrixPath $x64TestMatrix -ArtifactsRoot $artifactsRoot -ValidateOnly

.\scripts\Publish-Release.ps1 -Version 0.1.0 -Architecture x64 -ModelVariant $modelVariant -ModelLicensePath $modelLicense -ModelProvenancePath $modelProvenance -RuntimeFrameworkNoticesPath $x64RuntimeFrameworkNotices -ReleaseNotesPath $releaseNotes -ReleaseTestMatrixPath $x64TestMatrix -ArtifactsRoot $artifactsRoot
.\scripts\Publish-Release.ps1 -Version 0.1.0 -Architecture arm64 -ModelVariant $modelVariant -ModelLicensePath $modelLicense -ModelProvenancePath $modelProvenance -RuntimeFrameworkNoticesPath $arm64RuntimeFrameworkNotices -ReleaseNotesPath $releaseNotes -ReleaseTestMatrixPath $arm64TestMatrix -ArtifactsRoot $artifactsRoot
~~~

## Tag-based GitHub release

The [`Release` workflow](../../.github/workflows/release.yml) runs for canonical SemVer tags such as `v1.2.3` and `v1.2.3-rc.1`. Before creating the tag:

1. For a 0.x preview, record and approve any accepted validation limitations in both matrices. For a supported 1.x release, complete every x64 and ARM64 gate.
2. Copy the templates into `release-inputs/v<version>/` using the documented dossier layout. Use one shared `release-notes.md` that names both ZIPs and architecture-specific matrices/runtime notices.
3. Complete the model information, provenance, shared release notes, and both runtime/framework information files. Hash those files, bind the hashes in each matrix, set `Release source` to the future tag, and satisfy the version-derived matrix policy. Preview inputs are disclosure records; supported 1.x inputs follow the stricter reviewed-input policy.
4. Merge the dossier and all release changes to the repository's default branch. The tag workflow refuses a commit that is not reachable from that branch.
5. Create the tag on that exact commit and push it: `git tag v1.2.3; git push origin v1.2.3`. Do not move or reuse a release tag.

The workflow independently builds x64 and ARM64 from the tagged commit, runs the deterministic build/tests and hardening checks, invokes the same publisher used locally, and transfers only each ZIP, checksum sidecar, and release record. Every 0.x version and every version with a SemVer prerelease suffix is marked as a GitHub prerelease. Its publication job revalidates the assembled six-file set and embedded metadata/hashes, writes `SHA256SUMS.txt`, generates GitHub/Sigstore build-provenance attestations, and creates a **draft** GitHub Release from the reviewed shared notes. Configure the repository's `release` environment with required reviewers where the repository plan supports them so the write-capable job cannot run without approval. Also [enable immutable releases](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/establish-provenance-and-integrity/prevent-release-changes) and protect `v*` tags from unauthorized creation, update, and deletion with a [tag ruleset](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets). A human must inspect the draft assets/notes and publish it in GitHub; the workflow deliberately does not make a public release automatically. When release immutability is enabled, publishing the completed draft locks its tag and assets.

The release job needs only the scoped `GITHUB_TOKEN`; no signing secret is required for the current unsigned ZIPs. GitHub attestations establish which workflow and commit produced a ZIP, but they are not Authenticode signatures and do not prevent SmartScreen warnings. Consumers can verify an asset with `gh attestation verify <zip> --repo JamiesonLabUTSW/win-bulk-transcript` in addition to checking `SHA256SUMS.txt`.

The scripts support Windows PowerShell 5.1 and PowerShell 7. The build machine needs the SDK selected by [`global.json`](../../global.json) and the locked package graph; end-user clean-machine requirements apply to the published artifact, not the build machine.

`ArtifactsRoot` is mandatory and must be outside the repository (for example `C:\release-artifacts`). This keeps build output outside the source tree, so provenance verification does not depend on ignored-output policy.

For each architecture, the publisher snapshots the test matrix, App package lock, model license, model provenance, runtime/framework notice, release notes, and both release scripts before restore. It invokes the third-party-notices generator from its captured byte snapshot rather than rereading its mutable source path. It refuses to finalize if any snapshot, the canonical model contract, the host alias, `HEAD`, or the clean tracked source state changes during the build; it repeats this check after notices generation and immediately before finalization. It performs a locked restore, publishes to a unique staging directory, inventories the raw output before evidence files are copied, and only then moves artifacts to their final names.

The folder and ZIP include:

- `LICENSE` (the WinBulkTranscript project license), `PACKAGE-LOCK.json`, `MODEL-LICENSE.txt`, `MODEL-PROVENANCE.json`, `DOTNET-RUNTIME-NOTICES.txt`, `RELEASE-NOTES.md`, `RELEASE-TEST-MATRIX.md`, `PUBLISH-PAYLOAD.json`, `THIRD-PARTY-NOTICES.md`, and `RELEASE-METADATA.json`.

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

## Notices

The publisher invokes `New-ThirdPartyNotices.ps1` only after it has generated a staged `.deps.json`-bound `PUBLISH-PAYLOAD.json`. The generator refuses to overwrite an existing notice, validates that each actual lock-backed `.deps.json` package matches the App lock content hash and restored NuGet `.nupkg.metadata` content hash, and embeds available package license files plus the supplied model license/provenance. It separately lists declared runtime frameworks and non-lock runtime package libraries, then embeds the reviewed runtime/framework notice input.

The package lock alone does not describe every file in a self-contained release. Automation proves filename/hash/manifest relationships and checks that the supplied runtime text names the discovered framework/package records. For a 0.x preview, the generated notice records those current disclosures. The supported 1.x policy additionally requires reviewed legal completeness. The checked-in [template](THIRD-PARTY-NOTICES.md) is not a shipping notice.

## Unsigned artifacts

Version 1 artifacts are intentionally unsigned. A downloaded ZIP or executable can trigger Windows SmartScreen and enterprise policy can block it. Distribute through a trusted channel and do not tell users to disable Windows security controls.
