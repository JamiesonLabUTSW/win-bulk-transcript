[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$ArtifactsRoot,

    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$RepositoryRevision,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ChecksumManifestPath
)

$ErrorActionPreference = 'Stop'
$resolvedArtifactsRoot = [IO.Path]::GetFullPath($ArtifactsRoot).TrimEnd('\', '/')
$resolvedChecksumManifestPath = [IO.Path]::GetFullPath($ChecksumManifestPath)
$checksumManifestParent = [IO.Path]::GetDirectoryName($resolvedChecksumManifestPath).TrimEnd('\', '/')
if (-not [string]::Equals($resolvedArtifactsRoot, $checksumManifestParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ChecksumManifestPath must be a direct child of ArtifactsRoot.'
}

if (Test-Path -LiteralPath $resolvedChecksumManifestPath) {
    throw "Refusing to overwrite an existing checksum manifest: $resolvedChecksumManifestPath"
}

function Assert-Equal {
    param(
        [AllowNull()]
        [object]$Actual,

        [AllowNull()]
        [object]$Expected,

        [Parameter(Mandatory)]
        [string]$Description,

        [switch]$IgnoreCase
    )

    $matches = if ($IgnoreCase) {
        [string]::Equals([string]$Actual, [string]$Expected, [StringComparison]::OrdinalIgnoreCase)
    }
    else {
        [string]::Equals([string]$Actual, [string]$Expected, [StringComparison]::Ordinal)
    }

    if (-not $matches) {
        throw "$Description is '$Actual'; expected '$Expected'."
    }
}

function Get-Sha256FromBytes {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes
    )

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant())
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-ZipEntryBytes {
    param(
        [Parameter(Mandatory)]
        [IO.Compression.ZipArchiveEntry]$Entry
    )

    $entryStream = $Entry.Open()
    try {
        $memoryStream = [IO.MemoryStream]::new()
        try {
            $entryStream.CopyTo($memoryStream)
            return $memoryStream.ToArray()
        }
        finally {
            $memoryStream.Dispose()
        }
    }
    finally {
        $entryStream.Dispose()
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

$architectures = @('x64', 'arm64')
$expectedAssetNames = foreach ($architecture in $architectures) {
    $artifactName = "WinBulkTranscript-$Version-win-$architecture"
    "$artifactName.zip"
    "$artifactName.zip.sha256"
    "$artifactName.release-record.json"
}

$actualAssetNames = @(Get-ChildItem -LiteralPath $resolvedArtifactsRoot -File | ForEach-Object { $_.Name })
$missingAssetNames = @($expectedAssetNames | Where-Object { $_ -notin $actualAssetNames })
if ($missingAssetNames.Count -gt 0) {
    throw "Release asset set is incomplete: $($missingAssetNames -join ', ')"
}

$unexpectedAssetNames = @($actualAssetNames | Where-Object { $_ -notin $expectedAssetNames })
if ($unexpectedAssetNames.Count -gt 0) {
    throw "Release asset set contains unexpected files: $($unexpectedAssetNames -join ', ')"
}

$requiredZipEntries = @(
    'WinBulkTranscript.exe',
    'WinBulkTranscript.pri',
    'LICENSE',
    'PACKAGE-LOCK.json',
    'MODEL-LICENSE.txt',
    'MODEL-PROVENANCE.json',
    'DOTNET-RUNTIME-NOTICES.txt',
    'RELEASE-NOTES.md',
    'RELEASE-TEST-MATRIX.md',
    'PUBLISH-PAYLOAD.json',
    'THIRD-PARTY-NOTICES.md',
    'RELEASE-METADATA.json'
)

$checksumLines = @()
$sharedReleaseBindings = $null
foreach ($architecture in $architectures) {
    $rid = "win-$architecture"
    $artifactName = "WinBulkTranscript-$Version-$rid"
    $zipFileName = "$artifactName.zip"
    $checksumFileName = "$zipFileName.sha256"
    $releaseRecordFileName = "$artifactName.release-record.json"
    $zipPath = Join-Path $resolvedArtifactsRoot $zipFileName
    $checksumPath = Join-Path $resolvedArtifactsRoot $checksumFileName
    $releaseRecordPath = Join-Path $resolvedArtifactsRoot $releaseRecordFileName

    $zipSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumText = [IO.File]::ReadAllText($checksumPath)
    $expectedChecksumPattern = '\A' + [regex]::Escape($zipSha256) + ' \*' + [regex]::Escape($zipFileName) + '\r?\n?\z'
    if ($checksumText -cnotmatch $expectedChecksumPattern) {
        throw "Checksum sidecar '$checksumFileName' does not contain the exact ZIP SHA-256 and filename."
    }

    try {
        $releaseRecord = [IO.File]::ReadAllText($releaseRecordPath) | ConvertFrom-Json
    }
    catch {
        throw "Release record '$releaseRecordFileName' is not valid JSON: $($_.Exception.Message)"
    }

    Assert-Equal $releaseRecord.schemaVersion 2 "$releaseRecordFileName schemaVersion"
    Assert-Equal $releaseRecord.version $Version "$releaseRecordFileName version"
    Assert-Equal $releaseRecord.architecture $architecture "$releaseRecordFileName architecture"
    Assert-Equal $releaseRecord.runtimeIdentifier $rid "$releaseRecordFileName runtime identifier"
    Assert-Equal $releaseRecord.artifact.zipFile $zipFileName "$releaseRecordFileName ZIP filename"
    Assert-Equal $releaseRecord.artifact.zipSha256 $zipSha256 "$releaseRecordFileName ZIP SHA-256" -IgnoreCase
    Assert-Equal $releaseRecord.artifact.checksumFile $checksumFileName "$releaseRecordFileName checksum filename"
    Assert-Equal $releaseRecord.source.repositoryRevision $RepositoryRevision "$releaseRecordFileName source revision" -IgnoreCase
    Assert-Equal $releaseRecord.source.releaseSource "v$Version" "$releaseRecordFileName release source"
    Assert-Equal $releaseRecord.source.releaseSourceKind 'tag' "$releaseRecordFileName release source kind"
    if ([string]::IsNullOrWhiteSpace([string]$releaseRecord.modelVariant)) {
        throw "$releaseRecordFileName does not record the configured model variant."
    }

    $currentSharedBindings = [ordered]@{
        ModelVariant = [string]$releaseRecord.modelVariant
        PackageLockSha256 = [string]$releaseRecord.packageLock.sha256
        ModelLicenseSha256 = [string]$releaseRecord.modelLicense.sha256
        ModelProvenanceSha256 = [string]$releaseRecord.modelProvenance.sha256
        ReleaseNotesSha256 = [string]$releaseRecord.releaseNotes.sha256
    }
    if ($null -eq $sharedReleaseBindings) {
        $sharedReleaseBindings = $currentSharedBindings
    }
    else {
        foreach ($bindingName in $sharedReleaseBindings.Keys) {
            Assert-Equal $currentSharedBindings[$bindingName] $sharedReleaseBindings[$bindingName] "Cross-architecture $bindingName" -IgnoreCase
        }
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entriesByName = @{}
        foreach ($entry in $archive.Entries) {
            $normalizedName = $entry.FullName.Replace('\', '/')
            if ($normalizedName.StartsWith('/') -or $normalizedName -match '(^|/)\.\.(/|$)' -or $normalizedName -match '^[A-Za-z]:') {
                throw "ZIP '$zipFileName' contains an unsafe entry path: $($entry.FullName)"
            }

            if (-not [string]::IsNullOrEmpty($entry.Name)) {
                if ($entriesByName.ContainsKey($normalizedName)) {
                    throw "ZIP '$zipFileName' contains a duplicate entry: $normalizedName"
                }

                $entriesByName[$normalizedName] = $entry
            }
        }

        foreach ($requiredEntry in $requiredZipEntries) {
            if (-not $entriesByName.ContainsKey($requiredEntry)) {
                throw "ZIP '$zipFileName' is missing required root entry '$requiredEntry'."
            }

            if ($entriesByName[$requiredEntry].Length -eq 0) {
                throw "ZIP '$zipFileName' contains an empty required entry '$requiredEntry'."
            }
        }

        $metadataBytes = Get-ZipEntryBytes -Entry $entriesByName['RELEASE-METADATA.json']
        $metadataSha256 = Get-Sha256FromBytes -Bytes $metadataBytes
        Assert-Equal $releaseRecord.artifact.embeddedMetadataSha256 $metadataSha256 "$releaseRecordFileName embedded metadata SHA-256" -IgnoreCase
        try {
            $metadata = [Text.Encoding]::UTF8.GetString($metadataBytes) | ConvertFrom-Json
        }
        catch {
            throw "ZIP '$zipFileName' contains invalid RELEASE-METADATA.json: $($_.Exception.Message)"
        }

        Assert-Equal $metadata.schemaVersion 2 "$zipFileName metadata schemaVersion"
        Assert-Equal $metadata.version $Version "$zipFileName metadata version"
        Assert-Equal $metadata.architecture $architecture "$zipFileName metadata architecture"
        Assert-Equal $metadata.runtimeIdentifier $rid "$zipFileName metadata runtime identifier"
        Assert-Equal $metadata.source.repositoryRevision $RepositoryRevision "$zipFileName metadata source revision" -IgnoreCase
        Assert-Equal $metadata.source.releaseSource "v$Version" "$zipFileName metadata release source"
        Assert-Equal $metadata.source.releaseSourceKind 'tag' "$zipFileName metadata release source kind"
        Assert-Equal $metadata.model.variant $releaseRecord.modelVariant "$zipFileName metadata model variant"
        Assert-Equal $metadata.packageLock.sha256 $releaseRecord.packageLock.sha256 "$zipFileName metadata package lock SHA-256" -IgnoreCase
        Assert-Equal $metadata.model.licenseSha256 $releaseRecord.modelLicense.sha256 "$zipFileName metadata model license SHA-256" -IgnoreCase
        Assert-Equal $metadata.model.provenance.sha256 $releaseRecord.modelProvenance.sha256 "$zipFileName metadata model provenance SHA-256" -IgnoreCase
        Assert-Equal $metadata.releaseNotes.sha256 $releaseRecord.releaseNotes.sha256 "$zipFileName metadata release notes SHA-256" -IgnoreCase
        Assert-Equal $metadata.releaseTestMatrix.sha256 $releaseRecord.releaseTestMatrix.sha256 "$zipFileName metadata release matrix SHA-256" -IgnoreCase
        Assert-Equal $metadata.runtimeFrameworkNotices.sha256 $releaseRecord.runtimeFrameworkNotices.sha256 "$zipFileName metadata runtime/framework notices SHA-256" -IgnoreCase
        Assert-Equal $metadata.publishPayload.sha256 $releaseRecord.publishPayload.sha256 "$zipFileName metadata publish payload SHA-256" -IgnoreCase
        Assert-Equal $metadata.notices.sha256 $releaseRecord.thirdPartyNotices.sha256 "$zipFileName metadata third-party notices SHA-256" -IgnoreCase

        foreach ($evidenceBinding in @(
                [PSCustomObject]@{ Entry = 'RELEASE-NOTES.md'; Hash = $releaseRecord.releaseNotes.sha256; Description = 'release notes' },
                [PSCustomObject]@{ Entry = 'RELEASE-TEST-MATRIX.md'; Hash = $releaseRecord.releaseTestMatrix.sha256; Description = 'release test matrix' },
                [PSCustomObject]@{ Entry = 'PACKAGE-LOCK.json'; Hash = $releaseRecord.packageLock.sha256; Description = 'package lock' },
                [PSCustomObject]@{ Entry = 'MODEL-LICENSE.txt'; Hash = $releaseRecord.modelLicense.sha256; Description = 'model license' },
                [PSCustomObject]@{ Entry = 'MODEL-PROVENANCE.json'; Hash = $releaseRecord.modelProvenance.sha256; Description = 'model provenance' },
                [PSCustomObject]@{ Entry = 'DOTNET-RUNTIME-NOTICES.txt'; Hash = $releaseRecord.runtimeFrameworkNotices.sha256; Description = 'runtime/framework notices' },
                [PSCustomObject]@{ Entry = 'PUBLISH-PAYLOAD.json'; Hash = $releaseRecord.publishPayload.sha256; Description = 'publish payload inventory' },
                [PSCustomObject]@{ Entry = 'THIRD-PARTY-NOTICES.md'; Hash = $releaseRecord.thirdPartyNotices.sha256; Description = 'third-party notices' })) {
            $entryHash = Get-Sha256FromBytes -Bytes (Get-ZipEntryBytes -Entry $entriesByName[$evidenceBinding.Entry])
            Assert-Equal $evidenceBinding.Hash $entryHash "$zipFileName $($evidenceBinding.Description) SHA-256" -IgnoreCase
        }

        $releaseNotesText = [Text.Encoding]::UTF8.GetString((Get-ZipEntryBytes -Entry $entriesByName['RELEASE-NOTES.md']))
        foreach ($expectedArchitecture in $architectures) {
            $expectedZipName = "WinBulkTranscript-$Version-win-$expectedArchitecture.zip"
            if ($releaseNotesText.IndexOf($expectedZipName, [StringComparison]::Ordinal) -lt 0) {
                throw "Embedded release notes in '$zipFileName' do not name both architecture assets; missing '$expectedZipName'."
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    $checksumLines += "$zipSha256 *$zipFileName"
}

$checksumManifestText = ($checksumLines -join [Environment]::NewLine) + [Environment]::NewLine
[IO.File]::WriteAllText($resolvedChecksumManifestPath, $checksumManifestText, [Text.Encoding]::ASCII)
Write-Host "Validated release assets and wrote $resolvedChecksumManifestPath"
