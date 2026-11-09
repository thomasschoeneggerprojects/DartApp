$ErrorActionPreference = 'Continue'
Write-Host "== task-10 diag $(Get-Date)"
$root = Resolve-Path "$PSScriptRoot\..\.."
$adb = 'C:\DartForge\AndroidSdk\platform-tools\adb.exe'
$shots = Join-Path $root 'PlayStore\screenshots\raw'
& $adb devices
& $adb shell getprop ro.build.version.sdk
cmd /c "`"$adb`" exec-out screencap -p > `"$shots\diag-app.png`""
& $adb shell pidof com.dartforge.app
& $adb shell dumpsys activity top | Select-String 'ACTIVITY|mResumed' | Select-Object -First 4 | ForEach-Object { "$_" }
& $adb logcat -d -t 400 '*:W' 2>&1 | Select-String -Pattern 'dartforge|DOTNET|monodroid|chromium|AndroidRuntime|FATAL|WebView|cr_' | Select-Object -Last 60 | ForEach-Object { "$_" }
& $adb shell input keyevent KEYCODE_HOME
Start-Sleep 2
cmd /c "`"$adb`" exec-out screencap -p > `"$shots\diag-home.png`""
& $adb shell monkey -p com.dartforge.app -c android.intent.category.LAUNCHER 1 2>&1 | Out-Null
Start-Sleep 8
cmd /c "`"$adb`" exec-out screencap -p > `"$shots\diag-app2.png`""
Write-Host "== end $(Get-Date)"
