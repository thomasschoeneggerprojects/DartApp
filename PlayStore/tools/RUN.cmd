@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$t = Get-ChildItem task-*.ps1 | Sort-Object Name | Select-Object -Last 1; $log = [IO.Path]::ChangeExtension($t.FullName, '.log'); & { & $t.FullName } *>&1 | Tee-Object -FilePath $log; 'done' | Set-Content ([IO.Path]::ChangeExtension($t.FullName, '.done'))"
echo === TASK FINISHED ===
timeout /t 10
