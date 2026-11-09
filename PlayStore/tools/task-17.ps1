$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\lib.ps1"
Write-Host "== task-17 $(Get-Date)"
TapText 'START' | Out-Null
Start-Sleep 2
Shot 'y-01-match'; Dump 'y-01-match'
Write-Host "== end $(Get-Date)"
