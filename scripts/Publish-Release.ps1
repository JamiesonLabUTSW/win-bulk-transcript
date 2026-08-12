[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?$')]
    [string]$Version,

    [ValidatePattern('^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?$')]
    [string]$ReleaseSourceRef,

    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ModelVariant,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ModelLicensePath,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ModelProvenancePath,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$RuntimeFrameworkNoticesPath,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ReleaseNotesPath,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ReleaseTestMatrixPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ArtifactsRoot,

    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$Architecture = $Architecture.ToLowerInvariant()

function Get-UtcTimestamp {
    return [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
}

function Get-Sha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Sha256FromBytes {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes
    )

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $algorithm.ComputeHash($Bytes)
        return ([BitConverter]::ToString($hash).Replace('-', '').ToLowerInvariant())
    }
    finally {
        $algorithm.Dispose()
    }
}

function Invoke-Dotnet {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & dotnet @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $exitCode. No release artifact was finalized."
    }
}

function Get-DotnetSdkVersion {
    $output = & dotnet --version
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "dotnet --version failed with exit code $exitCode."
    }

    $version = [string](@($output | Select-Object -Last 1))
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw 'dotnet --version returned no SDK version.'
    }

    return $version.Trim()
}

function Get-RepositoryRevision {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $output = & git -C $RepositoryRoot rev-parse HEAD
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "git -C $RepositoryRoot rev-parse HEAD failed with exit code $exitCode. Release evidence requires the exact source revision."
    }

    $revision = [string](@($output | Select-Object -Last 1))
    if ($revision -notmatch '^[0-9a-fA-F]{40}$') {
        throw "git rev-parse HEAD returned an invalid revision: '$revision'."
    }

    return $revision.ToLowerInvariant()
}

function Get-ValidatedReleaseSource {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$RepositoryRevision,

        [AllowEmptyString()]
        [string]$SourceRef
    )

    if ([string]::IsNullOrWhiteSpace($SourceRef)) {
        return $RepositoryRevision
    }

    $tagRef = "refs/tags/$SourceRef"
    $output = & git -C $RepositoryRoot rev-parse --verify "${tagRef}^{commit}"
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Release source '$SourceRef' is not an existing local tag. Fetch or create the exact tag before publishing."
    }

    $tagRevision = [string](@($output | Select-Object -Last 1))
    if (-not [string]::Equals($tagRevision, $RepositoryRevision, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release source tag '$SourceRef' resolves to '$tagRevision', but the checked-out release revision is '$RepositoryRevision'."
    }

    return $SourceRef
}

function Assert-ReleaseWorkingTreeClean {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $status = @(& git -C $RepositoryRoot status --porcelain=v1 --untracked-files=all)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "git -C $RepositoryRoot status failed with exit code $exitCode. A release can only be associated with a verified committed source tree."
    }

    $changes = @($status | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($changes.Count -gt 0) {
        $sample = ($changes | Select-Object -First 10) -join '; '
        throw "Release publishing requires a clean committed working tree, including no untracked release inputs. Commit, remove, or relocate these entries before publishing: $sample"
    }
}

function Assert-ReleasePathsTracked {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string[]]$RepositoryPaths
    )

    foreach ($repositoryPath in $RepositoryPaths) {
        & git -C $RepositoryRoot ls-files --error-unmatch -- $repositoryPath 2>$null | Out-Null
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            throw "Release-owned source '$repositoryPath' is not committed at HEAD. Release evidence must be tied to tracked, committed source."
        }
    }
}

function Get-VerifiedReleaseRevision {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string[]]$RequiredTrackedPaths
    )

    Assert-ReleaseWorkingTreeClean -RepositoryRoot $RepositoryRoot
    Assert-ReleasePathsTracked -RepositoryRoot $RepositoryRoot -RepositoryPaths $RequiredTrackedPaths
    return Get-RepositoryRevision -RepositoryRoot $RepositoryRoot
}

function Assert-VerifiedReleaseRevision {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$ExpectedRevision,

        [Parameter(Mandatory)]
        [string[]]$RequiredTrackedPaths
    )

    $actualRevision = Get-VerifiedReleaseRevision -RepositoryRoot $RepositoryRoot -RequiredTrackedPaths $RequiredTrackedPaths
    if (-not [string]::Equals($actualRevision, $ExpectedRevision, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Repository HEAD changed from '$ExpectedRevision' to '$actualRevision' while the release was being prepared. Refuse to publish an artifact with unverifiable source provenance."
    }
}

function Get-ConfiguredReleaseModelVariant {
    param(
        [Parameter(Mandatory)]
        [string]$ModelHostSourcePath
    )

    $source = [IO.File]::ReadAllText($ModelHostSourcePath)
    $match = [regex]::Match(
        $source,
        'public\s+const\s+string\s+InitialCandidateModelVariant\s*=\s*"(?<variant>[^"]+)"\s*;',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        throw "Could not locate InitialCandidateModelVariant in '$ModelHostSourcePath'. Refuse to publish metadata that cannot be tied to the application configuration."
    }

    return $match.Groups['variant'].Value
}

function Assert-ModelHostUsesConfiguredContract {
    param(
        [Parameter(Mandatory)]
        [string]$ModelHostSourcePath
    )

    $source = [IO.File]::ReadAllText($ModelHostSourcePath)
    $match = [regex]::Match(
        $source,
        'public\s+const\s+string\s+InitialCandidateModelVariant\s*=\s*FoundryModelContract\.InitialCandidateModelVariant\s*;',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        throw "'$ModelHostSourcePath' must alias FoundryModelContract.InitialCandidateModelVariant. Refuse to publish model provenance when the application host does not use the canonical model contract."
    }
}

function Get-ReleaseMatrixHeaderValue {
    param(
        [Parameter(Mandatory)]
        [string]$MatrixText,

        [Parameter(Mandatory)]
        [string]$Label
    )

    $pattern = '(?m)^\s*' + [regex]::Escape($Label) + '\s*:\s*\x60(?<value>[^\x60\r\n]+)\x60[ \t]*\r?$'
    $match = [regex]::Match($MatrixText, $pattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        throw "Release test matrix is missing the required '$Label' header."
    }

    return $match.Groups['value'].Value.Trim()
}

function Get-RequiredJsonString {
    param(
        [Parameter(Mandatory)]
        [object]$Object,

        [Parameter(Mandatory)]
        [string]$PropertyName,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $property = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "$Description is missing required string property '$PropertyName'."
    }

    return ([string]$property.Value).Trim()
}

function Get-ReleaseInputReference {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Sha256
    )

    return "$([IO.Path]::GetFileName($Path)) sha256:$($Sha256.ToLowerInvariant())"
}

