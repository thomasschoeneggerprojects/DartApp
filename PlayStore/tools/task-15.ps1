$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Write-Host "== task-15 $(Get-Date)"
$root = Resolve-Path "$PSScriptRoot\..\.."
$adb = 'C:\DartForge\AndroidSdk\platform-tools\adb.exe'
$shots = Join-Path $root 'PlayStore\screenshots\raw'
$tmp = Join-Path $shots '_probe.png'
function Shot($n) { Start-Sleep -Milliseconds 2500; & $adb emu screenrecord screenshot "$shots\$n.png" | Out-Null; Write-Host "shot $n $(Get-Date -Format HH:mm:ss)" }
function Tap($x,$y) { & $adb shell input tap $x $y; Start-Sleep -Milliseconds 2000 }
function IsDark { & $adb emu screenrecord screenshot $tmp | Out-Null; Start-Sleep -Milliseconds 300
  $b = [System.Drawing.Bitmap]::FromFile($tmp); $p = $b.GetPixel(60, 1600); $b.Dispose(); return (($p.R + $p.G + $p.B) / 3) -lt 90 }
for ($i=0; $i -lt 40; $i++) { if (IsDark) { break }; Start-Sleep 3 }
Write-Host "home ready after ~$($i*3)s: $(IsDark)"
Start-Sleep 3
Shot 'w-01-home'
Tap 328 1767; Shot 'w-02-play'
& $adb shell input swipe 540 1500 540 700 500; Shot 'w-03-play-scrolled'
Tap 540 1767; Shot 'w-04-training'
Tap 752 1767; Shot 'w-05-goals'
Tap 965 1767; Shot 'w-06-stats'
Tap 992 184;  Shot 'w-07-settings'
Remove-Item $tmp -ErrorAction SilentlyContinue
Write-Host "== end $(Get-Date)"
