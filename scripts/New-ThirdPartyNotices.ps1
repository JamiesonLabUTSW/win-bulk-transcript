[CmdletBinding()]
param(
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

    [ValidateSet('preview', 'supported')]
    [string]$ReleasePolicy = 'supported',

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$PublishedDepsPath,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$PayloadInventoryPath,

    [string]$PackageLockPath,

    [string]$NuGetPackagesRoot,

    [string]$OutputPath,

    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}
else {
    $repositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
    if (-not (Test-Path -LiteralPath $repositoryRoot -PathType Container)) {
        throw "RepositoryRoot is not an existing directory: $repositoryRoot"
    }
}
if ([string]::IsNullOrWhiteSpace($PackageLockPath)) {
    $PackageLockPath = Join-Path $repositoryRoot 'src\WinBulkTranscript.App\packages.lock.json'
}

if ([string]::IsNullOrWhiteSpace($NuGetPackagesRoot)) {
    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        $NuGetPackagesRoot = $env:NUGET_PACKAGES
    }
    else {
        if ([string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
            throw 'NuGet packages root was not supplied and USERPROFILE is unavailable.'
        }

        $NuGetPackagesRoot = Join-Path $env:USERPROFILE '.nuget\packages'
    }
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot 'artifacts\THIRD-PARTY-NOTICES.md'
}

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

function ConvertTo-NormalizedSha512 {
    param(
        [Parameter(Mandatory)]
        [string]$Value,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $normalized = $Value.Trim()
    if ($normalized.StartsWith('sha512-', [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring('sha512-'.Length)
    }

    try {
        $decoded = [Convert]::FromBase64String($normalized)
    }
    catch {
        throw "$Description is not a valid SHA-512 base64 value."
    }

    if ($decoded.Length -ne 64) {
        throw "$Description is not a SHA-512 value."
    }

    return $normalized
}

function Write-Utf8FileWithoutOverwrite {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Contents
    )

    $outputDirectory = [IO.Path]::GetDirectoryName($Path)
    if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
        throw "The notices output path must have a containing directory: $Path"
    }

    if ((Test-Path -LiteralPath $outputDirectory) -and -not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
        throw "The notices output directory exists but is not a directory: $outputDirectory"
    }

    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    if (Test-Path -LiteralPath $Path) {
        throw "Refusing to overwrite an existing notices file: $Path"
    }

    $temporaryPath = Join-Path $outputDirectory ("." + [IO.Path]::GetFileName($Path) + ".tmp-" + [Guid]::NewGuid().ToString('N'))
    $ownsTemporaryPath = $false
    try {
        $stream = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $ownsTemporaryPath = $true
        try {
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Contents)
            $stream.Write($bytes, 0, $bytes.Length)
        }
        finally {
            $stream.Dispose()
        }

        try {
            [IO.File]::Move($temporaryPath, $Path)
        }
        catch [IO.IOException] {
            throw "Refusing to overwrite an existing notices file that appeared during generation: $Path"
        }
    }
    finally {
        if ($ownsTemporaryPath -and (Test-Path -LiteralPath $temporaryPath -PathType Leaf)) {
            [IO.File]::Delete($temporaryPath)
        }
    }
}

function ConvertTo-MarkdownCell {
    param([AllowNull()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ''
    }

    return $Value.Replace('|', '\|').Replace([Environment]::CarriageReturn, ' ').Replace([Environment]::LineFeed, ' ').Trim()
}

function Get-MitLicenseText {
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Copyright
    )

    return @'
MIT License

Copyright (c) __PACKAGE_COPYRIGHT__

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
'@.Replace('__PACKAGE_COPYRIGHT__', $Copyright.Trim())
}

function Get-PackageLegalFiles {
    param(
        [Parameter(Mandatory)]
        [string]$PackageDirectory
    )

    $files = Get-ChildItem -LiteralPath $PackageDirectory -Recurse -File | Where-Object {
        $_.Name -match '^(LICENSE|LICENCE)(\..+)?$' -or
        $_.Name -match '^(NOTICE|THIRD[-_. ]?PARTY[-_. ]?NOTICES?)(\..+)?$'
    }

    return @($files | Sort-Object FullName -Unique)
}

