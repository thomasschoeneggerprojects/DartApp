$ErrorActionPreference = 'Continue'
Write-Host "== task-13 $(Get-Date)"
$root = Resolve-Path "$PSScriptRoot\..\.."
$adb = 'C:\DartForge\AndroidSdk\platform-tools\adb.exe'
$emu = "${env:ProgramFiles(x86)}\Android\android-sdk\emulator\emulator.exe"
$shots = Join-Path $root 'PlayStore\screenshots\raw'
$me = Get-CimInstance Win32_Process -Filter "ProcessId=$PID"
function Stop-Tree($id) { Get-CimInstance Win32_Process -Filter "ParentProcessId=$id" | ForEach-Object { Stop-Tree $_.ProcessId }; Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $PID -and $_.ProcessId -ne $me.ParentProcessId -and $_.CommandLine -match 'Tee-Object' -and $_.CommandLine -match 'task' } | ForEach-Object { Write-Host "stop stale $($_.ProcessId)"; Stop-Tree $_.ProcessId }
& $adb emu kill | Out-Null
Start-Sleep 10
Get-Process qemu-system-* -ErrorAction SilentlyContinue | Stop-Process -Force
$env:ANDROID_SDK_ROOT = "${env:ProgramFiles(x86)}\Android\android-sdk"
Start-Process -FilePath $emu -ArgumentList '-avd','pixel_7_pro_-_api_34','-no-window','-no-audio','-no-boot-anim','-no-snapshot','-gpu','swiftshader_indirect' -WindowStyle Hidden
Start-Sleep 5
& $adb wait-for-device
for ($i=0; $i -lt 120; $i++) { if ((& $adb shell getprop sys.boot_completed) -match '1') { break }; Start-Sleep 2 }
Write-Host "booted after ~$($i*2)s"
Start-Sleep 20
& $adb shell svc wifi disable; & $adb shell svc data disable
& $adb shell wm size 1080x1920; & $adb shell wm density 420
& $adb shell settings put global window_animation_scale 0
& $adb shell settings put global transition_animation_scale 0
& $adb shell settings put global animator_duration_scale 0
Write-Host "setup done $(Get-Date)"
function Shot($n) { Start-Sleep -Milliseconds 1500; & $adb emu screenrecord screenshot "$shots\$n.png" | Out-Null; Write-Host "shot $n" }
function Tap($x,$y) { & $adb shell input tap $x $y; Start-Sleep -Milliseconds 1500 }
& $adb shell monkey -p com.dartforge.app -c android.intent.category.LAUNCHER 1 | Out-Null
Start-Sleep 20
Shot 'u-01-home'
Tap 328 1767; Shot 'u-02-play'
& $adb shell input swipe 540 1400 540 600 400; Shot 'u-03-play-scrolled'
Tap 540 1767; Shot 'u-04-training'
Tap 752 1767; Shot 'u-05-goals'
Tap 965 1767; Shot 'u-06-stats'
Tap 992 184;  Shot 'u-07-settings'
Write-Host "== end $(Get-Date)"
