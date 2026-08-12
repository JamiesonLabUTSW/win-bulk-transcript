[CmdletBinding()]
param(
    [string]$PublisherPath,

    [string]$NoticesGeneratorPath
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PublisherPath)) {
    $PublisherPath = Join-Path $PSScriptRoot 'Publish-Release.ps1'
}

if ([string]::IsNullOrWhiteSpace($NoticesGeneratorPath)) {
    $NoticesGeneratorPath = Join-Path $PSScriptRoot 'New-ThirdPartyNotices.ps1'
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

function Assert-ByteSnapshotCreatesScriptBlock {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $bytes = [IO.File]::ReadAllBytes($Path)
    $stream = [IO.MemoryStream]::new($bytes, $false)
    try {
        $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false), $true)
        try {
            $scriptText = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    [void][ScriptBlock]::Create($scriptText)
}

foreach ($path in @($PublisherPath, $NoticesGeneratorPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required script was not found: $path"
    }

    Assert-Parses -Path $path
}

Assert-ByteSnapshotCreatesScriptBlock -Path $NoticesGeneratorPath

$publisher = [IO.File]::ReadAllText([IO.Path]::GetFullPath($PublisherPath))
$noticesGenerator = [IO.File]::ReadAllText([IO.Path]::GetFullPath($NoticesGeneratorPath))

foreach ($expected in @(
        '$publisherScriptPath; Sha256 = $publisherScriptSha256',
        '$noticesGeneratorBytes = [IO.File]::ReadAllBytes($noticesGeneratorSourcePath)',
        '$noticesGeneratorSourcePath; Sha256 = $noticesGeneratorSha256',
        '$projectLicenseBytes = [IO.File]::ReadAllBytes($projectLicensePath)',
        '$projectLicensePath; Sha256 = $projectLicenseSha256',
        '[IO.File]::WriteAllBytes($stagedProjectLicensePath, $projectLicenseBytes)',
        'Get-ValidatedReleaseSource -RepositoryRoot $repositoryRoot',
        "`$releasePolicy = if (`$Version.StartsWith('0.'",
        "@('Passed', 'Accepted risk', 'Not applicable')",
        "-Label 'Preview risk acceptance'",
        'releasePolicy = $releasePolicy',
        'releaseSource = $releaseSource',
        "Invoke-Dotnet -Arguments @('restore', `$project, '--locked-mode')",
        "restoreCommand = @('dotnet', 'restore', `$projectRecordPath, '--locked-mode')",
        'New-VerifiedScriptBlock -ScriptBytes $noticesGeneratorBytes',
        '& $verifiedNoticesGenerator @noticesArguments',
        'Assert-ReleaseStateUnchanged @releaseStateAssertion')) {
    Assert-ContainsText -Text $publisher -Expected $expected -Description 'The release publisher'
}

Assert-ContainsText -Text $noticesGenerator -Expected '[string]$RepositoryRoot' -Description 'The notices generator'
Assert-ContainsText -Text $noticesGenerator -Expected 'if ([string]::IsNullOrWhiteSpace($RepositoryRoot))' -Description 'The notices generator'
Assert-ContainsText -Text $noticesGenerator -Expected 'Get-MitLicenseText -Copyright $packageCopyright' -Description 'The notices generator'
Assert-ContainsText -Text $noticesGenerator -Expected 'SupplementalNotices = @($supplementalNotices)' -Description 'The notices generator'
if ($publisher.IndexOf("Invoke-Dotnet -Arguments @('restore', `$project, '--runtime', `$rid, '--locked-mode')", [StringComparison]::Ordinal) -ge 0) {
    throw 'The release publisher must restore the project-declared dual-RID graph, not override it with one runtime identifier.'
}


$generatorInvocation = $publisher.IndexOf('& $verifiedNoticesGenerator @noticesArguments', [StringComparison]::Ordinal)
if ($generatorInvocation -lt 0) {
    throw 'The publisher does not invoke the verified notices generator.'
}

$postGeneratorCheck = $publisher.IndexOf('Assert-ReleaseStateUnchanged @releaseStateAssertion', $generatorInvocation, [StringComparison]::Ordinal)
$finalization = $publisher.IndexOf('$finalizationStarted = $true', [StringComparison]::Ordinal)
if ($finalization -lt 0) {
    throw 'The publisher no longer has the finalization marker.'
}

$preFinalizationCheck = $publisher.LastIndexOf('Assert-ReleaseStateUnchanged @releaseStateAssertion', $finalization, [StringComparison]::Ordinal)
if ($postGeneratorCheck -le $generatorInvocation) {
    throw 'The publisher does not revalidate source state after notices generation.'
}

if ($preFinalizationCheck -lt $postGeneratorCheck -or $preFinalizationCheck -ge $finalization) {
    throw 'The publisher does not revalidate source state immediately before artifact finalization.'
}

$publisherTokens = $null
$publisherParseErrors = $null
$publisherAst = [Management.Automation.Language.Parser]::ParseFile(
    [IO.Path]::GetFullPath($PublisherPath),
    [ref]$publisherTokens,
    [ref]$publisherParseErrors)
foreach ($functionName in @(
        'Get-ReleaseMatrixHeaderValue',
        'ConvertFrom-MarkdownTableRow',
        'Test-IsMarkdownTableSeparator',
        'Assert-CompletedReleaseTestMatrix')) {
    $targetName = $functionName
    $functionAst = $publisherAst.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            [string]::Equals($node.Name, $targetName, [StringComparison]::Ordinal)
        }, $true)
    if ($null -eq $functionAst) {
        throw "Could not load publisher function '$functionName' for behavioral policy verification."
    }

    . ([ScriptBlock]::Create($functionAst.Extent.Text))
}

