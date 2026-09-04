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
echo 正在映射端口 8090...
adb forward tcp:8090 tcp:8090
if %errorlevel% equ 0 (
    echo [OK] 端口转发成功！手机浏览器直接打开 http://127.0.0.1:8090/mobile 即可。
) else (
    echo [!] 转发失败，请检查手机是否开启了开发者模式与 USB 调试。
)
pause
