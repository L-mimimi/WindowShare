@echo off
title 窗享 WindowShare - 合并入口
cd /d "%~dp0"

echo ============================================
echo   窗享 WindowShare - 合并入口
echo   共享端 / 观看端 二合一
echo ============================================
echo.
echo 直接运行会弹出模式选择窗；
echo 也可以用命令行参数直接进入：
echo   WindowShare.exe --host     共享端
echo   WindowShare.exe --viewer   观看端
echo   WindowShare.exe --select   重新弹出选择窗
echo.

if not exist "WindowShare.exe" (
    echo [错误] 未找到 WindowShare.exe，请确认本文件与程序在同一目录。
    pause
    exit /b 1
)

start "" "WindowShare.exe"
exit /b 0
