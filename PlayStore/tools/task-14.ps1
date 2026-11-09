$ErrorActionPreference = 'Continue'
Write-Host "== task-14 $(Get-Date)"
$root = Resolve-Path "$PSScriptRoot\..\.."
$adb = 'C:\DartForge\AndroidSdk\platform-tools\adb.exe'
$shots = Join-Path $root 'PlayStore\screenshots\raw'
Write-Host "--- crash buffer"
& $adb logcat -b crash -d 2>&1 | Select-Object -Last 60 | ForEach-Object { "$_" }
Write-Host "--- dotnet/AndroidRuntime errors"
& $adb logcat -d '*:E' 2>&1 | Select-String 'dartforge|DOTNET|mono|AndroidRuntime|ANR' | Select-Object -Last 40 | ForEach-Object { "$_" }
Write-Host "--- am_crash / anr events"
& $adb logcat -b events -d 2>&1 | Select-String 'am_crash|am_anr|am_proc_died.*dartforge|am_kill.*dartforge' | Select-Object -Last 10 | ForEach-Object { "$_" }
function Shot($n) { Start-Sleep -Milliseconds 1500; & $adb emu screenrecord screenshot "$shots\$n.png" | Out-Null; Write-Host "shot $n $(Get-Date -Format HH:mm:ss)" }
& $adb logcat -c
& $adb shell monkey -p com.dartforge.app -c android.intent.category.LAUNCHER 1 | Out-Null
Start-Sleep 30
Shot 'v-01'
Start-Sleep 15
Shot 'v-02'
& $adb shell pidof com.dartforge.app
Write-Host "== end $(Get-Date)"
