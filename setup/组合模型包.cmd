@echo off
setlocal
if exist "%~dp0combine-model-pack.ps1" (
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0combine-model-pack.ps1" -ProgramRoot "%~dp0.." %*
) else (
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup\combine-model-pack.ps1" -ProgramRoot "%~dp0." %*
)
if errorlevel 1 (
  echo.
  echo Model combination failed. Extract the matching model pack beside this program folder, then retry.
  pause
  exit /b 1
)
echo.
echo Model validated. Run the install entry in this program folder when ready.
pause
