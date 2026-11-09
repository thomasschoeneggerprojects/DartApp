$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\lib4.ps1"
Write-Host "== task-21 $(Get-Date)"
function Dart($mult, $num) { if ($mult) { TapText $mult | Out-Null }; TapText $num | Out-Null }
Relaunch
Tap 328 1767
SwipeUp; SwipeUp
TapText 'START' | Out-Null
Start-Sleep 2
Dart 'T*3' '20'; Dart 'T*3' '20'; Dart 'T*3' '20'
Start-Sleep 8
Dart 'T*3' '20'; Dart 'T*3' '19'; Dart $null '18'
Start-Sleep 8
Dart 'T*3' '20'
SwipeDown; SwipeDown
Shot 'en-x01'
TapText 'Back to setup' | Out-Null
SwipeDown; SwipeDown
TapText 'Cricket' | Out-Null
SwipeUp; SwipeUp
TapText 'START' | Out-Null
Start-Sleep 2
SwipeDown; SwipeDown
Shot 'cricket-start'; Dump 'cricket-start'
SwipeUp
Dump 'cricket-start-2'
Tap 540 1767
TapText '*START*' | Out-Null
Start-Sleep 2
Shot 'training-run'; Dump 'training-run'
Write-Host "== end $(Get-Date)"
