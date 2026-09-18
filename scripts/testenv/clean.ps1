# ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
# Removes the E2E testing environment (ISO + VM images).

[CmdletBinding()]
param(
    [string]$TestImagesDir = "$PSScriptRoot\..\..\testimages"
)

$ErrorActionPreference = 'Stop'
$TestImagesDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($TestImagesDir)

if (Test-Path $TestImagesDir) {
    Remove-Item $TestImagesDir -Recurse -Force
    Write-Host "removed $TestImagesDir"
} else {
    Write-Host 'nothing to remove'
}
