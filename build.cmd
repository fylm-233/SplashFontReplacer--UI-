@echo off
rem 多合一启动图字体UI替换器 - 构建入口
rem 绕过 PowerShell 执行策略，直接调用 build.ps1
setlocal
pushd "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "CODE=%ERRORLEVEL%"
popd
if not "%CODE%"=="0" (
    echo.
    echo 构建失败，退出码 %CODE%
    pause
    exit /b %CODE%
)
echo.
echo 构建完成，产物在 build\dist
pause
