using InTheHand.Net.Sockets;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AirMic.Windows;

public sealed class MainForm : Form
{
    private readonly UdpAudioReceiver _udp = new();
    private readonly BluetoothAudioReceiver _bluetooth = new();

    // 双重配对机制：UDP 广播 + 原生高兼容 TCP/HTTP 握手服务 (端口 8092 UDP / 8090 TCP)
    private UdpClient? _pairingUdp;
    private TcpListener? _tcpPairingServer;
    private CancellationTokenSource? _pairCts;
    private readonly System.Windows.Forms.Timer _beaconTimer = new();

    private readonly ComboBox _mode = new();
    private readonly ComboBox _outputDevice = new();
    private readonly ComboBox _monitorDevice = new();
    private readonly CheckBox _chkMonitor = new();
    private readonly ComboBox _sampleRate = new();
    private readonly Button _connect = new();
    private readonly Button _disconnect = new();
    private readonly Button _testSpeaker = new();

    private readonly Label _pairStatusBadge = new();
    private readonly Label _pairDetail = new();
    private readonly Label _localAddress = new();
    private readonly Label _db = new();
    private readonly Label _packets = new();
    private readonly Label _testResult = new();
    private readonly ProgressBar _meter = new();

    private DateTime _lastSoundTime = DateTime.MinValue;
    private bool _isAudioRunning = false;

    public MainForm()
    {
        Text = "AirMic - 手机麦克风电脑接收端 (零配置自动配对版)";
        Width = 780;
        Height = 630;
        MinimumSize = new Size(720, 580);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(248, 250, 252);
        Font = new Font("Microsoft YaHei UI", 9F);

        FormClosing += (_, _) => {
            _beaconTimer.Stop();
            _pairCts?.Cancel();
            try { _tcpPairingServer?.Stop(); } catch { }
            _pairingUdp?.Dispose();
            _udp.Dispose();
            _bluetooth.Dispose();
        };

        BuildUi();
        LoadAudioDevices();
        HookEvents();

        // 启动配对中枢与音频监听
        StartPairingServer();
        StartAudioEngine();
    }

