$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\lib4.ps1"
Write-Host "== task-23 $(Get-Date)"
function Dart($mult, $num) { if ($mult) { TapText $mult | Out-Null }; TapText $num | Out-Null }
function H { & $adb shell input tap 386 812; Start-Sleep -Milliseconds 700 }
function M { & $adb shell input tap 837 812; Start-Sleep -Milliseconds 700 }
# --- training (already running on Training tab)
H; M; H; H; M; H; H; M; H; H; M; H; H
Shot 'en-training'
H; M; H; H; M; H; M; H
Start-Sleep 1
TapText 'Finish' | Out-Null
Start-Sleep 1
Shot 'en-training-done'
Tap 752 1767; Shot 'en-goals'
Tap 965 1767; Shot 'en-stats'
# --- cricket (match kept in Play tab)
Tap 328 1767
Start-Sleep 2
SwipeDown; SwipeDown
function C($y) { & $adb shell input tap 540 $y; Start-Sleep -Milliseconds 900 }
C 721; C 721; C 721
Start-Sleep 8
C 873; C 873; C 1027
Start-Sleep 8
C 721
Start-Sleep 1
Shot 'en-cricket'
# --- X01
Tap 100 368
Start-Sleep 2
SwipeDown; SwipeDown
Shot 'en-setup'
TapText 'X01' | Out-Null
SwipeUp; SwipeUp
Shot 'en-players'
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
Tap 116 1767; Shot 'en-home'
Write-Host "== end $(Get-Date)"
