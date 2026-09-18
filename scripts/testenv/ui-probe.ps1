# ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.
# Boots the XFCE test image, opens a terminal via Ctrl+Alt+T, types a marker
# command, and captures two screendumps for visual diagnosis.
param(
    [string]$Tag = "probe"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot | Split-Path -Parent
$qemu = "$root\runtimes\qemu\bin\qemu-system-x86_64.exe"
$img  = "$root\testimages\alpine-xfce.qcow2"
$work = "$root\testimages"
$overlay = "$work\ui-$Tag.qcow2"
$serial  = "$work\ui-$Tag-serial.log"
Remove-Item $overlay, $serial, "$work\shot1-$Tag.ppm", "$work\shot2-$Tag.ppm" -ErrorAction SilentlyContinue
& "$root\runtimes\qemu\bin\qemu-img.exe" create -f qcow2 -b $img -F qcow2 $overlay | Out-Null

Start-Process -FilePath $qemu -WindowStyle Hidden -ArgumentList @(
    '-L',"$root\runtimes\qemu\share",'-machine','q35','-m','1024','-smp','2',
    '-display','none','-device','virtio-gpu-pci',
    '-spice','port=5999,addr=127.0.0.1,disable-ticketing=on',
    '-drive',"if=virtio,format=qcow2,file=$overlay",
    '-serial',"file:$serial",
    '-qmp','tcp:127.0.0.1:5555,server=on,wait=off'
)

Start-Sleep -Seconds 60   # let lightdm autologin settle under TCG

$c = New-Object Net.Sockets.TcpClient('127.0.0.1',5555)
$s = $c.GetStream(); $r = New-Object IO.StreamReader($s); $w = New-Object IO.StreamWriter($s); $w.AutoFlush=$true
$null = $r.ReadLine()
$w.WriteLine('{"execute":"qmp_capabilities"}'); $null = $r.ReadLine()

function Qmp([string]$json) { $w.WriteLine($json); for($i=0;$i -lt 60;$i++){ $l=$r.ReadLine(); if($l -match 'return|error'){ Write-Host "QMP: $l"; return $l } } }

Qmp '{"execute":"screendump","arguments":{"filename":"'"$work\\shot1-$Tag.ppm"'"}}' | Out-Null
Start-Sleep -Seconds 90   # capture lands slowly under TCG

# Ctrl+Alt+T then type `touch /tmp/UIOK` + Enter, slowly for TCG
Qmp '{"execute":"send-key","arguments":{"keys":[{"type":"qcode","data":"ctrl"},{"type":"qcode","data":"alt"},{"type":"qcode","data":"t"}]}}' | Out-Null
Start-Sleep -Seconds 25
foreach($ch in 'touch /tmp/UIOK'.ToCharArray()){
    $code = switch -Regex ($ch) { '\s' {'spc'; break} '/' {'slash'; break} default { [string]$ch } }
    Qmp ('{"execute":"send-key","arguments":{"keys":[{"type":"qcode","data":"' + $code + '"}]}}') | Out-Null
    Start-Sleep -Milliseconds 400
}
Qmp '{"execute":"send-key","arguments":{"keys":[{"type":"qcode","data":"ret"}]}}' | Out-Null
Start-Sleep -Seconds 15

Qmp '{"execute":"screendump","arguments":{"filename":"'"$work\\shot2-$Tag.ppm"'"}}' | Out-Null
Start-Sleep -Seconds 90

Get-Item "$work\shot1-$Tag.ppm","$work\shot2-$Tag.ppm" | Select-Object Name,Length
Get-Content $serial -Tail 4
Stop-Process -Name qemu-system-x86_64 -Force