function Get-LicenseDescriptor {
    param(
        [Parameter(Mandatory)]
        [string]$PackageId,

        [Parameter(Mandatory)]
        [string]$PackageVersion,

        [Parameter(Mandatory)]
        [string]$ExpectedSha512,

        [Parameter(Mandatory)]
        [string]$PackagesRoot
    )

    $packageDirectory = Join-Path (Join-Path $PackagesRoot $PackageId.ToLowerInvariant()) $PackageVersion
    if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) {
        throw "Package '$PackageId' version '$PackageVersion' was not found under '$PackagesRoot'. Restore the locked release graph before generating notices."
    }

    $packageMetadataPath = Join-Path $packageDirectory '.nupkg.metadata'
    if (-not (Test-Path -LiteralPath $packageMetadataPath -PathType Leaf)) {
        throw "Package '$PackageId' version '$PackageVersion' has no .nupkg.metadata file under '$packageDirectory' to bind license metadata to the staged dependency manifest."
    }

    try {
        $packageMetadata = [IO.File]::ReadAllText($packageMetadataPath) | ConvertFrom-Json
    }
    catch {
        throw "Package '$PackageId' version '$PackageVersion' has invalid .nupkg.metadata JSON."
    }

    $cachedContentHash = [string]$packageMetadata.contentHash
    if ([string]::IsNullOrWhiteSpace($cachedContentHash)) {
        throw "Package '$PackageId' version '$PackageVersion' .nupkg.metadata has no contentHash."
    }

    $normalizedCachedSha512 = ConvertTo-NormalizedSha512 -Value $cachedContentHash -Description "Cached package contentHash for '$PackageId' version '$PackageVersion'"
    $normalizedExpectedSha512 = ConvertTo-NormalizedSha512 -Value $ExpectedSha512 -Description "Staged dependency manifest SHA-512 for '$PackageId' version '$PackageVersion'"
    if (-not [string]::Equals($normalizedCachedSha512, $normalizedExpectedSha512, [StringComparison]::Ordinal)) {
        throw "Package '$PackageId' version '$PackageVersion' cache contentHash does not match the value declared by the staged dependency manifest."
    }

    $nuspec = Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object { $_.Extension -eq '.nuspec' } | Select-Object -First 1
    if ($null -eq $nuspec) {
        throw "Package '$PackageId' version '$PackageVersion' has no .nuspec metadata under '$packageDirectory'."
    }

    [xml]$nuspecXml = Get-Content -LiteralPath $nuspec.FullName -Raw
    $metadata = $nuspecXml.package.metadata
    if ($null -eq $metadata) {
        throw "Package '$PackageId' version '$PackageVersion' has malformed .nuspec metadata."
    }

    $license = $metadata.license
    $licenseType = if ($null -eq $license) { '' } else { [string]$license.type }
    $licenseValue = if ($null -eq $license) { '' } else { [string]$license.'#text' }
    $licenseUrl = [string]$metadata.licenseUrl
    $licenseText = $null
    $licenseSource = $null
    $licensePath = $null
    $legalFiles = @(Get-PackageLegalFiles -PackageDirectory $packageDirectory)

    if ($licenseType -eq 'file' -and -not [string]::IsNullOrWhiteSpace($licenseValue)) {
        $licensePath = Join-Path $packageDirectory $licenseValue
        if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
            throw "Package '$PackageId' declares license file '$licenseValue', but it was not found in '$packageDirectory'."
        }

        $licenseText = Get-Content -LiteralPath $licensePath -Raw
        $licenseSource = "embedded file: $licenseValue"
    }
    elseif ($licenseType -eq 'expression' -and -not [string]::IsNullOrWhiteSpace($licenseValue)) {
        $licenseSource = "SPDX expression: $licenseValue"
        $embeddedLicense = $legalFiles | Where-Object { $_.Name -match '^(LICENSE|LICENCE)(\..+)?$' } | Select-Object -First 1
        if ($null -ne $embeddedLicense) {
            $licensePath = $embeddedLicense.FullName
            $licenseText = Get-Content -LiteralPath $licensePath -Raw
            $licenseSource += "; embedded file: $([IO.Path]::GetRelativePath($packageDirectory, $licensePath))"
        }
        elseif ($licenseValue -eq 'MIT') {
            $packageCopyright = [string]$metadata.copyright
            if ([string]::IsNullOrWhiteSpace($packageCopyright)) {
                throw "Package '$PackageId' version '$PackageVersion' uses the MIT expression but has neither an embedded license file nor package copyright metadata. Publishing would omit required attribution."
            }
            $licenseText = Get-MitLicenseText -Copyright $packageCopyright
            $licenseSource += '; canonical full text supplied by notice generator'
        }
        else {
            throw "Package '$PackageId' version '$PackageVersion' uses SPDX expression '$licenseValue' but contains no license file and the notice generator has no reviewed canonical text for that expression."
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($licenseUrl)) {
        $licenseSource = "license URL: $licenseUrl"
    }
    else {
        throw "Package '$PackageId' version '$PackageVersion' declares no license expression, file, or URL. Resolve this legal-review blocker before release."
    }

    $supplementalNotices = foreach ($legalFile in $legalFiles) {
        if ($null -ne $licensePath -and [string]::Equals($legalFile.FullName, $licensePath, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        if ($legalFile.Name -notmatch '^(NOTICE|THIRD[-_. ]?PARTY[-_. ]?NOTICES?)(\..+)?$') {
            continue
        }

        $noticeText = Get-Content -LiteralPath $legalFile.FullName -Raw
        if (-not [string]::IsNullOrWhiteSpace($noticeText)) {
            [PSCustomObject]@{
                Path = [IO.Path]::GetRelativePath($packageDirectory, $legalFile.FullName)
                Text = $noticeText
            }
        }
    }

    return [PSCustomObject]@{
        Id            = $PackageId
        Version       = $PackageVersion
        Authors       = [string]$metadata.authors
        Copyright     = [string]$metadata.copyright
        LicenseSource = $licenseSource
        LicenseText   = $licenseText
        SupplementalNotices = @($supplementalNotices)
    }
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

function Read-JsonFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Description
    )

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "$Description '$Path' is not valid JSON: $($_.Exception.Message)"
    }
}

