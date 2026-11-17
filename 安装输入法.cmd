@echo off
chcp 65001 >nul
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup\install.ps1" %*
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo 安装未完成。请保留上面的错误信息，查看“使用说明.txt”中的恢复办法。
if "%RESULT%"=="0" echo 安装完成。按 Win+Space 选择“Claude 中文输入法”。可从开始菜单打开设置。
echo 按任意键关闭此窗口。
pause >nul
endlocal & exit /b %RESULT%