using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AirMic.Windows;

internal sealed class BluetoothAudioReceiver : IDisposable
{
    public static readonly Guid SppUuid = BluetoothService.SerialPort;
    private BluetoothClient? _client;
    private CancellationTokenSource? _cts;
    private BufferedWaveProvider? _buffer;
    private WaveOutEvent? _waveOut;
    private long _packets;

    public event Action<int, long>? AudioLevel;
    public event Action<string>? Status;
    public event Action<byte[], int, int>? PcmDataReceived;

    public async Task<IReadOnlyList<BluetoothDeviceInfo>> DiscoverAsync()
    {
        using var client = new BluetoothClient();
        return await Task.Run(() => client.DiscoverDevices(255, true, true, true, false));
    }

    public async Task ConnectAsync(BluetoothDeviceInfo device, int sampleRate, int outputDeviceNumber)
    {
        Stop();
        _cts = new CancellationTokenSource();
        _buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromMilliseconds(400),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        _waveOut = new WaveOutEvent { DeviceNumber = outputDeviceNumber, DesiredLatency = 90, NumberOfBuffers = 3 };
        _waveOut.Init(_buffer);
        _waveOut.Play();
        _client = new BluetoothClient();
        Status?.Invoke("正在连接蓝牙设备 " + device.DeviceName + "...");
        await Task.Run(() => _client.Connect(device.DeviceAddress, SppUuid), _cts.Token);
        Status?.Invoke("蓝牙已连接: " + device.DeviceName);
        _ = ReadLoop(_client.GetStream(), _cts.Token);
    }

    private async Task ReadLoop(Stream stream, CancellationToken token)
    {
        byte[] prefix = new byte[4];
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ReadExact(stream, prefix, token);
                int length = (prefix[0] << 24) | (prefix[1] << 16) | (prefix[2] << 8) | prefix[3];
                if (length < 8 || length > 262144) throw new InvalidDataException("非法蓝牙音频帧长度");
                byte[] frame = new byte[length];
                await ReadExact(stream, frame, token);
                if (frame[0] != 0x41 || frame[1] != 0x4D) continue;
                int pcmLength = frame.Length - 8;
                _buffer?.AddSamples(frame, 8, pcmLength);
                PcmDataReceived?.Invoke(frame, 8, pcmLength);
                _packets++;
                AudioLevel?.Invoke(CalculateDb(frame, 8, pcmLength), _packets);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Status?.Invoke("蓝牙断开: " + ex.Message);
                break;
            }
        }
    }

    private static async Task ReadExact(Stream stream, byte[] buffer, CancellationToken token)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), token);
            if (n == 0) throw new EndOfStreamException();
            read += n;
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
        _client?.Dispose();
        _client = null;
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _buffer = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();
}
