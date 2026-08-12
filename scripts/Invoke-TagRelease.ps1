[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?$')]
    [string]$Tag,

    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ArtifactsRoot,

    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$Architecture = $Architecture.ToLowerInvariant()
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = $Tag.Substring(1)
$releaseInputsRoot = Join-Path $repositoryRoot ("release-inputs\" + $Tag)
$architectureInputsRoot = Join-Path $releaseInputsRoot ("win-" + $Architecture)
$publisherPath = Join-Path $PSScriptRoot 'Publish-Release.ps1'
$modelContractPath = Join-Path $repositoryRoot 'src\WinBulkTranscript.App\Foundry\FoundryModelContract.cs'

function Get-GitScalar {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $output = & git -C $repositoryRoot @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$Description failed with exit code $exitCode."
    }

    $value = [string](@($output | Select-Object -Last 1))
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "$Description returned no value."
    }

    return $value.Trim()
}

function Assert-TrackedReleaseInput {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryPath
    )

    & git -C $repositoryRoot ls-files --error-unmatch -- $RepositoryPath 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Tag release input '$RepositoryPath' must be tracked by the tagged commit."
    }
}

if (-not (Test-Path -LiteralPath $releaseInputsRoot -PathType Container)) {
    throw "Release dossier '$releaseInputsRoot' was not found. Complete and commit release-inputs/$Tag before creating the tag."
}

$releaseInputPaths = [ordered]@{
    ModelLicense = Join-Path $releaseInputsRoot 'model-license.txt'
    ModelProvenance = Join-Path $releaseInputsRoot 'model-provenance.json'
    ReleaseNotes = Join-Path $releaseInputsRoot 'release-notes.md'
    X64RuntimeFrameworkNotices = Join-Path $releaseInputsRoot 'win-x64\runtime-framework-notices.txt'
    X64ReleaseTestMatrix = Join-Path $releaseInputsRoot 'win-x64\release-test-matrix.md'
    Arm64RuntimeFrameworkNotices = Join-Path $releaseInputsRoot 'win-arm64\runtime-framework-notices.txt'
    Arm64ReleaseTestMatrix = Join-Path $releaseInputsRoot 'win-arm64\release-test-matrix.md'
}

foreach ($entry in $releaseInputPaths.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
        throw "Release dossier is missing $($entry.Key): $($entry.Value)"
    }

    $relativePath = $entry.Value.Substring($repositoryRoot.Length).TrimStart('\', '/').Replace('\', '/')
    Assert-TrackedReleaseInput -RepositoryPath $relativePath
}

$headRevision = Get-GitScalar -Arguments @('rev-parse', 'HEAD') -Description 'Reading repository HEAD'
$tagRef = "refs/tags/$Tag"
$tagRevision = Get-GitScalar -Arguments @('rev-parse', '--verify', "${tagRef}^{commit}") -Description "Resolving release tag '$Tag'"
if (-not [string]::Equals($headRevision, $tagRevision, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release tag '$Tag' resolves to '$tagRevision', but the checked-out HEAD is '$headRevision'."
}

$modelContract = [IO.File]::ReadAllText($modelContractPath)
$modelMatch = [regex]::Match(
    $modelContract,
    'public\s+const\s+string\s+InitialCandidateModelVariant\s*=\s*"(?<variant>[^"]+)"\s*;',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $modelMatch.Success) {
    throw "Could not read InitialCandidateModelVariant from '$modelContractPath'."
}

$publisherArguments = @{
    Version = $version
    ReleaseSourceRef = $Tag
    Architecture = $Architecture
    ModelVariant = $modelMatch.Groups['variant'].Value
    ModelLicensePath = $releaseInputPaths.ModelLicense
    ModelProvenancePath = $releaseInputPaths.ModelProvenance
    RuntimeFrameworkNoticesPath = Join-Path $architectureInputsRoot 'runtime-framework-notices.txt'
    ReleaseNotesPath = $releaseInputPaths.ReleaseNotes
    ReleaseTestMatrixPath = Join-Path $architectureInputsRoot 'release-test-matrix.md'
    ArtifactsRoot = $ArtifactsRoot
}
if ($ValidateOnly) {
    $publisherArguments.ValidateOnly = $true
}

& $publisherPath @publisherArguments