function Get-PackageKey {
    param(
        [Parameter(Mandatory)]
        [string]$PackageId,

        [Parameter(Mandatory)]
        [string]$PackageVersion
    )

    return "$($PackageId.ToUpperInvariant())|$PackageVersion"
}

function Get-LockedPackageMap {
    param(
        [Parameter(Mandatory)]
        [object]$Lock
    )

    if ($null -eq $Lock.dependencies) {
        throw 'Package lock has no dependencies object.'
    }

    $packagesByKey = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($frameworkProperty in $Lock.dependencies.PSObject.Properties) {
        foreach ($packageProperty in $frameworkProperty.Value.PSObject.Properties) {
            $package = $packageProperty.Value
            if ([string]::IsNullOrWhiteSpace([string]$package.resolved)) {
                continue
            }

            $packageId = [string]$packageProperty.Name
            $packageVersion = ([string]$package.resolved).Trim()
            $contentHash = [string]$package.contentHash
            if ([string]::IsNullOrWhiteSpace($contentHash)) {
                throw "Package lock entry '$packageId' version '$packageVersion' has no contentHash."
            }
            $packageKey = Get-PackageKey -PackageId $packageId -PackageVersion $packageVersion
            if (-not $packagesByKey.ContainsKey($packageKey)) {
                $packagesByKey.Add($packageKey, [PSCustomObject]@{
                    Id = $packageId
                    Version = $packageVersion
                    Type = [string]$package.type
                    ContentHash = ConvertTo-NormalizedSha512 -Value $contentHash -Description "Package lock contentHash for '$packageId' version '$packageVersion'"
                })
            }
        }
    }

    if ($packagesByKey.Count -eq 0) {
        throw 'Package lock contains no resolved packages.'
    }

    return [PSCustomObject]@{
        PackagesByKey = $packagesByKey
    }
}

function Get-PublishedPackageLibraries {
    param(
        [Parameter(Mandatory)]
        [string]$PublishedDepsPath
    )

    $manifest = Read-JsonFile -Path $PublishedDepsPath -Description 'Published dependency manifest'
    if ($null -eq $manifest.libraries) {
        throw "Published dependency manifest '$PublishedDepsPath' has no libraries object."
    }

    $packagesByKey = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($libraryProperty in $manifest.libraries.PSObject.Properties) {
        $library = $libraryProperty.Value
        $libraryType = Get-RequiredJsonString -Object $library -PropertyName 'type' -Description "Published dependency library '$($libraryProperty.Name)'"
        if ($libraryType -ine 'package') {
            continue
        }

        $libraryName = [string]$libraryProperty.Name
        $separatorIndex = $libraryName.LastIndexOf('/')
        if ($separatorIndex -le 0 -or $separatorIndex -ge ($libraryName.Length - 1)) {
            throw "Published package library '$libraryName' does not use '<package id>/<version>' naming."
        }

        $packageId = $libraryName.Substring(0, $separatorIndex)
        $packageVersion = $libraryName.Substring($separatorIndex + 1)
        $packagePath = Get-RequiredJsonString -Object $library -PropertyName 'path' -Description "Published package library '$libraryName'"
        $packageSha512 = Get-RequiredJsonString -Object $library -PropertyName 'sha512' -Description "Published package library '$libraryName'"
        $packageKey = Get-PackageKey -PackageId $packageId -PackageVersion $packageVersion
        if ($packagesByKey.ContainsKey($packageKey)) {
            throw "Published dependency manifest repeats package library '$libraryName'."
        }

        $packagesByKey.Add($packageKey, [PSCustomObject]@{
            Id = $packageId
            Version = $packageVersion
            Path = $packagePath
            Sha512 = $packageSha512
        })
    }

    if ($packagesByKey.Count -eq 0) {
        throw "Published dependency manifest '$PublishedDepsPath' contains no package libraries."
    }

    return @($packagesByKey.Values)
}

