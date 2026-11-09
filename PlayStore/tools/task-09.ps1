$ErrorActionPreference = 'Continue'
Write-Host "== task-09 emulator + first screenshot $(Get-Date)"
$root = Resolve-Path "$PSScriptRoot\..\.."
$adb = 'C:\DartForge\AndroidSdk\platform-tools\adb.exe'
$emu = "${env:ProgramFiles(x86)}\Android\android-sdk\emulator\emulator.exe"
$shots = Join-Path $root 'PlayStore\screenshots\raw'; New-Item -ItemType Directory -Force $shots | Out-Null
$apk = Get-ChildItem (Join-Path $root 'PlayStore\release') -Filter *.apk | Select-Object -First 1
Write-Host "APK: $($apk.FullName)"
$env:ANDROID_SDK_ROOT = "${env:ProgramFiles(x86)}\Android\android-sdk"
if (-not ((& $adb devices) -match 'emulator-\d+\s+device')) {
  Start-Process -FilePath $emu -ArgumentList '-avd','pixel_7_pro_-_api_34','-no-window','-no-audio','-no-boot-anim','-no-snapshot-save','-gpu','swiftshader_indirect' -WindowStyle Hidden
}
& $adb wait-for-device
for ($i=0; $i -lt 90; $i++) { if ((& $adb shell getprop sys.boot_completed) -match '1') { break }; Start-Sleep 2 }
Write-Host "booted after ~$($i*2)s"
& $adb shell settings put global window_animation_scale 0
& $adb shell settings put global transition_animation_scale 0
& $adb shell settings put global animator_duration_scale 0
& $adb shell svc wifi disable; & $adb shell svc data disable
& $adb shell wm size 1080x1920; & $adb shell wm density 420
& $adb shell settings put system user_rotation 0
& $adb shell cmd statusbar collapse 2>$null
# demo mode: clean status bar (12:00, full battery/wifi)
& $adb shell settings put global sysui_demo_allowed 1
& $adb shell am broadcast -a com.android.systemui.demo -e command enter | Out-Null
& $adb shell am broadcast -a com.android.systemui.demo -e command clock -e hhmm 1200 | Out-Null
& $adb shell am broadcast -a com.android.systemui.demo -e command battery -e level 100 -e plugged false | Out-Null
& $adb shell am broadcast -a com.android.systemui.demo -e command network -e wifi show -e level 4 | Out-Null
& $adb shell am broadcast -a com.android.systemui.demo -e command notifications -e visible false | Out-Null
& $adb uninstall com.dartforge.app 2>&1 | ForEach-Object { "$_" }
& $adb install -r $apk.FullName 2>&1 | ForEach-Object { "$_" }
& $adb shell cmd locale set-app-locales com.dartforge.app --locales en-US 2>&1 | ForEach-Object { "$_" }
& $adb shell monkey -p com.dartforge.app -c android.intent.category.LAUNCHER 1 | Out-Null
Start-Sleep 12
cmd /c "`"$adb`" exec-out screencap -p > `"$shots\probe-01.png`""
Start-Sleep 5
cmd /c "`"$adb`" exec-out screencap -p > `"$shots\probe-02.png`""
& $adb shell dumpsys window displays | Select-String 'cur=|app=' | ForEach-Object { "$_" }
& $adb logcat -d -s DOTNET:* AndroidRuntime:E monodroid:E | Select-Object -Last 30 | ForEach-Object { "$_" }
Write-Host "== end $(Get-Date)"
