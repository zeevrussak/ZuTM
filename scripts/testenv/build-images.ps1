# ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
#
# Builds the full E2E testing environment:
#   testimages\alpine-virt.iso           — verified Alpine "virt" ISO
#   testimages\alpine-headless.qcow2     — unattended Alpine install, serial console, root/zutm
#   testimages\alpine-xfce.qcow2         — + XFCE + spice-vdagent + qemu-guest-agent + lightdm autologin
#
# The installs are driven entirely over the VMs' serial consoles by
# tools/ZuTM.TestEnv (no human interaction). Budget ~5 min for headless and
# ~15 min for the UI image under TCG on a dev machine.

[CmdletBinding()]
param(
    [string]$TestImagesDir = "$PSScriptRoot\..\..\testimages",

    [ValidateSet('headless', 'ui', 'all')]
    [string]$Image = 'all'
)

$ErrorActionPreference = 'Stop'
$TestImagesDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($TestImagesDir)
$env:ZUTM_TESTENV_DIR = $TestImagesDir

# QEMU runtime is a prerequisite for everything below.
& "$PSScriptRoot\..\fetch-qemu.ps1" -DestinationRoot "$PSScriptRoot\..\..\runtimes"
if ($LASTEXITCODE -ne 0) { throw 'fetch-qemu failed' }

$targets = switch ($Image) {
    'headless' { @('headless') }
    'ui' { @('ui') }        # ui implies headless when the base is missing
    default { @('headless', 'ui') }
}

foreach ($target in $targets) {
    dotnet run --project "$PSScriptRoot\..\..\tools\ZuTM.TestEnv\ZuTM.TestEnv.csproj" -c Release -- $target
    if ($LASTEXITCODE -ne 0) { throw "building '$target' image failed" }
}

Write-Host "`nTesting environment ready in $TestImagesDir" -ForegroundColor Green
Write-Host 'Run the VM suites with:  $env:ZUTM_E2E=1; dotnet test tests/ZuTM.E2E -c Release' -ForegroundColor Green