function Assert-ReleaseInputOutsideArtifactsRoot {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$ArtifactsRoot,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedArtifactsRoot = [IO.Path]::GetFullPath($ArtifactsRoot)
    if (-not $resolvedArtifactsRoot.EndsWith([string][IO.Path]::DirectorySeparatorChar)) {
        $resolvedArtifactsRoot += [IO.Path]::DirectorySeparatorChar
    }

    if ($resolvedPath.StartsWith($resolvedArtifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must be a release input outside the artifacts root. Refuse to reuse a file that could be part of a previous or partial release: $resolvedPath"
    }
}

function Assert-ArtifactsRootOutsideRepository {
    param(
        [Parameter(Mandatory)]
        [string]$ArtifactsRoot,

        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $resolvedArtifactsRoot = [IO.Path]::GetFullPath($ArtifactsRoot)
    $resolvedRepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
    if (-not $resolvedRepositoryRoot.EndsWith([string][IO.Path]::DirectorySeparatorChar)) {
        $resolvedRepositoryRoot += [IO.Path]::DirectorySeparatorChar
    }

    if ($resolvedArtifactsRoot.StartsWith($resolvedRepositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or [string]::Equals($resolvedArtifactsRoot, $resolvedRepositoryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
        throw "ArtifactsRoot must be outside the repository so staging and reservation files cannot invalidate committed-source verification: $resolvedArtifactsRoot"
    }
}

function Assert-ReleaseNotes {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$ArtifactZipFileName
    )

    $notesText = [IO.File]::ReadAllText($Path)
    if ([string]::IsNullOrWhiteSpace($notesText)) {
        throw "The supplied release notes file is empty: $Path"
    }

    if ($notesText.IndexOf($Version, [StringComparison]::Ordinal) -lt 0) {
        throw "Release notes '$Path' do not name requested version '$Version'."
    }

    if ($notesText.IndexOf($ArtifactZipFileName, [StringComparison]::Ordinal) -lt 0) {
        throw "Release notes '$Path' do not name the exact artifact '$ArtifactZipFileName'."
    }

    if ($notesText -notmatch '(?i)\bunsigned\b' -or $notesText -notmatch '(?i)\bSmartScreen\b') {
        throw "Release notes '$Path' must retain the unsigned-download and SmartScreen warning."
    }
}

function Read-ModelProvenance {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$ExpectedModelVariant,

        [Parameter(Mandatory)]
        [string]$ModelLicensePath,

        [Parameter(Mandatory)]
        [string]$ModelLicenseSha256
    )

    try {
        $provenance = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
    }
    catch {
        throw "Model provenance input '$Path' is not valid JSON: $($_.Exception.Message)"
    }

    $schemaVersion = Get-RequiredJsonString -Object $provenance -PropertyName 'schemaVersion' -Description "Model provenance '$Path'"
    if ($schemaVersion -cne '1') {
        throw "Model provenance '$Path' has unsupported schemaVersion '$schemaVersion'; expected '1'."
    }

    $modelVariant = Get-RequiredJsonString -Object $provenance -PropertyName 'modelVariant' -Description "Model provenance '$Path'"
    if (-not [string]::Equals($modelVariant, $ExpectedModelVariant, [StringComparison]::Ordinal)) {
        throw "Model provenance variant '$modelVariant' does not match the model configured by the application ('$ExpectedModelVariant')."
    }

    $artifactIdentifier = Get-RequiredJsonString -Object $provenance -PropertyName 'artifactIdentifier' -Description "Model provenance '$Path'"
    $sourceUri = Get-RequiredJsonString -Object $provenance -PropertyName 'sourceUri' -Description "Model provenance '$Path'"
    try {
        $uri = [Uri]$sourceUri
    }
    catch {
        throw "Model provenance sourceUri '$sourceUri' is not an absolute URI."
    }

    if (-not $uri.IsAbsoluteUri -or ($uri.Scheme -cne 'https' -and $uri.Scheme -cne 'urn')) {
        throw "Model provenance sourceUri '$sourceUri' must be an absolute https or urn URI."
    }

    $licenseFileName = Get-RequiredJsonString -Object $provenance -PropertyName 'licenseFileName' -Description "Model provenance '$Path'"
    $expectedLicenseFileName = [IO.Path]::GetFileName($ModelLicensePath)
    if (-not [string]::Equals($licenseFileName, $expectedLicenseFileName, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Model provenance licenseFileName '$licenseFileName' does not match the supplied license file '$expectedLicenseFileName'."
    }

    $licenseSha256 = Get-RequiredJsonString -Object $provenance -PropertyName 'licenseSha256' -Description "Model provenance '$Path'"
    if ($licenseSha256 -notmatch '^[0-9a-fA-F]{64}$' -or -not [string]::Equals($licenseSha256, $ModelLicenseSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Model provenance licenseSha256 does not match the supplied model license file."
    }

    return [PSCustomObject]@{
        SchemaVersion = $schemaVersion
        ModelVariant = $modelVariant
        ArtifactIdentifier = $artifactIdentifier
        SourceUri = $uri.AbsoluteUri
        LicenseFileName = $licenseFileName
        LicenseSha256 = $licenseSha256.ToLowerInvariant()
    }
}

function ConvertFrom-MarkdownTableRow {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Line
    )

    $trimmed = $Line.Trim()
    if (-not $trimmed.StartsWith('|') -or -not $trimmed.EndsWith('|')) {
        return $null
    }

    return @($trimmed.Trim('|').Split('|') | ForEach-Object { $_.Trim() })
}

function Test-IsMarkdownTableSeparator {
    param(
        [Parameter(Mandatory)]
        [string[]]$Cells
    )

    if ($Cells.Count -eq 0) {
        return $false
    }

    foreach ($cell in $Cells) {
        if ($cell -notmatch '^:?-{3,}:?$') {
            return $false
        }
    }

    return $true
}

function Assert-CompletedReleaseTestMatrix {
    param(
        [Parameter(Mandatory)]
        [string]$MatrixText,

        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$ModelVariant,

        [Parameter(Mandatory)]
        [string]$Architecture,

        [Parameter(Mandatory)]
        [string]$ReleaseNotesSource,

        [Parameter(Mandatory)]
        [string]$ModelProvenanceSource,

        [Parameter(Mandatory)]
        [string]$RuntimeFrameworkNoticesSource,

        [Parameter(Mandatory)]
        [string]$ReleaseSource
    )

    $matrixVersion = Get-ReleaseMatrixHeaderValue -MatrixText $MatrixText -Label 'Release version'
    if (-not [string]::Equals($matrixVersion, $Version, [StringComparison]::Ordinal)) {
        throw "Release test matrix version '$matrixVersion' does not match requested version '$Version'."
    }

    $releaseDate = Get-ReleaseMatrixHeaderValue -MatrixText $MatrixText -Label 'Release date'
    if ([string]::IsNullOrWhiteSpace($releaseDate) -or $releaseDate -match '^(?i:TBD|Pending)$') {
        throw 'Release test matrix must contain a completed release date.'
    }

    $matrixModelVariant = Get-ReleaseMatrixHeaderValue -MatrixText $MatrixText -Label 'Release Foundry model variant'
    if (-not [string]::Equals($matrixModelVariant, $ModelVariant, [StringComparison]::Ordinal)) {
        throw "Release test matrix model '$matrixModelVariant' does not match configured model '$ModelVariant'."
    }

    $matrixReleaseSource = Get-ReleaseMatrixHeaderValue -MatrixText $MatrixText -Label 'Release source'
    if (-not [string]::Equals($matrixReleaseSource, $ReleaseSource, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release test matrix source '$matrixReleaseSource' does not match the verified release source '$ReleaseSource'."
    }

    $matrixReleaseNotesSource = Get-ReleaseMatrixHeaderValue -MatrixText $MatrixText -Label 'Release notes source'
    if (-not [string]::Equals($matrixReleaseNotesSource, $ReleaseNotesSource, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release test matrix release-notes source '$matrixReleaseNotesSource' does not match the supplied input '$ReleaseNotesSource'."
    }

    $matrixModelProvenanceSource = Get-ReleaseMatrixHeaderValue -MatrixText $MatrixText -Label 'Model provenance source'
    if (-not [string]::Equals($matrixModelProvenanceSource, $ModelProvenanceSource, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release test matrix model-provenance source '$matrixModelProvenanceSource' does not match the supplied input '$ModelProvenanceSource'."
    }

    $matrixRuntimeFrameworkNoticesSource = Get-ReleaseMatrixHeaderValue -MatrixText $MatrixText -Label 'Runtime framework notices source'
    if (-not [string]::Equals($matrixRuntimeFrameworkNoticesSource, $RuntimeFrameworkNoticesSource, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release test matrix runtime/framework notices source '$matrixRuntimeFrameworkNoticesSource' does not match the supplied input '$RuntimeFrameworkNoticesSource'."
    }

    $lines = $MatrixText -split '\r?\n'
    $headerIndex = -1
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $cells = ConvertFrom-MarkdownTableRow -Line $lines[$index]
        if ($null -eq $cells -or $cells.Count -ne 4) {
            continue
        }

        if ($cells[0] -ceq 'Area' -and $cells[1] -ceq 'x64' -and $cells[2] -ceq 'ARM64' -and $cells[3] -ceq 'Evidence / notes') {
            $headerIndex = $index
            break
        }
    }

    if ($headerIndex -lt 0) {
        throw 'Release test matrix does not contain the required Area/x64/ARM64/Evidence table.'
    }

    $architectureColumn = if ($Architecture -ceq 'x64') { 1 } else { 2 }
    $validatedRows = 0
    for ($index = $headerIndex + 1; $index -lt $lines.Count; $index++) {
        if ([string]::IsNullOrWhiteSpace($lines[$index])) {
            break
        }

        $cells = ConvertFrom-MarkdownTableRow -Line $lines[$index]
        if ($null -eq $cells) {
            throw "Release test matrix contains a malformed row at line $($index + 1)."
        }

        if ($cells.Count -ne 4) {
            throw "Release test matrix row at line $($index + 1) must contain exactly four cells."
        }

        if (Test-IsMarkdownTableSeparator -Cells $cells) {
            continue
        }

        if ([string]::IsNullOrWhiteSpace($cells[0])) {
            throw "Release test matrix row at line $($index + 1) has no test area."
        }

        if ($cells[$architectureColumn] -cne 'Passed') {
            throw "Release test matrix marks '$($cells[0])' as '$($cells[$architectureColumn])' for $Architecture. Every applicable row must be exactly 'Passed'."
        }

        if ([string]::IsNullOrWhiteSpace($cells[3])) {
            throw "Release test matrix row '$($cells[0])' needs retained evidence or notes for $Architecture."
        }

        $validatedRows++
    }

    if ($validatedRows -eq 0) {
        throw 'Release test matrix contains no applicable validation rows.'
    }

    $expectedArtifactName = "WinBulkTranscript-$Version-win-$Architecture.zip"
    $artifactLine = $lines | Where-Object { $_.IndexOf($expectedArtifactName, [StringComparison]::Ordinal) -ge 0 } | Select-Object -First 1
    if ($null -eq $artifactLine) {
        throw "Release test matrix must include an artifact receipt row for '$expectedArtifactName'."
    }

    $artifactCells = ConvertFrom-MarkdownTableRow -Line $artifactLine
    if ($null -eq $artifactCells -or $artifactCells.Count -lt 2 -or [string]::IsNullOrWhiteSpace($artifactCells[1]) -or $artifactCells[1] -match '^(?i:Pending|TBD)$') {
        throw "Artifact receipt row for '$expectedArtifactName' must state that the publisher will provide its checksum sidecar."
    }
}

function Assert-SourceMatchesSnapshot {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$ExpectedSha256,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $actualSha256 = Get-Sha256 -Path $Path
    if (-not [string]::Equals($actualSha256, $ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description changed while the release was being prepared. Refuse to associate a release with inputs different from the ones that were validated."
    }
}

function Assert-ReleaseStateUnchanged {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$ExpectedRevision,

        [Parameter(Mandatory)]
        [string[]]$RequiredTrackedPaths,

        [Parameter(Mandatory)]
        [object[]]$ReleaseInputSnapshots
    )

    Assert-VerifiedReleaseRevision -RepositoryRoot $RepositoryRoot -ExpectedRevision $ExpectedRevision -RequiredTrackedPaths $RequiredTrackedPaths
    foreach ($snapshot in $ReleaseInputSnapshots) {
        Assert-SourceMatchesSnapshot -Path $snapshot.Path -ExpectedSha256 $snapshot.Sha256 -Description $snapshot.Description
    }
}

function ConvertFrom-ScriptBytes {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes
    )

    $stream = [IO.MemoryStream]::new($Bytes, $false)
    try {
        $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false), $true)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function New-VerifiedScriptBlock {
    param(
        [Parameter(Mandatory)]
        [byte[]]$ScriptBytes,

        [Parameter(Mandatory)]
        [string]$Description
    )

    try {
        return [ScriptBlock]::Create((ConvertFrom-ScriptBytes -Bytes $ScriptBytes))
    }
    catch {
        throw "$Description could not be parsed from its verified byte snapshot: $($_.Exception.Message)"
    }
}

function New-ExclusiveReservation {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$ArtifactName
    )

    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        throw "Release artifact '$ArtifactName' is already reserved or incomplete: $Path"
    }

    try {
        $newline = [Environment]::NewLine
        $reservationText = "Release artifact reservation for $ArtifactName$newline" + "Created UTC: $(Get-UtcTimestamp)$newline" + "Process ID: $PID$newline"
        $bytes = [Text.Encoding]::UTF8.GetBytes($reservationText)
        $stream.Write($bytes, 0, $bytes.Length)
    }
    finally {
        $stream.Dispose()
    }
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [object]$Value
    )

    $json = $Value | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function Get-RelativePathWithinDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$RootPath,

        [Parameter(Mandatory)]
        [string]$Path
    )

    $resolvedRootPath = [IO.Path]::GetFullPath($RootPath)
    if (-not $resolvedRootPath.EndsWith([string][IO.Path]::DirectorySeparatorChar)) {
        $resolvedRootPath += [IO.Path]::DirectorySeparatorChar
    }

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedRootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$resolvedPath' is outside expected directory '$resolvedRootPath'."
    }

    return $resolvedPath.Substring($resolvedRootPath.Length).Replace('\', '/')
}

function Assert-RequiredNonEmptyPublishedFile {
    param(
        [Parameter(Mandatory)]
        [string]$PublishDirectory,

        [Parameter(Mandatory)]
        [string]$FileName,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if (-not [string]::Equals($FileName, [IO.Path]::GetFileName($FileName), [StringComparison]::Ordinal)) {
        throw "Required published $Description file name must not contain a path: '$FileName'."
    }

    $path = Join-Path $PublishDirectory $FileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Staged publish output is missing required $Description '$FileName'. Refuse to package an artifact that cannot load its application resources."
    }

    $file = Get-Item -LiteralPath $path -Force
    if ($file.Length -le 0) {
        throw "Staged publish output has an empty required $Description '$FileName'. Refuse to package an artifact that cannot load its application resources."
    }
}

function New-PublishPayloadInventory {
    param(
        [Parameter(Mandatory)]
        [string]$PublishDirectory,

        [Parameter(Mandatory)]
        [string]$RuntimeIdentifier,

        [Parameter(Mandatory)]
        [string]$RequiredApplicationPriFileName,

        [Parameter(Mandatory)]
        [string]$OutputPath
    )

    $resolvedPublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
    if (-not (Test-Path -LiteralPath $resolvedPublishDirectory -PathType Container)) {
        throw "Cannot inventory missing publish directory: $resolvedPublishDirectory"
    }

    Assert-RequiredNonEmptyPublishedFile -PublishDirectory $resolvedPublishDirectory -FileName $RequiredApplicationPriFileName -Description 'application PRI resource index'

    $resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)

    if (Test-Path -LiteralPath $resolvedOutputPath) {
        throw "Refusing to overwrite an existing publish payload inventory: $resolvedOutputPath"
    }

    $depsFiles = @(Get-ChildItem -LiteralPath $resolvedPublishDirectory -File | Where-Object { $_.Name.EndsWith('.deps.json', [StringComparison]::OrdinalIgnoreCase) })
    if ($depsFiles.Count -ne 1) {
        throw "Expected exactly one top-level .deps.json file in '$resolvedPublishDirectory'; found $($depsFiles.Count)."
    }

    $runtimeConfigFiles = @(Get-ChildItem -LiteralPath $resolvedPublishDirectory -File | Where-Object { $_.Name.EndsWith('.runtimeconfig.json', [StringComparison]::OrdinalIgnoreCase) })
    if ($runtimeConfigFiles.Count -ne 1) {
        throw "Expected exactly one top-level .runtimeconfig.json file in '$resolvedPublishDirectory'; found $($runtimeConfigFiles.Count)."
    }

    try {
        $dependencyManifest = Get-Content -LiteralPath $depsFiles[0].FullName -Raw | ConvertFrom-Json
    }
    catch {
        throw "Published dependency manifest '$($depsFiles[0].FullName)' is not valid JSON: $($_.Exception.Message)"
    }

    try {
        $runtimeConfiguration = Get-Content -LiteralPath $runtimeConfigFiles[0].FullName -Raw | ConvertFrom-Json
    }
    catch {
        throw "Published runtime configuration '$($runtimeConfigFiles[0].FullName)' is not valid JSON: $($_.Exception.Message)"
    }

    $runtimeTargetName = Get-RequiredJsonString -Object $dependencyManifest.runtimeTarget -PropertyName 'name' -Description "Published dependency manifest '$($depsFiles[0].Name)'"
    if (-not $runtimeTargetName.EndsWith("/$RuntimeIdentifier", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Published dependency manifest runtime target '$runtimeTargetName' does not match requested RID '$RuntimeIdentifier'."
    }

    if ($null -eq $dependencyManifest.libraries) {
        throw "Published dependency manifest '$($depsFiles[0].Name)' has no libraries object."
    }

    $packageLibrariesByKey = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    $nonPackageLibrariesByKey = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($libraryProperty in $dependencyManifest.libraries.PSObject.Properties) {
        $libraryName = [string]$libraryProperty.Name
        $library = $libraryProperty.Value
        $libraryType = Get-RequiredJsonString -Object $library -PropertyName 'type' -Description "Published dependency library '$libraryName'"

        if ($libraryType -ieq 'package') {
            $separatorIndex = $libraryName.LastIndexOf('/')
            if ($separatorIndex -le 0 -or $separatorIndex -ge ($libraryName.Length - 1)) {
                throw "Published package library '$libraryName' does not use the required '<package id>/<version>' name."
            }

            $packageId = $libraryName.Substring(0, $separatorIndex)
            $packageVersion = $libraryName.Substring($separatorIndex + 1)
            $packagePath = Get-RequiredJsonString -Object $library -PropertyName 'path' -Description "Published package library '$libraryName'"
            $packageSha512 = Get-RequiredJsonString -Object $library -PropertyName 'sha512' -Description "Published package library '$libraryName'"
            $packageKey = "$($packageId.ToUpperInvariant())|$packageVersion"
            if ($packageLibrariesByKey.ContainsKey($packageKey)) {
                throw "Published dependency manifest repeats package library '$libraryName'."
            }

            $packageLibrariesByKey.Add($packageKey, [ordered]@{
                id = $packageId
                version = $packageVersion
                path = $packagePath
                sha512 = $packageSha512
            })
        }
        else {
            $pathProperty = $library.PSObject.Properties['path']
            $sha512Property = $library.PSObject.Properties['sha512']
            $nonPackageKey = "$($libraryName.ToUpperInvariant())|$libraryType"
            if ($nonPackageLibrariesByKey.ContainsKey($nonPackageKey)) {
                throw "Published dependency manifest repeats non-package library '$libraryName'."
            }

            $nonPackageLibrariesByKey.Add($nonPackageKey, [ordered]@{
                name = $libraryName
                type = $libraryType
                path = if ($null -eq $pathProperty) { $null } else { [string]$pathProperty.Value }
                sha512 = if ($null -eq $sha512Property) { $null } else { [string]$sha512Property.Value }
            })
        }
    }

    if ($packageLibrariesByKey.Count -eq 0) {
        throw "Published dependency manifest '$($depsFiles[0].Name)' contains no package libraries."
    }

    if ($null -eq $runtimeConfiguration.runtimeOptions) {
        throw "Published runtime configuration '$($runtimeConfigFiles[0].Name)' has no runtimeOptions object."
    }

    $frameworksByKey = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    $frameworkDefinitions = [Collections.Generic.List[object]]::new()
    $frameworkProperty = $runtimeConfiguration.runtimeOptions.PSObject.Properties['framework']
    if ($null -ne $frameworkProperty -and $null -ne $frameworkProperty.Value) {
        $frameworkDefinitions.Add($frameworkProperty.Value)
    }

    $includedFrameworksProperty = $runtimeConfiguration.runtimeOptions.PSObject.Properties['includedFrameworks']
    if ($null -ne $includedFrameworksProperty -and $null -ne $includedFrameworksProperty.Value) {
        foreach ($includedFramework in @($includedFrameworksProperty.Value)) {
            $frameworkDefinitions.Add($includedFramework)
        }
    }

    foreach ($framework in $frameworkDefinitions) {
        $frameworkName = Get-RequiredJsonString -Object $framework -PropertyName 'name' -Description "Published runtime configuration '$($runtimeConfigFiles[0].Name)' framework"
        $frameworkVersion = Get-RequiredJsonString -Object $framework -PropertyName 'version' -Description "Published runtime configuration '$($runtimeConfigFiles[0].Name)' framework"
        $frameworkKey = "$($frameworkName.ToUpperInvariant())|$frameworkVersion"
        if (-not $frameworksByKey.ContainsKey($frameworkKey)) {
            $frameworksByKey.Add($frameworkKey, [ordered]@{
                name = $frameworkName
                version = $frameworkVersion
            })
        }
    }

    if ($frameworksByKey.Count -eq 0) {
        throw "Published runtime configuration '$($runtimeConfigFiles[0].Name)' declares no framework payload."
    }

    $filesByPath = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in Get-ChildItem -LiteralPath $resolvedPublishDirectory -File -Recurse -Force) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Published output contains a reparse-point file, which cannot be safely inventoried: $($file.FullName)"
        }

        $relativePath = Get-RelativePathWithinDirectory -RootPath $resolvedPublishDirectory -Path $file.FullName
        if ($filesByPath.ContainsKey($relativePath)) {
            throw "Published output contains duplicate relative path '$relativePath'."
        }

        $filesByPath.Add($relativePath, [ordered]@{
            relativePath = $relativePath
            length = [int64]$file.Length
            sha256 = Get-Sha256 -Path $file.FullName
        })
    }

    $inventory = [ordered]@{
        schemaVersion = 1
        scope = 'rawDotnetPublishOutputBeforeReleaseEvidence'
        runtimeIdentifier = $RuntimeIdentifier
        dependencyManifest = [ordered]@{
            fileName = $depsFiles[0].Name
            sha256 = Get-Sha256 -Path $depsFiles[0].FullName
            runtimeTargetName = $runtimeTargetName
        }
        runtimeConfiguration = [ordered]@{
            fileName = $runtimeConfigFiles[0].Name
            sha256 = Get-Sha256 -Path $runtimeConfigFiles[0].FullName
            frameworks = @($frameworksByKey.Values)
        }
        packageLibraries = @($packageLibrariesByKey.Values)
        nonPackageLibraries = @($nonPackageLibrariesByKey.Values)
        files = @($filesByPath.Values)
    }

    Write-JsonFile -Path $resolvedOutputPath -Value $inventory
    return $inventory
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publisherScriptPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Publish-Release.ps1'))
$noticesGeneratorSourcePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'New-ThirdPartyNotices.ps1'))
$project = Join-Path $repositoryRoot 'src\WinBulkTranscript.App\WinBulkTranscript.App.csproj'
$modelHostSourcePath = Join-Path $repositoryRoot 'src\WinBulkTranscript.App\Foundry\FoundryLocalModelHost.cs'
$modelContractSourcePath = Join-Path $repositoryRoot 'src\WinBulkTranscript.App\Foundry\FoundryModelContract.cs'
$packageLockPath = Join-Path $repositoryRoot 'src\WinBulkTranscript.App\packages.lock.json'
$projectLicensePath = Join-Path $repositoryRoot 'LICENSE'
$projectRecordPath = 'src/WinBulkTranscript.App/WinBulkTranscript.App.csproj'
$modelHostRecordPath = 'src/WinBulkTranscript.App/Foundry/FoundryLocalModelHost.cs'
$modelContractRecordPath = 'src/WinBulkTranscript.App/Foundry/FoundryModelContract.cs'
$packageLockRecordPath = 'src/WinBulkTranscript.App/packages.lock.json'
$publisherScriptRecordPath = 'scripts/Publish-Release.ps1'
$noticesGeneratorRecordPath = 'scripts/New-ThirdPartyNotices.ps1'
$releaseOwnedRepositoryPaths = @(
    'LICENSE',
    'Directory.Build.props',
    'Directory.Packages.props',
    'global.json',
    'WinBulkTranscript.sln',
    'src/WinBulkTranscript.App/WinBulkTranscript.App.csproj',
    'src/WinBulkTranscript.App/packages.lock.json',
    'src/WinBulkTranscript.App/Foundry/FoundryLocalModelHost.cs',
    'src/WinBulkTranscript.App/Foundry/FoundryModelContract.cs',
    'scripts/Publish-Release.ps1',
    'scripts/New-ThirdPartyNotices.ps1',
    'scripts/Invoke-TagRelease.ps1',
    'scripts/Test-ReleaseArtifactVerifier.ps1',
    'scripts/Test-ReleaseArtifacts.ps1',
    'scripts/Test-ReleaseMechanics.ps1',
    '.github/workflows/release.yml',
    'release-inputs/README.md',
    'docs/release/README.md',
    'docs/release/release-test-matrix.md',
    'docs/release/release-notes-template.md',
    'docs/release/model-provenance-template.json',
    'docs/release/runtime-framework-notices-template.txt',
    'docs/release/THIRD-PARTY-NOTICES.md'
)

