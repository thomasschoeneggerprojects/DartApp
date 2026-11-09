$ErrorActionPreference = 'Continue'
Write-Host "== task-11 $(Get-Date)"
$root = Resolve-Path "$PSScriptRoot\..\.."
$adb = 'C:\DartForge\AndroidSdk\platform-tools\adb.exe'
$emu = "${env:ProgramFiles(x86)}\Android\android-sdk\emulator\emulator.exe"
$shots = Join-Path $root 'PlayStore\screenshots\raw'
& $adb emu screenrecord screenshot "$shots\emu-shot.png" 2>&1 | ForEach-Object { "$_" }
Start-Sleep 2
Write-Host "emu-shot exists: $(Test-Path "$shots\emu-shot.png")"
# restart emulator with guest GPU rendering (headless swiftshader produced white frames)
& $adb emu kill 2>&1 | ForEach-Object { "$_" }
Start-Sleep 8
$env:ANDROID_SDK_ROOT = "${env:ProgramFiles(x86)}\Android\android-sdk"
Start-Process -FilePath $emu -ArgumentList '-avd','pixel_7_pro_-_api_34','-no-window','-no-audio','-no-boot-anim','-no-snapshot','-gpu','guest' -WindowStyle Hidden
Start-Sleep 5
& $adb wait-for-device
for ($i=0; $i -lt 120; $i++) { if ((& $adb shell getprop sys.boot_completed) -match '1') { break }; Start-Sleep 2 }
Write-Host "booted after ~$($i*2)s"
Start-Sleep 10
& $adb shell svc wifi disable; & $adb shell svc data disable
& $adb shell wm size 1080x1920; & $adb shell wm density 420
& $adb shell settings put global sysui_demo_allowed 1
& $adb shell am broadcast -a com.android.systemui.demo -e command enter | Out-Null
& $adb shell am broadcast -a com.android.systemui.demo -e command clock -e hhmm 1200 | Out-Null
& $adb shell am broadcast -a com.android.systemui.demo -e command battery -e level 100 -e plugged false | Out-Null
& $adb shell am broadcast -a com.android.systemui.demo -e command network -e wifi show -e level 4 | Out-Null
& $adb shell am broadcast -a com.android.systemui.demo -e command notifications -e visible false | Out-Null
cmd /c "`"$adb`" exec-out screencap -p > `"$shots\g-home.png`""
& $adb shell monkey -p com.dartforge.app -c android.intent.category.LAUNCHER 1 2>&1 | Out-Null
Start-Sleep 15
cmd /c "`"$adb`" exec-out screencap -p > `"$shots\g-app.png`""
Write-Host "== end $(Get-Date)"
