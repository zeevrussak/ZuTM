# ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
#
# Renders raster brand art from assets/logo.svg using headless Edge:
#   assets/logo-1024.png       master raster (transparent background)
#   assets/logo-256.png        README / GitHub page embed
#   assets/social-preview.png  1280x640 GitHub repository social card
#
# Generated PNGs are committed, so Edge is only needed when logo.svg changes.

[CmdletBinding()]
param(
    [string]$EdgePath = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $EdgePath)) { throw "Edge not found at $EdgePath" }

$repoRoot = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $repoRoot 'assets'
$svgUri = 'file:///' + ((Join-Path $assets 'logo.svg') -replace '\\', '/')

function Invoke-EdgeScreenshot {
    param([int]$Width, [int]$Height, [string]$Html, [string]$OutFile)

    $tmp = Join-Path $env:TEMP ("zutm-logo-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $htmlPath = Join-Path $tmp 'render.html'
        [IO.File]::WriteAllText($htmlPath, $Html)
        & $EdgePath `
            --headless=new --disable-gpu --no-first-run --hide-scrollbars `
            --force-device-scale-factor=1 --default-background-color=00000000 `
            --user-data-dir="$tmp\profile" `
            --screenshot="$OutFile" `
            --window-size="$Width,$Height" `
            ("file:///" + ($htmlPath -replace '\\', '/')) | Out-Null
        if (-not (Test-Path $OutFile)) { throw "Edge did not produce $OutFile" }
    }
    finally {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    }
}

function New-SquareHtml {
    param([int]$Size)
    $img = '<img src="' + $svgUri + '" width="' + $Size + '" height="' + $Size + '">'
    return '<!doctype html><html><head><style>html,body{margin:0;padding:0;background:transparent}</style></head><body>' + $img + '</body></html>'
}

Invoke-EdgeScreenshot 1024 1024 (New-SquareHtml 1024) (Join-Path $assets 'logo-1024.png')
Write-Host 'Wrote assets/logo-1024.png'

Invoke-EdgeScreenshot 256 256 (New-SquareHtml 256) (Join-Path $assets 'logo-256.png')
Write-Host 'Wrote assets/logo-256.png'

$socialHtml = @"
<!doctype html><html><head><meta charset="utf-8"><style>
html,body{margin:0;padding:0;width:1280px;height:640px}
body{display:flex;align-items:center;background:linear-gradient(135deg,#223047 0%,#0B1220 100%);
     font-family:'Segoe UI',sans-serif;color:#E2E8F0}
img{width:420px;height:420px;margin-left:88px}
h1{font-size:132px;margin:0;font-weight:600;letter-spacing:-2px}
p{font-size:38px;line-height:1.4;margin:16px 0 0 6px;color:#94A3B8;font-weight:400}
</style></head><body>
<img src="$svgUri">
<div><h1>ZuTM</h1><p>Virtual machines for Windows<br>UTM-compatible &#183; QEMU-backed</p></div>
</body></html>
"@
Invoke-EdgeScreenshot 1280 640 $socialHtml (Join-Path $assets 'social-preview.png')
Write-Host 'Wrote assets/social-preview.png'
