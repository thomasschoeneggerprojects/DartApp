$ErrorActionPreference = 'Continue'
Write-Host "== install android-36 $(Get-Date)"
$local = "$env:LOCALAPPDATA\Android\Sdk"
$jdk = Get-ChildItem 'C:\Program Files\Android\jdk\*' -Directory | Where-Object { Test-Path "$($_.FullName)\bin\java.exe" } | Select-Object -First 1 -ExpandProperty FullName
Write-Host "JDK: $jdk"; & "$jdk\bin\java.exe" -version 2>&1 | ForEach-Object { "$_" }
$env:JAVA_HOME = $jdk
$sdkm = Get-ChildItem "$local\cmdline-tools\*\bin\sdkmanager.bat", "${env:ProgramFiles(x86)}\Android\android-sdk\cmdline-tools\*\bin\sdkmanager.bat" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
Write-Host "sdkmanager: $sdkm"
$yes = Join-Path $env:TEMP 'yes.txt'; Set-Content $yes (("y`r`n") * 30)
cmd /c "`"$sdkm`" --sdk_root=`"$local`" --licenses < `"$yes`"" 2>&1 | Select-Object -Last 3 | ForEach-Object { "$_" }
cmd /c "`"$sdkm`" --sdk_root=`"$local`" `"platforms;android-36`" `"build-tools;35.0.0`" `"platform-tools`" < `"$yes`"" 2>&1 | Where-Object { $_ -notmatch '^\[=*' } | Select-Object -Last 15 | ForEach-Object { "$_" }
Get-ChildItem "$local\platforms", "$local\build-tools" | ForEach-Object FullName
Write-Host "== build"
Set-Location (Resolve-Path "$PSScriptRoot\..\..")
Write-Host "build-release.ps1 lines: $((Get-Content .\build-release.ps1).Count)"
& .\build-release.ps1
Write-Host "build exit: $LASTEXITCODE"
