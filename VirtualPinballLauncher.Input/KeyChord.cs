using System.Windows.Input;

namespace VirtualPinballLauncher.Input;

/// <summary>
/// A single key ("Escape") or a two-key chord ("F12+Escape"): the first key is held
/// as a modifier while the second is pressed. Key names follow the WPF <see cref="Key"/>
/// enumeration, with a few friendlier aliases such as "1" for D1 and "Esc" for Escape.
/// </summary>
public sealed record KeyChord(Key? Modifier, Key Key)
{
    public bool IsChord => Modifier != null;

    public int ModifierVirtualKey => Modifier is { } modifier ? KeyNames.ToVirtualKey(modifier) : 0;
    public int VirtualKey => KeyNames.ToVirtualKey(Key);

    public static KeyChord Parse(string text)
    {
        if (!TryParse(text, out var chord, out var error))
            throw new FormatException(error);
        return chord;
    }

    public static bool TryParse(string? text, out KeyChord chord, out string error)
    {
        chord = null!;
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Key name is empty.";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || parts.Any(string.IsNullOrEmpty))
        {
            error = $"'{text}' must be a single key or two keys joined with '+', such as 'F12+Escape'.";
            return false;
        }

        var keys = new Key[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!KeyNames.TryParse(parts[i], out keys[i]))
            {
                error = $"'{parts[i]}' in '{text}' is not a recognized key name.";
                return false;
            }
        }

        if (keys.Length == 2 && keys[0] == keys[1])
        {
            error = $"'{text}' uses the same key twice.";
            return false;
        }

        chord = keys.Length == 1 ? new KeyChord(null, keys[0]) : new KeyChord(keys[0], keys[1]);
        return true;
    }

    public override string ToString() =>
        Modifier is { } modifier ? $"{KeyNames.ToName(modifier)}+{KeyNames.ToName(Key)}" : KeyNames.ToName(Key);
}

public static class KeyNames
{
    private static readonly Dictionary<string, Key> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0"] = Key.D0, ["1"] = Key.D1, ["2"] = Key.D2, ["3"] = Key.D3, ["4"] = Key.D4,
        ["5"] = Key.D5, ["6"] = Key.D6, ["7"] = Key.D7, ["8"] = Key.D8, ["9"] = Key.D9,
        ["Esc"] = Key.Escape,
        ["Return"] = Key.Enter,
        ["Del"] = Key.Delete,
        ["Ins"] = Key.Insert,
        ["PgUp"] = Key.PageUp,
        ["PgDn"] = Key.PageDown,
        ["Ctrl"] = Key.LeftCtrl,
        ["Shift"] = Key.LeftShift,
        ["Alt"] = Key.LeftAlt,
        ["Spacebar"] = Key.Space,
    };

    public static bool TryParse(string? name, out Key key)
    {
        key = Key.None;
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim();
        if (Aliases.TryGetValue(name, out key)) return true;
        if (Enum.TryParse(name, ignoreCase: true, out key) && key != Key.None && Enum.IsDefined(key))
            return true;
        key = Key.None;
        return false;
    }

    public static int ToVirtualKey(Key key) => KeyInterop.VirtualKeyFromKey(key);

    public static Key FromVirtualKey(int virtualKey) => KeyInterop.KeyFromVirtualKey(virtualKey);

    /// <summary>Prefers the familiar spelling over the enum's canonical member name.</summary>
    public static string ToName(Key key) => key switch
    {
        Key.Enter => "Enter",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4",
        Key.D5 => "5", Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9",
        _ => key.ToString()
    };
}
