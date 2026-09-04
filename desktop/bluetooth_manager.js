const { exec } = require('child_process');

/**
 * Windows 蓝牙管理器 (基于 PowerShell WinRT / PnpDevice)
 * 支持扫描系统当前已配对的手机蓝牙设备及蓝牙串口 (SPP)
 */
class WindowsBluetoothManager {
  constructor() {
    this.devices = [];
  }

  // 获取 Windows 系统已配对的蓝牙设备列表
  getPairedDevices() {
    return new Promise((resolve) => {
      // 通过 PowerShell 查询 Class 为 Bluetooth 的已配对设备
      const psCmd = `powershell -NoProfile -Command "Get-PnpDevice -Class Bluetooth | Where-Object { $_.Status -eq 'OK' -and $_.FriendlyName -notlike '*Adapter*' -and $_.FriendlyName -notlike '*Radio*' -and $_.FriendlyName -notlike '*Enumerator*' } | Select-Object -Property FriendlyName, InstanceId | ConvertTo-Json"`;
      
      exec(psCmd, { windowsHide: true, timeout: 5000 }, (err, stdout) => {
        if (err || !stdout.trim()) {
          // 提供常用默认值
          resolve([
            { name: "未发现配对手机 (请在Windows蓝牙设置中配对手机)", id: "none" }
          ]);
          return;
        }

        try {
          const parsed = JSON.parse(stdout);
          const list = Array.isArray(parsed) ? parsed : [parsed];
          const result = list.map(item => ({
            name: item.FriendlyName || "蓝牙设备",
            id: item.InstanceId
          }));
          this.devices = result;
          resolve(result);
        } catch (e) {
          resolve([{ name: "已配对蓝牙设备 (默认)", id: "default_bt" }]);
        }
      });
    });
  }

  // 探测 Windows 系统当前可用的蓝牙虚拟串口 (COM Port)
  getBluetoothComPorts() {
    return new Promise((resolve) => {
      const psCmd = `powershell -NoProfile -Command "Get-CimInstance Win32_SerialPort | Select-Object DeviceID, Description | ConvertTo-Json"`;
      exec(psCmd, { windowsHide: true, timeout: 5000 }, (err, stdout) => {
        if (err || !stdout.trim()) {
          resolve([]);
          return;
        }
        try {
          const parsed = JSON.parse(stdout);
          const list = Array.isArray(parsed) ? parsed : [parsed];
          resolve(list.map(p => ({ port: p.DeviceID, desc: p.Description })));
        } catch (e) {
          resolve([]);
        }
      });
    });
  }
}

module.exports = new WindowsBluetoothManager();