foreach ($requiredPath in @($publisherScriptPath, $noticesGeneratorSourcePath, $project, $modelHostSourcePath, $modelContractSourcePath, $packageLockPath, $projectLicensePath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required release input was not found: $requiredPath"
    }
}

$resolvedArtifactsRoot = [IO.Path]::GetFullPath($ArtifactsRoot)
if ((Test-Path -LiteralPath $resolvedArtifactsRoot) -and -not (Test-Path -LiteralPath $resolvedArtifactsRoot -PathType Container)) {
    throw "Artifacts root exists but is not a directory: $resolvedArtifactsRoot"
}
Assert-ArtifactsRootOutsideRepository -ArtifactsRoot $resolvedArtifactsRoot -RepositoryRoot $repositoryRoot

$resolvedReleaseTestMatrixPath = [IO.Path]::GetFullPath($ReleaseTestMatrixPath)
$resolvedModelLicensePath = [IO.Path]::GetFullPath($ModelLicensePath)
$resolvedModelProvenancePath = [IO.Path]::GetFullPath($ModelProvenancePath)
$resolvedRuntimeFrameworkNoticesPath = [IO.Path]::GetFullPath($RuntimeFrameworkNoticesPath)
$resolvedReleaseNotesPath = [IO.Path]::GetFullPath($ReleaseNotesPath)
foreach ($releaseInput in @(
        [PSCustomObject]@{ Path = $resolvedReleaseTestMatrixPath; Description = 'Release test matrix' },
        [PSCustomObject]@{ Path = $resolvedModelLicensePath; Description = 'Model license' },
        [PSCustomObject]@{ Path = $resolvedModelProvenancePath; Description = 'Model provenance' },
        [PSCustomObject]@{ Path = $resolvedRuntimeFrameworkNoticesPath; Description = 'Runtime/framework notices' },
        [PSCustomObject]@{ Path = $resolvedReleaseNotesPath; Description = 'Release notes' })) {
    Assert-ReleaseInputOutsideArtifactsRoot -Path $releaseInput.Path -ArtifactsRoot $resolvedArtifactsRoot -Description $releaseInput.Description
}

