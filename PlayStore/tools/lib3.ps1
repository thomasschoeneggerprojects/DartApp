# helpers for screenshot automation (dot-sourced by task scripts)
$root = Resolve-Path "$PSScriptRoot\..\.."
$adb = 'C:\DartForge\AndroidSdk\platform-tools\adb.exe'
$shots = Join-Path $root 'PlayStore\screenshots\raw'
function Shot($n) { Start-Sleep -Milliseconds 2000; & $adb emu screenrecord screenshot "$shots\$n.png" | Out-Null; Write-Host "shot $n $(Get-Date -Format HH:mm:ss)" }
function Tap($x,$y) { & $adb shell input tap $x $y; Start-Sleep -Milliseconds 1500 }
function Dump($n) { & $adb shell uiautomator dump /sdcard/ui.xml | Out-Null; & $adb pull /sdcard/ui.xml "$shots\$n.xml" | Out-Null; Write-Host "dump $n" }
function Nodes { & $adb shell uiautomator dump /sdcard/ui.xml | Out-Null; [xml]$x = (& $adb exec-out cat /sdcard/ui.xml); $x.SelectNodes('//node') }
function TapText([string]$t, [int]$index = 0) {
  for ($try = 0; $try -lt 4; $try++) {
    $hits = @(Nodes | Where-Object { ($_.text -like $t -or $_.'content-desc' -like $t) -and $_.bounds -match '\[(\d+),(\d+)\]\[(\d+),(\d+)\]' -and [int]$Matches[3] -gt [int]$Matches[1] -and [int]$Matches[4] -gt [int]$Matches[2] })
    if ($hits.Count -le $index) { Write-Host "  !! '$t' not found"; return $false }
    $null = $hits[$index].bounds -match '\[(\d+),(\d+)\]\[(\d+),(\d+)\]'
    $x = ([int]$Matches[1] + [int]$Matches[3]) / 2; $y = ([int]$Matches[2] + [int]$Matches[4]) / 2
    $h = [int]$Matches[4] - [int]$Matches[2]
    if ($y -gt 1640 -or $h -lt 40 -and $y -gt 1500) { & $adb shell input swipe 540 1300 540 700 400; Start-Sleep -Milliseconds 800; continue }
    if ($y -lt 260) { & $adb shell input swipe 540 700 540 1300 400; Start-Sleep -Milliseconds 800; continue }
    Write-Host "  tap '$t' @ $x,$y"; Tap $x $y; return $true }
  Write-Host "  !! '$t' not reachable"; return $false }
function SwipeUp { & $adb shell input swipe 540 1450 540 450 400; Start-Sleep -Milliseconds 800 }
function SwipeDown { & $adb shell input swipe 540 450 540 1450 400; Start-Sleep -Milliseconds 800 }
function WaitHome { Add-Type -AssemblyName System.Drawing; $tmp = Join-Path $shots '_probe.png'
  for ($i=0; $i -lt 40; $i++) { & $adb emu screenrecord screenshot $tmp | Out-Null; Start-Sleep -Milliseconds 300
    $b = [System.Drawing.Bitmap]::FromFile($tmp); $p = $b.GetPixel(60, 1600); $b.Dispose()
    if ((($p.R + $p.G + $p.B) / 3) -lt 90) { break }; Start-Sleep 2 }
  Remove-Item $tmp -ErrorAction SilentlyContinue; Start-Sleep 2; Write-Host "home ready (~$($i*3)s)" }
function Relaunch { & $adb shell am force-stop com.dartforge.app; & $adb shell monkey -p com.dartforge.app -c android.intent.category.LAUNCHER 1 2>&1 | Out-Null; WaitHome }
