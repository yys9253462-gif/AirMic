using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AirMic.Windows;

internal sealed class UdpAudioReceiver : IDisposable
{
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private BufferedWaveProvider? _mainBuffer;
    private BufferedWaveProvider? _monitorBuffer;
    private WaveOutEvent? _mainWaveOut;
    private WaveOutEvent? _monitorWaveOut;
    private int _sampleRate;
    private long _packets;

    public event Action<int, long>? AudioLevel;
    public event Action<string>? Status;

    public IReadOnlyList<WaveOutCapabilities> GetOutputDevices()
    {
        var devices = new List<WaveOutCapabilities>();
        for (int i = 0; i < WaveOut.DeviceCount; i++) devices.Add(WaveOut.GetCapabilities(i));
        return devices;
    }

    public void Start(int port, int sampleRate, int outputDeviceNumber, int monitorDeviceNumber = -1)
    {
        Stop();
        _sampleRate = sampleRate;
        _packets = 0;
        _cts = new CancellationTokenSource();

        var format = new WaveFormat(sampleRate, 16, 1);

        // 主音频设备 (通常选 CABLE Input 或默认系统设备)
        // 将缓冲时长缩减至 60ms，DesiredLatency 缩减至 20ms，实现近乎零感知延迟！
        _mainBuffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromMilliseconds(60),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        _mainWaveOut = new WaveOutEvent
        {
            DeviceNumber = outputDeviceNumber,
            DesiredLatency = 20,
            NumberOfBuffers = 2
        };
        _mainWaveOut.Init(_mainBuffer);
        _mainWaveOut.Play();

        // 耳机/扬声器测试监听设备 (如果启用)
        SetMonitorDevice(monitorDeviceNumber);

        // 启动 UDP 音频流监听，支持端口复用以防止快速重启冲突
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));

        Status?.Invoke($"正在监听 UDP 端口 {port}，音频引擎已就绪");
        _ = ReceiveLoop(_cts.Token);
    }

    public void SetMonitorDevice(int monitorDeviceNumber)
    {
        if (monitorDeviceNumber < 0)
        {
            _monitorWaveOut?.Stop();
            _monitorWaveOut?.Dispose();
            _monitorWaveOut = null;
            _monitorBuffer = null;
            return;
        }

        try
        {
            _monitorWaveOut?.Stop();
            _monitorWaveOut?.Dispose();

            var format = new WaveFormat(_sampleRate > 0 ? _sampleRate : 48000, 16, 1);
            _monitorBuffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromMilliseconds(60),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            _monitorWaveOut = new WaveOutEvent
            {
                DeviceNumber = monitorDeviceNumber,
                DesiredLatency = 20,
                NumberOfBuffers = 2
            };
            _monitorWaveOut.Init(_monitorBuffer);
            _monitorWaveOut.Play();
        }
        catch { }
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        if (_udp is null) return;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(token);
                var frame = result.Buffer;
                // AirMic 协议帧: 0x41 0x4D + 2字节序列号 + 4字节时间戳 + PCM 数据
                if (frame.Length < 10 || frame[0] != 0x41 || frame[1] != 0x4D) continue;

                int pcmLength = frame.Length - 8;
                if (pcmLength <= 0) continue;

                // 注入主播放管线
                _mainBuffer?.AddSamples(frame, 8, pcmLength);

                // 同步注入耳机/扬声器监听管线 (测试用)
                _monitorBuffer?.AddSamples(frame, 8, pcmLength);

                _packets++;
                int db = CalculateDb(frame, 8, pcmLength);
                AudioLevel?.Invoke(db, _packets);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Status?.Invoke("音频流接收中断: " + ex.Message);
                await Task.Delay(200, token).ConfigureAwait(false);
            }
        }
    }

    private static int CalculateDb(byte[] data, int offset, int count)
    {
        double sum = 0;
        int samples = count / 2;
        for (int i = 0; i < samples; i++)
        {
            short value = BitConverter.ToInt16(data, offset + i * 2);
            sum += value * value;
        }
        double rms = Math.Sqrt(sum / Math.Max(1, samples));
        return (int)Math.Max(-60, 20 * Math.Log10(Math.Max(rms / 32768d, 0.001)));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;

        _mainWaveOut?.Stop();
        _mainWaveOut?.Dispose();
        _mainWaveOut = null;
        _mainBuffer = null;

        _monitorWaveOut?.Stop();
        _monitorWaveOut?.Dispose();
        _monitorWaveOut = null;
        _monitorBuffer = null;

        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();
}
