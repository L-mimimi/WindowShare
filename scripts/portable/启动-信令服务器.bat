@echo off
title 窗享 信令服务器
cd /d "%~dp0signaling"

echo ============================================
echo   窗享 WindowShare - 信令服务器
echo   房间号注册 / 密码校验 / SDP-ICE 中继
echo ============================================
echo.
echo 默认监听：http://0.0.0.0:5000
echo 客户端「信令地址」填：http://本机IP:5000
echo.
echo 关闭本窗口即停止服务。
echo.

if not exist "WindowShare.Signaling.exe" (
    echo [错误] 未找到 signaling\WindowShare.Signaling.exe
    pause
    exit /b 1
)

echo [提示] 若需要让外部网络访问，请放行 TCP 5000 端口：
echo        netsh advfirewall firewall add rule name="WindowShare Signaling" dir=in action=allow protocol=TCP localport=5000
echo.
pause

"WindowShare.Signaling.exe" --urls http://0.0.0.0:5000
pause