function New-PolicyTestMatrix {
    param(
        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$Policy,

        [Parameter(Mandatory)]
        [string]$Status,

        [Parameter(Mandatory)]
        [string]$RiskAcceptance
    )

    return @"
# Policy test matrix

Release version: ``$Version``
Release date: ``2026-08-12``
Release Foundry model variant: ``synthetic-model:1``
Release source: ``v$Version``
Release policy: ``$Policy``
Preview risk acceptance: ``$RiskAcceptance``
Release notes source: ``release-notes.md sha256:$('a' * 64)``
Model provenance source: ``model-provenance.json sha256:$('b' * 64)``
Runtime framework notices source: ``runtime-framework-notices.txt sha256:$('c' * 64)``

| Area | x64 | ARM64 | Evidence / notes |
|---|---|---|---|
| Synthetic policy gate | $Status | Pending | Retained evidence and explicit rationale. |

| Artifact | SHA-256 | Generated by |
|---|---|---|
| ``WinBulkTranscript-$Version-win-x64.zip`` | Publisher writes the matching sidecar. | publisher |
"@
}

function Invoke-PolicyMatrixAssertion {
    param(
        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$Policy,

        [Parameter(Mandatory)]
        [string]$Status,

        [Parameter(Mandatory)]
        [string]$RiskAcceptance
    )

    Assert-CompletedReleaseTestMatrix `
        -MatrixText (New-PolicyTestMatrix -Version $Version -Policy $Policy -Status $Status -RiskAcceptance $RiskAcceptance) `
        -Version $Version `
        -ModelVariant 'synthetic-model:1' `
        -Architecture 'x64' `
        -ReleaseNotesSource ("release-notes.md sha256:" + ('a' * 64)) `
        -ModelProvenanceSource ("model-provenance.json sha256:" + ('b' * 64)) `
        -RuntimeFrameworkNoticesSource ("runtime-framework-notices.txt sha256:" + ('c' * 64)) `
        -ReleaseSource "v$Version" `
        -ReleasePolicy $Policy
}

Invoke-PolicyMatrixAssertion -Version '0.1.0' -Policy 'preview' -Status 'Accepted risk' -RiskAcceptance 'Approver: Release owner; Date: 2026-08-12; Decision: Accept documented preview limitations for v0.1.0.'
Invoke-PolicyMatrixAssertion -Version '1.0.0' -Policy 'supported' -Status 'Passed' -RiskAcceptance 'Not applicable.'

$rejectedUnapprovedRisk = $false
try {
    Invoke-PolicyMatrixAssertion -Version '0.1.0' -Policy 'preview' -Status 'Accepted risk' -RiskAcceptance 'Missing approval'
}
catch {
    $rejectedUnapprovedRisk = $_.Exception.Message.IndexOf('Preview risk acceptance is not finalized', [StringComparison]::Ordinal) -ge 0
}
if (-not $rejectedUnapprovedRisk) {
    throw 'Preview policy did not reject accepted risk without a finalized approver/date/decision.'
}

$rejectedStableRisk = $false
try {
    Invoke-PolicyMatrixAssertion -Version '1.0.0' -Policy 'supported' -Status 'Accepted risk' -RiskAcceptance 'Approver: Release owner; Date: 2026-08-12; Decision: Accept risk.'
}
catch {
    $rejectedStableRisk = $_.Exception.Message.IndexOf("Policy 'supported' allows only: Passed", [StringComparison]::Ordinal) -ge 0
}
if (-not $rejectedStableRisk) {
    throw 'Supported policy did not reject an accepted-risk matrix row.'
}

Write-Host 'Publish-Release hardening and preview-policy verification passed. No release was created.'
