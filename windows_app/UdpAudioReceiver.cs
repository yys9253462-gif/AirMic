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
    private ushort _expectedSequence;
    private readonly SortedDictionary<ushort, byte[]> _reorder = new();
    private long _lostPackets;

    public event Action<int, long>? AudioLevel;
    public event Action<string>? Status;
    public event Action<byte[], int, int>? PcmDataReceived;

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
        _lostPackets = 0;
        _expectedSequence = 0;
        _reorder.Clear();
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
                if (frame.Length < 10 || frame[0] != 0x41 || (frame[1] != 0x4D && frame[1] != 0x52)) continue;
                ushort sequence = BitConverter.ToUInt16(frame, 2);
                int headerSize = frame[1] == 0x52 ? 12 : 8;
                if (frame.Length <= headerSize) continue;
                if (frame[1] == 0x52)
                {
                    int negotiatedRate = BitConverter.ToInt32(frame, 8);
                    if (negotiatedRate >= 8000 && negotiatedRate <= 192000 && negotiatedRate != _sampleRate)
                    {
                        Status?.Invoke($"已协商手机采样率 {negotiatedRate} Hz，请保持两端采样率一致");
                    }
                }
                var payload = new byte[frame.Length - headerSize];
                Buffer.BlockCopy(frame, headerSize, payload, 0, payload.Length);
                lock (_reorder) _reorder[sequence] = payload;
                while (true)
                {
                    byte[]? ready = null;
                    lock (_reorder)
                    {
                        if (_reorder.TryGetValue(_expectedSequence, out ready)) _reorder.Remove(_expectedSequence);
                        else if (_reorder.Count > 8)
                        {
                            _lostPackets++;
                            _expectedSequence++;
                            continue;
                        }
                    }
                    if (ready == null) break;
                    ProcessPayload(ready);
                    _expectedSequence++;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Status?.Invoke("音频流接收中断: " + ex.Message);
                await Task.Delay(200, token).ConfigureAwait(false);
            }
        }
    }

    private void ProcessPayload(byte[] payload)
    {
        _mainBuffer?.AddSamples(payload, 0, payload.Length);
        _monitorBuffer?.AddSamples(payload, 0, payload.Length);
        PcmDataReceived?.Invoke(payload, 0, payload.Length);
        _packets++;
        int db = CalculateDb(payload, 0, payload.Length);
        AudioLevel?.Invoke(db, _packets);
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
        lock (_reorder) _reorder.Clear();
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
