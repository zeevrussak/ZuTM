# ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — Apache License 2.0.
#
# UIA driver for ZuTM: launches the app and walks the New-VM dialog via UI
# Automation (same in-process ordering as human input, so ContentDialog
# deferral and close-path races reproduce faithfully).
#
# Usage: powershell -File scripts\testenv\ui-drive.ps1 -AppExe <ZuTM.exe>
#        [-Name <vm-name>] [-Distro <catalog entry text>] [-TimeoutSeconds 90]
# Exits 0 when the app survived the flow; 2 when the process died (crash).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppExe,
    [string]$Name = 'uia-vm',
    [string]$Distro = '',
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-ById([System.Windows.Automation.AutomationElement]$root, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    try {
        return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    } catch [System.Runtime.InteropServices.COMException] {
        return $null  # UIA tree busy mid-layout — poller will retry
    }
}

function Find-ByName([System.Windows.Automation.AutomationElement]$root, [string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    try {
        return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    } catch [System.Runtime.InteropServices.COMException] {
        return $null
    }
}

function Wait-Element([System.Windows.Automation.AutomationElement]$root, [scriptblock]$probe, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $el = & $probe $root
        if ($el) { return $el }
        Start-Sleep -Milliseconds 300
    }
    throw "element not found within ${seconds}s"
}

# --- launch ---------------------------------------------------------------
$proc = Get-Process -Name ZuTM -ErrorAction SilentlyContinue
if ($proc) { $proc | Stop-Process -Force; Start-Sleep -Seconds 2 }

# The app honors ZUTM_VM_FOLDER (E2E isolation hook in AppSettings): point it
# at a throwaway library unless the caller chose one, so neither this script
# nor CI ever touches the developer's real VM folder.
if (-not $env:ZUTM_VM_FOLDER) {
    $env:ZUTM_VM_FOLDER = Join-Path ([IO.Path]::GetTempPath()) ("zutm-ui-e2e-vms-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $env:ZUTM_VM_FOLDER -Force | Out-Null
}
Write-Host "vm folder: $env:ZUTM_VM_FOLDER"

$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = $AppExe
$start.UseShellExecute = $true
$app = [System.Diagnostics.Process]::Start($start)
Write-Host "launched pid $($app.Id)"

$window = Wait-Element ([System.Windows.Automation.AutomationElement]::RootElement) {
    param($root)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
    $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
} 30
Write-Host "window: $($window.Current.Name)"

# --- New VM dialog ---------------------------------------------------------
$newVm = Wait-Element $window { param($root) Find-ById $root 'NewVmButton' } 20
($newVm.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Write-Host 'New VM opened'

$dlg = Wait-Element $window { param($root) Find-ByName $root 'New Virtual Machine' } 15

# Name field: the TextBox whose header is "Name" (edit inside the dialog)
$nameBox = Wait-Element $dlg { param($root) Find-ById $root 'NameBox' } 10
($nameBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Name)
Write-Host "name set to $Name"

# --- optional distro selection --------------------------------------------
if ($Distro -ne '') {
    # The download source is a collapsed panel: select its radio card first.
    $radio = Wait-Element $dlg { param($root) Find-ById $root 'DownloadSourceRadio' } 10
    ($radio.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    $combo = Wait-Element $dlg { param($root) Find-ByName $root 'Download an official Linux installer ISO' } 10
    $expand = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    $item = Wait-Element $dlg { param($root) Find-ByName $root $Distro } 10
    ($item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    $expand.Collapse()
    Write-Host "distro selected: $Distro"
    # give the resolver a moment like a human would
    Start-Sleep -Seconds 5
}

# --- Create ----------------------------------------------------------------
$create = Wait-Element $dlg { param($root) Find-ByName $root 'Create' } 10
($create.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Write-Host 'Create clicked'

# --- survival watch --------------------------------------------------------
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $alive = -not $app.HasExited
    $newDlg = Find-ByName $window 'New Virtual Machine'
    if (-not $alive) {
        Write-Host "PROCESS DIED (exit code $($app.ExitCode))"
        exit 2
    }
    if (-not $newDlg -and -not (Find-ByName $window 'New Virtual Machine')) {
        # dialog closed — creation finished; app still alive?
        if ($app.HasExited) { Write-Host 'PROCESS DIED after dialog close'; exit 2 }
        Write-Host 'dialog closed, app alive — SUCCESS'
        $app.CloseMainWindow() | Out-Null
        Start-Sleep -Seconds 3
        if (-not $app.HasExited) { $app | Stop-Process -Force }
        exit 0
    }
}

Write-Host 'timeout — dialog never closed'
$app | Stop-Process -Force
exit 3
