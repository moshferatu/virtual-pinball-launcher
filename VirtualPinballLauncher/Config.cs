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
    /// <summary>Holds the Playfield and Backglass preview video folders.</summary>
    public string PreviewsDirectory { get; set; } = "";
    public string VpxExePath { get; set; } = "";
    public string VideoExtension { get; set; } = ".mp4";
    public int RecordSeconds { get; set; } = 30;
    public int RecordDelaySeconds { get; set; } = 10;

    [JsonIgnore]
    public string PlayfieldPreviewsDirectory => Path.Combine(PreviewsDirectory, "Playfield");

    [JsonIgnore]
    public string BackglassPreviewsDirectory => Path.Combine(PreviewsDirectory, "Backglass");

    /// <summary>The playfield and backglass video paths for a table, by its file name.</summary>
    public (string Playfield, string Backglass) PreviewVideoPaths(string tableName)
    {
        var file = tableName + (string.IsNullOrWhiteSpace(VideoExtension) ? ".mp4" : VideoExtension);
        return (Path.Combine(PlayfieldPreviewsDirectory, file), Path.Combine(BackglassPreviewsDirectory, file));
    }

    /// <summary>Colors and placement of the overlays.</summary>
    public Appearance Appearance { get; set; } = new();

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
        var loaded = Deserialize(json);

        // Older files predate the key binding list and the appearance section. Write
        // the defaults back so those sections are visible and editable without
        // hand-typing them. Settings this launcher no longer has are dropped at the same time.
        var removed = loaded.AdditionalFields?.Keys.Where(IsRemovedField).ToList() ?? new();
        foreach (var name in removed) loaded.AdditionalFields!.Remove(name);
        var present = TopLevelPropertyNames(json);
        if (!present.Contains(nameof(KeyBindings)) || !present.Contains(nameof(Appearance)) || removed.Count > 0)
            await SaveAsync(path, loaded);

        return loaded;
    }

    public static Task SaveAsync(string path, Config config) =>
        File.WriteAllTextAsync(path, Serialize(config));

    public static string Serialize(Config config) => JsonSerializer.Serialize(config, SerializerOptions);

    public static Config Deserialize(string json)
    {
        var config = JsonSerializer.Deserialize<Config>(json, SerializerOptions) ?? new Config();
        config.Appearance ??= new Appearance();
        return config;
    }

    private static readonly string[] RemovedFields =
        { "ReturnToLauncherHotkey", "VideosDirectory", "BackglassVideosDirectory" };

    private static bool IsRemovedField(string name) =>
        RemovedFields.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> TopLevelPropertyNames(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
            foreach (var property in document.RootElement.EnumerateObject())
                names.Add(property.Name);
        return names;
    }
}
