$ErrorActionPreference = 'Continue'
Write-Host "== task-12 tour $(Get-Date)"
$root = Resolve-Path "$PSScriptRoot\..\.."
$adb = 'C:\DartForge\AndroidSdk\platform-tools\adb.exe'
$shots = Join-Path $root 'PlayStore\screenshots\raw'
function Shot($n) { Start-Sleep -Milliseconds 1500; & $adb emu screenrecord screenshot "$shots\$n.png" | Out-Null; Write-Host "shot $n" }
function Tap($x,$y) { & $adb shell input tap $x $y; Start-Sleep -Milliseconds 1200 }
for ($i=0; $i -lt 60; $i++) { if ((& $adb shell getprop sys.boot_completed) -match '1') { break }; Start-Sleep 2 }
& $adb shell am force-stop com.dartforge.app
& $adb shell monkey -p com.dartforge.app -c android.intent.category.LAUNCHER 1 | Out-Null
Start-Sleep 14
Shot 't-01-home'
Tap 328 1767; Shot 't-02-play'
& $adb shell input swipe 540 1400 540 600 400; Shot 't-03-play-scrolled'
Tap 540 1767; Shot 't-04-training'
Tap 752 1767; Shot 't-05-goals'
Tap 965 1767; Shot 't-06-stats'
Tap 992 184;  Shot 't-07-settings'
Write-Host "== end $(Get-Date)"
