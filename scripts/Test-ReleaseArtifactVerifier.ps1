[CmdletBinding()]
param(
    [string]$VerifierPath
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($VerifierPath)) {
    $VerifierPath = Join-Path $PSScriptRoot 'Test-ReleaseArtifacts.ps1'
}

if (-not (Test-Path -LiteralPath $VerifierPath -PathType Leaf)) {
    throw "Release artifact verifier was not found: $VerifierPath"
}

function Get-Sha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-Utf8 {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Text
    )

    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Write-Json {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [object]$Value
    )

    Write-Utf8 -Path $Path -Text (($Value | ConvertTo-Json -Depth 20) + [Environment]::NewLine)
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

$version = '1.2.3'
$revision = '0123456789abcdef0123456789abcdef01234567'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("WinBulkTranscript-release-verifier-" + [Guid]::NewGuid().ToString('N'))
$assetsRoot = Join-Path $temporaryRoot 'assets'
[IO.Directory]::CreateDirectory($assetsRoot) | Out-Null

try {
    $releaseNotes = @"
# WinBulkTranscript release notes - $version

WinBulkTranscript-$version-win-x64.zip
WinBulkTranscript-$version-win-arm64.zip
This version is unsigned and Windows SmartScreen may warn.
"@

    foreach ($architecture in @('x64', 'arm64')) {
        $rid = "win-$architecture"
        $artifactName = "WinBulkTranscript-$version-$rid"
        $stagingRoot = Join-Path $temporaryRoot ("staging-" + $architecture)
        [IO.Directory]::CreateDirectory($stagingRoot) | Out-Null

        $fileContents = [ordered]@{
            'WinBulkTranscript.exe' = 'synthetic executable'
            'WinBulkTranscript.pri' = 'synthetic resource index'
            'LICENSE' = 'synthetic project license'
            'PACKAGE-LOCK.json' = '{"version":2}'
            'MODEL-LICENSE.txt' = 'synthetic model license'
            'MODEL-PROVENANCE.json' = '{"schemaVersion":1}'
            'DOTNET-RUNTIME-NOTICES.txt' = 'synthetic runtime notices'
            'RELEASE-NOTES.md' = $releaseNotes
            'RELEASE-TEST-MATRIX.md' = "Release source: ``v$version``"
            'PUBLISH-PAYLOAD.json' = '{"schemaVersion":1}'
            'THIRD-PARTY-NOTICES.md' = 'synthetic third-party notices'
        }
        foreach ($entry in $fileContents.GetEnumerator()) {
            Write-Utf8 -Path (Join-Path $stagingRoot $entry.Key) -Text $entry.Value
        }

        $metadata = [ordered]@{
            schemaVersion = 2
            version = $version
            architecture = $architecture
            runtimeIdentifier = $rid
            source = [ordered]@{
                repositoryRevision = $revision
                releaseSource = "v$version"
                releaseSourceKind = 'tag'
            }
            model = [ordered]@{
                variant = 'synthetic-model:1'
                licenseSha256 = Get-Sha256 -Path (Join-Path $stagingRoot 'MODEL-LICENSE.txt')
                provenance = [ordered]@{ sha256 = Get-Sha256 -Path (Join-Path $stagingRoot 'MODEL-PROVENANCE.json') }
            }
            packageLock = [ordered]@{ sha256 = Get-Sha256 -Path (Join-Path $stagingRoot 'PACKAGE-LOCK.json') }
            releaseTestMatrix = [ordered]@{ sha256 = Get-Sha256 -Path (Join-Path $stagingRoot 'RELEASE-TEST-MATRIX.md') }
            releaseNotes = [ordered]@{ sha256 = Get-Sha256 -Path (Join-Path $stagingRoot 'RELEASE-NOTES.md') }
            runtimeFrameworkNotices = [ordered]@{ sha256 = Get-Sha256 -Path (Join-Path $stagingRoot 'DOTNET-RUNTIME-NOTICES.txt') }
            publishPayload = [ordered]@{ sha256 = Get-Sha256 -Path (Join-Path $stagingRoot 'PUBLISH-PAYLOAD.json') }
            notices = [ordered]@{ sha256 = Get-Sha256 -Path (Join-Path $stagingRoot 'THIRD-PARTY-NOTICES.md') }
        }
        $metadataPath = Join-Path $stagingRoot 'RELEASE-METADATA.json'
        Write-Json -Path $metadataPath -Value $metadata

        $zipFileName = "$artifactName.zip"
        $zipPath = Join-Path $assetsRoot $zipFileName
        [IO.Compression.ZipFile]::CreateFromDirectory($stagingRoot, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
        $zipSha256 = Get-Sha256 -Path $zipPath

        $checksumFileName = "$zipFileName.sha256"
        [IO.File]::WriteAllText(
            (Join-Path $assetsRoot $checksumFileName),
            "$zipSha256 *$zipFileName$([Environment]::NewLine)",
            [Text.Encoding]::ASCII)

        $releaseRecord = [ordered]@{
            schemaVersion = 2
            version = $version
            architecture = $architecture
            runtimeIdentifier = $rid
            modelVariant = 'synthetic-model:1'
            artifact = [ordered]@{
                zipFile = $zipFileName
                zipSha256 = $zipSha256
                checksumFile = $checksumFileName
                embeddedMetadataSha256 = Get-Sha256 -Path $metadataPath
            }
            releaseNotes = [ordered]@{ sha256 = Get-Sha256 -Path (Join-Path $stagingRoot 'RELEASE-NOTES.md') }
            releaseTestMatrix = [ordered]@{ sha256 = Get-Sha256 -Path (Join-Path $stagingRoot 'RELEASE-TEST-MATRIX.md') }
            packageLock = [ordered]@{ sha256 = Get-Sha256 -Path (Join-Path $stagingRoot 'PACKAGE-LOCK.json') }
            modelLicense = [ordered]@{ sha256 = Get-Sha256 -Path (Join-Path $stagingRoot 'MODEL-LICENSE.txt') }
            modelProvenance = [ordered]@{ sha256 = Get-Sha256 -Path (Join-Path $stagingRoot 'MODEL-PROVENANCE.json') }
            runtimeFrameworkNotices = [ordered]@{ sha256 = Get-Sha256 -Path (Join-Path $stagingRoot 'DOTNET-RUNTIME-NOTICES.txt') }
            publishPayload = [ordered]@{ sha256 = Get-Sha256 -Path (Join-Path $stagingRoot 'PUBLISH-PAYLOAD.json') }
            thirdPartyNotices = [ordered]@{ sha256 = Get-Sha256 -Path (Join-Path $stagingRoot 'THIRD-PARTY-NOTICES.md') }
            source = $metadata.source
        }
        Write-Json -Path (Join-Path $assetsRoot "$artifactName.release-record.json") -Value $releaseRecord
    }

    $checksumManifestPath = Join-Path $assetsRoot 'SHA256SUMS.txt'
    & $VerifierPath -ArtifactsRoot $assetsRoot -Version $version -RepositoryRevision $revision -ChecksumManifestPath $checksumManifestPath
    $manifestLines = @([IO.File]::ReadAllLines($checksumManifestPath) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($manifestLines.Count -ne 2) {
        throw "The verifier wrote $($manifestLines.Count) combined checksum lines; expected 2."
    }

    [IO.File]::Delete($checksumManifestPath)
    $corruptSidecarPath = Join-Path $assetsRoot "WinBulkTranscript-$version-win-x64.zip.sha256"
    [IO.File]::WriteAllText($corruptSidecarPath, (('0' * 64) + " *WinBulkTranscript-$version-win-x64.zip`r`n"), [Text.Encoding]::ASCII)
    $rejectedCorruptSidecar = $false
    try {
        & $VerifierPath -ArtifactsRoot $assetsRoot -Version $version -RepositoryRevision $revision -ChecksumManifestPath $checksumManifestPath
    }
    catch {
        $rejectedCorruptSidecar = $_.Exception.Message.IndexOf('Checksum sidecar', [StringComparison]::Ordinal) -ge 0
    }

    if (-not $rejectedCorruptSidecar) {
        throw 'The verifier did not reject a corrupt architecture checksum sidecar.'
    }

    Write-Host 'Release artifact verifier behavioral tests passed. No release was created.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        [IO.Directory]::Delete($temporaryRoot, $true)
    }
}
