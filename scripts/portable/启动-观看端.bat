@echo off
title 窗享 Viewer - 观看端
cd /d "%~dp0"

echo ============================================
echo   窗享 WindowShare - 观看端（Viewer）
echo   只读观看 · 无远程控制功能
echo ============================================
echo.
echo 使用步骤（二选一）：
echo.
echo   [局域网直连]
echo     - 选择「直连 IP」
echo     - 地址填 Host 的 IP，端口默认 48750
echo     - 填入 Host 显示的密码，点「连接」
echo.
echo   [跨网段 / 房间号]
echo     - 选择「房间号」
echo     - 填入房间号、密码、信令服务器地址
echo     - 点「连接」（自动尝试直连，失败回退 WebRTC）
echo.

if not exist "WindowShare.Viewer.exe" (
    echo [错误] 未找到 WindowShare.Viewer.exe，请确认本文件与程序在同一目录。
    pause
    exit /b 1
)

start "" "WindowShare.Viewer.exe"
exit /b 0