function Get-RuntimeConfigurationFrameworks {
    param(
        [Parameter(Mandatory)]
        [string]$RuntimeConfigurationPath
    )

    $configuration = Read-JsonFile -Path $RuntimeConfigurationPath -Description 'Published runtime configuration'
    if ($null -eq $configuration.runtimeOptions) {
        throw "Published runtime configuration '$RuntimeConfigurationPath' has no runtimeOptions object."
    }

    $definitions = [Collections.Generic.List[object]]::new()
    $frameworkProperty = $configuration.runtimeOptions.PSObject.Properties['framework']
    if ($null -ne $frameworkProperty -and $null -ne $frameworkProperty.Value) {
        $definitions.Add($frameworkProperty.Value)
    }

    $includedFrameworksProperty = $configuration.runtimeOptions.PSObject.Properties['includedFrameworks']
    if ($null -ne $includedFrameworksProperty -and $null -ne $includedFrameworksProperty.Value) {
        foreach ($includedFramework in @($includedFrameworksProperty.Value)) {
            $definitions.Add($includedFramework)
        }
    }

    $frameworksByKey = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($framework in $definitions) {
        $frameworkName = Get-RequiredJsonString -Object $framework -PropertyName 'name' -Description "Published runtime configuration '$RuntimeConfigurationPath' framework"
        $frameworkVersion = Get-RequiredJsonString -Object $framework -PropertyName 'version' -Description "Published runtime configuration '$RuntimeConfigurationPath' framework"
        $frameworkKey = "$($frameworkName.ToUpperInvariant())|$frameworkVersion"
        if (-not $frameworksByKey.ContainsKey($frameworkKey)) {
            $frameworksByKey.Add($frameworkKey, [PSCustomObject]@{
                Name = $frameworkName
                Version = $frameworkVersion
            })
        }
    }

    if ($frameworksByKey.Count -eq 0) {
        throw "Published runtime configuration '$RuntimeConfigurationPath' declares no frameworks."
    }

    return @($frameworksByKey.Values)
}

