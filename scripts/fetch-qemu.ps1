# ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
#
# Downloads the QEMU runtime for Windows into runtimes\qemu (bin\ + share\),
# which ZuTM discovers at runtime and the MSI bundles. QEMU is GPLv2 and is
# fetched as a separate program (see NOTICE).
#
# The qemu.weilnetz.de build is an InnoSetup executable; we extract its
# payload with 7-Zip instead of running the installer — running it raises a
# UAC prompt that hangs non-interactive CI shells.

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

$sevenZip = @(
    "$env:ProgramFiles\7-Zip\7z.exe",
    "${env:ProgramFiles(x86)}\7-Zip\7z.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $sevenZip) { throw '7-Zip not found (required to extract the QEMU installer without running it).' }

if (-not $InstallerUrl) {
    Write-Host 'Locating newest QEMU Windows installer…' -ForegroundColor Cyan
    # qemu.weilnetz.de serves a plain HTML directory index; parse it with a regex.
    $html = (Invoke-WebRequest -Uri 'https://qemu.weilnetz.de/w64/' -UseBasicParsing).Content
    $newest = [regex]::Matches($html, 'qemu-w64-setup-(?<ver>\d+)\.exe') |
        Sort-Object { [long]$_.Groups['ver'].Value } -Descending |
        Select-Object -First 1
    if (-not $newest) { throw 'Could not find a QEMU installer in the qemu.weilnetz.de index.' }
    $InstallerUrl = "https://qemu.weilnetz.de/w64/$($newest.Value)"
}

$fileName = [uri]::UnescapeDataString((Split-Path -Leaf $InstallerUrl))
$downloadPath = Join-Path $env:TEMP $fileName

if (-not (Test-Path $downloadPath)) {
    Write-Host "Downloading $InstallerUrl" -ForegroundColor Cyan
    Invoke-WebRequest $InstallerUrl -OutFile $downloadPath
}

Write-Host 'Extracting QEMU payload with 7-Zip (no installer execution)…' -ForegroundColor Cyan
$extractDir = Join-Path $env:TEMP "qemu-extract-$([IO.Path]::GetFileNameWithoutExtension($fileName))"
if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
& $sevenZip x -y "-o$extractDir" $downloadPath | Out-Null
if ($LASTEXITCODE -ne 0) { throw "7-Zip extraction failed with $LASTEXITCODE" }

# Keep the installer's flat layout: executables and DLLs at the archive
# root, firmware under share\, QEMU modules under lib\. QEMU resolves its
# modules relative to the executable — moving the exes into a bin\ subdir
# breaks modular devices (QXL and friends silently vanish).
Get-ChildItem $targetDir -File | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem $extractDir -File |
    Where-Object Name -notin @('install_script.iss') |
    ForEach-Object { Move-Item $_.FullName (Join-Path $targetDir $_.Name) -Force }

foreach ($folder in 'share', 'lib', 'python') {
    $source = Join-Path $extractDir $folder
    if (Test-Path $source) {
        $dest = Join-Path $targetDir $folder
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
        Move-Item $source $dest
    }
}
Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue

$qemuExe = Join-Path $targetDir 'qemu-system-x86_64.exe'
if (-not (Test-Path $qemuExe)) { throw "Expected $qemuExe after extraction" }

Write-Host "QEMU runtime ready: $qemuExe" -ForegroundColor Green
Write-Host 'SPICE console client: ship remote-viewer.exe (virt-viewer) under runtimes\spice\bin\ — bundled by CI.' -ForegroundColor Yellow
