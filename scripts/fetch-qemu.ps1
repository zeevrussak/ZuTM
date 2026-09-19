# ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
#
# Downloads the QEMU runtime for Windows into runtimes\qemu, native to the
# host architecture, which ZuTM discovers at runtime and the MSI bundles.
# QEMU is GPLv2 and is fetched as a separate program (see NOTICE).
#
#   x64   hosts → https://qemu.weilnetz.de/w64/      (qemu-w64-setup-*.exe)
#   ARM64 hosts → https://qemu.weilnetz.de/aarch64/  (qemu-arm-setup-*.exe,
#                "QEMU Installer for Windows on ARM")
#
# The builds are InnoSetup executables; we extract the payload with 7-Zip
# instead of running the installer — running it raises a UAC prompt that
# hangs non-interactive CI shells.

[CmdletBinding()]
param(
    [string]$DestinationRoot = "$PSScriptRoot\..\runtimes",

    # Optional pinned installer URL; default = newest for the host arch.
    [string]$InstallerUrl = '',

    # Force a download arch (x64|arm64); default = detect the host.
    [ValidateSet('', 'x64', 'arm64')]
    [string]$Architecture = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $Architecture) {
    # Acceptable ARM64 identifiers per the platform docs.
    $Architecture = if ($env:PROCESSOR_ARCHITECTURE -in @('ARM64', 'ARMv8', 'aarch64')) { 'arm64' } else { 'x64' }
}

$channel = switch ($Architecture) {
    'arm64' { @{ Dir = 'aarch64'; Pattern = 'qemu-arm-setup-(?<ver>\d+)\.exe';  Marker = 'qemu-system-aarch64.exe'; Hash = 'SHA512' } }
    default { @{ Dir = 'w64';      Pattern = 'qemu-w64-setup-(?<ver>\d+)\.exe'; Marker = 'qemu-system-x86_64.exe'; Hash = 'SHA256' } }
}

$targetDir = Join-Path $DestinationRoot 'qemu'
New-Item -ItemType Directory -Force -Path $targetDir, $DestinationRoot | Out-Null

$sevenZip = @(
    "$env:ProgramFiles\7-Zip\7z.exe",
    "${env:ProgramFiles(x86)}\7-Zip\7z.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $sevenZip) { throw '7-Zip not found (required to extract the QEMU installer without running it).' }

if (-not $InstallerUrl) {
    Write-Host "Locating newest QEMU installer for $Architecture ($($channel.Dir))…" -ForegroundColor Cyan
    # qemu.weilnetz.de serves a plain HTML directory index; parse it with a regex.
    $html = (Invoke-WebRequest -Uri "https://qemu.weilnetz.de/$($channel.Dir)/" -UseBasicParsing).Content
    $newest = [regex]::Matches($html, $channel.Pattern) |
        Sort-Object { [long]$_.Groups['ver'].Value } -Descending |
        Select-Object -First 1
    if (-not $newest) { throw "Could not find a QEMU $($Architecture) installer in the qemu.weilnetz.de index." }
    $InstallerUrl = "https://qemu.weilnetz.de/$($channel.Dir)/$($newest.Value)"
}

$fileName = [uri]::UnescapeDataString((Split-Path -Leaf $InstallerUrl))
$downloadPath = Join-Path $env:TEMP $fileName

if (-not (Test-Path $downloadPath)) {
    Write-Host "Downloading $InstallerUrl" -ForegroundColor Cyan
    Invoke-WebRequest $InstallerUrl -OutFile $downloadPath
}

# Integrity: w64 publishes per-asset .sha256 companions, aarch64 .sha512.
$checksumExt = $channel.Hash.ToLowerInvariant()
$checksumUrl = "$InstallerUrl.$checksumExt"
try {
    $checksum = (Invoke-WebRequest -Uri $checksumUrl -UseBasicParsing).Content
    $algorithm = $channel.Hash
    $expected = [regex]::Match($checksum, "[0-9a-fA-F]{64,128}").Value
    if ($expected.Length -ge 64) {
        Write-Host "Verifying $algorithm…" -ForegroundColor Cyan
        $actual = (Get-FileHash $downloadPath -Algorithm $algorithm).Hash.ToLowerInvariant()
        if ($actual -ne $expected.ToLowerInvariant()) {
            Remove-Item $downloadPath -Force
            throw "$algorithm mismatch for $fileName."
        }
        Write-Host "$algorithm verified." -ForegroundColor Green
    }
} catch [System.Net.WebException] {
    Write-Host "No published checksum found at $checksumUrl — skipping verification." -ForegroundColor Yellow
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

# Architecture-specific validation: the channel marker + qemu-img must exist.
$qemuExe = Join-Path $targetDir $channel.Marker
if (-not (Test-Path $qemuExe)) { throw "Expected $qemuExe after extraction — the archive is not a $Architecture QEMU build." }
if (-not (Test-Path (Join-Path $targetDir 'qemu-img.exe'))) { throw 'Expected qemu-img.exe after extraction.' }

Write-Host "QEMU runtime ($Architecture) ready: $qemuExe" -ForegroundColor Green
if ($Architecture -eq 'arm64') {
    Write-Host 'WHPX acceleration for ARM64 guests needs Windows 11 24H2+ with HypervisorPlatform enabled; otherwise TCG.' -ForegroundColor Yellow
}
Write-Host 'SPICE console client: ship remote-viewer.exe (virt-viewer) under runtimes\spice\bin\ — bundled by CI.' -ForegroundColor Yellow
