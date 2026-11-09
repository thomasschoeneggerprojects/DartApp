$ErrorActionPreference = 'Continue'
Write-Host "== task-16 $(Get-Date)"
$root = Resolve-Path "$PSScriptRoot\..\.."
$adb = 'C:\DartForge\AndroidSdk\platform-tools\adb.exe'
$shots = Join-Path $root 'PlayStore\screenshots\raw'
function Shot($n) { Start-Sleep -Milliseconds 2500; & $adb emu screenrecord screenshot "$shots\$n.png" | Out-Null; Write-Host "shot $n $(Get-Date -Format HH:mm:ss)" }
function Tap($x,$y) { & $adb shell input tap $x $y; Start-Sleep -Milliseconds 2000 }
function Dump($n) { & $adb shell uiautomator dump /sdcard/ui.xml | Out-Null; & $adb pull /sdcard/ui.xml "$shots\$n.xml" | Out-Null; Write-Host "dump $n" }
Tap 328 1767
& $adb shell input swipe 540 1500 540 500 500
& $adb shell input swipe 540 1500 540 500 500
Shot 'x-01-play-bottom'
Dump 'x-01-play-bottom'
Write-Host "== end $(Get-Date)"
