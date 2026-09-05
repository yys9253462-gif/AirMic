using System;
using System.IO;
using System.Text.Json;

namespace AirMic.Windows;

public sealed class AppConfig
{
    public string ApiUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string AsrModel { get; set; } = "whisper-1";
    public string TranslationModel { get; set; } = "gpt-4o-mini";
    public bool AutoTranslate { get; set; } = true;
    public bool EnableSubtitle { get; set; } = false;
    public bool ShowLyricBar { get; set; } = true;
    public int ProviderIndex { get; set; } = 0;
}

public static class ConfigManager
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AirMic"
    );
    private static readonly string ConfigFilePath = Path.Combine(ConfigDir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                string json = File.ReadAllText(ConfigFilePath);
                var config = JsonSerializer.Deserialize<AppConfig>(json);
                if (config != null) return config;
            }
        }
        catch { }

        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        try
        {
            if (!Directory.Exists(ConfigDir))
            {
                Directory.CreateDirectory(ConfigDir);
            }
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFilePath, json);
        }
        catch { }
    }
}
