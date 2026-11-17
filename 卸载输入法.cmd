@echo off
chcp 65001 >nul
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup\uninstall.ps1" %*
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo 卸载未完成。请保留上面的错误信息，查看“使用说明.txt”中的恢复办法。
if "%RESULT%"=="0" echo 卸载处理完成。若有占用提示，关闭相关应用后再次运行此入口。
echo 按任意键关闭此窗口。
pause >nul
endlocal & exit /b %RESULT%