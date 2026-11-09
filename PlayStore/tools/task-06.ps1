$ErrorActionPreference = 'Continue'
Write-Host "== task-06 $(Get-Date)"
$env:JAVA_HOME = 'C:\Program Files (x86)\Android\openjdk\jdk-17.0.14'
$sdk = 'C:\DartForge\AndroidSdk'
New-Item -ItemType Directory -Force $sdk | Out-Null
$sdkm = "$env:LOCALAPPDATA\Android\Sdk\cmdline-tools\11.0\bin\sdkmanager.bat"
$yes = 'C:\DartForge\yes.txt'; Set-Content $yes (("y`r`n") * 30)
cmd /c "`"$sdkm`" --sdk_root=$sdk --licenses < $yes" 2>&1 | Select-Object -Last 1 | ForEach-Object { "$_" }
cmd /c "`"$sdkm`" --sdk_root=$sdk `"platforms;android-36`" `"build-tools;35.0.0`" `"platform-tools`" < $yes" 2>&1 | Where-Object { $_ -notmatch '^\[' } | Select-Object -Last 8 | ForEach-Object { "$_" }
Remove-Item $yes -ErrorAction SilentlyContinue
Get-ChildItem "$sdk\platforms", "$sdk\build-tools" | ForEach-Object FullName
Write-Host "== build $(Get-Date)"
Set-Location (Resolve-Path "$PSScriptRoot\..\..")
Write-Host "build-release.ps1 bytes: $((Get-Item .\build-release.ps1).Length)"
& .\build-release.ps1
Write-Host "build exit: $LASTEXITCODE  $(Get-Date)"
