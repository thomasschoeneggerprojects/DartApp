$ErrorActionPreference = 'Continue'
Write-Host "== task-07 build $(Get-Date)"
Set-Location (Resolve-Path "$PSScriptRoot\..\..")
Write-Host "nuget.config present: $(Test-Path .\nuget.config)"
& .\build-release.ps1
Write-Host "build exit: $LASTEXITCODE  $(Get-Date)"