$rid = "win-$Architecture"
$artifactName = "WinBulkTranscript-$Version-$rid"
$artifactZipFileName = "$artifactName.zip"

$configuredModelVariant = Get-ConfiguredReleaseModelVariant -ModelHostSourcePath $modelContractSourcePath
Assert-ModelHostUsesConfiguredContract -ModelHostSourcePath $modelHostSourcePath
if (-not [string]::Equals($ModelVariant, $configuredModelVariant, [StringComparison]::Ordinal)) {
    throw "Requested model variant '$ModelVariant' does not match the exact variant configured by the application ('$configuredModelVariant'). The release script cannot change the application's model; update and validate the application configuration first."
}

$repositoryRevision = Get-VerifiedReleaseRevision -RepositoryRoot $repositoryRoot -RequiredTrackedPaths $releaseOwnedRepositoryPaths
$releaseSource = Get-ValidatedReleaseSource -RepositoryRoot $repositoryRoot -RepositoryRevision $repositoryRevision -SourceRef $ReleaseSourceRef
$dotnetSdkVersion = Get-DotnetSdkVersion
$publisherScriptBytes = [IO.File]::ReadAllBytes($publisherScriptPath)
$noticesGeneratorBytes = [IO.File]::ReadAllBytes($noticesGeneratorSourcePath)
$packageLockBytes = [IO.File]::ReadAllBytes($packageLockPath)
$projectLicenseBytes = [IO.File]::ReadAllBytes($projectLicensePath)
$releaseTestMatrixBytes = [IO.File]::ReadAllBytes($resolvedReleaseTestMatrixPath)
$modelLicenseBytes = [IO.File]::ReadAllBytes($resolvedModelLicensePath)
$modelProvenanceBytes = [IO.File]::ReadAllBytes($resolvedModelProvenancePath)
$runtimeFrameworkNoticesBytes = [IO.File]::ReadAllBytes($resolvedRuntimeFrameworkNoticesPath)
$releaseNotesBytes = [IO.File]::ReadAllBytes($resolvedReleaseNotesPath)
$packageLockSha256 = Get-Sha256FromBytes -Bytes $packageLockBytes
$projectLicenseSha256 = Get-Sha256FromBytes -Bytes $projectLicenseBytes
$publisherScriptSha256 = Get-Sha256FromBytes -Bytes $publisherScriptBytes
$noticesGeneratorSha256 = Get-Sha256FromBytes -Bytes $noticesGeneratorBytes
$releaseTestMatrixSha256 = Get-Sha256FromBytes -Bytes $releaseTestMatrixBytes
$modelLicenseSha256 = Get-Sha256FromBytes -Bytes $modelLicenseBytes
$modelProvenanceSha256 = Get-Sha256FromBytes -Bytes $modelProvenanceBytes
$runtimeFrameworkNoticesSha256 = Get-Sha256FromBytes -Bytes $runtimeFrameworkNoticesBytes
$releaseNotesSha256 = Get-Sha256FromBytes -Bytes $releaseNotesBytes
$modelConfigurationSha256 = Get-Sha256 -Path $modelContractSourcePath
$modelHostConfigurationSha256 = Get-Sha256 -Path $modelHostSourcePath
$releaseTestMatrixText = [IO.File]::ReadAllText($resolvedReleaseTestMatrixPath)
$runtimeFrameworkNoticesText = [IO.File]::ReadAllText($resolvedRuntimeFrameworkNoticesPath)

