$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\lib4.ps1"
Write-Host "== task-24 $(Get-Date)"
function Progress { (Nodes | Where-Object { $_.text -match '/ 21' } | Select-Object -First 1).text }
Tap 540 1767
Start-Sleep 1
TapText '*START*' | Out-Null
Start-Sleep 2
Write-Host "start: $(Progress)"
& $adb shell input tap 386 812; Start-Sleep 3
Write-Host "after hit tap center: $(Progress)"
& $adb shell input tap 250 760; Start-Sleep 3
Write-Host "after hit tap left-top: $(Progress)"
TapText 'Hit' | Out-Null; Start-Sleep 2
Write-Host "after TapText Hit: $(Progress)"
& $adb shell input tap 837 812; Start-Sleep 3
Write-Host "after miss tap: $(Progress)"
Shot 'dbg-training'
Write-Host "== end $(Get-Date)"
