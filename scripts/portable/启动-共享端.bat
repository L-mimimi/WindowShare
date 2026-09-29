@echo off
title 窗享 Host - 共享端
cd /d "%~dp0"

echo ============================================
echo   窗享 WindowShare - 共享端（Host）
echo   只读屏幕/窗口共享 · 无远程控制功能
echo ============================================
echo.
echo 数据目录：%~dp0data
echo.
echo 使用步骤：
echo   1. 选择要共享的屏幕或窗口
echo   2. 点击「开始共享」
echo   3. 把界面上的房间号与密码告诉观看方
echo   4. 共享期间屏幕顶部会出现红色提示条，点「停止」可立即结束
echo.

if not exist "WindowShare.Host.exe" (
    echo [错误] 未找到 WindowShare.Host.exe，请确认本文件与程序在同一目录。
    pause
    exit /b 1
)

start "" "WindowShare.Host.exe"
exit /b 0