if ([string]::IsNullOrWhiteSpace([IO.File]::ReadAllText($resolvedModelLicensePath))) {
    throw "The supplied model license file is empty: $resolvedModelLicensePath"
}

if ([string]::IsNullOrWhiteSpace($runtimeFrameworkNoticesText)) {
    throw "The supplied runtime/framework notices file is empty: $resolvedRuntimeFrameworkNoticesPath"
}

Assert-ReleaseNotes -Path $resolvedReleaseNotesPath -Version $Version -ArtifactZipFileName $artifactZipFileName
$modelProvenance = Read-ModelProvenance -Path $resolvedModelProvenancePath -ExpectedModelVariant $configuredModelVariant -ModelLicensePath $resolvedModelLicensePath -ModelLicenseSha256 $modelLicenseSha256
$releaseNotesSource = Get-ReleaseInputReference -Path $resolvedReleaseNotesPath -Sha256 $releaseNotesSha256
$modelProvenanceSource = Get-ReleaseInputReference -Path $resolvedModelProvenancePath -Sha256 $modelProvenanceSha256
$runtimeFrameworkNoticesSource = Get-ReleaseInputReference -Path $resolvedRuntimeFrameworkNoticesPath -Sha256 $runtimeFrameworkNoticesSha256
$releaseInputSnapshots = @(
    [PSCustomObject]@{ Path = $publisherScriptPath; Sha256 = $publisherScriptSha256; Description = 'The release publisher source' },
    [PSCustomObject]@{ Path = $noticesGeneratorSourcePath; Sha256 = $noticesGeneratorSha256; Description = 'The third-party notices generator source' },
    [PSCustomObject]@{ Path = $packageLockPath; Sha256 = $packageLockSha256; Description = 'The App package lock' },
    [PSCustomObject]@{ Path = $projectLicensePath; Sha256 = $projectLicenseSha256; Description = 'The project license' },
    [PSCustomObject]@{ Path = $modelContractSourcePath; Sha256 = $modelConfigurationSha256; Description = 'The configured release model contract source' },
    [PSCustomObject]@{ Path = $modelHostSourcePath; Sha256 = $modelHostConfigurationSha256; Description = 'The App model-host alias source' },
    [PSCustomObject]@{ Path = $resolvedReleaseTestMatrixPath; Sha256 = $releaseTestMatrixSha256; Description = 'The release test matrix input' },
    [PSCustomObject]@{ Path = $resolvedModelLicensePath; Sha256 = $modelLicenseSha256; Description = 'The model license input' },
    [PSCustomObject]@{ Path = $resolvedModelProvenancePath; Sha256 = $modelProvenanceSha256; Description = 'The model provenance input' },
    [PSCustomObject]@{ Path = $resolvedRuntimeFrameworkNoticesPath; Sha256 = $runtimeFrameworkNoticesSha256; Description = 'The runtime/framework notices input' },
    [PSCustomObject]@{ Path = $resolvedReleaseNotesPath; Sha256 = $releaseNotesSha256; Description = 'The release notes input' }
)

