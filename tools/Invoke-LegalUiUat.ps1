[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$AppPath,

    [switch]$ExpectMissingNotices,

    [ValidateRange(10, 120)]
    [int]$TimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-UiaElement {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.AutomationProperty]$Property,
        [object]$Value
    )

    $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new($Property, $Value))
}

function Wait-Until {
    param([string]$Description, [scriptblock]$Condition)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            if (& $Condition) {
                return
            }
        }
        catch {
            # UI Automation elements can become stale while ContentDialog changes content.
        }

        Start-Sleep -Milliseconds 200
    }

    throw "Timed out waiting for $Description."
}

function Find-Named {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)

    Find-UiaElement -Root $Root -Property ([System.Windows.Automation.AutomationElement]::NameProperty) -Value $Name
}

function Invoke-Named {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)

    $element = Find-Named -Root $Root -Name $Name
    if ($null -eq $element) {
        throw "The UI did not expose '$Name'."
    }

    ([System.Windows.Automation.InvokePattern]$element.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

function Select-UiaTab {
    param([System.Windows.Automation.AutomationElement]$Tab)

    $candidate = $Tab
    foreach ($level in 0..4) {
        $pattern = $null
        if ($candidate.TryGetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern,
                [ref]$pattern)) {
            ([System.Windows.Automation.SelectionItemPattern]$pattern).Select()
            return
        }

        if ($candidate.TryGetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern,
                [ref]$pattern)) {
            ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
            return
        }

        $candidate = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($candidate)
        if ($null -eq $candidate) {
            break
        }
    }

    throw "The '$($Tab.Current.Name)' tab does not expose a selection or invoke pattern."
}

