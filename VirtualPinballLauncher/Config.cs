using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace VirtualPinballLauncher;

public sealed class Config
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public string TablesDirectory { get; set; } = "";
    public string VideosDirectory { get; set; } = "";
    public string BackglassVideosDirectory { get; set; } = "";
    public string VpxExePath { get; set; } = @"";
    public string VideoExtension { get; set; } = ".mp4";
    public int RecordSeconds { get; set; } = 30;
    public string FfmpegPath { get; set; } = "ffmpeg";

    public static async Task<Config> LoadAsync(string path)
    {
        if (!File.Exists(path))
        {
            var config = new Config();
            await SaveAsync(path, config);
            return config;
        }

        var json = await File.ReadAllTextAsync(path);
        var loaded = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return loaded ?? new Config();
    }

    public static Task SaveAsync(string path, Config config)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        return File.WriteAllTextAsync(path, json);
    }
}