function Assert-PayloadInventoryMatchesPublishedPayload {
    param(
        [Parameter(Mandatory)]
        [string]$PayloadInventoryPath,

        [Parameter(Mandatory)]
        [string]$PublishedDepsPath
    )

    $inventory = Read-JsonFile -Path $PayloadInventoryPath -Description 'Publish payload inventory'
    $schemaVersion = Get-RequiredJsonString -Object $inventory -PropertyName 'schemaVersion' -Description "Publish payload inventory '$PayloadInventoryPath'"
    if ($schemaVersion -cne '1') {
        throw "Publish payload inventory '$PayloadInventoryPath' has unsupported schemaVersion '$schemaVersion'."
    }

    $scope = Get-RequiredJsonString -Object $inventory -PropertyName 'scope' -Description "Publish payload inventory '$PayloadInventoryPath'"
    if ($scope -cne 'rawDotnetPublishOutputBeforeReleaseEvidence') {
        throw "Publish payload inventory '$PayloadInventoryPath' does not describe the required raw publish-output scope."
    }

    $dependencyManifest = $inventory.dependencyManifest
    if ($null -eq $dependencyManifest) {
        throw "Publish payload inventory '$PayloadInventoryPath' has no dependencyManifest section."
    }

    $expectedDepsFileName = Get-RequiredJsonString -Object $dependencyManifest -PropertyName 'fileName' -Description 'Publish payload dependency manifest'
    $actualDepsFileName = [IO.Path]::GetFileName($PublishedDepsPath)
    if (-not [string]::Equals($expectedDepsFileName, $actualDepsFileName, [StringComparison]::Ordinal)) {
        throw "Publish payload inventory dependency manifest '$expectedDepsFileName' does not match staged manifest '$actualDepsFileName'."
    }

    $expectedDepsSha256 = Get-RequiredJsonString -Object $dependencyManifest -PropertyName 'sha256' -Description 'Publish payload dependency manifest'
    $actualDepsSha256 = Get-Sha256 -Path $PublishedDepsPath
    if (-not [string]::Equals($expectedDepsSha256, $actualDepsSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publish payload inventory dependency-manifest SHA-256 does not match the staged .deps.json file.'
    }

    $runtimeTargetName = Get-RequiredJsonString -Object $dependencyManifest -PropertyName 'runtimeTargetName' -Description 'Publish payload dependency manifest'
    $actualPackages = @(Get-PublishedPackageLibraries -PublishedDepsPath $PublishedDepsPath)
    $actualPackagesByKey = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($package in $actualPackages) {
        $packageKey = Get-PackageKey -PackageId $package.Id -PackageVersion $package.Version
        $actualPackagesByKey.Add($packageKey, $package)
    }

    if ($null -eq $inventory.packageLibraries) {
        throw "Publish payload inventory '$PayloadInventoryPath' has no packageLibraries section."
    }

    $inventoryPackagesByKey = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($package in @($inventory.packageLibraries)) {
        $packageId = Get-RequiredJsonString -Object $package -PropertyName 'id' -Description 'Publish payload package library'
        $packageVersion = Get-RequiredJsonString -Object $package -PropertyName 'version' -Description 'Publish payload package library'
        $packagePath = Get-RequiredJsonString -Object $package -PropertyName 'path' -Description 'Publish payload package library'
        $packageSha512 = Get-RequiredJsonString -Object $package -PropertyName 'sha512' -Description 'Publish payload package library'
        $packageKey = Get-PackageKey -PackageId $packageId -PackageVersion $packageVersion
        if ($inventoryPackagesByKey.ContainsKey($packageKey)) {
            throw "Publish payload inventory repeats package '$packageId' version '$packageVersion'."
        }

        $inventoryPackagesByKey.Add($packageKey, [PSCustomObject]@{
            Id = $packageId
            Version = $packageVersion
            Path = $packagePath
            Sha512 = $packageSha512
        })
    }

    if ($inventoryPackagesByKey.Count -ne $actualPackagesByKey.Count) {
        throw 'Publish payload inventory package-library count does not match staged .deps.json.'
    }

    foreach ($packageKey in $actualPackagesByKey.Keys) {
        if (-not $inventoryPackagesByKey.ContainsKey($packageKey)) {
            throw "Publish payload inventory omits actual staged package library '$packageKey'."
        }

        $actualPackage = $actualPackagesByKey[$packageKey]
        $inventoryPackage = $inventoryPackagesByKey[$packageKey]
        if (-not [string]::Equals($actualPackage.Path, $inventoryPackage.Path, [StringComparison]::Ordinal) -or -not [string]::Equals($actualPackage.Sha512, $inventoryPackage.Sha512, [StringComparison]::Ordinal)) {
            throw "Publish payload inventory does not match the staged path or SHA-512 for package '$($actualPackage.Id)' version '$($actualPackage.Version)'."
        }
    }

    $runtimeConfiguration = $inventory.runtimeConfiguration
    if ($null -eq $runtimeConfiguration) {
        throw "Publish payload inventory '$PayloadInventoryPath' has no runtimeConfiguration section."
    }

    $runtimeConfigurationFileName = Get-RequiredJsonString -Object $runtimeConfiguration -PropertyName 'fileName' -Description 'Publish payload runtime configuration'
    if (-not [string]::Equals($runtimeConfigurationFileName, [IO.Path]::GetFileName($runtimeConfigurationFileName), [StringComparison]::Ordinal)) {
        throw "Publish payload runtime configuration file name '$runtimeConfigurationFileName' must not contain a path."
    }

    $runtimeConfigurationPath = Join-Path ([IO.Path]::GetDirectoryName($PublishedDepsPath)) $runtimeConfigurationFileName
    if (-not (Test-Path -LiteralPath $runtimeConfigurationPath -PathType Leaf)) {
        throw "Publish payload inventory names missing runtime configuration '$runtimeConfigurationFileName'."
    }

    $expectedRuntimeConfigurationSha256 = Get-RequiredJsonString -Object $runtimeConfiguration -PropertyName 'sha256' -Description 'Publish payload runtime configuration'
    if (-not [string]::Equals($expectedRuntimeConfigurationSha256, (Get-Sha256 -Path $runtimeConfigurationPath), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publish payload inventory runtime-configuration SHA-256 does not match the staged file.'
    }

    $actualFrameworks = @(Get-RuntimeConfigurationFrameworks -RuntimeConfigurationPath $runtimeConfigurationPath)
    $actualFrameworksByKey = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($framework in $actualFrameworks) {
        $frameworkKey = "$($framework.Name.ToUpperInvariant())|$($framework.Version)"
        $actualFrameworksByKey.Add($frameworkKey, $framework)
    }

    if ($null -eq $runtimeConfiguration.frameworks) {
        throw "Publish payload inventory '$PayloadInventoryPath' has no runtime framework list."
    }

    $inventoryFrameworksByKey = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($framework in @($runtimeConfiguration.frameworks)) {
        $frameworkName = Get-RequiredJsonString -Object $framework -PropertyName 'name' -Description 'Publish payload runtime framework'
        $frameworkVersion = Get-RequiredJsonString -Object $framework -PropertyName 'version' -Description 'Publish payload runtime framework'
        $frameworkKey = "$($frameworkName.ToUpperInvariant())|$frameworkVersion"
        if ($inventoryFrameworksByKey.ContainsKey($frameworkKey)) {
            throw "Publish payload inventory repeats runtime framework '$frameworkName' version '$frameworkVersion'."
        }

        $inventoryFrameworksByKey.Add($frameworkKey, [PSCustomObject]@{ Name = $frameworkName; Version = $frameworkVersion })
    }

    if ($inventoryFrameworksByKey.Count -ne $actualFrameworksByKey.Count) {
        throw 'Publish payload inventory framework count does not match staged runtime configuration.'
    }

    foreach ($frameworkKey in $actualFrameworksByKey.Keys) {
        if (-not $inventoryFrameworksByKey.ContainsKey($frameworkKey)) {
            throw "Publish payload inventory omits staged runtime framework '$frameworkKey'."
        }
    }

    return [PSCustomObject]@{
        RuntimeTargetName = $runtimeTargetName
        Packages = $actualPackages
        Frameworks = $actualFrameworks
    }
}

function Test-IsRuntimeFrameworkPackage {
    param(
        [Parameter(Mandatory)]
        [string]$PackageId
    )

    foreach ($prefix in @(
            'Microsoft.NETCore.App.',
            'Microsoft.AspNetCore.App.',
            'Microsoft.WindowsDesktop.App.',
            'Microsoft.NETCore.DotNetHost')) {
        if ($PackageId.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Assert-RuntimeFrameworkNoticeCoverage {
    param(
        [Parameter(Mandatory)]
        [string]$NoticesText,

        [Parameter(Mandatory)]
        [object[]]$Frameworks,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$RuntimePackages
    )

    foreach ($framework in $Frameworks) {
        foreach ($requiredValue in @($framework.Name, $framework.Version)) {
            if ($NoticesText.IndexOf($requiredValue, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                throw "Runtime/framework notices do not name actual runtime framework '$($framework.Name)' version '$($framework.Version)'."
            }
        }
    }

    foreach ($package in $RuntimePackages) {
        foreach ($requiredValue in @($package.Id, $package.Version)) {
            if ($NoticesText.IndexOf($requiredValue, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                throw "Runtime/framework notices do not name actual non-lock runtime package '$($package.Id)' version '$($package.Version)'."
            }
        }
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

    $provenance = Read-JsonFile -Path $Path -Description 'Model provenance'
    $schemaVersion = Get-RequiredJsonString -Object $provenance -PropertyName 'schemaVersion' -Description "Model provenance '$Path'"
    if ($schemaVersion -cne '1') {
        throw "Model provenance '$Path' has unsupported schemaVersion '$schemaVersion'."
    }

    $modelVariant = Get-RequiredJsonString -Object $provenance -PropertyName 'modelVariant' -Description "Model provenance '$Path'"
    if (-not [string]::Equals($modelVariant, $ExpectedModelVariant, [StringComparison]::Ordinal)) {
        throw "Model provenance variant '$modelVariant' does not match expected '$ExpectedModelVariant'."
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
    if (-not [string]::Equals($licenseFileName, [IO.Path]::GetFileName($ModelLicensePath), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Model provenance licenseFileName '$licenseFileName' does not match supplied model license '$([IO.Path]::GetFileName($ModelLicensePath))'."
    }

    $licenseSha256 = Get-RequiredJsonString -Object $provenance -PropertyName 'licenseSha256' -Description "Model provenance '$Path'"
    if ($licenseSha256 -notmatch '^[0-9a-fA-F]{64}$' -or -not [string]::Equals($licenseSha256, $ModelLicenseSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Model provenance licenseSha256 does not match the supplied model license.'
    }

    return [PSCustomObject]@{
        ArtifactIdentifier = $artifactIdentifier
        SourceUri = $uri.AbsoluteUri
        LicenseFileName = $licenseFileName
        LicenseSha256 = $licenseSha256.ToLowerInvariant()
    }
}

$resolvedLockPath = [IO.Path]::GetFullPath($PackageLockPath)
if (-not (Test-Path -LiteralPath $resolvedLockPath -PathType Leaf)) {
    throw "Package lock file was not found: $resolvedLockPath"
}

$resolvedModelLicensePath = [IO.Path]::GetFullPath($ModelLicensePath)
$resolvedModelProvenancePath = [IO.Path]::GetFullPath($ModelProvenancePath)
$resolvedRuntimeFrameworkNoticesPath = [IO.Path]::GetFullPath($RuntimeFrameworkNoticesPath)
$resolvedPublishedDepsPath = [IO.Path]::GetFullPath($PublishedDepsPath)
$resolvedPayloadInventoryPath = [IO.Path]::GetFullPath($PayloadInventoryPath)
$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $resolvedOutputPath) {
    throw "Refusing to overwrite an existing notices file: $resolvedOutputPath"
}

$lock = Read-JsonFile -Path $resolvedLockPath -Description 'Package lock'
$lockPackageGraph = Get-LockedPackageMap -Lock $lock
$lockedPackagesByKey = $lockPackageGraph.PackagesByKey
$payloadValidation = Assert-PayloadInventoryMatchesPublishedPayload -PayloadInventoryPath $resolvedPayloadInventoryPath -PublishedDepsPath $resolvedPublishedDepsPath
$actualPackages = @($payloadValidation.Packages)
$lockBackedPackages = [Collections.Generic.List[object]]::new()
$runtimePackageEntries = [Collections.Generic.List[object]]::new()
foreach ($package in $actualPackages) {
    $packageKey = Get-PackageKey -PackageId $package.Id -PackageVersion $package.Version
    if ($lockedPackagesByKey.ContainsKey($packageKey)) {
        $lockPackage = $lockedPackagesByKey[$packageKey]
        $normalizedPublishedSha512 = ConvertTo-NormalizedSha512 -Value $package.Sha512 -Description "Staged dependency manifest SHA-512 for '$($package.Id)' version '$($package.Version)'"
        if (-not [string]::Equals($normalizedPublishedSha512, $lockPackage.ContentHash, [StringComparison]::Ordinal)) {
            throw "Actual staged package '$($package.Id)' version '$($package.Version)' SHA-512 does not match the App lock contentHash."
        }

        $lockBackedPackages.Add([PSCustomObject]@{
            Id = $package.Id
            Version = $package.Version
            Type = $lockPackage.Type
            Path = $package.Path
            Sha512 = $package.Sha512
        })
        continue
    }

    if (-not (Test-IsRuntimeFrameworkPackage -PackageId $package.Id)) {
        throw "Actual staged package '$($package.Id)' version '$($package.Version)' is absent from the App lock and is not a recognized runtime/framework package. Add it to the lock or extend runtime attribution after legal review; do not publish with unaccounted payload."
    }

    $runtimePackageEntries.Add($package)
}

$licenseEntries = foreach ($package in $lockBackedPackages) {
    $descriptor = Get-LicenseDescriptor -PackageId $package.Id -PackageVersion $package.Version -ExpectedSha512 $package.Sha512 -PackagesRoot $NuGetPackagesRoot
    [PSCustomObject]@{
        Id            = $descriptor.Id
        Version       = $descriptor.Version
        Type          = $package.Type
        Authors       = $descriptor.Authors
        Copyright     = $descriptor.Copyright
        LicenseSource = $descriptor.LicenseSource
        LicenseText   = $descriptor.LicenseText
        SupplementalNotices = @($descriptor.SupplementalNotices)
    }
}

$modelLicenseText = Get-Content -LiteralPath $resolvedModelLicensePath -Raw
if ([string]::IsNullOrWhiteSpace($modelLicenseText)) {
    throw "The supplied model license file is empty: $resolvedModelLicensePath"
}

$runtimeFrameworkNoticesText = Get-Content -LiteralPath $resolvedRuntimeFrameworkNoticesPath -Raw
if ([string]::IsNullOrWhiteSpace($runtimeFrameworkNoticesText)) {
    throw "The supplied runtime/framework notices file is empty: $resolvedRuntimeFrameworkNoticesPath"
}

Assert-RuntimeFrameworkNoticeCoverage -NoticesText $runtimeFrameworkNoticesText -Frameworks @($payloadValidation.Frameworks) -RuntimePackages @($runtimePackageEntries)

$packageLockSha256 = Get-Sha256 -Path $resolvedLockPath
$modelLicenseSha256 = Get-Sha256 -Path $resolvedModelLicensePath
$modelProvenanceSha256 = Get-Sha256 -Path $resolvedModelProvenancePath
$runtimeFrameworkNoticesSha256 = Get-Sha256 -Path $resolvedRuntimeFrameworkNoticesPath
$publishedDepsSha256 = Get-Sha256 -Path $resolvedPublishedDepsPath
$payloadInventorySha256 = Get-Sha256 -Path $resolvedPayloadInventoryPath
$packageLockFileName = [IO.Path]::GetFileName($resolvedLockPath)
$modelLicenseFileName = [IO.Path]::GetFileName($resolvedModelLicensePath)
$modelProvenanceFileName = [IO.Path]::GetFileName($resolvedModelProvenancePath)
$runtimeFrameworkNoticesFileName = [IO.Path]::GetFileName($resolvedRuntimeFrameworkNoticesPath)
$publishedDepsFileName = [IO.Path]::GetFileName($resolvedPublishedDepsPath)
$payloadInventoryFileName = [IO.Path]::GetFileName($resolvedPayloadInventoryPath)
$modelProvenance = Read-ModelProvenance -Path $resolvedModelProvenancePath -ExpectedModelVariant $ModelVariant -ModelLicensePath $resolvedModelLicensePath -ModelLicenseSha256 $modelLicenseSha256

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# Third-party and model notices')
$lines.Add('')
$lines.Add("Generated UTC: $(Get-UtcTimestamp)")
$lines.Add("Published dependency manifest: $publishedDepsFileName")
$lines.Add("Published dependency manifest SHA-256: $publishedDepsSha256")
$lines.Add("Publish payload inventory: $payloadInventoryFileName")
$lines.Add("Publish payload inventory SHA-256: $payloadInventorySha256")
$lines.Add("Package lock file: $packageLockFileName")
$lines.Add("Package lock SHA-256: $packageLockSha256")
$lines.Add("Model variant: $ModelVariant")
$lines.Add("Model license file: $modelLicenseFileName")
$lines.Add("Model license SHA-256: $modelLicenseSha256")
$lines.Add("Model provenance file: $modelProvenanceFileName")
$lines.Add("Model provenance SHA-256: $modelProvenanceSha256")
$lines.Add("Runtime/framework notices file: $runtimeFrameworkNoticesFileName")
$lines.Add("Runtime/framework notices SHA-256: $runtimeFrameworkNoticesSha256")
$lines.Add("Release policy: $ReleasePolicy")
$lines.Add('')
$lines.Add('This release-specific file was generated from the actual package libraries declared by the staged .deps.json and cross-checked against the staged payload inventory. Only those actual package libraries appear below; the lock file is not treated as an inventory of every file in the release.')
if ($ReleasePolicy -ceq 'preview') {
    $lines.Add('Lock-backed package libraries are verified against the App lock and restored package SHA-512. The self-contained runtime/framework payload is recorded from the staged runtime configuration and non-lock runtime packages. This version-zero notice is a preview disclosure tied to the matching artifact.')
}
else {
    $lines.Add('Lock-backed package libraries are verified against the App lock and restored package SHA-512. The self-contained runtime/framework payload is recorded from the staged runtime configuration and non-lock runtime packages. Retain this file with the matching publish artifact and complete the supported-release review before distribution.')
}
$lines.Add('')
$lines.Add('## Lock-backed package libraries in the actual payload')
$lines.Add('')
$lines.Add('| Package | Version | Dependency type | Authors | Copyright | License source |')
$lines.Add('|---|---:|---|---|---|---|')
foreach ($entry in $licenseEntries) {
    $lines.Add("| $(ConvertTo-MarkdownCell $entry.Id) | $(ConvertTo-MarkdownCell $entry.Version) | $(ConvertTo-MarkdownCell $entry.Type) | $(ConvertTo-MarkdownCell $entry.Authors) | $(ConvertTo-MarkdownCell $entry.Copyright) | $(ConvertTo-MarkdownCell $entry.LicenseSource) |")
}

if ($licenseEntries.Count -eq 0) {
    $lines.Add('| _None_ |  |  |  |  |  |')
}

$lines.Add('')
$lines.Add('## Self-contained runtime/framework payload')
$lines.Add('')
$lines.Add("Runtime target from staged dependency manifest: $($payloadValidation.RuntimeTargetName)")
$lines.Add('')
$lines.Add('| Declared runtime framework | Version |')
$lines.Add('|---|---:|')
foreach ($framework in @($payloadValidation.Frameworks)) {
    $lines.Add("| $(ConvertTo-MarkdownCell $framework.Name) | $(ConvertTo-MarkdownCell $framework.Version) |")
}

if ($runtimePackageEntries.Count -gt 0) {
    $lines.Add('')
    $lines.Add('### Non-lock runtime packages declared by the staged dependency manifest')
    $lines.Add('')
    $lines.Add('| Package | Version | Payload path | SHA-512 |')
    $lines.Add('|---|---:|---|---|')
    foreach ($package in @($runtimePackageEntries)) {
        $lines.Add("| $(ConvertTo-MarkdownCell $package.Id) | $(ConvertTo-MarkdownCell $package.Version) | $(ConvertTo-MarkdownCell $package.Path) | $(ConvertTo-MarkdownCell $package.Sha512) |")
    }
}

$lines.Add('')
if ($ReleasePolicy -ceq 'preview') {
    $lines.Add('The following version-zero runtime/framework information is the disclosure supplied for this preview. Automation verifies that it names every declared framework and non-lock runtime package above.')
}
else {
    $lines.Add('The following runtime/framework notice text is the reviewed input for this supported release. Automation verifies that it names every declared framework and non-lock runtime package above; the supported-release review determines legal completeness.')
}
$lines.Add('')
$lines.Add('~~~text')
$lines.Add($runtimeFrameworkNoticesText.TrimEnd())
$lines.Add('~~~')

$embeddedLicenseEntries = @($licenseEntries | Where-Object { -not [string]::IsNullOrWhiteSpace($_.LicenseText) })
if ($embeddedLicenseEntries.Count -gt 0) {
    $lines.Add('')
    $lines.Add('## Embedded package license texts')
    foreach ($entry in $embeddedLicenseEntries) {
        $lines.Add('')
        $lines.Add("### $($entry.Id) $($entry.Version)")
        $lines.Add('')
        $lines.Add('~~~text')
        $lines.Add($entry.LicenseText.TrimEnd())
        $lines.Add('~~~')
    }
}

$noticeEntries = @($licenseEntries | Where-Object { $_.SupplementalNotices.Count -gt 0 })
if ($noticeEntries.Count -gt 0) {
    $lines.Add('')
    $lines.Add('## Package notices and third-party attribution files')
    foreach ($entry in $noticeEntries) {
        foreach ($notice in $entry.SupplementalNotices) {
            $lines.Add('')
            $lines.Add("### $($entry.Id) $($entry.Version) - $($notice.Path)")
            $lines.Add('')
            $lines.Add('~~~text')
            $lines.Add($notice.Text.TrimEnd())
            $lines.Add('~~~')
        }
    }
}

$lines.Add('')
$lines.Add('## Model provenance')
$lines.Add('')
$lines.Add('| Field | Value |')
$lines.Add('|---|---|')
$lines.Add("| Configured variant | $(ConvertTo-MarkdownCell $ModelVariant) |")
$lines.Add("| Artifact identifier | $(ConvertTo-MarkdownCell $modelProvenance.ArtifactIdentifier) |")
$lines.Add("| Source URI | $(ConvertTo-MarkdownCell $modelProvenance.SourceUri) |")
$lines.Add("| Source license file | $(ConvertTo-MarkdownCell $modelProvenance.LicenseFileName) |")
$lines.Add("| Source license SHA-256 | $(ConvertTo-MarkdownCell $modelProvenance.LicenseSha256) |")

$lines.Add('')
$lines.Add('## Model license text')
$lines.Add('')
$lines.Add('The supplied license text below was verified against the model provenance record above.')
$lines.Add('')
$lines.Add('~~~text')
$lines.Add($modelLicenseText.TrimEnd())
$lines.Add('~~~')
$lines.Add('')

Write-Utf8FileWithoutOverwrite -Path $resolvedOutputPath -Contents ($lines -join [Environment]::NewLine)
Write-Host "Generated third-party and model notices: $resolvedOutputPath"
