# ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
#
# Downloads the QEMU runtime for Windows into runtimes\qemu (bin\ + share\),
# which ZuTM discovers at runtime and the MSI bundles. QEMU is GPLv2 and is
# fetched as a separate program (see NOTICE).
#
# Windows ARM64 note: official QEMU Windows builds are x64-only today. On
# ARM64 hosts the x64 build runs under emulation; WHPX-accelerated ARM64
# guests will switch to native builds when they ship.

[CmdletBinding()]
param(
    [string]$DestinationRoot = "$PSScriptRoot\..\runtimes",

    # Optional pinned installer URL; default = newest from qemu.weilnetz.de.
    [string]$InstallerUrl = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$targetDir = Join-Path $DestinationRoot 'qemu'
New-Item -ItemType Directory -Force -Path $targetDir, $DestinationRoot | Out-Null

if (-not $InstallerUrl) {
    Write-Host 'Locating newest QEMU Windows installer…' -ForegroundColor Cyan
    $index = Invoke-RestMethod 'https://qemu.weilnetz.de/w64/'
    $newest = ($index.links | Where-Object href -match '^qemu-w64-setup-[\d]+\.exe$' |
        Sort-Object { [version]($_.href -replace '^qemu-w64-setup-|\.exe$', '') } -Descending |
        Select-Object -First 1).href
    if (-not $newest) { throw 'Could not find a QEMU installer in the qemu.weilnetz.de index.' }
    $InstallerUrl = "https://qemu.weilnetz.de/w64/$newest"
}

$fileName = [uri]::UnescapeDataString((Split-Path -Leaf $InstallerUrl))
$downloadPath = Join-Path $env:TEMP $fileName

Write-Host "Downloading $InstallerUrl" -ForegroundColor Cyan
Invoke-WebRequest $InstallerUrl -OutFile $downloadPath

Write-Host 'Installing silently (InnoSetup)…' -ForegroundColor Cyan
$installDir = Join-Path $targetDir 'extracted'
$process = Start-Process -FilePath $downloadPath -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=`"$installDir`"" -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "QEMU installer exited with $($process.ExitCode)" }

# Normalize to runtimes/qemu/{bin,share}
foreach ($folder in 'bin', 'share') {
    $source = Join-Path $installDir $folder
    $dest = Join-Path $targetDir $folder
    if (Test-Path $source) {
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
        Move-Item $source $dest
    }
}
Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue

$qemuExe = Join-Path $targetDir 'bin\qemu-system-x86_64.exe'
if (-not (Test-Path $qemuExe)) { throw "Expected $qemuExe after install" }

Write-Host "QEMU runtime ready: $qemuExe" -ForegroundColor Green
Write-Host 'SPICE console client: ship remote-viewer.exe (virt-viewer) under runtimes\spice\bin\ — bundled by CI.' -ForegroundColor Yellow
