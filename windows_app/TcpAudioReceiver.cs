using NAudio.Wave;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AirMic.Windows;

internal sealed class TcpAudioReceiver : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private BufferedWaveProvider? _buffer;
    private WaveOutEvent? _waveOut;
    private int _sampleRate;
    private long _packets;
    public event Action<int, long>? AudioLevel;
    public event Action<string>? Status;
    public event Action<byte[], int, int>? PcmDataReceived;

    public void Start(int port, int sampleRate, int outputDeviceNumber)
    {
        Stop(); _sampleRate = sampleRate; _packets = 0; _cts = new CancellationTokenSource();
        _buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1)) { BufferDuration = TimeSpan.FromMilliseconds(400), DiscardOnBufferOverflow = true, ReadFully = true };
        _waveOut = new WaveOutEvent { DeviceNumber = outputDeviceNumber, DesiredLatency = 40, NumberOfBuffers = 2 };
        _waveOut.Init(_buffer); _waveOut.Play();
        _listener = new TcpListener(IPAddress.Loopback, port); _listener.Start();
        Status?.Invoke($"USB TCP 音频监听已启动: {port}"); _ = AcceptLoop(_cts.Token);
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _listener != null)
            {
                using var client = await _listener.AcceptTcpClientAsync(token);
                Status?.Invoke("USB 音频已连接");
                using var stream = client.GetStream();
                var lenBytes = new byte[4];
                while (!token.IsCancellationRequested)
                {
                    await ReadExact(stream, lenBytes, token);
                    int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lenBytes, 0));
                    if (length < 10 || length > 262144) throw new InvalidDataException("非法 USB 音频帧");
                    var frame = new byte[length]; await ReadExact(stream, frame, token);
                    if (frame[0] != 0x41 || (frame[1] != 0x4D && frame[1] != 0x52)) continue;
                    int header = frame[1] == 0x52 ? 12 : 8;
                    _buffer?.AddSamples(frame, header, length - header);
                    PcmDataReceived?.Invoke(frame, header, length - header);
                    _packets++; AudioLevel?.Invoke(CalculateDb(frame, header, length - header), _packets);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) Status?.Invoke("USB 音频断开: " + ex.Message); }
    }

    private static async Task ReadExact(Stream stream, byte[] buffer, CancellationToken token)
    { int read = 0; while (read < buffer.Length) { int n = await stream.ReadAsync(buffer.AsMemory(read), token); if (n == 0) throw new EndOfStreamException(); read += n; } }
    private static int CalculateDb(byte[] data, int offset, int count)
    { double sum = 0; int samples = count / 2; for (int i = 0; i < samples; i++) { short v = BitConverter.ToInt16(data, offset + i * 2); sum += v * v; } return (int)Math.Max(-60, 20 * Math.Log10(Math.Max(Math.Sqrt(sum / Math.Max(1, samples)) / 32768d, 0.001))); }
    public void Stop() { _cts?.Cancel(); try { _listener?.Stop(); } catch { } _listener = null; _waveOut?.Stop(); _waveOut?.Dispose(); _waveOut = null; _buffer = null; _cts?.Dispose(); _cts = null; }
    public void Dispose() => Stop();
}
