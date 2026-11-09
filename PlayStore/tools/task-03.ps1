$ErrorActionPreference = 'Continue'
Write-Host "== probe 2 $(Get-Date)"
$local = "$env:LOCALAPPDATA\Android\Sdk"
$pf    = "${env:ProgramFiles(x86)}\Android\android-sdk"
foreach ($s in @($local,$pf)) {
  Write-Host "--- $s"
  Get-ChildItem $s -ErrorAction SilentlyContinue | ForEach-Object Name
  Get-ChildItem "$s\cmdline-tools" -ErrorAction SilentlyContinue | ForEach-Object Name
  Get-ChildItem "$s\platforms" -ErrorAction SilentlyContinue | ForEach-Object Name
  Get-ChildItem "$s\system-images" -Recurse -Depth 2 -ErrorAction SilentlyContinue | ForEach-Object FullName
}
Write-Host "--- java"
Get-ChildItem 'C:\Program Files\Microsoft','C:\Program Files\Android','C:\Program Files\Java','C:\Program Files\Eclipse Adoptium',"$env:LOCALAPPDATA\Programs" -Directory -ErrorAction SilentlyContinue | ForEach-Object FullName
Get-ChildItem 'C:\Program Files\Android\Android Studio\jbr\bin\java.exe' -ErrorAction SilentlyContinue | ForEach-Object FullName
Write-Host "JAVA_HOME=$env:JAVA_HOME"
Write-Host "--- avd"
Get-ChildItem "$env:USERPROFILE\.android\avd" -ErrorAction SilentlyContinue | ForEach-Object Name
Write-Host "--- dotnet android settings"
Get-Content "$env:APPDATA\Xamarin\monodroid-config.xml" -ErrorAction SilentlyContinue
Write-Host "== end"
