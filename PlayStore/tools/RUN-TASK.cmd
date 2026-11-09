@echo off
cd /d "%~dp0"
del /q task.done 2>nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "& { & .\task.ps1 } *>&1 | Tee-Object -FilePath .\task.log; 'done' | Set-Content .\task.done"
echo.
echo === TASK FINISHED ===
timeout /t 20