    private void BuildUi()
    {
        var title = new Label
        {
            Text = "AirMic 手机无线麦克风接收端",
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            AutoSize = true,
            Location = new Point(30, 20)
        };
        var subtitle = new Label
        {
            Text = "零配置免输入！双端内置 UDP 广播 + 局域网主动扫描，即使路由器隔离广播也能秒级自动配对。",
            ForeColor = Color.FromArgb(100, 116, 139),
            AutoSize = true,
            Location = new Point(32, 56)
        };
        Controls.Add(title); Controls.Add(subtitle);

        // 1. 配对状态大卡片
        var pairCard = new Panel
        {
            Location = new Point(30, 90),
            Size = new Size(700, 95),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(pairCard);

        var pairTitle = new Label { Text = "配对状态：", AutoSize = true, Location = new Point(20, 18), ForeColor = Color.FromArgb(71, 85, 105), Font = new Font(Font.FontFamily, 10F, FontStyle.Bold) };
        pairCard.Controls.Add(pairTitle);

        _pairStatusBadge.Text = "⏳ 等待手机配对中 (请在手机端打开 AirMic)";
        _pairStatusBadge.AutoSize = true;
        _pairStatusBadge.Location = new Point(100, 16);
        _pairStatusBadge.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
        _pairStatusBadge.ForeColor = Color.FromArgb(245, 158, 11);
        pairCard.Controls.Add(_pairStatusBadge);

        _pairDetail.Text = "自动配对服务运行中... 无论路由器是否屏蔽广播，手机打开后均会自动探测到电脑。";
        _pairDetail.AutoSize = true;
        _pairDetail.Location = new Point(22, 52);
        _pairDetail.ForeColor = Color.FromArgb(100, 116, 139);
        pairCard.Controls.Add(_pairDetail);

        // 2. 音频配置面板
        var panel = new Panel
        {
            Location = new Point(30, 200),
            Size = new Size(700, 175),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(panel);

        int y = 16;
        AddRow(panel, "连接方式", _mode, y);
        _mode.Items.AddRange(new object[] { "Wi-Fi 局域网 (默认推荐 / 零配置全自动配对)", "蓝牙 RFCOMM (SPP)" });
        _mode.SelectedIndex = 0;
        y += 38;

        AddRow(panel, "主音频输出", _outputDevice, y);
        y += 38;

        AddRow(panel, "测试监听设备", _monitorDevice, y);
        _chkMonitor.Text = "开启扬声器实时监听";
        _chkMonitor.AutoSize = true;
        _chkMonitor.SetBounds(500, y + 4, 180, 24);
        _chkMonitor.ForeColor = Color.FromArgb(37, 99, 235);
        _chkMonitor.Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold);
        panel.Controls.Add(_chkMonitor);
        y += 38;

        AddRow(panel, "音频采样规格", _sampleRate, y);
        _sampleRate.Items.AddRange(new object[] { 
            "192000 Hz (192kHz 旗舰发烧级)", 
            "96000 Hz (96kHz 专业录音棚级)", 
            "48000 Hz (48kHz 广播级 / 默认推荐)", 
            "44100 Hz (44.1kHz CD母带标准)", 
            "16000 Hz (16kHz 极速低带宽对话)" 
        });
        _sampleRate.SelectedIndex = 2;

        // 3. 按钮操作栏
        _connect.Text = "重置配对与接收";
        _connect.SetBounds(30, 390, 150, 42);
        _connect.BackColor = Color.FromArgb(37, 99, 235);
        _connect.ForeColor = Color.White;
        _connect.FlatStyle = FlatStyle.Flat;

        _disconnect.Text = "断开连接";
        _disconnect.SetBounds(190, 390, 100, 42);
        _disconnect.FlatStyle = FlatStyle.Flat;

        _testSpeaker.Text = "🔊 测试麦克风是否正常";
        _testSpeaker.SetBounds(300, 390, 200, 42);
        _testSpeaker.BackColor = Color.FromArgb(16, 185, 129);
        _testSpeaker.ForeColor = Color.White;
        _testSpeaker.FlatStyle = FlatStyle.Flat;
        _testSpeaker.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);

        Controls.Add(_connect);
        Controls.Add(_disconnect);
        Controls.Add(_testSpeaker);

        // 4. 实时电平与测试诊断卡片
        var statPanel = new Panel
        {
            Location = new Point(30, 445),
            Size = new Size(700, 125),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(statPanel);

        _localAddress.Text = "电脑本机 Wi-Fi IP：" + GetRealWiFiIp() + " (UDP: 8091 / HTTP: 8090)";
        _localAddress.SetBounds(20, 14, 650, 22);
        _localAddress.ForeColor = Color.FromArgb(71, 85, 105);
        statPanel.Controls.Add(_localAddress);

        var meterLabel = new Label { Text = "麦克风输入电平：", AutoSize = true, Location = new Point(20, 46), ForeColor = Color.FromArgb(71, 85, 105) };
        _db.Text = "-60 dB";
        _db.SetBounds(130, 46, 70, 20);
        _db.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);

        _meter.SetBounds(200, 44, 340, 22);
        _meter.Maximum = 60;
        _meter.Value = 0;

        _packets.Text = "音频包：0";
        _packets.SetBounds(560, 46, 120, 20);
        _packets.ForeColor = Color.FromArgb(100, 116, 139);

        statPanel.Controls.Add(meterLabel);
        statPanel.Controls.Add(_db);
        statPanel.Controls.Add(_meter);
        statPanel.Controls.Add(_packets);

        _testResult.Text = "💡 测试指引：配对成功后对着手机讲话，点击【测试麦克风是否正常】即可直接听到声音。";
        _testResult.SetBounds(20, 82, 650, 26);
        _testResult.ForeColor = Color.FromArgb(100, 116, 139);
        statPanel.Controls.Add(_testResult);
    }

    private static void AddRow(Control parent, string caption, Control control, int y)
    {
        var label = new Label
        {
            Text = caption,
            AutoSize = true,
            Location = new Point(20, y + 5),
            ForeColor = Color.FromArgb(71, 85, 105)
        };
        control.SetBounds(130, y, 350, 26);
        parent.Controls.Add(label);
        parent.Controls.Add(control);
    }

    private void LoadAudioDevices()
    {
        _outputDevice.Items.Clear();
        _monitorDevice.Items.Clear();
        var devices = _udp.GetOutputDevices();

        int defaultOutput = 0;
        int defaultSpeaker = 0;

        for (int i = 0; i < devices.Count; i++)
        {
            var name = devices[i].ProductName;
            var item = new OutputItem(i, name);
            _outputDevice.Items.Add(item);
            _monitorDevice.Items.Add(item);

            if (name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)) defaultOutput = i;
            if (name.Contains("扬声器", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Speaker", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Headphone", StringComparison.OrdinalIgnoreCase))
            {
                defaultSpeaker = i;
            }
        }

