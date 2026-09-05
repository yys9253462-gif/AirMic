# AirMic v2 双端互通与全协议规范 (Wi-Fi / USB / Bluetooth)

## 1. 架构总览
AirMic 双端互通架构由 **安卓原生端 (Android App)** 与 **Windows 电脑端 (Desktop Client & Driver)** 组成。
支持三种连接媒介无缝切换：
1. **Bluetooth 蓝牙模式**：基于 Bluetooth Classic RFCOMM (SPP)，固定 Service UUID，已配对设备即开即连，免路由器依赖。
2. **Wi-Fi 局域网模式**：支持 UDP（极低延迟）和 TCP/WebSocket（抗抖动），支持电脑端 IP 广播与扫码一键发现。
3. **USB 物理连接模式**：支持 ADB 端口映射或 USB 辅助配件模式（Accessory），0 丢包、0 射频干扰。

```
+-------------------------------------------------------------------------------+
|                             Android 原生端 (AirMic App)                        |
|  - AudioRecord (PCM 16-bit 48kHz / 44.1kHz / 16kHz)                           |
|  - 传输层抽象 (ITransport): BluetoothTransmitter / NetTransmitter / UsbTrans  |
|  - 前台服务 (Foreground Service) + 常驻录音通知，防止系统休眠杀后台             |
+-------------------------------------------------------------------------------+
                                      │
              ┌───────────────────────┼───────────────────────┐
              ▼                       ▼                       ▼
      [Bluetooth RFCOMM]          [Wi-Fi UDP/TCP]          [USB ADB/Serial]
       UUID: 00001101-...           Port: 8090/8091          Port: 8090
              │                       │                       │
              └───────────────────────┼───────────────────────┘
                                      ▼
+-------------------------------------------------------------------------------+
|                            Windows 电脑端 (Desktop Server)                     |
|  - Bluetooth RFCOMM 适配器 (Winsock AF_BTH / 虚拟 COM 串口)                     |
|  - UDP / TCP 音频接收器 & Jitter Buffer 动态抗抖动缓冲                         |
|  - 系统级虚拟声卡输出 (VB-Audio Cable / VAC 桥接)                              |
|  - 实时电平表、声波显示、增益控制、远程静音与开箱即用控制台                     |
+-------------------------------------------------------------------------------+
                                      │
                                      ▼
             [Windows 系统原生麦克风: CABLE Output / Virtual Mic]
                                      │
          ┌───────────────────────────┼───────────────────────────┐
          ▼                           ▼                           ▼
      腾讯会议                      微信/QQ                     OBS/直播/录音
```

## 2. 蓝牙传输规范
- **协议**：Bluetooth RFCOMM (Serial Port Profile, SPP)
- **固定 Service UUID**：`00001101-0000-1000-8000-00805F9B34FB` (标准串口服务) 或 AirMic 专用 UUID `a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d`
- **连接流程**：
  1. 手机与 Windows 在系统设置中完成常规蓝牙配对。
  2. 手机端选择「Bluetooth」并点击「开始」：启动 `BluetoothServerSocket.accept()` 等待连接。
  3. 电脑端扫描已配对的蓝牙设备列表，点击选中的手机即可建立 RFCOMM Socket。
  4. 电脑端向蓝牙 Socket 发送握手命令后，手机端持续推流 PCM 数据帧。

## 3. 音频数据帧格式 (通用二进制格式)
新版本音频包由 12 字节头部 + PCM 数据组成；旧版 8 字节 AM 帧仍兼容：
```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|   Magic 'A'   |   Magic 'R'   |        Sequence Number        |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       Timestamp (32-bit ms)                   |
|                   Sample Rate (32-bit Hz)                     |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                 PCM 16-bit Mono Audio Payload ...             |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```
- **Magic**: `0x41 0x4D` ('AM' 标识符)
- **Sequence Number**: 16 位自增编号（0-65535），用于丢包与乱序统计。
- **Timestamp**: 发送端本地相对毫秒时间戳，用于电脑端抖动缓冲区补偿。
- **Payload**: 单声道 PCM 16-bit Little-Endian 音频采样。
