$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\lib.ps1"
Write-Host "== task-18 $(Get-Date)"
function Dart($mult, $num) { if ($mult) { TapText $mult | Out-Null }; TapText $num | Out-Null }
# visit 1: 180
Dart 'T ×3' '20'; Dart 'T ×3' '20'; Dart 'T ×3' '20'
Start-Sleep 8; Shot 'z-after-v1'
# visit 2: 60 + 57 + 18
Dart 'T ×3' '20'; Dart 'T ×3' '19'; Dart $null '18'
Start-Sleep 8
# visit 3 partially: T20
Dart 'T ×3' '20'
SwipeDown; SwipeDown
Shot 'en-x01'; Dump 'en-x01'
Write-Host "== end $(Get-Date)"
