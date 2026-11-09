# helpers for screenshot automation (dot-sourced by task scripts)
$root = Resolve-Path "$PSScriptRoot\..\.."
$adb = 'C:\DartForge\AndroidSdk\platform-tools\adb.exe'
$shots = Join-Path $root 'PlayStore\screenshots\raw'
function Shot($n) { Start-Sleep -Milliseconds 2000; & $adb emu screenrecord screenshot "$shots\$n.png" | Out-Null; Write-Host "shot $n $(Get-Date -Format HH:mm:ss)" }
function Tap($x,$y) { & $adb shell input tap $x $y; Start-Sleep -Milliseconds 1500 }
function Dump($n) { & $adb shell uiautomator dump /sdcard/ui.xml | Out-Null; & $adb pull /sdcard/ui.xml "$shots\$n.xml" | Out-Null; Write-Host "dump $n" }
function Nodes { & $adb shell uiautomator dump /sdcard/ui.xml | Out-Null; [xml]$x = (& $adb exec-out cat /sdcard/ui.xml); $x.SelectNodes('//node') }
function TapText([string]$t, [int]$index = 0) {
  $hits = @(Nodes | Where-Object { ($_.text -eq $t -or $_.'content-desc' -eq $t) -and $_.bounds -match '\[(\d+),(\d+)\]\[(\d+),(\d+)\]' -and [int]$Matches[3] -gt [int]$Matches[1] -and [int]$Matches[4] -gt [int]$Matches[2] })
  if ($hits.Count -le $index) { Write-Host "  !! '$t' not found"; return $false }
  $null = $hits[$index].bounds -match '\[(\d+),(\d+)\]\[(\d+),(\d+)\]'
  $x = ([int]$Matches[1] + [int]$Matches[3]) / 2; $y = ([int]$Matches[2] + [int]$Matches[4]) / 2
  Write-Host "  tap '$t' @ $x,$y"; Tap $x $y; return $true }
function SwipeUp { & $adb shell input swipe 540 1450 540 450 400; Start-Sleep -Milliseconds 800 }
function SwipeDown { & $adb shell input swipe 540 450 540 1450 400; Start-Sleep -Milliseconds 800 }