$matrixAssertion = @{
    MatrixText = $releaseTestMatrixText
    Version = $Version
    ModelVariant = $configuredModelVariant
    Architecture = $Architecture
    ReleaseNotesSource = $releaseNotesSource
    ModelProvenanceSource = $modelProvenanceSource
    RuntimeFrameworkNoticesSource = $runtimeFrameworkNoticesSource
    ReleaseSource = $releaseSource
}
$releaseStateAssertion = @{
    RepositoryRoot = $repositoryRoot
    ExpectedRevision = $repositoryRevision
    RequiredTrackedPaths = $releaseOwnedRepositoryPaths
    ReleaseInputSnapshots = $releaseInputSnapshots
}
Assert-ReleaseStateUnchanged @releaseStateAssertion
Assert-CompletedReleaseTestMatrix @matrixAssertion

if ($ValidateOnly) {
    Write-Host "Validated release evidence for $artifactName. No restore, publish, or artifact write was performed."
    return
}

New-Item -ItemType Directory -Force -Path $resolvedArtifactsRoot | Out-Null

$publishDirectory = Join-Path $resolvedArtifactsRoot $artifactName
$zipPath = Join-Path $resolvedArtifactsRoot "$artifactName.zip"
$hashPath = "$zipPath.sha256"
$releaseRecordPath = Join-Path $resolvedArtifactsRoot "$artifactName.release-record.json"
$reservationPath = Join-Path $resolvedArtifactsRoot "$artifactName.publish-reservation"

foreach ($finalPath in @($publishDirectory, $zipPath, $hashPath, $releaseRecordPath, $reservationPath)) {
    if (Test-Path -LiteralPath $finalPath) {
        throw "Refusing to overwrite an existing or incomplete release path: $finalPath"
    }
}

