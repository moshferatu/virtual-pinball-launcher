using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace VirtualPinballLauncher;

public enum LauncherAction
{
    NextTable,
    PreviousTable,
    LaunchTable,
    RandomTable,
    ToggleSort,
    RecordPreview,
    Exit,
    Run,
    KillTable,
    VolumeUp,
    VolumeDown,
    ShutdownPc,
    RestartPc
}

/// <summary>One entry of the KeyBindings list in config.json.</summary>
public sealed class KeyBinding
{
    /// <summary>One key ("Escape"), a chord ("F12+Escape"), or a list of keys that share the action.</summary>
    [JsonConverter(typeof(KeyListJsonConverter))]
    public List<string> Keys { get; set; } = new();

    /// <summary>A <see cref="LauncherAction"/> name. Kept as text so a typo is reported rather than failing the whole config.</summary>
    public string Action { get; set; } = "";

    // Run action only.
    public string? Command { get; set; }
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }

    /// <summary>Run only: wait on (and activate) this process name instead of the launched process tree.</summary>
    public string? WaitForProcess { get; set; }

    /// <summary>Run only: if the program is already running, bring it to the front instead of starting another copy.</summary>
    public bool? SingleInstance { get; set; }

    /// <summary>Run only: console windows of the program (and its batch file) are hidden unless this is true.</summary>
    public bool? ShowConsole { get; set; }

    public static KeyBinding For(LauncherAction action, params string[] keys) =>
        new() { Keys = keys.ToList(), Action = action.ToString() };

    public static KeyBinding RunProgram(string keys, string command, string? arguments = null, string? workingDirectory = null) =>
        new() { Keys = new List<string> { keys }, Action = nameof(LauncherAction.Run), Command = command, Arguments = arguments, WorkingDirectory = workingDirectory };
}

/// <summary>Accepts "Keys": "F12" as well as "Keys": ["Up", "Left"], and writes a lone key back as a string.</summary>
public sealed class KeyListJsonConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new List<string> { reader.GetString() ?? "" };
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Keys must be a string or an array of strings.");

        var keys = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Keys must contain only strings.");
            keys.Add(reader.GetString() ?? "");
        }
        return keys;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        if (value.Count == 1)
        {
            writer.WriteStringValue(value[0]);
            return;
        }
        writer.WriteStartArray();
        foreach (var key in value) writer.WriteStringValue(key);
        writer.WriteEndArray();
    }
}

public sealed record ResolvedBinding(KeyChord Chord, LauncherAction Action, KeyBinding Source);

/// <summary>
/// The validated form of the KeyBindings list: single keys, which the launcher window
/// handles while it has focus, and chords, which the global hook handles everywhere.
/// </summary>
public sealed class KeyBindingMap
{
    private readonly Dictionary<Key, ResolvedBinding> singles = new();
    private readonly Dictionary<KeyChord, ResolvedBinding> chords = new();
    private readonly HashSet<Key> modifiers = new();
    private readonly List<string> errors = new();

    private KeyBindingMap() { }

    public IReadOnlyList<string> Errors => errors;
    public IReadOnlyCollection<KeyChord> Chords => chords.Keys;

    public bool TryGetSingle(Key key, out ResolvedBinding binding) => singles.TryGetValue(key, out binding!);
    public bool TryGetChord(KeyChord chord, out ResolvedBinding binding) => chords.TryGetValue(chord, out binding!);
    public bool IsModifier(Key key) => modifiers.Contains(key);

    public static KeyBindingMap Build(IEnumerable<KeyBinding> bindings)
    {
        var map = new KeyBindingMap();
        var index = 0;
        foreach (var binding in bindings)
        {
            index++;
            var label = $"KeyBindings entry {index}";
            if (!Enum.TryParse<LauncherAction>(binding.Action, ignoreCase: true, out var action))
            {
                map.errors.Add($"{label}: '{binding.Action}' is not a known action. Valid actions: {string.Join(", ", Enum.GetNames<LauncherAction>())}.");
                continue;
            }

            if (action == LauncherAction.Run && string.IsNullOrWhiteSpace(binding.Command))
            {
                map.errors.Add($"{label}: the Run action needs a Command.");
                continue;
            }

            if (binding.Keys.Count == 0)
            {
                map.errors.Add($"{label} ({action}): no Keys given.");
                continue;
            }

            foreach (var text in binding.Keys)
            {
                if (!KeyChord.TryParse(text, out var chord, out var error))
                {
                    map.errors.Add($"{label} ({action}): {error}");
                    continue;
                }

                var resolved = new ResolvedBinding(chord, action, binding);
                if (chord.IsChord)
                {
                    if (!map.chords.TryAdd(chord, resolved))
                        map.errors.Add($"{label} ({action}): '{chord}' is already bound to {map.chords[chord].Action}; the first binding wins.");
                    else
                        map.modifiers.Add(chord.Modifier!.Value);
                }
                else if (!map.singles.TryAdd(chord.Key, resolved))
                {
                    map.errors.Add($"{label} ({action}): '{chord}' is already bound to {map.singles[chord.Key].Action}; the first binding wins.");
                }
            }
        }
        return map;
    }
}
