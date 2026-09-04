@echo off
chcp 65001 >nul
title AirMic - 电脑端手机麦克风虚拟声卡服务
echo ==========================================================
echo          AirMic (无线手机麦克风服务 - Windows版)
echo ==========================================================
echo.
echo 正在启动电脑端服务端...
cd /d "%~dp0..\desktop"
start http://localhost:8090
node server.js
pause
