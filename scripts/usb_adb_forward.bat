@echo off
chcp 65001 >nul
title AirMic - USB 模式端口映射
echo ==========================================================
echo          AirMic USB ADB 极速端口映射工具
echo ==========================================================
echo.
echo 提示：请确保手机开启了【USB 开发者调试】并通过数据线连接到电脑。
echo.
adb devices
echo.
echo 正在映射音频端口 8091...
adb forward tcp:8091 tcp:8091
if %errorlevel% equ 0 (
    echo [OK] 音频端口转发成功！手机端选择 USB 模式后即可传输。
) else (
    echo [!] 转发失败，请检查手机是否开启了开发者模式与 USB 调试。
)
pause
