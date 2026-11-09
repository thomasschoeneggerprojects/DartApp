$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\lib4.ps1"
Write-Host "== task-22 $(Get-Date)"
function Dart($mult, $num) { if ($mult) { TapText $mult | Out-Null }; TapText $num | Out-Null }
function C($y) { Tap 540 $y }
# Cricket (board at top): visit 1: 20,20,20 ; visit 2: 19,19,18 ; visit 3: 20
C 721; C 721; C 721
Start-Sleep 8
C 873; C 873; C 1027
Start-Sleep 8
C 721
Start-Sleep 1
Shot 'en-cricket'
# back to setup, pick X01
Tap 100 368
Start-Sleep 2
Shot 'dbg-setup'
TapText 'X01' | Out-Null
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
SwipeUp
Shot 'en-x01-pad'
# training
Tap 540 1767
Start-Sleep 1
Tap 540 693
Start-Sleep 2
Shot 'training-run'; Dump 'training-run'
Write-Host "== end $(Get-Date)"
