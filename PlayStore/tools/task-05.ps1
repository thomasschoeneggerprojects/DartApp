$ErrorActionPreference = 'Continue'
Write-Host "== task-05 $(Get-Date)  pid=$PID"
# 1. stop stale runs from earlier attempts (only our own PlayStore\tools runners)
$me = Get-CimInstance Win32_Process -Filter "ProcessId=$PID"
Get-CimInstance Win32_Process | Where-Object {
  $_.ProcessId -ne $PID -and $_.ProcessId -ne $me.ParentProcessId -and
  ($_.CommandLine -match 'Tee-Object' -and $_.CommandLine -match 'task') -and $_.CreationDate -lt (Get-Date).AddMinutes(-1)
} | ForEach-Object { Write-Host "stopping stale pid $($_.ProcessId): $($_.CommandLine.Substring(0,[Math]::Min(80,$_.CommandLine.Length)))"; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'keytool|dotnet|java' } | ForEach-Object { Write-Host "running: $($_.ProcessId) $($_.Name) $($_.CreationDate)" }

# 2. JDK 17+
$javas = Get-ChildItem 'C:\Program Files\Android\jdk','C:\Program Files\Microsoft','C:\Program Files (x86)\Android' -Recurse -Filter java.exe -Depth 4 -ErrorAction SilentlyContinue | ForEach-Object FullName
$javas | ForEach-Object { Write-Host "java: $_" }
$jdk = $null
foreach ($j in $javas) { $v = (& $j -version 2>&1 | Select-Object -First 1) -as [string]; Write-Host "  $v"; if ($v -match 'version "(\d+)' -and [int]$Matches[1] -ge 17) { $jdk = Split-Path (Split-Path $j); break } }
Write-Host "JDK17: $jdk"
if (-not $jdk) { Write-Host 'NO JDK 17 FOUND'; return }
$env:JAVA_HOME = $jdk

# 3. android-36 into the user SDK
$local = "$env:LOCALAPPDATA\Android\Sdk"
$sdkm = Get-ChildItem "$local\cmdline-tools\*\bin\sdkmanager.bat", "${env:ProgramFiles(x86)}\Android\android-sdk\cmdline-tools\*\bin\sdkmanager.bat" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
Write-Host "sdkmanager: $sdkm"
$yes = Join-Path $env:TEMP 'yes.txt'; Set-Content $yes (("y`r`n") * 30)
cmd /c "`"$sdkm`" --sdk_root=`"$local`" --licenses < `"$yes`"" 2>&1 | Select-Object -Last 2 | ForEach-Object { "$_" }
cmd /c "`"$sdkm`" --sdk_root=`"$local`" `"platforms;android-36`" `"build-tools;35.0.0`" `"platform-tools`" < `"$yes`"" 2>&1 | Where-Object { $_ -notmatch '^\[' } | Select-Object -Last 10 | ForEach-Object { "$_" }
Get-ChildItem "$local\platforms", "$local\build-tools" | ForEach-Object FullName

# 4. build
Write-Host "== build $(Get-Date)"
Set-Location (Resolve-Path "$PSScriptRoot\..\..")
& .\build-release.ps1
Write-Host "build exit: $LASTEXITCODE  $(Get-Date)"
