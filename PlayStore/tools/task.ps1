$ErrorActionPreference = 'Continue'
Write-Host "== release build $(Get-Date)"
Set-Location (Resolve-Path "$PSScriptRoot\..\..")
& .\build-release.ps1
Write-Host "build exit: $LASTEXITCODE"
