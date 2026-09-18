# ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
#
# Downloads and SHA-256-verifies the Alpine "virt" ISO used by the E2E VM
# suites into testimages\. Idempotent.

[CmdletBinding()]
param(
    [string]$TestImagesDir = "$PSScriptRoot\..\..\testimages"
)

$ErrorActionPreference = 'Stop'
$env:ZUTM_TESTENV_DIR = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($TestImagesDir)

dotnet run --project "$PSScriptRoot\..\..\tools\ZuTM.TestEnv\ZuTM.TestEnv.csproj" -c Release -- iso
if ($LASTEXITCODE -ne 0) { throw "ISO download failed" }
