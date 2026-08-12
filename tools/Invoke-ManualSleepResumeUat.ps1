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

    [ValidateRange(3, 30)]
    [int]$BatchFileCount = 30,

    [ValidateRange(1, 30)]
    [int]$TransitionTimeoutMinutes = 10,

    [ValidateRange(5, 120)]
    [int]$UiTimeoutSeconds = 30
)

<#
.SYNOPSIS
    Records manual, literal S3 sleep/resume evidence for a published WinBulkTranscript app.

.DESCRIPTION
    This probe never invokes a sleep, hibernate, shutdown, wake, scheduled-task, or power-setting API.
    It creates only a new probe-owned artifact root, starts a real batch through the published app's
    folder pickers, and waits for a human to put the computer into S3 standby and wake it locally.

    A pass requires new System-log evidence after the run's baseline record ID:
      * Microsoft-Windows-Kernel-Power event 42 with TargetState=4 (S3), and
      * a later Kernel-Power event 107 or Power-Troubleshooter event 1.

    It also requires the running batch to remain active after resume, then exercises the app's real
    close confirmation/cancellation path and checks the probe-owned output tree for temporary files.
    It is intentionally not a simulation harness; absence of those OS records is a failed manual run.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Wait-Until {
    param(
        [Parameter(Mandatory)]
        [string]$Description,

        [Parameter(Mandatory)]
        [scriptblock]$Condition,

        [Parameter(Mandatory)]
        [int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            if (& $Condition) {
                return
            }
        }
        catch {
            # UIA elements are briefly unavailable while the picker/dialog changes state.
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

function Compact-Message {
    param([AllowNull()][string]$Message)

    if ([string]::IsNullOrWhiteSpace($Message)) {
        return $null
    }

    $compact = ($Message -replace '\s+', ' ').Trim()
    return if ($compact.Length -le 500) { $compact } else { $compact.Substring(0, 499) + '…' }
}

function Get-EventDataMap {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Eventing.Reader.EventRecord]$Event
    )

    $values = @{}
    [xml]$xml = $Event.ToXml()
    foreach ($data in @($xml.Event.EventData.Data)) {
        if ($null -ne $data -and -not [string]::IsNullOrWhiteSpace([string]$data.Name)) {
            $values[[string]$data.Name] = [string]$data.'#text'
        }
    }

    return $values
}

function Get-PowerEventEvidence {
    param(
        [Parameter(Mandatory)]
        [long]$BaselineRecordId
    )

    $events = @(
        Get-WinEvent -LogName System -MaxEvents 8192 -ErrorAction Stop |
            Where-Object {
                $_.RecordId -gt $BaselineRecordId -and (
                    ($_.ProviderName -eq 'Microsoft-Windows-Kernel-Power' -and $_.Id -in @(42, 107)) -or
                    ($_.ProviderName -eq 'Microsoft-Windows-Power-Troubleshooter' -and $_.Id -eq 1)
                )
            }
    )

    return $events | Sort-Object RecordId
}

function Find-RealS3ResumePair {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Diagnostics.Eventing.Reader.EventRecord[]]$Events
    )

    $s3Sleeps = @(
        $Events |
            Where-Object {
                if ($_.ProviderName -ne 'Microsoft-Windows-Kernel-Power' -or $_.Id -ne 42) {
                    return $false
                }

                $data = Get-EventDataMap -Event $_
                return $data['TargetState'] -eq '4'
            } |
            Sort-Object TimeCreated, RecordId
    )
    $resumes = @(
        $Events |
            Where-Object {
                ($_.ProviderName -eq 'Microsoft-Windows-Kernel-Power' -and $_.Id -eq 107) -or
                ($_.ProviderName -eq 'Microsoft-Windows-Power-Troubleshooter' -and $_.Id -eq 1)
            } |
            Sort-Object TimeCreated, RecordId
    )

    foreach ($sleep in $s3Sleeps) {
        $resume = $resumes |
            Where-Object { $_.TimeCreated.ToUniversalTime() -ge $sleep.TimeCreated.ToUniversalTime() } |
            Select-Object -First 1
        if ($null -ne $resume) {
            return [pscustomobject]@{
                Sleep = $sleep
                Resume = $resume
            }
        }
    }

    return $null
}