function Start-AppAndGetRoot {
    param([System.Collections.Generic.List[System.Diagnostics.Process]]$Processes)

    $process = Start-Process -FilePath ([IO.Path]::GetFullPath($AppPath)) -PassThru
    $Processes.Add($process)
    Wait-Until -Description 'the application window' -Condition {
        $process.Refresh()
        -not $process.HasExited -and $process.MainWindowHandle -ne [IntPtr]::Zero
    }

    [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
}

$processes = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
try {
    # Launch one: inspect both legal tabs, return, accept, and verify Setup-link focus restoration.
    $firstRoot = Start-AppAndGetRoot -Processes $processes
    Wait-Until -Description 'the first-launch acknowledgement' -Condition {
        $null -ne (Find-Named -Root $firstRoot -Name 'Academic Research Use Acknowledgement')
    }
    Invoke-Named -Root $firstRoot -Name 'View License'
    Wait-Until -Description 'the visible legal-information view' -Condition {
        $heading = Find-Named -Root $firstRoot -Name 'License and Third-Party Notices'
        $licenseTab = Find-Named -Root $firstRoot -Name 'Application License'
        $noticesTab = Find-Named -Root $firstRoot -Name 'Third-Party Notices'
        $null -ne $heading -and -not $heading.Current.IsOffscreen -and
            $null -ne $licenseTab -and -not $licenseTab.Current.IsOffscreen -and
            $null -ne $noticesTab -and -not $noticesTab.Current.IsOffscreen
    }
    $thirdPartyTab = Find-Named -Root $firstRoot -Name 'Third-Party Notices'
    Select-UiaTab -Tab $thirdPartyTab
    Wait-Until -Description 'the third-party document text' -Condition {
        $candidate = Find-Named -Root $firstRoot -Name 'Third-Party Notices document text'
        $null -ne $candidate -and -not $candidate.Current.IsOffscreen
    }
    if ($ExpectMissingNotices) {
        $noticesText = Find-Named -Root $firstRoot -Name 'Third-Party Notices document text'
        $value = [string]$noticesText.GetCurrentPropertyValue(
            [System.Windows.Automation.AutomationElement]::ItemStatusProperty)
        $expected = 'Third-party notices are not available in this development build or installation.'
        if ($value -ne $expected) {
            throw "Unexpected missing-notices explanation: '$value'."
        }
    }
    $applicationTab = Find-Named -Root $firstRoot -Name 'Application License'
    Select-UiaTab -Tab $applicationTab
    Wait-Until -Description 'the selectable application-license text' -Condition {
        $candidate = Find-Named -Root $firstRoot -Name 'Application License document text'
        $null -ne $candidate -and -not $candidate.Current.IsOffscreen
    }
    $licenseDocument = Find-Named -Root $firstRoot -Name 'Application License document text'
    $licenseScroller = Find-Named -Root $firstRoot -Name 'Application License scrollable document'
    $documentBounds = $licenseDocument.Current.BoundingRectangle
    $scrollerBounds = $licenseScroller.Current.BoundingRectangle
    if ($documentBounds.Width -le 0 -or $documentBounds.Height -le 0) {
        throw "The application license has empty rendered bounds: document=$documentBounds."
    }
    if ($documentBounds.Left -lt ($scrollerBounds.Left - 1) -or
        $documentBounds.Right -gt ($scrollerBounds.Right + 1)) {
        throw "The application license extends outside its horizontal viewport: document=$documentBounds; viewport=$scrollerBounds."
    }
    $scrollPattern = [System.Windows.Automation.ScrollPattern]$licenseScroller.GetCurrentPattern(
        [System.Windows.Automation.ScrollPattern]::Pattern)
    if (-not $scrollPattern.Current.VerticallyScrollable) {
        throw 'The application license does not expose vertical scrolling.'
    }
    if ($scrollPattern.Current.HorizontallyScrollable) {
        throw 'The application license unexpectedly exposes horizontal scrolling.'
    }
    $initialScrollPercent = $scrollPattern.Current.VerticalScrollPercent
    $scrollPattern.Scroll(
        [System.Windows.Automation.ScrollAmount]::NoAmount,
        [System.Windows.Automation.ScrollAmount]::LargeIncrement)
    Wait-Until -Description 'the application license to scroll vertically' -Condition {
        $scrollPattern.Current.VerticalScrollPercent -gt $initialScrollPercent
    }
    Invoke-Named -Root $firstRoot -Name 'Back to Acknowledgement'
    Wait-Until -Description 'the acknowledgement actions after returning from legal information' -Condition {
        $null -ne (Find-Named -Root $firstRoot -Name 'Accept and Continue')
    }
    Invoke-Named -Root $firstRoot -Name 'Accept and Continue'
    Wait-Until -Description 'the enabled Setup License link' -Condition {
        $candidate = Find-UiaElement -Root $firstRoot -Property (
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty) -Value 'LicenseButton'
        $null -ne $candidate -and $candidate.Current.IsEnabled
    }
    $licenseLink = Find-UiaElement -Root $firstRoot -Property (
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty) -Value 'LicenseButton'
    ([System.Windows.Automation.InvokePattern]$licenseLink.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Wait-Until -Description 'the direct legal-information dialog' -Condition {
        $null -ne (Find-Named -Root $firstRoot -Name 'License and Third-Party Notices')
    }
    Wait-Until -Description 'the direct legal-information Close action' -Condition {
        $null -ne (Find-Named -Root $firstRoot -Name 'Close')
    }
    Invoke-Named -Root $firstRoot -Name 'Close'
    Wait-Until -Description 'focus to return to the Setup License link' -Condition {
        $licenseLink.Current.HasKeyboardFocus
    }
    ([System.Windows.Automation.WindowPattern]$firstRoot.GetCurrentPattern(
            [System.Windows.Automation.WindowPattern]::Pattern)).Close()
    Wait-Until -Description 'the accepted first launch to close' -Condition {
        $processes[0].Refresh()
        $processes[0].HasExited
    }

    # Launch two proves acceptance was not persisted and decline exits.
    $secondRoot = Start-AppAndGetRoot -Processes $processes
    Wait-Until -Description 'the second-launch acknowledgement' -Condition {
        $null -ne (Find-Named -Root $secondRoot -Name 'Academic Research Use Acknowledgement')
    }
    Invoke-Named -Root $secondRoot -Name 'Decline and Exit'
    Wait-Until -Description 'decline to exit the second launch' -Condition {
        $processes[1].Refresh()
        $processes[1].HasExited
    }

    # Launch three verifies closing the window before acceptance also exits.
    $thirdRoot = Start-AppAndGetRoot -Processes $processes
    Wait-Until -Description 'the third-launch acknowledgement' -Condition {
        $null -ne (Find-Named -Root $thirdRoot -Name 'Academic Research Use Acknowledgement')
    }
    ([System.Windows.Automation.WindowPattern]$thirdRoot.GetCurrentPattern(
            [System.Windows.Automation.WindowPattern]::Pattern)).Close()
    Wait-Until -Description 'startup window-close to exit' -Condition {
        $processes[2].Refresh()
        $processes[2].HasExited
    }

    Write-Output 'Legal UI UAT passed: per-launch gating, tabs, acceptance, Setup link, decline, and startup close.'
}
finally {
    foreach ($process in $processes) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force
            }
        }
        catch {
            # Best-effort cleanup after a failed UI Automation assertion.
        }
    }
}
