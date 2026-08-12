[CmdletBinding()]
param(
    [string]$WorkflowPath,

    [string]$TagReleaseScriptPath,

    [string]$ArtifactVerifierPath,

    [string]$ArtifactVerifierTestPath
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($WorkflowPath)) {
    $WorkflowPath = Join-Path (Join-Path $PSScriptRoot '..') '.github\workflows\release.yml'
}

if ([string]::IsNullOrWhiteSpace($TagReleaseScriptPath)) {
    $TagReleaseScriptPath = Join-Path $PSScriptRoot 'Invoke-TagRelease.ps1'
}

if ([string]::IsNullOrWhiteSpace($ArtifactVerifierPath)) {
    $ArtifactVerifierPath = Join-Path $PSScriptRoot 'Test-ReleaseArtifacts.ps1'
}

if ([string]::IsNullOrWhiteSpace($ArtifactVerifierTestPath)) {
    $ArtifactVerifierTestPath = Join-Path $PSScriptRoot 'Test-ReleaseArtifactVerifier.ps1'
}

function Assert-ContainsText {
    param(
        [Parameter(Mandatory)]
        [string]$Text,

        [Parameter(Mandatory)]
        [string]$Expected,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if ($Text.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) {
        throw "$Description is missing required text: $Expected"
    }
}

function Assert-Parses {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($Path), [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) {
        $details = ($errors | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Message)" }) -join '; '
        throw "PowerShell parser rejected '$Path': $details"
    }
}

foreach ($path in @($TagReleaseScriptPath, $ArtifactVerifierPath, $ArtifactVerifierTestPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required release script was not found: $path"
    }

    Assert-Parses -Path $path
}

if (-not (Test-Path -LiteralPath $WorkflowPath -PathType Leaf)) {
    throw "Release workflow was not found: $WorkflowPath"
}

$workflow = [IO.File]::ReadAllText([IO.Path]::GetFullPath($WorkflowPath))
foreach ($expected in @(
        "tags: ['v*']",
        'environment: release',
        'contents: write',
        'id-token: write',
        'attestations: write',
        'artifact-metadata: write',
        'actions/attest@',
        'merge-base --is-ancestor',
        'gh release create',
        '--verify-tag',
        '--draft')) {
    Assert-ContainsText -Text $workflow -Expected $expected -Description 'The release workflow'
}

$createIndex = $workflow.IndexOf('gh release create', [StringComparison]::Ordinal)
$attestIndex = $workflow.IndexOf('actions/attest@', [StringComparison]::Ordinal)
$verifyIndex = $workflow.IndexOf('Test-ReleaseArtifacts.ps1', [StringComparison]::Ordinal)
if ($verifyIndex -lt 0 -or $attestIndex -le $verifyIndex -or $createIndex -le $attestIndex) {
    throw 'The release workflow must verify the assembled assets, attest them, and only then create the draft GitHub Release.'
}

Write-Host 'Release mechanics static verification passed. No release was created.'
