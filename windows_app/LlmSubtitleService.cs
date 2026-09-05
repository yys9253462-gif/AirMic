using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AirMic.Windows;

public sealed class LlmSubtitleService : IDisposable
{
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    
    public string ApiUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "whisper-1";
    public string TranslationModel { get; set; } = "gpt-4o-mini";
    public bool AutoTranslate { get; set; } = true;
    public bool IsEnabled { get; set; } = false;

    // 事件通知：原文字幕、英文字幕、状态信息
    public event Action<string, string>? SubtitleReceived; // (chineseText, englishText)
    public event Action<string>? StatusChanged;

    private readonly MemoryStream _audioBuffer = new();
    private readonly object _lock = new();
    private DateTime _lastFlushTime = DateTime.UtcNow;
    private int _sampleRate = 48000;
    private bool _isProcessing = false;

    public void SetSampleRate(int rate)
    {
        lock (_lock)
        {
            _sampleRate = rate;
            _audioBuffer.SetLength(0);
            _lastFlushTime = DateTime.UtcNow;
        }
    }

    public void FeedPcmData(byte[] pcmData, int offset, int length)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(ApiKey)) return;

        byte[]? chunkToProcess = null;
        int currentRate = _sampleRate;

        lock (_lock)
        {
            _audioBuffer.Write(pcmData, offset, length);
            
            // 每当音频积攒大约 2.5 ~ 3 秒（例如 48000 * 2(bytes) * 2.5s = 240,000 bytes），且没有正在处理时，提取进行语音识别
            double currentSeconds = (double)_audioBuffer.Length / (_sampleRate * 2);
            var elapsed = DateTime.UtcNow - _lastFlushTime;

            if ((currentSeconds >= 2.5 || (currentSeconds >= 1.5 && elapsed.TotalSeconds >= 3.5)) && !_isProcessing)
            {
                chunkToProcess = _audioBuffer.ToArray();
                _audioBuffer.SetLength(0);
                _lastFlushTime = DateTime.UtcNow;
                _isProcessing = true;
            }
            else if (currentSeconds > 8.0)
            {
                // 超过 8 秒未处理则丢弃旧数据，防止积压
                _audioBuffer.SetLength(0);
            }
        }

        if (chunkToProcess != null && chunkToProcess.Length > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessAudioChunkAsync(chunkToProcess, currentRate);
                }
                finally
                {
                    lock (_lock)
                    {
                        _isProcessing = false;
                    }
                }
            });
        }
    }

    private async Task ProcessAudioChunkAsync(byte[] pcmData, int sampleRate)
    {
        try
        {
            // 简单静音检测：如果整段音量太小，则跳过，避免无效调用
            if (IsSilent(pcmData))
            {
                return;
            }

            StatusChanged?.Invoke("AI 字幕：正在转写语音...");
            byte[] wavBytes = CreateWav(pcmData, sampleRate);

            // 1. 调用 Whisper ASR
            string originalText = await RequestTranscriptionAsync(wavBytes);
            if (string.IsNullOrWhiteSpace(originalText))
            {
                StatusChanged?.Invoke("AI 字幕：未检测到清晰语音");
                return;
            }

            string translatedText = "";
            if (AutoTranslate)
            {
                StatusChanged?.Invoke($"AI 字幕识别:「{originalText}」，正在翻译...");
                translatedText = await RequestTranslationAsync(originalText);
            }

            StatusChanged?.Invoke("AI 字幕：实时同步中");
            SubtitleReceived?.Invoke(originalText.Trim(), translatedText.Trim());
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("AI 字幕调用失败: " + ex.Message);
        }
    }

    private static bool IsSilent(byte[] pcmData)
    {
        long sum = 0;
        int samples = pcmData.Length / 2;
        if (samples == 0) return true;

        for (int i = 0; i < samples; i++)
        {
            short val = BitConverter.ToInt16(pcmData, i * 2);
            sum += Math.Abs(val);
        }
        double avg = (double)sum / samples;
        return avg < 200; // 极低底噪直接忽略
    }

    private async Task<string> RequestTranscriptionAsync(byte[] wavBytes)
    {
        string baseUrl = ApiUrl.TrimEnd('/');
        string endpoint = baseUrl.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase) 
            ? baseUrl 
            : $"{baseUrl}/audio/transcriptions";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey.Trim());

        using var content = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(wavBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audioContent, "file", "speech.wav");
        content.Add(new StringContent(Model), "model");
        content.Add(new StringContent("zh"), "language");
        content.Add(new StringContent("json"), "response_format");

        request.Content = content;

        using var response = await _httpClient.SendAsync(request);
        string json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"ASR 接口返回错误 ({(int)response.StatusCode}): {json}");
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("text", out var textProp))
        {
            return textProp.GetString() ?? "";
        }
        return "";
    }

    private async Task<string> RequestTranslationAsync(string text)
    {
        try
        {
            string baseUrl = ApiUrl.TrimEnd('/');
            // 修正 endpoint 为 /chat/completions
            string endpoint;
            if (baseUrl.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = baseUrl.Substring(0, baseUrl.LastIndexOf("/audio/transcriptions")) + "/chat/completions";
            }
            else if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = baseUrl;
            }
            else
            {
                endpoint = $"{baseUrl}/chat/completions";
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey.Trim());

            var payload = new
            {
                model = TranslationModel,
                temperature = 0.3,
                messages = new[]
                {
                    new { role = "system", content = "You are a professional real-time subtitle translator. Translate the following Chinese speech directly to fluent, natural English. Output ONLY the translated English text without any quotes, notes or explanation." },
                    new { role = "user", content = text }
                }
            };

            string body = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            string json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return $"(翻译接口响应异常: {(int)response.StatusCode})";
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var msg = choices[0].GetProperty("message");
                if (msg.TryGetProperty("content", out var contentProp))
                {
                    return contentProp.GetString()?.Trim() ?? "";
                }
            }
            return "";
        }
        catch (Exception ex)
        {
            return $"[翻译出错: {ex.Message}]";
        }
    }

    private static byte[] CreateWav(byte[] pcmData, int sampleRate)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        short channels = 1;
        short bitsPerSample = 16;
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        short blockAlign = (short)(channels * (bitsPerSample / 8));

        // RIFF header
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcmData.Length);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt subchunk
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); // Subchunk1Size (16 for PCM)
        bw.Write((short)1); // AudioFormat (1 for PCM)
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);

        // data subchunk
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(pcmData.Length);
        bw.Write(pcmData);

        return ms.ToArray();
    }

    public void Dispose()
    {
        _audioBuffer.Dispose();
    }
}
