using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualPinballLauncher;

public sealed class Config
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Keep "F12+1" and "Devil's Crush" readable instead of + and '.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string TablesDirectory { get; set; } = "";
    public string VideosDirectory { get; set; } = "";
    public string BackglassVideosDirectory { get; set; } = "";
    public string VpxExePath { get; set; } = @"";
    public string VideoExtension { get; set; } = ".mp4";
    public int RecordSeconds { get; set; } = 30;
    public int RecordDelaySeconds { get; set; } = 10;

    /// <summary>Every key the launcher reacts to, including global chords.</summary>
    public List<KeyBinding> KeyBindings { get; set; } = DefaultKeyBindings();

    /// <summary>Preserves fields this version does not know about when the file is rewritten.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalFields { get; set; }

    public static List<KeyBinding> DefaultKeyBindings() => new()
    {
        KeyBinding.For(LauncherAction.NextTable, "Down", "Right", "RightShift", "PageDown"),
        KeyBinding.For(LauncherAction.PreviousTable, "Up", "Left", "LeftShift", "PageUp"),
        KeyBinding.For(LauncherAction.LaunchTable, "Enter", "Space", "1", "NumPad1"),
        KeyBinding.For(LauncherAction.RandomTable, "F12"),
        KeyBinding.For(LauncherAction.ToggleSort, "LeftCtrl", "RightCtrl"),
        KeyBinding.For(LauncherAction.RecordPreview, "R"),
        KeyBinding.For(LauncherAction.Exit, "Escape"),
        KeyBinding.For(LauncherAction.VolumeDown, "5+Left"),
        KeyBinding.For(LauncherAction.VolumeUp, "5+Right"),
        KeyBinding.RunProgram("F12+1", @"C:\Devil's Crush\devils-crush.exe", "--cabinet --music-voice-overlay", @"C:\Devil's Crush"),
        KeyBinding.RunProgram("F12+5", @"C:\Alien Crush\Launch-Cabinet.cmd"),
        KeyBinding.RunProgram("F12+Escape", @"C:\Tetris\TetrisOLED\bin\Debug\net8.0-windows\TetrisOLED.exe")
    };

    public static async Task<Config> LoadAsync(string path)
    {
        if (!File.Exists(path))
        {
            var config = new Config();
            await SaveAsync(path, config);
            return config;
        }

        var json = await File.ReadAllTextAsync(path);
        var loaded = JsonSerializer.Deserialize<Config>(json, SerializerOptions) ?? new Config();

        // Older files predate the key binding list. Write the defaults back so the
        // section is visible and editable without hand-typing it. Settings this
        // launcher no longer has are dropped at the same time.
        var removed = loaded.AdditionalFields?.Keys.Where(IsRemovedField).ToList() ?? new();
        foreach (var name in removed) loaded.AdditionalFields!.Remove(name);
        if (!HasProperty(json, nameof(KeyBindings)) || removed.Count > 0)
            await SaveAsync(path, loaded);

        return loaded;
    }

    public static Task SaveAsync(string path, Config config) =>
        File.WriteAllTextAsync(path, Serialize(config));

    public static string Serialize(Config config) => JsonSerializer.Serialize(config, SerializerOptions);

    public static Config Deserialize(string json) =>
        JsonSerializer.Deserialize<Config>(json, SerializerOptions) ?? new Config();

    private static bool IsRemovedField(string name) =>
        name.Equals("ReturnToLauncherHotkey", StringComparison.OrdinalIgnoreCase);

    private static bool HasProperty(string json, string name)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in document.RootElement.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
