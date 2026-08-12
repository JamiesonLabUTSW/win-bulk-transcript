[CmdletBinding()]
param(
    [string]$PublisherPath = (Join-Path $PSScriptRoot 'Publish-Release.ps1'),

    [string]$NoticesGeneratorPath = (Join-Path $PSScriptRoot 'New-ThirdPartyNotices.ps1')
)

$ErrorActionPreference = 'Stop'

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

Write-Host 'Publish-Release hardening static verification passed. No release was created.'