New-ExclusiveReservation -Path $reservationPath -ArtifactName $artifactName
$reservationAcquired = $true
$finalizationStarted = $false
$releaseCompleted = $false
$stagingPublishDirectory = $null

try {
    $stagingToken = [Guid]::NewGuid().ToString('N')
    $stagingPublishDirectory = Join-Path $resolvedArtifactsRoot ("." + $artifactName + ".publish-" + $stagingToken)
    $stagingZipPath = Join-Path $resolvedArtifactsRoot ("." + $artifactName + ".zip-" + $stagingToken)
    $stagingHashPath = "$stagingZipPath.sha256"
    $stagingReleaseRecordPath = Join-Path $resolvedArtifactsRoot ("." + $artifactName + ".release-record-" + $stagingToken + ".json")
    foreach ($stagingPath in @($stagingPublishDirectory, $stagingZipPath, $stagingHashPath, $stagingReleaseRecordPath)) {
        if (Test-Path -LiteralPath $stagingPath) {
            throw "Unexpected staging path already exists: $stagingPath"
        }
    }

    # The App declares both release RIDs. Restore that declared graph once in locked mode,
    # then publish the selected RID from the same assets with --no-restore.
    Invoke-Dotnet -Arguments @('restore', $project, '--locked-mode')
    Assert-ReleaseStateUnchanged @releaseStateAssertion

    Invoke-Dotnet -Arguments @(
        'publish',
        $project,
        '--configuration',
        'Release',
        '--runtime',
        $rid,
        '--self-contained',
        'true',
        '--no-restore',
        "--property:Version=$Version",
        '--property:WindowsAppSDKSelfContained=true',
        '--property:PublishSingleFile=false',
        '--output',
        $stagingPublishDirectory)
    Assert-ReleaseStateUnchanged @releaseStateAssertion

    $payloadInventoryPath = Join-Path $stagingPublishDirectory 'PUBLISH-PAYLOAD.json'
    $payloadInventory = New-PublishPayloadInventory -PublishDirectory $stagingPublishDirectory -RuntimeIdentifier $rid -RequiredApplicationPriFileName 'WinBulkTranscript.pri' -OutputPath $payloadInventoryPath
    $payloadInventorySha256 = Get-Sha256 -Path $payloadInventoryPath
    $stagedPublishedDepsPath = Join-Path $stagingPublishDirectory ([string]$payloadInventory.dependencyManifest.fileName)
    if (-not (Test-Path -LiteralPath $stagedPublishedDepsPath -PathType Leaf)) {
        throw "The payload inventory refers to a missing staged dependency manifest: $stagedPublishedDepsPath"
    }

    $stagedPackageLockPath = Join-Path $stagingPublishDirectory 'PACKAGE-LOCK.json'
    $stagedProjectLicensePath = Join-Path $stagingPublishDirectory 'LICENSE'
    $stagedReleaseMatrixPath = Join-Path $stagingPublishDirectory 'RELEASE-TEST-MATRIX.md'
    $stagedModelLicensePath = Join-Path $stagingPublishDirectory 'MODEL-LICENSE.txt'
    $stagedModelProvenancePath = Join-Path $stagingPublishDirectory 'MODEL-PROVENANCE.json'
    $stagedRuntimeFrameworkNoticesPath = Join-Path $stagingPublishDirectory 'DOTNET-RUNTIME-NOTICES.txt'
    $stagedReleaseNotesPath = Join-Path $stagingPublishDirectory 'RELEASE-NOTES.md'
    [IO.File]::WriteAllBytes($stagedPackageLockPath, $packageLockBytes)
    [IO.File]::WriteAllBytes($stagedProjectLicensePath, $projectLicenseBytes)
    [IO.File]::WriteAllBytes($stagedReleaseMatrixPath, $releaseTestMatrixBytes)
    [IO.File]::WriteAllBytes($stagedModelLicensePath, $modelLicenseBytes)
    [IO.File]::WriteAllBytes($stagedModelProvenancePath, $modelProvenanceBytes)
    [IO.File]::WriteAllBytes($stagedRuntimeFrameworkNoticesPath, $runtimeFrameworkNoticesBytes)
    [IO.File]::WriteAllBytes($stagedReleaseNotesPath, $releaseNotesBytes)

    $noticesArguments = @{
        ModelVariant = $configuredModelVariant
        ModelLicensePath = $stagedModelLicensePath
        ModelProvenancePath = $stagedModelProvenancePath
        RuntimeFrameworkNoticesPath = $stagedRuntimeFrameworkNoticesPath
        PackageLockPath = $stagedPackageLockPath
        PublishedDepsPath = $stagedPublishedDepsPath
        PayloadInventoryPath = $payloadInventoryPath
        OutputPath = (Join-Path $stagingPublishDirectory 'THIRD-PARTY-NOTICES.md')
        RepositoryRoot = $repositoryRoot
    }
    # The generator is executed from the captured bytes rather than its mutable source path.
    # A post-generation source/snapshot check still rejects any concurrent tree drift.
    $verifiedNoticesGenerator = New-VerifiedScriptBlock -ScriptBytes $noticesGeneratorBytes -Description 'The third-party notices generator'
    & $verifiedNoticesGenerator @noticesArguments
    Assert-ReleaseStateUnchanged @releaseStateAssertion

    $noticesPath = Join-Path $stagingPublishDirectory 'THIRD-PARTY-NOTICES.md'
    $noticesSha256 = Get-Sha256 -Path $noticesPath
    $metadataPath = Join-Path $stagingPublishDirectory 'RELEASE-METADATA.json'
    $releaseMetadata = [ordered]@{
        schemaVersion = 2
        generatedAtUtc = Get-UtcTimestamp
        version = $Version
        architecture = $Architecture
        runtimeIdentifier = $rid
        source = [ordered]@{
            repositoryRevision = $repositoryRevision
            releaseSource = $releaseSource
            releaseSourceKind = if ([string]::IsNullOrWhiteSpace($ReleaseSourceRef)) { 'commit' } else { 'tag' }
            sourceTreeRequirement = 'Clean working tree with all release-owned paths tracked at HEAD before, during, and after publish.'
            publisherSource = $publisherScriptRecordPath
            publisherSourceSha256 = $publisherScriptSha256
            noticesGeneratorSource = $noticesGeneratorRecordPath
            noticesGeneratorSourceSha256 = $noticesGeneratorSha256
            noticesGeneratorExecution = 'Executed from the verified byte snapshot captured before restore.'
        }
        projectLicense = [ordered]@{
            source = 'LICENSE'
            artifactFile = 'LICENSE'
            sha256 = $projectLicenseSha256
            identifier = 'LicenseRef-UTSW-Academic-Research-Only'
            scope = 'Original WinBulkTranscript project code and documentation only; excludes third-party packages, runtime/framework files, and models.'
        }
        model = [ordered]@{
            variant = $configuredModelVariant
            configurationContractSource = $modelContractRecordPath
            configurationContractSha256 = $modelConfigurationSha256
            hostAliasSource = $modelHostRecordPath
            hostAliasSha256 = $modelHostConfigurationSha256
            licenseFile = 'MODEL-LICENSE.txt'
            licenseSha256 = $modelLicenseSha256
            provenance = [ordered]@{
                artifactFile = 'MODEL-PROVENANCE.json'
                sha256 = $modelProvenanceSha256
                artifactIdentifier = $modelProvenance.ArtifactIdentifier
                sourceUri = $modelProvenance.SourceUri
                sourceLicenseFileName = $modelProvenance.LicenseFileName
                sourceLicenseSha256 = $modelProvenance.LicenseSha256
            }
        }
        packageLock = [ordered]@{
            source = $packageLockRecordPath
            artifactFile = 'PACKAGE-LOCK.json'
            sha256 = $packageLockSha256
            repositoryRevision = $repositoryRevision
            coverage = 'Validated only for package libraries actually declared by the staged .deps.json; runtime/framework payloads are separately recorded.'
        }
        releaseTestMatrix = [ordered]@{
            sourceFileName = [IO.Path]::GetFileName($resolvedReleaseTestMatrixPath)
            artifactFile = 'RELEASE-TEST-MATRIX.md'
            sha256 = $releaseTestMatrixSha256
        }
        releaseNotes = [ordered]@{
            sourceFileName = [IO.Path]::GetFileName($resolvedReleaseNotesPath)
            artifactFile = 'RELEASE-NOTES.md'
            sha256 = $releaseNotesSha256
        }
        runtimeFrameworkNotices = [ordered]@{
            sourceFileName = [IO.Path]::GetFileName($resolvedRuntimeFrameworkNoticesPath)
            artifactFile = 'DOTNET-RUNTIME-NOTICES.txt'
            sha256 = $runtimeFrameworkNoticesSha256
            legalReviewRequired = $true
            coverage = 'Must name each declared runtime framework and every non-lock framework package library from the staged .deps.json; automated checks cannot establish legal completeness.'
        }
        publishPayload = [ordered]@{
            artifactFile = 'PUBLISH-PAYLOAD.json'
            sha256 = $payloadInventorySha256
            scope = $payloadInventory.scope
            dependencyManifestFile = $payloadInventory.dependencyManifest.fileName
            dependencyManifestSha256 = $payloadInventory.dependencyManifest.sha256
            runtimeConfigurationFile = $payloadInventory.runtimeConfiguration.fileName
            runtimeConfigurationSha256 = $payloadInventory.runtimeConfiguration.sha256
        }
        notices = [ordered]@{
            artifactFile = 'THIRD-PARTY-NOTICES.md'
            sha256 = $noticesSha256
            coverage = 'Generated from actual staged .deps.json package libraries after lock, model provenance, payload inventory, and runtime/framework notice validation.'
        }
        build = [ordered]@{
            project = $projectRecordPath
            dotnetSdkVersion = $dotnetSdkVersion
            restoreCommand = @('dotnet', 'restore', $projectRecordPath, '--locked-mode')
            publishCommand = @('dotnet', 'publish', $projectRecordPath, '--configuration', 'Release', '--runtime', $rid, '--self-contained', 'true', '--no-restore', "--property:Version=$Version", '--property:WindowsAppSDKSelfContained=true', '--property:PublishSingleFile=false', '--output', '<release publish folder>')
            publishSettings = [ordered]@{
                selfContained = $true
                windowsAppSdkSelfContained = $true
                singleFile = $false
            }
        }
    }
    Write-JsonFile -Path $metadataPath -Value $releaseMetadata
    $metadataSha256 = Get-Sha256 -Path $metadataPath

    Compress-Archive -Path (Join-Path $stagingPublishDirectory '*') -DestinationPath $stagingZipPath -CompressionLevel Optimal
    $zipSha256 = Get-Sha256 -Path $stagingZipPath
    $checksumText = "$zipSha256 *$([IO.Path]::GetFileName($zipPath))$([Environment]::NewLine)"
    [IO.File]::WriteAllText($stagingHashPath, $checksumText, [Text.Encoding]::ASCII)

    $releaseRecord = [ordered]@{
        schemaVersion = 2
        generatedAtUtc = Get-UtcTimestamp
        artifact = [ordered]@{
            publishDirectory = $artifactName
            zipFile = [IO.Path]::GetFileName($zipPath)
            zipSha256 = $zipSha256
            checksumFile = [IO.Path]::GetFileName($hashPath)
            embeddedMetadataFile = 'RELEASE-METADATA.json'
            embeddedMetadataSha256 = $metadataSha256
        }
        version = $Version
        architecture = $Architecture
        runtimeIdentifier = $rid
        modelVariant = $configuredModelVariant
        packageLock = [ordered]@{
            artifactFile = 'PACKAGE-LOCK.json'
            sha256 = $packageLockSha256
            repositoryRevision = $repositoryRevision
        }
        releaseTestMatrix = [ordered]@{
            artifactFile = 'RELEASE-TEST-MATRIX.md'
            sha256 = $releaseTestMatrixSha256
        }
        modelLicense = [ordered]@{
            artifactFile = 'MODEL-LICENSE.txt'
            sha256 = $modelLicenseSha256
        }
        modelProvenance = $releaseMetadata.model.provenance
        releaseNotes = $releaseMetadata.releaseNotes
        runtimeFrameworkNotices = $releaseMetadata.runtimeFrameworkNotices
        publishPayload = $releaseMetadata.publishPayload
        thirdPartyNotices = [ordered]@{
            artifactFile = 'THIRD-PARTY-NOTICES.md'
            sha256 = $noticesSha256
        }
        source = $releaseMetadata.source
        build = $releaseMetadata.build
    }
    Write-JsonFile -Path $stagingReleaseRecordPath -Value $releaseRecord

    # This is deliberately the last check before irreversible artifact moves. It covers changes
    # that occur while generating notices, metadata, the ZIP, or its receipt.
    Assert-ReleaseStateUnchanged @releaseStateAssertion
    $finalizationStarted = $true
    [IO.Directory]::Move($stagingPublishDirectory, $publishDirectory)
    [IO.File]::Move($stagingZipPath, $zipPath)
    [IO.File]::Move($stagingHashPath, $hashPath)
    [IO.File]::Move($stagingReleaseRecordPath, $releaseRecordPath)
    $releaseCompleted = $true
}
catch {
    if (-not $finalizationStarted -and -not [string]::IsNullOrWhiteSpace($stagingPublishDirectory) -and (Test-Path -LiteralPath $stagingPublishDirectory)) {
        Write-Warning "Publishing stopped before finalization. Staging output was retained for inspection at '$stagingPublishDirectory'."
    }

    throw
}
finally {
    if ($reservationAcquired -and $releaseCompleted) {
        try {
            [IO.File]::Delete($reservationPath)
        }
        catch {
            Write-Warning "Release completed, but the reservation file could not be removed: '$reservationPath'. The final artifacts are intact."
        }
    }
    elseif ($reservationAcquired -and -not $finalizationStarted) {
        [IO.File]::Delete($reservationPath)
    }
    elseif ($reservationAcquired) {
        Write-Warning "Release finalization did not complete. Preserve '$reservationPath' and inspect the existing final paths before attempting recovery."
    }
}

Write-Host "Published $publishDirectory"
Write-Host "Created $zipPath"
Write-Host "Created $hashPath"
Write-Host "Created $releaseRecordPath"
