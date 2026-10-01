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

function Test-MsiLayout {
    # Guards the shortcut/install-location contract: the Start-menu shortcut
    # targets [INSTALLFOLDER]ZuTM.exe, so the app file must be harvested
    # directly into INSTALLFOLDER (regression: heat once put it under a
    # publish-<arch> subdirectory and the shortcut was dead on arrival),
    # and the ARP/shortcut icon must be embedded.
    param([string]$MsiPath)

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.OpenDatabase($MsiPath, 0)
    try {
        function Get-Rows([string]$sql, [int]$fields) {
            $view = $db.OpenView($sql)
            # MSI COM methods return $null; capture so they never leak into
            # this function's output pipeline.
            $null = $view.Execute()
            $rows = @()
            while ($true) {
                $rec = $view.Fetch()
                if (-not $rec) { break }
                $rows += , @(for ($i = 1; $i -le $fields; $i++) { [string]$rec.StringData($i) })
            }
            $null = $view.Close()
            # Leading comma keeps the rows array nested when the pipeline
            # unwraps single-element returns.
            return , $rows
        }

        $shortcut = Get-Rows 'SELECT `Target` FROM `Shortcut` WHERE `Shortcut` = ''ZutmStartMenuShortcut''' 1
        if ($shortcut.Count -ne 1) { throw 'MSI is missing the ZutmStartMenuShortcut shortcut' }
        if ($shortcut[0][0] -ne '[INSTALLFOLDER]ZuTM.exe') { throw "unexpected shortcut target: $($shortcut[0][0])" }

        $exe = Get-Rows 'SELECT `File`.`Component_`, `Component`.`Directory_` FROM `File`, `Component` WHERE `File`.`Component_` = `Component`.`Component` AND `File`.`FileName` = ''ZuTM.exe''' 2
        if ($exe.Count -ne 1) { throw "expected exactly one ZuTM.exe in the File table, found $($exe.Count)" }
        if ($exe[0][1] -ne 'INSTALLFOLDER') { throw "ZuTM.exe installs to directory '$($exe[0][1])', expected INSTALLFOLDER" }

        $icon = Get-Rows 'SELECT `Name` FROM `Icon` WHERE `Name` = ''ZutmIcon''' 1
        if ($icon.Count -ne 1) { throw 'MSI is missing the ZutmIcon icon (ARP/shortcut icon)' }
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($db)
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
    }
    Write-Host 'MSI layout verified: shortcut -> INSTALLFOLDER\ZuTM.exe, icon embedded' -ForegroundColor Green
}

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

    Test-MsiLayout -MsiPath $msi

    # Deterministic digest for the release manifest (the updater verifies this).
    $hash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  ZuTM-$Arch.msi" | Add-Content (Join-Path $artifacts 'SHA256SUMS.txt')
}

if ($Architecture -in 'x64', 'all') { Build-One 'x64' }
if ($Architecture -in 'arm64', 'all') { Build-One 'arm64' }

Write-Host "`nDone. Artifacts in $artifacts" -ForegroundColor Cyan
