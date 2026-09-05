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

    private readonly Panel _pairCard = new();
    private readonly Label _pairStatusBadge = new();
    private readonly Label _pairDetail = new();
    private readonly Label _localAddress = new();
    private readonly Label _db = new();
    private readonly Label _packets = new();
    private readonly Label _testResult = new();
    private readonly ProgressBar _meter = new();

    private DateTime _lastSoundTime = DateTime.MinValue;
    private bool _isAudioRunning = false;

    // 现代配色规范
    private static readonly Color BgColor = Color.FromArgb(248, 250, 252);        // #F8FAFC
    private static readonly Color CardBg = Color.White;
    private static readonly Color CardBorder = Color.FromArgb(226, 232, 240);    // #E2E8F0
    private static readonly Color TextMain = Color.FromArgb(15, 23, 42);          // #0F172A
    private static readonly Color TextSub = Color.FromArgb(71, 85, 105);          // #475569
    private static readonly Color TextMuted = Color.FromArgb(148, 163, 184);      // #94A3B8
    private static readonly Color AccentBlue = Color.FromArgb(37, 99, 235);       // #2563EB
    private static readonly Color AccentGreen = Color.FromArgb(16, 185, 129);     // #10B981
    private static readonly Color AccentYellow = Color.FromArgb(217, 119, 6);     // #D97706
    private static readonly Color AccentRed = Color.FromArgb(239, 68, 68);        // #EF4444

    public MainForm()
    {
        Text = "AirMic - 手机无线麦克风电脑端 (v1.0.0 Pro)";
        // 允许用户自由缩放窗口，设置合理初始尺寸与最小边界
        ClientSize = new Size(860, 720);
        MinimumSize = new Size(760, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgColor;
        Font = new Font("Microsoft YaHei UI", 9F);
        // 关键：基于 Dpi 自动缩放，并在代码中用锚定布局保证任何分辨率与缩放比例下不溢出、不重叠
        AutoScaleMode = AutoScaleMode.Dpi;

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
        // 外部全局滚动容器，保障极端分辨率或小窗口时也能轻松完整浏览
        var scrollContainer = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = BgColor,
            Padding = new Padding(25, 18, 25, 25)
        };
        Controls.Add(scrollContainer);

        // 顶部品牌横幅 (自适应宽度)
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 70,
            BackColor = Color.Transparent
        };
        scrollContainer.Controls.Add(headerPanel);

        var iconLabel = new Label
        {
            Text = "🎙️",
            Font = new Font("Segoe UI Emoji", 26F),
            Location = new Point(0, 0),
            Size = new Size(50, 56),
            TextAlign = ContentAlignment.MiddleCenter
        };
        headerPanel.Controls.Add(iconLabel);

        var title = new Label
        {
            Text = "AirMic 手机无线麦克风系统",
            Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
            ForeColor = TextMain,
            AutoSize = true,
            Location = new Point(54, 4)
        };
        var subtitle = new Label
        {
            Text = "零配置免输入 IP · 局域网全自动秒级配对 · 20ms 超低延迟 · 最高 192kHz 无损解析",
            ForeColor = TextMuted,
            Font = new Font("Microsoft YaHei UI", 9F),
            AutoSize = true,
            Location = new Point(56, 36)
        };
        headerPanel.Controls.Add(title);
        headerPanel.Controls.Add(subtitle);

        // 1. 配对状态大卡片
        SetupCardPanel(_pairCard, 0, 80, 800, 100);
        _pairCard.BackColor = Color.FromArgb(254, 252, 232); // 柔和琥珀黄
        _pairCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        scrollContainer.Controls.Add(_pairCard);

        var pairTitle = new Label 
        { 
            Text = "双端互通状态：", 
            AutoSize = true, 
            Location = new Point(20, 18), 
            ForeColor = TextSub, 
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold) 
        };
        _pairCard.Controls.Add(pairTitle);

        _pairStatusBadge.Text = "⏳ 等待手机配对中 (请在手机端打开 AirMic)";
        _pairStatusBadge.AutoSize = true;
        _pairStatusBadge.Location = new Point(130, 16);
        _pairStatusBadge.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
        _pairStatusBadge.ForeColor = AccentYellow;
        _pairCard.Controls.Add(_pairStatusBadge);

        _pairDetail.Text = "自动配对服务运行中... 手机打开 App 后无需输入 IP，秒级自动探测连接。";
        _pairDetail.SetBounds(22, 52, 740, 36);
        _pairDetail.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _pairDetail.Font = new Font("Microsoft YaHei UI", 9F);
        _pairDetail.ForeColor = TextSub;
        _pairCard.Controls.Add(_pairDetail);

        // 2. 音频配置卡片 (加大高度至 230，彻底杜绝采样率文字截断)
        var configPanel = new Panel();
        SetupCardPanel(configPanel, 0, 195, 800, 235);
        configPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        scrollContainer.Controls.Add(configPanel);

        var configHeader = new Label
        {
            Text = "⚙️ 核心音频与设备配置",
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold),
            ForeColor = TextMain,
            Location = new Point(20, 15),
            AutoSize = true
        };
        configPanel.Controls.Add(configHeader);

        int y = 50;
        AddStyledRow(configPanel, "连接传输方式", _mode, y);
        _mode.Items.AddRange(new object[] { "Wi-Fi 局域网 (默认推荐 / 零配置全自动配对)", "蓝牙 RFCOMM (SPP)" });
        _mode.SelectedIndex = 0;
        y += 44;

        AddStyledRow(configPanel, "主音频输出设备", _outputDevice, y);
        y += 44;

        AddStyledRow(configPanel, "实时试听监听", _monitorDevice, y, hasMonitorCheck: true);
        y += 44;

        AddStyledRow(configPanel, "音频采样规格", _sampleRate, y);
        _sampleRate.Items.AddRange(new object[] { 
            "192000 Hz (192kHz 旗舰发烧级)", 
            "96000 Hz (96kHz 专业录音棚级)", 
            "48000 Hz (48kHz 广播级 / 默认推荐)", 
            "44100 Hz (44.1kHz CD母带标准)", 
            "16000 Hz (16kHz 极速低带宽对话)" 
        });
        _sampleRate.SelectedIndex = 2;

        // 3. 现代化操作按钮栏
        var btnPanel = new Panel
        {
            Location = new Point(0, 442),
            Size = new Size(800, 50),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.Transparent
        };
        scrollContainer.Controls.Add(btnPanel);

        StylePrimaryButton(_connect, "⚡ 重置配对与接收", 0, 2, 175, 42, AccentBlue);
        StyleOutlineButton(_disconnect, "断开连接", 188, 2, 115, 42);
        StylePrimaryButton(_testSpeaker, "🔊 麦克风自检回放 (测试)", 315, 2, 225, 42, AccentGreen);

        btnPanel.Controls.Add(_connect);
        btnPanel.Controls.Add(_disconnect);
        btnPanel.Controls.Add(_testSpeaker);

        // 4. 实时动态电平与诊断卡片
        var statPanel = new Panel();
        SetupCardPanel(statPanel, 0, 502, 800, 150);
        statPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        scrollContainer.Controls.Add(statPanel);

        _localAddress.Text = "📍 本机局域网 Wi-Fi IP：" + GetRealWiFiIp() + "  |  音频传输端口: 8091  |  配对握手: 8090/8092";
        _localAddress.SetBounds(20, 16, 750, 24);
        _localAddress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _localAddress.Font = new Font("Microsoft YaHei UI", 9F);
        _localAddress.ForeColor = TextSub;
        statPanel.Controls.Add(_localAddress);

        var meterLabel = new Label 
        { 
            Text = "麦克风实时动态电平：", 
            AutoSize = true, 
            Location = new Point(20, 52), 
            ForeColor = TextMain,
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold)
        };
        statPanel.Controls.Add(meterLabel);

        _db.Text = "-60 dB";
        _db.SetBounds(165, 52, 65, 20);
        _db.Font = new Font("Consolas", 10.5F, FontStyle.Bold);
        _db.ForeColor = TextMuted;
        statPanel.Controls.Add(_db);

        _meter.SetBounds(235, 50, 410, 24);
        _meter.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _meter.Maximum = 60;
        _meter.Value = 0;
        statPanel.Controls.Add(_meter);

        _packets.Text = "已收数据包：0";
        _packets.SetBounds(660, 52, 120, 20);
        _packets.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _packets.Font = new Font("Consolas", 9F);
        _packets.ForeColor = TextMuted;
        statPanel.Controls.Add(_packets);

        _testResult.Text = "💡 测试指引：配对成功后对着手机讲话，点击【麦克风自检回放】即可从耳机直接听取回放与时延表现。";
        _testResult.SetBounds(20, 95, 750, 38);
        _testResult.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _testResult.Font = new Font("Microsoft YaHei UI", 9F);
        _testResult.ForeColor = TextMuted;
        statPanel.Controls.Add(_testResult);
    }

    private static void SetupCardPanel(Panel panel, int x, int y, int width, int height)
    {
        panel.Location = new Point(x, y);
        panel.Size = new Size(width, height);
        panel.BackColor = CardBg;
        panel.Paint += (s, e) =>
        {
            var p = (Panel)s!;
            using var pen = new Pen(CardBorder, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
        };
    }

    private void AddStyledRow(Control parent, string caption, ComboBox control, int y, bool hasMonitorCheck = false)
    {
        var label = new Label
        {
            Text = caption,
            AutoSize = true,
            Location = new Point(20, y + 4),
            ForeColor = TextSub,
            Font = new Font("Microsoft YaHei UI", 9.5F)
        };
        parent.Controls.Add(label);

        if (hasMonitorCheck)
        {
            // 带复选框的特殊行：下拉框宽度留出空间给复选框
            control.SetBounds(135, y, 420, 28);
            control.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _chkMonitor.Text = "启用耳机/扬声器耳返";
            _chkMonitor.AutoSize = true;
            _chkMonitor.SetBounds(570, y + 3, 190, 26);
            _chkMonitor.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _chkMonitor.ForeColor = AccentBlue;
            _chkMonitor.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            _chkMonitor.Cursor = Cursors.Hand;
            parent.Controls.Add(_chkMonitor);
        }
        else
        {
            // 常规行：下拉框自适应铺满卡片宽度
            control.SetBounds(135, y, 620, 28);
            control.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        }

        control.DropDownStyle = ComboBoxStyle.DropDownList;
        control.FlatStyle = FlatStyle.System;
        parent.Controls.Add(control);
    }

    private static void StylePrimaryButton(Button btn, string text, int x, int y, int w, int h, Color bg)
    {
        btn.Text = text;
        btn.SetBounds(x, y, w, h);
        btn.BackColor = bg;
        btn.ForeColor = Color.White;
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderSize = 0;
        btn.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        btn.Cursor = Cursors.Hand;
    }

    private static void StyleOutlineButton(Button btn, string text, int x, int y, int w, int h)
    {
        btn.Text = text;
        btn.SetBounds(x, y, w, h);
        btn.BackColor = Color.White;
        btn.ForeColor = TextSub;
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderColor = CardBorder;
        btn.Font = new Font("Microsoft YaHei UI", 9.5F);
        btn.Cursor = Cursors.Hand;
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
            _pairCard.BackColor = Color.FromArgb(240, 253, 244); // 柔和薄荷绿
            _pairStatusBadge.Text = "✅ 配对成功 (已建立受信任音频连接)";
            _pairStatusBadge.ForeColor = AccentGreen;
            _pairDetail.Text = detail + " | 音频链路已就绪，请在手机上开始说话。";
            _pairDetail.ForeColor = Color.FromArgb(22, 101, 52);
        }
        else
        {
            _pairCard.BackColor = Color.FromArgb(254, 252, 232); // 柔和琥珀黄
            _pairStatusBadge.Text = "⏳ 等待手机配对中";
            _pairStatusBadge.ForeColor = AccentYellow;
            _pairDetail.Text = detail;
            _pairDetail.ForeColor = TextSub;
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
            _testResult.Text = "🔊 已开启实时试听耳返！对着手机说话即可从电脑扬声器/耳机听到声音。";
            _testResult.ForeColor = AccentBlue;
        }
        else
        {
            _testResult.Text = "🔇 已关闭实时试听耳返。";
            _testResult.ForeColor = TextMuted;
        }
    }

    private void RunMicrophoneSelfTest()
    {
        _chkMonitor.Checked = true;
        _testResult.Text = "🎙️ 正在自检回放：请对着手机说话或吹气。电脑扬声器将实时播出，电平条将跳动！";
        _testResult.ForeColor = AccentGreen;
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
        _packets.Text = "已收数据包：" + packets;

        if (db > -45)
        {
            _lastSoundTime = DateTime.Now;
            _testResult.Text = $"✅ 麦克风工作完美！检测到声音信号 ({db} dB)，手机声音正在低延迟输入电脑！";
            _testResult.ForeColor = AccentGreen;
            _db.ForeColor = AccentGreen;
        }
        else
        {
            _db.ForeColor = TextMuted;
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
                        string s = ip.Address.ToString();
                        if (s.StartsWith("192.168.") && !IPAddress.IsLoopback(ip.Address))
                        {
                            return s;
                        }
                    }
                }
            }

            // 兜底方案
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
                    {
                        return ip.Address.ToString();
                    }
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }
}

internal sealed record OutputItem(int Index, string Name)
{
    public override string ToString() => Name;
}
