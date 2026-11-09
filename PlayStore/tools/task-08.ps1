$ErrorActionPreference = 'Continue'
Write-Host "== task-08 build $(Get-Date) pid=$PID"
$me = Get-CimInstance Win32_Process -Filter "ProcessId=$PID"
function Stop-Tree($id) { Get-CimInstance Win32_Process -Filter "ParentProcessId=$id" | ForEach-Object { Stop-Tree $_.ProcessId }; Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
Get-CimInstance Win32_Process | Where-Object {
  $_.ProcessId -ne $PID -and $_.ProcessId -ne $me.ParentProcessId -and $_.CommandLine -match 'Tee-Object' -and $_.CommandLine -match 'task'
} | ForEach-Object { Write-Host "stopping stale run pid $($_.ProcessId)"; Stop-Tree $_.ProcessId }
Start-Sleep 2
Set-Location (Resolve-Path "$PSScriptRoot\..\..")
Write-Host "nuget.config present: $(Test-Path .\nuget.config)"
& .\build-release.ps1
Write-Host "build exit: $LASTEXITCODE  $(Get-Date)"
