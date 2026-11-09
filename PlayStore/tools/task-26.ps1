$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\lib4.ps1"
Write-Host "== task-26 $(Get-Date)"
function Dart($mult, $num) { if ($mult) { TapText $mult | Out-Null }; TapText $num | Out-Null }
function Slow($x,$y) { & $adb shell input tap $x $y; Start-Sleep -Milliseconds 1600 }
function Capture($lang, $locale) {
  Write-Host "--- $lang ($locale) $(Get-Date -Format HH:mm:ss)"
  & $adb shell cmd locale set-app-locales com.dartforge.app --locales $locale 2>&1 | Out-Null
  & $adb shell am force-stop com.dartforge.app
  & $adb shell monkey -p com.dartforge.app -c android.intent.category.LAUNCHER 1 2>&1 | Out-Null
  Start-Sleep 40
  Shot "$lang-01-home"
  Slow 328 1767; SwipeDown; SwipeDown
  Shot "$lang-02-modes"
  SwipeUp; SwipeUp
  Shot "$lang-03-players"
  Slow 540 1410
  Start-Sleep 5
  Dart 'T*3' '20'; Dart 'T*3' '20'; Dart 'T*3' '20'
  Start-Sleep 8
  Dart 'T*3' '20'; Dart 'T*3' '19'; Dart $null '18'
  Start-Sleep 8
  Dart 'T*3' '20'
  SwipeDown; SwipeDown
  Shot "$lang-04-x01"
  Slow 100 368
  SwipeDown; SwipeDown
  Slow 765 462
  SwipeUp; SwipeUp
  Slow 540 1410
  Start-Sleep 2
  SwipeDown; SwipeDown
  Slow 540 721; Slow 540 721; Slow 540 721
  Start-Sleep 8
  Slow 540 873; Slow 540 873; Slow 540 1027
  Start-Sleep 8
  Slow 540 721
  Shot "$lang-05-cricket"
  Slow 540 1767
  Slow 540 693
  foreach ($h in 1,1,0,1,1,1,0,1,1,0,1,1) { if ($h) { Slow 386 812 } else { Slow 837 812 } }
  Shot "$lang-06-training"
  Slow 752 1767
  Shot "$lang-07-goals"
}
Capture 'es' 'es-ES'
Capture 'en' 'en-US'
Write-Host "== end $(Get-Date)"
