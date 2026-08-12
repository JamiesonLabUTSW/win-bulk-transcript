[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$AppPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$FixturePath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ArtifactRoot,

    [ValidateRange(15, 180)]
    [int]$TimeoutSeconds = 90
)

<#
.SYNOPSIS
    Produces non-keyboard structural UI Automation evidence for a published WinUI app.

.DESCRIPTION
    The probe owns a previously nonexistent artifact root, creates long Unicode input/output
    directories, and uses the published app's real folder picker and close dialog. The standard
    picker is controlled by its dialog handles because this CI-style desktop session exposes its
    controls without UIA patterns. No global keyboard input, display-scale setting, or
    high-contrast setting is changed.

    This is deliberately not keyboard-only or human accessibility/usability UAT.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Wait-Until {
    param(
        [Parameter(Mandatory)]
        [string]$Description,

        [Parameter(Mandatory)]
        [scriptblock]$Condition
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            if (& $Condition) {
                return
            }
        }
        catch {
            # UIA elements can be transient while the dialog opens or closes.
        }

        Start-Sleep -Milliseconds 200
    }

    throw "Timed out waiting for $Description."
}

function Find-UiaElement {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationProperty]$Property,

        [Parameter(Mandatory)]
        [object]$Value
    )

    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new($Property, $Value))
}

function Get-UiaValue {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Element
    )

    return [string]$Element.GetCurrentPropertyValue([System.Windows.Automation.ValuePattern]::ValueProperty)
}

function Add-ProbeStep {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$Steps,

        [Parameter(Mandatory)]
        [string]$Name,

        [object]$Data
    )

    [void]$Steps.Add([ordered]@{
            name = $Name
            timestampUtc = [DateTime]::UtcNow.ToString('O')
            data = $Data
        })
}

if (-not (Test-Path -LiteralPath $AppPath -PathType Leaf)) {
    throw "AppPath does not exist: $AppPath"
}

if (-not (Test-Path -LiteralPath $FixturePath -PathType Leaf)) {
    throw "FixturePath does not exist: $FixturePath"
}

if (-not [string]::Equals([IO.Path]::GetExtension($FixturePath), '.mp4', [StringComparison]::OrdinalIgnoreCase)) {
    throw "FixturePath must name an MP4 file: $FixturePath"
}