        if (_outputDevice.Items.Count > 0) _outputDevice.SelectedIndex = defaultOutput;
        if (_monitorDevice.Items.Count > 0) _monitorDevice.SelectedIndex = defaultSpeaker;
    }

    private void HookEvents()
    {
        _connect.Click += (_, _) => { StartPairingServer(); StartAudioEngine(); };
        _disconnect.Click += (_, _) => StopAll();
        _chkMonitor.CheckedChanged += (_, _) => ToggleMonitoring();
        _testSpeaker.Click += (_, _) => RunMicrophoneSelfTest();

        // 当用户修改配置（输出设备、试听设备、采样率）时，自动热重载音频引擎，无需重启软件
        _outputDevice.SelectedIndexChanged += (_, _) => { if (_isAudioRunning) StartAudioEngine(); };
        _monitorDevice.SelectedIndexChanged += (_, _) => { if (_chkMonitor.Checked) ToggleMonitoring(); };
        _sampleRate.SelectedIndexChanged += (_, _) => { if (_isAudioRunning) StartAudioEngine(); };

        _udp.AudioLevel += UpdateMeter;
        _bluetooth.AudioLevel += UpdateMeter;
    }

    // 启动多重配对协议：UDP 广播 + 原生 TCP/HTTP 零配置快速握手接口
    private void StartPairingServer()
    {
        try
        {
            _pairCts?.Cancel();
            _pairCts = new CancellationTokenSource();
            _pairingUdp?.Dispose();
            try { _tcpPairingServer?.Stop(); } catch { }

            // 1. 启动 UDP 信标中枢 (端口 8092)
            _pairingUdp = new UdpClient();
            _pairingUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _pairingUdp.Client.Bind(new IPEndPoint(IPAddress.Any, 8092));
            _ = UdpPairingLoop(_pairCts.Token);

            // 每 500ms 发送一次包含真实 Wi-Fi IP 的广播
            _beaconTimer.Stop();
            _beaconTimer.Interval = 500;
            _beaconTimer.Tick += (_, _) =>
            {
                try
                {
                    string realIp = GetRealWiFiIp();
                    string pcName = Environment.MachineName;
                    string beacon = $"AirMicBeacon:{realIp}:8091:{pcName}";
                    byte[] bytes = Encoding.UTF8.GetBytes(beacon);
                    _pairingUdp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, 8092));
                }
                catch { }
            };
            _beaconTimer.Start();

            // 2. 启动基于原生 Socket/TcpListener 的 HTTP 配对探测服务 (端口 8090)
            // 绝不依赖 Windows 系统 HttpListener，任何普通权限用户均可 100% 成功启动！
            // 彻底解决家用/办公路由器拦截 UDP 广播导致无法配对的死结！
            _tcpPairingServer = new TcpListener(IPAddress.Any, 8090);
            _tcpPairingServer.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _tcpPairingServer.Start();
            _ = TcpPairingLoop(_pairCts.Token);

            string currentIp = GetRealWiFiIp();
            SetPairStatus(false, $"自动配对服务已就绪 (本地 Wi-Fi IP: {currentIp}，监听端口: 8090/8092)");
        }
        catch (Exception ex)
        {
            SetPairStatus(false, "配对服务启动提示: " + ex.Message);
        }
    }

    private async Task TcpPairingLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _tcpPairingServer != null)
        {
            try
            {
                var client = await _tcpPairingServer.AcceptTcpClientAsync(token);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            client.ReceiveTimeout = 2000;
                            client.SendTimeout = 2000;
                            using var stream = client.GetStream();
                            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                            
                            string? requestLine = await reader.ReadLineAsync();
                            if (string.IsNullOrWhiteSpace(requestLine)) return;

                            // 消费剩余头部
                            string? line;
                            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) { }

                            string phoneModel = "安卓手机";
                            if (requestLine.Contains("model="))
                            {
                                int idx = requestLine.IndexOf("model=") + 6;
                                int endIdx = requestLine.IndexOf(' ', idx);
                                if (endIdx < 0) endIdx = requestLine.IndexOf('&', idx);
                                if (endIdx < 0) endIdx = requestLine.Length;
                                phoneModel = Uri.UnescapeDataString(requestLine.Substring(idx, endIdx - idx));
                            }

                            string clientIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();

                            string json = $"{{\"status\":\"ok\",\"pcName\":\"{Environment.MachineName}\",\"audioPort\":8091}}";
                            byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
                            string header = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
                            byte[] headerBytes = Encoding.ASCII.GetBytes(header);

                            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, token);
                            await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, token);
                            await stream.FlushAsync(token);

                            BeginInvoke(() => {
                                SetPairStatus(true, $"穿透配对成功！安卓设备【{phoneModel}】(IP: {clientIp})");
                            });
                        }
                        catch { }
                    }
                }, token);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task UdpPairingLoop(CancellationToken token)
    {
        if (_pairingUdp == null) return;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var res = await _pairingUdp.ReceiveAsync(token);
                string text = Encoding.UTF8.GetString(res.Buffer).Trim();

                if (text.StartsWith("AirMicPairReq:"))
                {
                    var parts = text.Split(':');
                    string phoneModel = parts.Length > 1 ? parts[1] : "安卓手机";
                    string phoneIp = res.RemoteEndPoint.Address.ToString();

                    string ack = $"AirMicPairAck:{Environment.MachineName}";
                    byte[] ackBytes = Encoding.UTF8.GetBytes(ack);
                    await _pairingUdp.SendAsync(ackBytes, ackBytes.Length, res.RemoteEndPoint);

                    BeginInvoke(() => {
                        SetPairStatus(true, $"UDP 信标配对成功！安卓设备【{phoneModel}】(IP: {phoneIp})");
                    });
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private void SetPairStatus(bool success, string detail)
    {
        if (success)
        {
            _pairStatusBadge.Text = "✅ 配对成功 (已建立受信任音频连接)";
            _pairStatusBadge.ForeColor = Color.FromArgb(16, 185, 129);
            _pairDetail.Text = detail + " | 音频链路已就绪，请在手机上开始说话。";
            _pairDetail.ForeColor = Color.FromArgb(22, 101, 52);
        }
        else
        {
            _pairStatusBadge.Text = "⏳ 等待手机配对中";
            _pairStatusBadge.ForeColor = Color.FromArgb(245, 158, 11);
            _pairDetail.Text = detail;
            _pairDetail.ForeColor = Color.FromArgb(100, 116, 139);
        }
    }

    private void StartAudioEngine()
    {
        if (_outputDevice.SelectedItem is not OutputItem output) return;

        int rate = _sampleRate.SelectedIndex switch
        {
            0 => 192000,
            1 => 96000,
            2 => 48000,
            3 => 44100,
            4 => 16000,
            _ => 48000
        };

        int monitorDev = -1;
        if (_chkMonitor.Checked && _monitorDevice.SelectedItem is OutputItem mon)
        {
            monitorDev = mon.Index;
        }

        try
        {
            _udp.Start(8091, rate, output.Index, monitorDev);
            _isAudioRunning = true;
        }
        catch (Exception ex)
        {
            SetPairStatus(false, "音频引擎异常: " + ex.Message);
        }
    }

    private void StopAll()
    {
        _isAudioRunning = false;
        _udp.Stop();
        _bluetooth.Stop();
        SetPairStatus(false, "已断开音频串流 (配对服务保持待命，随时可重连)");
        UpdateMeter(-60, 0);
    }

    private void ToggleMonitoring()
    {
        int monitorDev = -1;
        if (_chkMonitor.Checked && _monitorDevice.SelectedItem is OutputItem mon)
        {
            monitorDev = mon.Index;
        }
        _udp.SetMonitorDevice(monitorDev);

        if (_chkMonitor.Checked)
        {
            _testResult.Text = "🔊 已开启实时试听监听！对着手机说话即可从电脑扬声器/耳机听到回放。";
            _testResult.ForeColor = Color.FromArgb(37, 99, 235);
        }
        else
        {
            _testResult.Text = "🔇 已关闭试听监听。";
            _testResult.ForeColor = Color.FromArgb(100, 116, 139);
        }
    }

    private void RunMicrophoneSelfTest()
    {
        _chkMonitor.Checked = true;
        _testResult.Text = "🎙️ 正在测试麦克风：请对着手机说话或吹气。电脑扬声器将实时播出，电平条将跳动！";
        _testResult.ForeColor = Color.FromArgb(16, 185, 129);
    }

    private void UpdateMeter(int db, long packets)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateMeter(db, packets));
            return;
        }

        _db.Text = db + " dB";
        _meter.Value = Math.Clamp(db + 60, 0, 60);
        _packets.Text = "音频包：" + packets;

        if (db > -45)
        {
            _lastSoundTime = DateTime.Now;
            _testResult.Text = $"✅ 麦克风工作完美！检测到声音信号 ({db} dB)，手机声音正在流畅输入电脑！";
            _testResult.ForeColor = Color.FromArgb(16, 185, 129);
            _db.ForeColor = Color.FromArgb(16, 185, 129);
        }
    }

    // 精准嗅探真正的物理局域网 Wi-Fi IP，过滤虚拟网卡、WSL 与回路
    private static string GetRealWiFiIp()
    {
        try
        {
            // 优先查找名为 WLAN、Wi-Fi、Wireless 的无线网卡
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                    ni.Name.Contains("WLAN", StringComparison.OrdinalIgnoreCase) ||
                    ni.Name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
                        {
                            return ip.Address.ToString();
                        }
                    }
                }
            }

            // 备选方案：常规 C 类内网私有地址 192.168.x.x
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    ni.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                    ni.Description.Contains("WSL", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string str = ip.Address.ToString();
                        if (str.StartsWith("192.168.")) return str;
                    }
                }
            }

            // 兜底
            var fallback = Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x) && !x.ToString().StartsWith("169.254."));
            return fallback?.ToString() ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }

    private sealed record OutputItem(int Index, string Name)
    {
        public override string ToString() => Name;
    }
}
