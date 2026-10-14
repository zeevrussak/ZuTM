# ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
#
# Installer E2E: a real msiexec round trip against a built MSI —
#   1. silent install at the default location (the exact flow a double-click
#      runs; when a previous ZuTM is installed this is also the in-place
#      upgrade the in-app updater relies on — INSTALLDIR redirects are
#      overridden by the major-upgrade location inheritance, so the E2E
#      tests the location users actually get)
#   2. payload present (app + WinUI runtime)
#   3. Start-menu shortcut resolves at the installed exe (the "installer does
#      nothing when launched" regression was a dead shortcut)
#   4. the installed app shows its main window
#   5. uninstall removes the payload
#
# Usage: powershell -File scripts\testenv\installer-e2e.ps1 -Msi artifacts\ZuTM-x64.msi
# Exit 0 = pass, 1 = fail (message on stdout and stderr).
#
# CI: the installer-x64/installer-arm64 jobs run this right after building
# the MSI (their runners are elevated).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Msi
)

$ErrorActionPreference = 'Stop'

# --- preconditions ----------------------------------------------------------
$msiPath = (Resolve-Path $Msi).Path
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]$identity
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Output "FAIL: installer E2E requires an elevated process (per-machine MSI)."
    Write-Error "installer E2E requires an elevated process (per-machine MSI)."
    exit 1
}

$vmFolder = Join-Path ([IO.Path]::GetTempPath()) ("zutm-e2e-installer-vms-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $vmFolder | Out-Null

function Fail([string]$message) {
    Write-Output "FAIL: $message"
    Write-Error $message
    Remove-Item -Recurse -Force $vmFolder -ErrorAction SilentlyContinue
    exit 1
}

Write-Host "installer E2E: $msiPath"

# --- 1. install / upgrade in place -----------------------------------------
# No explicit /l*v: MsiLogging writes the log automatically — exactly the
# real-world double-click scenario this contract protects. (An explicit log
# would suppress the automatic one and the check below would be vacuous.)
$installStarted = Get-Date
$p = Start-Process -FilePath msiexec.exe -ArgumentList @(
    '/i', "`"$msiPath`"", '/qn', '/norestart'
) -Wait -PassThru
if ($p.ExitCode -ne 0) { Fail "msiexec install failed with exit code $($p.ExitCode)" }
Write-Host 'install: ok'

# Automatic logging contract (MsiLogging property): every run of this
# package leaves %TEMP%\MSI*.log behind, so real-world failures — where no
# explicit /l*v is passed — are still diagnosable.
$autoLog = Get-ChildItem ([IO.Path]::GetTempPath()) -Filter 'MSI*.log' -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -ge $installStarted } |
    Select-Object -First 1
if (-not $autoLog) { Fail 'no automatic MSI log (%TEMP%\MSI*.log) appeared after install — MsiLogging missing?' }
Write-Host "automatic log: ok ($($autoLog.Name))"

# --- 2. shortcut (also the authoritative install location — the MSI leaves
#        the registry InstallLocation empty). The package is perMachine, so
#        the shortcut lives in the ALL-USERS Start menu. ---------------------
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'ZuTM\ZuTM.lnk'
if (-not (Test-Path $shortcut)) { Fail "Start-menu shortcut missing at $shortcut" }
$shell = New-Object -ComObject WScript.Shell
try {
    $installDir = $shell.CreateShortcut($shortcut).TargetPath | Split-Path -Parent
} finally {
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
}
if (-not $installDir) { Fail "shortcut at $shortcut has no target" }
Write-Host "shortcut: ok -> $installDir"

# --- 3. payload -------------------------------------------------------------
$exePath = Join-Path $installDir 'ZuTM.exe'
if (-not (Test-Path $exePath)) { Fail "ZuTM.exe missing from $installDir" }
if (-not (Test-Path (Join-Path $installDir 'Microsoft.UI.Xaml.dll'))) {
    Fail 'WinUI runtime not installed alongside the app'
}
Write-Host 'payload: ok'

# --- 4. launch --------------------------------------------------------------
# ZUTM_VM_FOLDER keeps the installed app off the developer's real library.
$env:ZUTM_VM_FOLDER = $vmFolder
$app = Start-Process -FilePath $exePath -WorkingDirectory $installDir -PassThru
$windowSeen = $false
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $app.Refresh()
    if ($app.HasExited) { Fail "installed app exited before showing its window (exit $($app.ExitCode))" }
    if ($app.MainWindowTitle -like '*ZuTM*') { $windowSeen = $true; break }
}
if (-not $windowSeen) { Fail 'installed app did not show its main window within 30 s' }
Write-Host "launch: ok (window '$($app.MainWindowTitle)')"
try { $app | Stop-Process -Force -ErrorAction SilentlyContinue } catch { }
Remove-Item Env:\ZUTM_VM_FOLDER -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# --- 5. uninstall -----------------------------------------------------------
$p = Start-Process -FilePath msiexec.exe -ArgumentList @('/x', "`"$msiPath`"", '/qn') -Wait -PassThru
if ($p.ExitCode -ne 0) { Fail "msiexec uninstall failed with exit code $($p.ExitCode)" }
if (Test-Path $exePath) { Fail 'ZuTM.exe survived uninstall' }
Write-Host 'uninstall: ok'

Remove-Item -Recurse -Force $vmFolder -ErrorAction SilentlyContinue
Write-Host 'installer E2E PASSED' -ForegroundColor Green
exit 0