$appFullPath = [IO.Path]::GetFullPath($AppPath)
$fixtureFullPath = [IO.Path]::GetFullPath($FixturePath)
$artifactRootFullPath = [IO.Path]::GetFullPath($ArtifactRoot)
if (Test-Path -LiteralPath $artifactRootFullPath) {
    throw "ArtifactRoot must not already exist so the probe never touches pre-existing files: $artifactRootFullPath"
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if ($null -eq ('WinBulkTranscriptStandardPickerNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class WinBulkTranscriptStandardPickerNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast
    {
        public uint Size;
        public uint Flags;
        public IntPtr DefaultScheme;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetDlgItem(IntPtr dialog, int itemId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, string lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrast data, uint winIni);

    public static bool IsHighContrastEnabled()
    {
        var highContrast = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
        if (!SystemParametersInfo(0x0042, highContrast.Size, ref highContrast, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return (highContrast.Flags & 0x0001) != 0;
    }
}
'@
}

$steps = [System.Collections.Generic.List[object]]::new()
$result = [ordered]@{
    schemaVersion = 1
    status = 'running'
    startedUtc = [DateTime]::UtcNow.ToString('O')
    scenario = 'PublishedX64StructuralLongUnicodePickerAndClose'
    scope = [ordered]@{
        automatedStructuralEvidence = $true
        keyboardOnlyWorkflow = $false
        humanDisplayScaleUat = $false
        humanHighContrastUat = $false
        humanUsabilityUat = $false
        notes = 'Uses the app UIA and the standard folder-picker dialog handles; it does not inject keyboard input or alter global display accessibility settings.'
    }
    app = [ordered]@{
        path = $appFullPath
        sha256 = (Get-FileHash -LiteralPath $appFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    fixture = [ordered]@{
        sourcePath = $fixtureFullPath
        sha256 = (Get-FileHash -LiteralPath $fixtureFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    paths = [ordered]@{}
    environment = [ordered]@{}
    assertions = [ordered]@{
        longUnicodeInputPath = $false
        longUnicodeOutputPath = $false
        pickerSelectedBothFolders = $false
        startEnabledAfterSelection = $false
        batchBegan = $false
        closeConfirmationObserved = $false
        keepWorkingPreservedRunningBatch = $false
        cancelAndCloseCompleted = $false
        noTemporaryOutputArtifacts = $false
    }
    steps = $steps
    failure = $null
}

$process = $null
$exitCode = 0
try {
    [void][IO.Directory]::CreateDirectory($artifactRootFullPath)
    $inputDirectory = Join-Path $artifactRootFullPath 'input'
    $outputDirectory = Join-Path $artifactRootFullPath 'output'
    foreach ($index in 1..3) {
        $inputDirectory = Join-Path $inputDirectory (('入力-{0:D2}-長い名前-' -f $index) + ('字幕解析' * 18))
        $outputDirectory = Join-Path $outputDirectory (('出力-{0:D2}-長い名前-' -f $index) + ('WebVTT結果' * 16))
    }

    [void][IO.Directory]::CreateDirectory($inputDirectory)
    [void][IO.Directory]::CreateDirectory($outputDirectory)
    foreach ($index in 1..3) {
        [IO.File]::Copy($fixtureFullPath, (Join-Path $inputDirectory ('structural-control-{0:D2}.mp4' -f $index)))
    }

    $result.paths = [ordered]@{
        artifactRoot = $artifactRootFullPath
        inputDirectory = $inputDirectory
        inputDirectoryCharacters = $inputDirectory.Length
        outputDirectory = $outputDirectory
        outputDirectoryCharacters = $outputDirectory.Length
    }
    if ($inputDirectory.Length -lt 260 -or $outputDirectory.Length -lt 260) {
        throw 'The probe paths must exceed 260 characters to exercise the intended long-path case.'
    }
    if ($inputDirectory -notmatch '[^\u0000-\u007F]' -or $outputDirectory -notmatch '[^\u0000-\u007F]') {
        throw 'The probe paths must contain non-ASCII Unicode characters.'
    }

    $result.assertions.longUnicodeInputPath = $true
    $result.assertions.longUnicodeOutputPath = $true
    Add-ProbeStep -Steps $steps -Name 'CreatedProbeOwnedLongUnicodePaths' -Data $result.paths

    $process = Start-Process -FilePath $appFullPath -PassThru
    Wait-Until -Description 'the published app main window' -Condition {
        $process.Refresh()
        return -not $process.HasExited -and $process.MainWindowHandle -ne [IntPtr]::Zero
    }

    $appRoot = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    if ($appRoot.Current.Name -ne 'WinBulkTranscript by Jamieson Lab') {
        throw "Unexpected application window name '$($appRoot.Current.Name)'."
    }

    $dpi = [WinBulkTranscriptStandardPickerNative]::GetDpiForWindow($process.MainWindowHandle)
    $result.environment = [ordered]@{
        dpi = $dpi
        displayScalePercent = [Math]::Round(($dpi / 96.0) * 100, 2)
        highContrastEnabledAtStart = [WinBulkTranscriptStandardPickerNative]::IsHighContrastEnabled()
        operatingSystem = [Environment]::OSVersion.VersionString
        processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    }
    Add-ProbeStep -Steps $steps -Name 'PublishedAppLaunched' -Data $result.environment

    $inputTextBox = Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) -Value 'InputFolderTextBox'
    $browseInput = Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) -Value 'BrowseInputButton'
    $outputTextBox = Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) -Value 'OutputFolderTextBox'
    $browseOutput = Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) -Value 'BrowseOutputButton'
    $startButton = Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) -Value 'StartButton'
    $cancelButton = Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) -Value 'CancelButton'
    if ($null -in @($inputTextBox, $browseInput, $outputTextBox, $browseOutput, $startButton, $cancelButton)) {
        throw 'The published app did not expose all required named controls through UI Automation.'
    }

    function Select-FolderThroughStandardPicker {
        param(
            [Parameter(Mandatory)]
            [System.Windows.Automation.AutomationElement]$BrowseButton,

            [Parameter(Mandatory)]
            [System.Windows.Automation.AutomationElement]$TargetTextBox,

            [Parameter(Mandatory)]
            [string]$FolderPath
        )

        $invoke = [System.Windows.Automation.InvokePattern]$BrowseButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $invoke.Invoke()
        Wait-Until -Description 'the standard Select Folder dialog' -Condition {
            return $null -ne (Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::NameProperty) -Value 'Select Folder')
        }

        $dialog = Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::NameProperty) -Value 'Select Folder'
        $dialogHandle = [IntPtr]$dialog.Current.NativeWindowHandle
        $folderEdit = [WinBulkTranscriptStandardPickerNative]::GetDlgItem($dialogHandle, 1152)
        $selectFolderButton = [WinBulkTranscriptStandardPickerNative]::GetDlgItem($dialogHandle, 1)
        if ($dialogHandle -eq [IntPtr]::Zero -or $folderEdit -eq [IntPtr]::Zero -or $selectFolderButton -eq [IntPtr]::Zero) {
            throw 'The standard picker did not expose its expected Folder edit and Select Folder controls.'
        }

        [void][WinBulkTranscriptStandardPickerNative]::SendMessage($folderEdit, 0x000C, [IntPtr]::Zero, $FolderPath)
        [void][WinBulkTranscriptStandardPickerNative]::SendMessage($folderEdit, 0x0100, [IntPtr]13, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 250
        [void][WinBulkTranscriptStandardPickerNative]::SendMessage($selectFolderButton, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
        Wait-Until -Description "the selected folder '$FolderPath' in the app" -Condition {
            return (Get-UiaValue -Element $TargetTextBox) -eq $FolderPath
        }
    }

    Select-FolderThroughStandardPicker -BrowseButton $browseInput -TargetTextBox $inputTextBox -FolderPath $inputDirectory
    Select-FolderThroughStandardPicker -BrowseButton $browseOutput -TargetTextBox $outputTextBox -FolderPath $outputDirectory
    $result.assertions.pickerSelectedBothFolders = $true
    Add-ProbeStep -Steps $steps -Name 'SelectedLongUnicodeFoldersThroughRealPickers' -Data ([ordered]@{
        inputValue = Get-UiaValue -Element $inputTextBox
        outputValue = Get-UiaValue -Element $outputTextBox
    })

    Wait-Until -Description 'Start to become enabled after folder validation' -Condition {
        return $startButton.Current.IsEnabled
    }
    $result.assertions.startEnabledAfterSelection = $true

    $start = [System.Windows.Automation.InvokePattern]$startButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $start.Invoke()
    Wait-Until -Description 'the real batch to enter its running state' -Condition {
        return $cancelButton.Current.IsEnabled
    }
    $result.assertions.batchBegan = $true
    Add-ProbeStep -Steps $steps -Name 'RealBatchBegan' -Data ([ordered]@{
        cancelEnabled = $cancelButton.Current.IsEnabled
        startEnabled = $startButton.Current.IsEnabled
    })

    $window = [System.Windows.Automation.WindowPattern]$appRoot.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $window.Close()
    Wait-Until -Description 'the first close confirmation dialog' -Condition {
        return $null -ne (Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::NameProperty) -Value 'Cancel transcription and close?')
    }
    $closeDialog = Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::NameProperty) -Value 'Cancel transcription and close?'
    $keepWorking = Find-UiaElement -Root $closeDialog -Property ([System.Windows.Automation.AutomationElement]::NameProperty) -Value 'Keep working'
    if ($null -eq $keepWorking) {
        throw 'The close confirmation did not expose its Keep working action.'
    }
    $result.assertions.closeConfirmationObserved = $true
    Add-ProbeStep -Steps $steps -Name 'CloseConfirmationObserved' -Data ([ordered]@{
        title = $closeDialog.Current.Name
        keepWorkingName = $keepWorking.Current.Name
    })

    ([System.Windows.Automation.InvokePattern]$keepWorking.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Wait-Until -Description 'the close confirmation to dismiss after Keep working' -Condition {
        return $null -eq (Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::NameProperty) -Value 'Cancel transcription and close?')
    }
    if (-not $cancelButton.Current.IsEnabled -or $process.HasExited) {
        throw 'Keep working did not preserve the active batch.'
    }
    $result.assertions.keepWorkingPreservedRunningBatch = $true
    Add-ProbeStep -Steps $steps -Name 'KeepWorkingPreservedBatch' -Data ([ordered]@{
        cancelEnabled = $cancelButton.Current.IsEnabled
        processExited = $process.HasExited
    })

    $window.Close()
    Wait-Until -Description 'the second close confirmation dialog' -Condition {
        return $null -ne (Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::NameProperty) -Value 'Cancel transcription and close?')
    }
    $closeDialog = Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::NameProperty) -Value 'Cancel transcription and close?'
    $cancelAndClose = Find-UiaElement -Root $closeDialog -Property ([System.Windows.Automation.AutomationElement]::NameProperty) -Value 'Cancel batch and close'
    if ($null -eq $cancelAndClose) {
        throw 'The close confirmation did not expose its Cancel batch and close action.'
    }
    ([System.Windows.Automation.InvokePattern]$cancelAndClose.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Wait-Until -Description 'the app to cancel and close' -Condition {
        $process.Refresh()
        return $process.HasExited
    }
    $result.assertions.cancelAndCloseCompleted = $true
    Add-ProbeStep -Steps $steps -Name 'CancelAndCloseCompleted' -Data ([ordered]@{
        processExited = $process.HasExited
    })

    $temporaryArtifacts = @(Get-ChildItem -LiteralPath $outputDirectory -Recurse -Force -File |
        Where-Object { $_.Name -match '\.tmp($|\.)' -or $_.Name -match '\.part($|\.)' } |
        ForEach-Object { $_.FullName })
    $result.assertions.noTemporaryOutputArtifacts = $temporaryArtifacts.Count -eq 0
    if (-not $result.assertions.noTemporaryOutputArtifacts) {
        throw "Cancellation left temporary output artifacts: $($temporaryArtifacts -join ', ')"
    }
    Add-ProbeStep -Steps $steps -Name 'OutputCleanupChecked' -Data ([ordered]@{
        temporaryArtifacts = $temporaryArtifacts
        finalVttCount = @(Get-ChildItem -LiteralPath $outputDirectory -Recurse -Force -File -Filter *.vtt).Count
    })
}
catch {
    $exitCode = 1
    $result.failure = $_.Exception.Message
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        try {
            Stop-Process -Id $process.Id -ErrorAction Stop
            Add-ProbeStep -Steps $steps -Name 'StoppedProbeProcessAfterFailure' -Data ([ordered]@{ processId = $process.Id })
        }
        catch {
            $result.cleanupFailure = $_.Exception.Message
        }
    }

    $result.completedUtc = [DateTime]::UtcNow.ToString('O')
    $result.status = if ($exitCode -eq 0) { 'passed' } else { 'failed' }
    if (Test-Path -LiteralPath $artifactRootFullPath) {
        $reportPath = Join-Path $artifactRootFullPath 'structural-long-unicode-close.json'
        $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8
        Write-Output "UI Automation UAT report: $reportPath"
    }
}

exit $exitCode
