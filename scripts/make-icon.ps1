# ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
#
# Builds the Windows application icon src/ZuTM.App/Assets/app.ico from
# assets/logo-1024.png (rendered from assets/logo.svg by make-logo-art.ps1).
#
# Sizes 16-128 are stored as classic 32-bit BGRA bitmaps, 256 as PNG
# (Vista+); the result is the maximally compatible ICO layout.

[CmdletBinding()]
param(
    [string]$PngPath = '',
    [string]$OutPath = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PngPath) { $PngPath = Join-Path $repoRoot 'assets\logo-1024.png' }
if (-not $OutPath) { $OutPath = Join-Path $repoRoot 'src\ZuTM.App\Assets\app.ico' }
if (-not (Test-Path $PngPath)) { throw "source PNG not found: $PngPath (run scripts/make-logo-art.ps1 first)" }

$src = New-Object System.Drawing.Bitmap($PngPath)

function Resize-Icon {
    param([System.Drawing.Bitmap]$Source, [int]$Size)
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $dest = New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)
    $g.DrawImage($Source, $dest)
    $g.Dispose()
    return $bmp
}

function ConvertTo-BgraBitmapData {
    # ICO bitmap entry: BITMAPINFOHEADER + bottom-up BGRA rows + empty AND mask.
    param([System.Drawing.Bitmap]$Bmp, [int]$Size)

    $rect = New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)
    $data = $Bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $xor = New-Object byte[] ($stride * $Size)
        $row = New-Object byte[] $stride
        for ($y = 0; $y -lt $Size; $y++) {
            $ptr = [System.IntPtr]::Add($data.Scan0, [System.IntPtr]($y * $stride))
            [System.Runtime.InteropServices.Marshal]::Copy($ptr, $row, 0, $stride)
            [Array]::Copy($row, 0, $xor, ($Size - 1 - $y) * $stride, $stride)
        }
    }
    finally {
        $Bmp.UnlockBits($data)
    }

    $andStride = ((($Size + 31) -shr 5) -shl 2)
    $and = New-Object byte[] ($andStride * $Size)

    $header = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($header)
    $bw.Write([uint32]40)                                  # biSize
    $bw.Write([int32]$Size)                                # biWidth
    $bw.Write([int32]($Size * 2))                          # biHeight (XOR + AND)
    $bw.Write([uint16]1)                                   # biPlanes
    $bw.Write([uint16]32)                                  # biBitCount
    $bw.Write([uint32]0)                                   # biCompression (BI_RGB)
    $bw.Write([uint32]($xor.Length + $and.Length))         # biSizeImage
    $bw.Write([int32]0); $bw.Write([int32]0)               # biXPels/biYPels
    $bw.Write([uint32]0); $bw.Write([uint32]0)             # biClrUsed/biClrImportant
    $bw.Flush()

    $entry = New-Object System.IO.MemoryStream
    $entry.Write($header.ToArray(), 0, $header.Length)
    $entry.Write($xor, 0, $xor.Length)
    $entry.Write($and, 0, $and.Length)
    return $entry.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = New-Object System.Collections.Generic.List[byte[]]
foreach ($s in $sizes) {
    $bmp = Resize-Icon $src $s
    try {
        if ($s -eq 256) {
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $images.Add($ms.ToArray())
        }
        else {
            $images.Add((ConvertTo-BgraBitmapData $bmp $s))
        }
    }
    finally {
        $bmp.Dispose()
    }
}
$src.Dispose()

$outDir = Split-Path -Parent $OutPath
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ico)
$bw.Write([uint16]0)   # reserved
$bw.Write([uint16]1)   # type: icon
$bw.Write([uint16]$images.Count)

$offset = 6 + (16 * $images.Count)
for ($i = 0; $i -lt $images.Count; $i++) {
    $s = $sizes[$i]
    $dirByte = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dirByte)              # width (0 = 256)
    $bw.Write([byte]$dirByte)              # height
    $bw.Write([byte]0)                     # palette count
    $bw.Write([byte]0)                     # reserved
    $bw.Write([uint16]1)                   # planes
    $bw.Write([uint16]32)                  # bit count
    $bw.Write([uint32]$images[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()

[IO.File]::WriteAllBytes($OutPath, $ico.ToArray())
Write-Host ("Wrote {0} ({1} bytes, sizes {2})" -f $OutPath, $ico.Length, ($sizes -join '/'))