function Convert-PowerEvent {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Eventing.Reader.EventRecord]$Event
    )

    $data = Get-EventDataMap -Event $Event
    return [ordered]@{
        recordId = [long]$Event.RecordId
        provider = $Event.ProviderName
        id = [int]$Event.Id
        timeUtc = $Event.TimeCreated.ToUniversalTime().ToString('O')
        targetState = $data['TargetState']
        effectiveState = $data['EffectiveState']
        message = Compact-Message -Message $Event.FormatDescription()
    }
}

function Select-FolderThroughStandardPicker {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$AppRoot,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$BrowseButton,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$TargetTextBox,

        [Parameter(Mandatory)]
        [string]$FolderPath,

        [Parameter(Mandatory)]
        [int]$TimeoutSeconds
    )

    $invoke = [System.Windows.Automation.InvokePattern]$BrowseButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    Wait-Until -Description 'the standard Select Folder dialog' -TimeoutSeconds $TimeoutSeconds -Condition {
        return $null -ne (Find-UiaElement -Root $AppRoot -Property ([System.Windows.Automation.AutomationElement]::NameProperty) -Value 'Select Folder')
    }

    $dialog = Find-UiaElement -Root $AppRoot -Property ([System.Windows.Automation.AutomationElement]::NameProperty) -Value 'Select Folder'
    $dialogHandle = [IntPtr]$dialog.Current.NativeWindowHandle
    $folderEdit = [WinBulkTranscriptManualSleepResumeNative]::GetDlgItem($dialogHandle, 1152)
    $selectFolderButton = [WinBulkTranscriptManualSleepResumeNative]::GetDlgItem($dialogHandle, 1)
    if ($dialogHandle -eq [IntPtr]::Zero -or $folderEdit -eq [IntPtr]::Zero -or $selectFolderButton -eq [IntPtr]::Zero) {
        throw 'The standard picker did not expose its expected Folder edit and Select Folder controls.'
    }

    [void][WinBulkTranscriptManualSleepResumeNative]::SendMessage($folderEdit, 0x000C, [IntPtr]::Zero, $FolderPath)
    [void][WinBulkTranscriptManualSleepResumeNative]::SendMessage($folderEdit, 0x0100, [IntPtr]13, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 250
    [void][WinBulkTranscriptManualSleepResumeNative]::SendMessage($selectFolderButton, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-Until -Description "the selected folder '$FolderPath' in the app" -TimeoutSeconds $TimeoutSeconds -Condition {
        return (Get-UiaValue -Element $TargetTextBox) -eq $FolderPath
    }
}

function Get-AppRoot {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    $Process.Refresh()
    if ($Process.HasExited -or $Process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'The published app is no longer running with a main window.'
    }

    return [System.Windows.Automation.AutomationElement]::FromHandle($Process.MainWindowHandle)
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

if ($null -eq ('WinBulkTranscriptManualSleepResumeNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WinBulkTranscriptManualSleepResumeNative
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetDlgItem(IntPtr dialog, int itemId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, string lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
'@
}

$steps = [System.Collections.Generic.List[object]]::new()
$result = [ordered]@{
    schemaVersion = 1
    status = 'running'
    startedUtc = [DateTime]::UtcNow.ToString('O')
    scenario = 'PublishedX64ManualLiteralS3SleepResume'
    scope = [ordered]@{
        manualRealS3TransitionRequired = $true
        scriptInvokesPowerTransition = $false
        changesGlobalPowerSettings = $false
        acceptsSimulatedPowerEvents = $false
        notes = 'A passing report requires post-baseline Kernel-Power 42 TargetState=4 plus a later Kernel-Power 107 or Power-Troubleshooter 1 record. The operator must initiate and wake S3 manually.'
    }
    app = [ordered]@{
        path = $appFullPath
        sha256 = (Get-FileHash -LiteralPath $appFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    fixture = [ordered]@{
        sourcePath = $fixtureFullPath
        sha256 = (Get-FileHash -LiteralPath $fixtureFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        copiedFileCount = $BatchFileCount
    }
    transition = [ordered]@{
        timeoutMinutes = $TransitionTimeoutMinutes
        systemLogBaselineRecordId = $null
        armedUtc = $null
        observedUtc = $null
        realS3SleepObserved = $false
        realResumeObserved = $false
        transitionDurationSeconds = $null
        sleepEvent = $null
        resumeEvent = $null
        postBaselinePowerEvents = @()
    }
    assertions = [ordered]@{
        probeOwnedDirectoriesCreated = $false
        pickerSelectedBothFolders = $false
        batchBeganBeforeManualSleep = $false
        processAliveAfterResume = $false
        batchStillRunningAfterResume = $false
        closeConfirmationObservedAfterResume = $false
        cancelAndCloseCompletedAfterResume = $false
        noTemporaryOutputArtifacts = $false
    }
    paths = [ordered]@{}
    powerCapabilities = $null
    steps = $steps
    failure = $null
    cleanupFailure = $null
}

$process = $null
$appRoot = $null
$exitCode = 0
try {
    [void][IO.Directory]::CreateDirectory($artifactRootFullPath)
    $inputDirectory = Join-Path $artifactRootFullPath 'input'
    $outputDirectory = Join-Path $artifactRootFullPath 'output'
    [void][IO.Directory]::CreateDirectory($inputDirectory)
    [void][IO.Directory]::CreateDirectory($outputDirectory)
    foreach ($index in 1..$BatchFileCount) {
        [IO.File]::Copy($fixtureFullPath, (Join-Path $inputDirectory ('sleep-resume-control-{0:D2}.mp4' -f $index)))
    }

    $result.paths = [ordered]@{
        artifactRoot = $artifactRootFullPath
        inputDirectory = $inputDirectory
        outputDirectory = $outputDirectory
    }
    $result.assertions.probeOwnedDirectoriesCreated = $true
    Add-ProbeStep -Steps $steps -Name 'CreatedProbeOwnedInputAndOutput' -Data $result.paths

    $powercfg = & powercfg /a 2>&1
    $result.powerCapabilities = [ordered]@{
        s3Advertised = (($powercfg -join [Environment]::NewLine) -match 'Standby \(S3\)')
        output = @($powercfg | ForEach-Object { [string]$_ })
    }

    $baselineEvent = Get-WinEvent -LogName System -MaxEvents 1 -ErrorAction Stop | Select-Object -First 1
    if ($null -eq $baselineEvent -or $null -eq $baselineEvent.RecordId) {
        throw 'Could not establish a System event-log record ID baseline.'
    }
    $result.transition.systemLogBaselineRecordId = [long]$baselineEvent.RecordId
    Add-ProbeStep -Steps $steps -Name 'CapturedSystemLogBaseline' -Data ([ordered]@{
        recordId = $result.transition.systemLogBaselineRecordId
        timeUtc = $baselineEvent.TimeCreated.ToUniversalTime().ToString('O')
    })

    $process = Start-Process -FilePath $appFullPath -PassThru
    Wait-Until -Description 'the published app main window' -TimeoutSeconds $UiTimeoutSeconds -Condition {
        $process.Refresh()
        return -not $process.HasExited -and $process.MainWindowHandle -ne [IntPtr]::Zero
    }
    $appRoot = Get-AppRoot -Process $process
    if ($appRoot.Current.Name -ne 'WinBulkTranscript by Jamieson Lab') {
        throw "Unexpected application window name '$($appRoot.Current.Name)'."
    }

    $inputTextBox = Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) -Value 'InputFolderTextBox'
    $browseInput = Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) -Value 'BrowseInputButton'
    $outputTextBox = Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) -Value 'OutputFolderTextBox'
    $browseOutput = Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) -Value 'BrowseOutputButton'
    $startButton = Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) -Value 'StartButton'
    if ($null -in @($inputTextBox, $browseInput, $outputTextBox, $browseOutput, $startButton)) {
        throw 'The published app did not expose all folder and start controls through UI Automation.'
    }

    Select-FolderThroughStandardPicker -AppRoot $appRoot -BrowseButton $browseInput -TargetTextBox $inputTextBox -FolderPath $inputDirectory -TimeoutSeconds $UiTimeoutSeconds
    Select-FolderThroughStandardPicker -AppRoot $appRoot -BrowseButton $browseOutput -TargetTextBox $outputTextBox -FolderPath $outputDirectory -TimeoutSeconds $UiTimeoutSeconds
    $result.assertions.pickerSelectedBothFolders = $true
    Add-ProbeStep -Steps $steps -Name 'SelectedProbeOwnedFoldersThroughRealPickers' -Data ([ordered]@{
        inputValue = Get-UiaValue -Element $inputTextBox
        outputValue = Get-UiaValue -Element $outputTextBox
    })

    Wait-Until -Description 'Start to become enabled after folder validation' -TimeoutSeconds $UiTimeoutSeconds -Condition { $startButton.Current.IsEnabled }
    ([System.Windows.Automation.InvokePattern]$startButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    $cancelButton = Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) -Value 'CancelButton'
    if ($null -eq $cancelButton) {
        throw 'The published app did not expose its named Cancel button through UI Automation.'
    }
    Wait-Until -Description 'the real batch to enter its running state' -TimeoutSeconds $UiTimeoutSeconds -Condition { $cancelButton.Current.IsEnabled }
    $result.assertions.batchBeganBeforeManualSleep = $true
    Add-ProbeStep -Steps $steps -Name 'RealBatchBeganBeforeManualSleep' -Data ([ordered]@{
        cancelEnabled = $cancelButton.Current.IsEnabled
        processId = $process.Id
    })

    $result.transition.armedUtc = [DateTime]::UtcNow.ToString('O')
    Add-ProbeStep -Steps $steps -Name 'ManualS3TransitionArmed' -Data ([ordered]@{
        instructions = 'Within the configured timeout, use the physical power control or Windows power menu to put this test machine into S3 standby. Wake it locally. This script does not invoke any sleep API and will continue only after post-baseline OS events prove a real S3 transition.'
        timeoutMinutes = $TransitionTimeoutMinutes
    })
    Write-Host ''
    Write-Host 'Manual action required: put this test machine into S3 standby using its normal physical/Windows control, then wake it locally.' -ForegroundColor Yellow
    Write-Host "The observer waits up to $TransitionTimeoutMinutes minute(s) and will only pass on real post-baseline Windows power events. It does not invoke a sleep API." -ForegroundColor Yellow

    $deadline = [DateTime]::UtcNow.AddMinutes($TransitionTimeoutMinutes)
    $pair = $null
    $postBaselinePowerEvents = @()
    while ([DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) {
            throw 'The published app exited before a real sleep/resume transition was observed.'
        }

        $postBaselinePowerEvents = @(Get-PowerEventEvidence -BaselineRecordId $result.transition.systemLogBaselineRecordId)
        $pair = Find-RealS3ResumePair -Events $postBaselinePowerEvents
        if ($null -ne $pair) {
            break
        }

        Start-Sleep -Seconds 2
    }

    $result.transition.postBaselinePowerEvents = @($postBaselinePowerEvents | ForEach-Object { Convert-PowerEvent -Event $_ })
    if ($null -eq $pair) {
        throw "No real post-baseline S3/resume event pair was observed within $TransitionTimeoutMinutes minute(s). The run is not acceptance evidence; do not replace it with simulated power events."
    }

    $result.transition.observedUtc = [DateTime]::UtcNow.ToString('O')
    $result.transition.realS3SleepObserved = $true
    $result.transition.realResumeObserved = $true
    $result.transition.sleepEvent = Convert-PowerEvent -Event $pair.Sleep
    $result.transition.resumeEvent = Convert-PowerEvent -Event $pair.Resume
    $result.transition.transitionDurationSeconds = [Math]::Round(($pair.Resume.TimeCreated.ToUniversalTime() - $pair.Sleep.TimeCreated.ToUniversalTime()).TotalSeconds, 3)
    Add-ProbeStep -Steps $steps -Name 'RealS3SleepResumeObserved' -Data ([ordered]@{
        sleep = $result.transition.sleepEvent
        resume = $result.transition.resumeEvent
        durationSeconds = $result.transition.transitionDurationSeconds
    })

    $appRoot = Get-AppRoot -Process $process
    $cancelButton = Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) -Value 'CancelButton'
    $result.assertions.processAliveAfterResume = -not $process.HasExited
    $result.assertions.batchStillRunningAfterResume = $null -ne $cancelButton -and $cancelButton.Current.IsEnabled
    if (-not $result.assertions.processAliveAfterResume -or -not $result.assertions.batchStillRunningAfterResume) {
        throw 'The published batch was not still active after the real S3 resume.'
    }
    Add-ProbeStep -Steps $steps -Name 'PublishedBatchStillActiveAfterResume' -Data ([ordered]@{
        processId = $process.Id
        cancelEnabled = $cancelButton.Current.IsEnabled
    })

    $window = [System.Windows.Automation.WindowPattern]$appRoot.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $window.Close()
    Wait-Until -Description 'the post-resume close confirmation dialog' -TimeoutSeconds $UiTimeoutSeconds -Condition {
        return $null -ne (Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::NameProperty) -Value 'Cancel transcription and close?')
    }
    $closeDialog = Find-UiaElement -Root $appRoot -Property ([System.Windows.Automation.AutomationElement]::NameProperty) -Value 'Cancel transcription and close?'
    $cancelAndClose = Find-UiaElement -Root $closeDialog -Property ([System.Windows.Automation.AutomationElement]::NameProperty) -Value 'Cancel batch and close'
    if ($null -eq $cancelAndClose) {
        throw 'The post-resume close confirmation did not expose its Cancel batch and close action.'
    }
    $result.assertions.closeConfirmationObservedAfterResume = $true
    ([System.Windows.Automation.InvokePattern]$cancelAndClose.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Wait-Until -Description 'the app to cancel and close after resume' -TimeoutSeconds $UiTimeoutSeconds -Condition {
        $process.Refresh()
        return $process.HasExited
    }
    $result.assertions.cancelAndCloseCompletedAfterResume = $true
    Add-ProbeStep -Steps $steps -Name 'PostResumeCancelAndCloseCompleted' -Data ([ordered]@{
        processExited = $process.HasExited
    })

    $temporaryArtifacts = @(Get-ChildItem -LiteralPath $outputDirectory -Recurse -Force -File |
        Where-Object { $_.Name -match '\.tmp($|\.)' -or $_.Name -match '\.part($|\.)' } |
        ForEach-Object { $_.FullName })
    $result.assertions.noTemporaryOutputArtifacts = $temporaryArtifacts.Count -eq 0
    if (-not $result.assertions.noTemporaryOutputArtifacts) {
        throw "Cancellation after resume left temporary output artifacts: $($temporaryArtifacts -join ', ')"
    }
    Add-ProbeStep -Steps $steps -Name 'PostResumeOutputCleanupChecked' -Data ([ordered]@{
        temporaryArtifacts = $temporaryArtifacts
        finalVttCount = @(Get-ChildItem -LiteralPath $outputDirectory -Recurse -Force -File -Filter *.vtt).Count
    })
}
catch {
    $exitCode = 1
    $result.failure = $_.Exception.Message
}
finally {
    if ($null -ne $process) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -ErrorAction Stop
                Add-ProbeStep -Steps $steps -Name 'StoppedProbeProcessAfterIncompleteRun' -Data ([ordered]@{ processId = $process.Id })
            }
        }
        catch {
            $result.cleanupFailure = $_.Exception.Message
        }
    }

    $result.completedUtc = [DateTime]::UtcNow.ToString('O')
    $result.status = if ($exitCode -eq 0) { 'passed' } else { 'failed' }
    if (Test-Path -LiteralPath $artifactRootFullPath) {
        $reportPath = Join-Path $artifactRootFullPath 'manual-literal-s3-sleep-resume.json'
        $result | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $reportPath -Encoding utf8
        Write-Output "Manual S3 sleep/resume UAT report: $reportPath"
    }
}

exit $exitCode
