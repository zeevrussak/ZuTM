# ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
#
# Builds MSI installers:
#   artifacts/ZuTM-x64.msi
#   artifacts/ZuTM-arm64.msi
#
# These are the assets the in-app updater downloads from GitHub Releases.
#
# Prereqs: .NET 10 SDK. The WiX v6 toolchain + Heat extension restore as
# NuGet packages of the installer project (no global wix CLI needed).

[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64', 'all')]
    [string]$Architecture = 'all',

    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

$version = $null
foreach ($line in Get-Content (Join-Path $repoRoot 'Directory.Build.props')) {
    if ($line -match '<VersionPrefix>([\d\.]+)</VersionPrefix>') {
        $version = $Matches[1]
        break
    }
}
if (-not $version) { throw 'Could not read VersionPrefix from Directory.Build.props' }
Write-Host "ZuTM $version installer build" -ForegroundColor Cyan

function Build-One {
    param([string]$Arch)

    Write-Host "`n=== $Arch ===" -ForegroundColor Cyan
    $publishDir = Join-Path $artifacts "publish-$Arch"

    dotnet publish (Join-Path $repoRoot 'src\ZuTM.App\ZuTM.App.csproj') `
        -c $Configuration -p:Platform=$Arch -r "win-$Arch" `
        --self-contained true `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $Arch" }

    # The WiX project harvests the publish folder (WixToolset.Heat) and
    # produces the per-architecture MSI: ZuTM-<arch>.msi.
    dotnet build (Join-Path $repoRoot 'installer\ZuTM.Installer\ZuTM.Installer.wixproj') `
        -c $Configuration -p:Platform=$Arch `
        "-p:PublishDir=$publishDir\" `
        "-p:InstallerVersion=$version" `
        "-p:OutputPath=$artifacts\"
    if ($LASTEXITCODE -ne 0) { throw "installer build failed for $Arch" }

    $msi = Join-Path $artifacts "ZuTM-$Arch.msi"
    if (-not (Test-Path $msi)) { throw "expected $msi but it was not produced" }
    Write-Host "Built $msi" -ForegroundColor Green

    # Deterministic digest for the release manifest (the updater verifies this).
    $hash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  ZuTM-$Arch.msi" | Add-Content (Join-Path $artifacts 'SHA256SUMS.txt')
}

if ($Architecture -in 'x64', 'all') { Build-One 'x64' }
if ($Architecture -in 'arm64', 'all') { Build-One 'arm64' }

Write-Host "`nDone. Artifacts in $artifacts" -ForegroundColor Cyan
