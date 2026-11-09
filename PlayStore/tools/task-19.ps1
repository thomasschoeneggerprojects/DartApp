$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\lib3.ps1"
Write-Host "== task-19 $(Get-Date)"
function Dart($mult, $num) { if ($mult) { TapText $mult | Out-Null }; TapText $num | Out-Null }
Relaunch
Tap 328 1767
SwipeUp; SwipeUp
TapText 'START' | Out-Null
Start-Sleep 2
Dart 'T ×3' '20'; Dart 'T ×3' '20'; Dart 'T ×3' '20'
Start-Sleep 8
Dart 'T ×3' '20'; Dart 'T ×3' '19'; Dart $null '18'
Start-Sleep 8
Dart 'T ×3' '20'
SwipeDown; SwipeDown
Shot 'en-x01'
Dump 'en-x01'
Write-Host "== end $(Get-Date)"
