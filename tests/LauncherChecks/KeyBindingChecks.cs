using System.IO;
using System.Windows.Input;
using VirtualPinballLauncher;
using VirtualPinballLauncher.Input;
using KeyBinding = VirtualPinballLauncher.KeyBinding;

internal static class KeyBindingChecks
{
    public static void Run()
    {
        CheckParsing();
        CheckDefaultMap();
        CheckValidation();
        CheckChordListener();
        CheckConfigRoundTrip();
    }

    private static void CheckParsing()
    {
        Check(KeyChord.Parse("F12+Escape") == new KeyChord(Key.F12, Key.Escape), "Chords parse as modifier plus key");
        Check(KeyChord.Parse("Esc") == new KeyChord(null, Key.Escape), "Esc is an alias for Escape");
        Check(KeyChord.Parse("1") == new KeyChord(null, Key.D1) && KeyChord.Parse("D1").Key == Key.D1,
            "Digits are accepted as '1' or 'D1'");
        Check(KeyChord.Parse(" f12 + 5 ") == new KeyChord(Key.F12, Key.D5), "Key names ignore case and spacing");
        Check(KeyChord.Parse("Return") == new KeyChord(null, Key.Enter) && KeyChord.Parse("PageDown").Key == Key.PageDown,
            "Enter and PageDown aliases resolve to the same keys WPF reports");
        Check(KeyChord.Parse("F12+Enter").ToString() == "F12+Enter" && KeyChord.Parse("D1").ToString() == "1",
            "Chords print in the familiar spelling");
        Check(!KeyChord.TryParse("Foo", out _, out var error) && error.Contains("Foo"), "Unknown key names are rejected by name");
        Check(!KeyChord.TryParse("F12+1+2", out _, out _), "Three-key chords are rejected");
        Check(!KeyChord.TryParse("F12+F12", out _, out _), "A chord cannot repeat its key");
        Check(!KeyChord.TryParse("", out _, out _) && !KeyChord.TryParse("None", out _, out _), "Empty and None are rejected");
        Check(KeyChord.Parse("F12+1").VirtualKey == 0x31 && KeyChord.Parse("F12+1").ModifierVirtualKey == 0x7B,
            "Chords expose the virtual keys the hook reports");
    }

    private static void CheckDefaultMap()
    {
        var map = KeyBindingMap.Build(Config.DefaultKeyBindings());
        Check(map.Errors.Count == 0, "Default key bindings validate cleanly: " + string.Join(" | ", map.Errors));
        Check(map.TryGetSingle(Key.RightShift, out var next) && next.Action == LauncherAction.NextTable,
            "Key lists expand so every listed key shares the action");
        Check(map.TryGetSingle(Key.D1, out var launch) && launch.Action == LauncherAction.LaunchTable,
            "Digit aliases bind the same key WPF reports for the cabinet button");
        Check(map.TryGetSingle(Key.Escape, out var exit) && exit.Action == LauncherAction.Exit, "Escape is bound to Exit");
        Check(map.TryGetSingle(Key.F12, out var randomTable) && randomTable.Action == LauncherAction.RandomTable
            && map.IsModifier(Key.F12), "F12 is both a single-key action and a chord modifier");
        Check(map.TryGetChord(new KeyChord(Key.D5, Key.Left), out var volume) && volume.Action == LauncherAction.VolumeDown
            && map.IsModifier(Key.D5), "Volume chords use the 5 key as a modifier");
        Check(map.TryGetChord(new KeyChord(Key.F12, Key.Escape), out var tetris) && tetris.Action == LauncherAction.Run
            && tetris.Source.Command!.EndsWith("TetrisOLED.exe"), "F12+Escape runs a program while Escape alone exits");
        Check(map.TryGetChord(new KeyChord(Key.F12, Key.D5), out var alien) && alien.Source.WaitForProcess == null,
            "Alien Crush waits on its own process tree rather than a named process");
        Check(map.Chords.Count == 5, "All five chords are registered");
    }

    private static void CheckValidation()
    {
        var map = KeyBindingMap.Build(new[]
        {
            KeyBinding.For(LauncherAction.RecordPreview, "R"),
            KeyBinding.For(LauncherAction.ToggleSort, "R", "T"),
            new KeyBinding { Keys = { "Q" }, Action = "Teleport" },
            new KeyBinding { Keys = { "F12+9" }, Action = "Run" },
            new KeyBinding { Keys = { "Bogus" }, Action = "NextTable" },
            KeyBinding.RunProgram("F12+8", @"C:\missing\game.exe"),
            KeyBinding.RunProgram("F12+8", @"C:\other\game.exe")
        });
        Check(map.Errors.Count == 5, $"Every problem is reported once: {string.Join(" | ", map.Errors)}");
        Check(map.Errors.Any(e => e.Contains("'R' is already bound to RecordPreview")), "Duplicate single keys are reported");
        Check(map.Errors.Any(e => e.Contains("'F12+8' is already bound to Run")), "Duplicate chords are reported");
        Check(map.Errors.Any(e => e.Contains("Teleport")), "Unknown actions are reported by name");
        Check(map.Errors.Any(e => e.Contains("needs a Command")), "Run without a Command is reported");
        Check(map.Errors.Any(e => e.Contains("Bogus")), "Unknown keys are reported by name");
        Check(map.TryGetSingle(Key.R, out var r) && r.Action == LauncherAction.RecordPreview, "The first binding of a key wins");
        Check(map.TryGetSingle(Key.T, out var t) && t.Action == LauncherAction.ToggleSort, "Valid keys in a partly bad list still load");
        Check(map.TryGetChord(new KeyChord(Key.F12, Key.D8), out var run) && run.Source.Command == @"C:\missing\game.exe",
            "A missing executable is kept and reported when the key is pressed, not dropped");
    }

    private static void CheckChordListener()
    {
        const int F12 = 0x7B, D1 = 0x31, D5 = 0x35, Left = 0x25, Enter = 0x0D, Escape = 0x1B;
        // Simulated physical keyboard state, so the listener's stale-modifier check has something to consult.
        var physicallyDown = new HashSet<int>();
        var listener = new ChordListener(new[]
        {
            new KeyChord(Key.F12, Key.D1), new KeyChord(Key.F12, Key.Escape), new KeyChord(Key.F12, Key.D5), new KeyChord(Key.D5, Key.Left)
        }, physicallyDown.Contains);
        var chords = new List<string>();
        var taps = new List<int>();
        var stale = new List<int>();
        listener.ChordPressed += chord => chords.Add(chord.ToString());
        listener.ModifierTapped += taps.Add;
        listener.StaleModifierDropped += stale.Add;
        bool Down(int vk) { physicallyDown.Add(vk); return listener.Handle(vk, true); }
        bool Up(int vk) { physicallyDown.Remove(vk); return listener.Handle(vk, false); }

        Check(!Down(Enter) && !Up(Enter), "Unbound keys pass through untouched");
        Check(!Down(Escape) && !Up(Escape) && chords.Count == 0, "A bare Escape is not a chord");

        Check(!Down(F12) && listener.IsModifierHeld, "The modifier itself passes through and is tracked as held");
        Check(!Down(F12), "Modifier auto-repeat is ignored");
        Check(Down(D1) && chords.SequenceEqual(new[] { "F12+1" }), "A chord key is swallowed and fires once");
        Check(Down(D1) && chords.Count == 1, "Chord key auto-repeat is swallowed without refiring");
        Check(Up(D1), "The chord key release is swallowed too");
        Check(!Down(Enter) && !Up(Enter), "Keys not in a chord pass through while the modifier is held");
        Check(!Up(F12) && taps.Count == 0 && !listener.IsModifierHeld, "Releasing the modifier after a chord is not a tap");

        Down(F12);
        Up(F12);
        Check(taps.SequenceEqual(new[] { F12 }), "Pressing and releasing the modifier alone is a tap");

        Down(F12);
        Check(Down(Escape) && chords.Last() == "F12+Escape", "F12+Escape fires as a chord");
        Up(Escape);
        Up(F12);
        Check(taps.Count == 1, "No tap after the F12+Escape chord");

        // The cabinet's 5 key completes F12+5 and is also the modifier of the volume chords.
        Down(F12);
        Check(Down(D5) && chords.Last() == "F12+5" && listener.IsModifierHeld, "A chord whose second key is itself a modifier still fires");
        Check(Up(D5), "That chord key's release is swallowed like any other");
        Check(!Down(Left) && !Up(Left), "Left is not a volume chord while F12, not 5, is the held modifier");
        Up(F12);
        Check(taps.Count == 1, "No tap after the F12+5 chord");

        var volumeDown = KeyChord.Parse("5+Left");
        Check(!listener.IsChordHeld(volumeDown), "A chord is not held before it fires");
        Down(D5);
        Check(Down(Left) && chords.Last() == "5+Left", "The volume chord uses its own modifier");
        Check(listener.IsChordHeld(volumeDown) && !listener.IsChordHeld(KeyChord.Parse("5+Right")),
            "A chord counts as held from the moment it fires, and only that chord");
        Check(!Down(D1), "Chord keys of a different modifier pass through");
        Down(Left);
        Check(listener.IsChordHeld(volumeDown), "Chord key auto-repeat keeps the chord held");
        Up(Left);
        Check(!listener.IsChordHeld(volumeDown), "Releasing the chord key ends the hold");
        Down(Left);
        physicallyDown.Remove(D5); // the modifier release never reaches the hook
        Check(!listener.IsChordHeld(volumeDown), "A chord is not held once its modifier is physically up, even if the hook missed the release");
        physicallyDown.Add(D5);
        Up(D5);
        Check(!listener.IsChordHeld(volumeDown), "Releasing the modifier ends the hold");
        Check(!Down(Left) && !Up(Left), "Left navigates again once 5 is released");

        // A chord key whose release was never delivered must not block the next press of the same chord.
        Down(F12);
        Check(Down(D1) && chords.Count == 6, "Chord fires before a lost release");
        physicallyDown.Remove(D1); // the release never reaches the hook
        Up(F12);
        Down(F12);
        Check(Down(D1) && chords.Count == 7, "The same chord fires again after its key release was lost");
        Up(D1);
        Up(F12);

        // A modifier whose release was never delivered must not keep every other key hostage.
        Down(F12);
        physicallyDown.Remove(F12); // the release never reaches the hook
        Check(!Down(Enter) && stale.SequenceEqual(new[] { F12 }) && !listener.IsModifierHeld,
            "A modifier that is no longer physically down is dropped on the next key");
        Up(Enter);
        Check(!Down(D1) && chords.Count == 7, "Chord keys pass through once the stale modifier is dropped");
        Up(D1);
        Check(taps.Count == 1, "Dropping a stale modifier is not reported as a tap");
        Console.WriteLine("PASS: Chord listener swallows chorded keys, reports modifier taps, heals lost releases, and passes everything else through");
    }

    private static void CheckConfigRoundTrip()
    {
        var json = Config.Serialize(new Config());
        Check(json.Contains("\"Keys\": \"Escape\"") && json.Contains("\"Keys\": ["), "Lone keys serialize as strings and lists as arrays");
        Check(!json.Contains("SingleInstance") && !json.Contains("WaitForProcess"), "Unused optional fields are omitted from config.json");
        Check(!json.Contains("ReturnToLauncherHotkey"), "The removed return hotkey setting is not written");

        var reloaded = Config.Deserialize(json);
        Check(reloaded.KeyBindings.Count == new Config().KeyBindings.Count
            && reloaded.KeyBindings.SelectMany(b => b.Keys).SequenceEqual(new Config().KeyBindings.SelectMany(b => b.Keys)),
            "Key bindings survive a save and reload");
        Check(reloaded.KeyBindings.Single(b => b.Keys.Contains("F12+1")).Arguments == "--cabinet --music-voice-overlay",
            "Run arguments survive a save and reload");

        var custom = Config.Deserialize("""
            {
              "TablesDirectory": "D:\\Tables",
              "FfmpegPath": "ffmpeg",
              "keybindings": [
                { "keys": ["Up", "Left"], "action": "previoustable" },
                { "Keys": "F12+Escape", "Action": "Run", "Command": "C:\\game.exe", "SingleInstance": true, "WaitForProcess": "game.exe" }
              ]
            }
            """);
        Check(custom.KeyBindings.Count == 2 && custom.KeyBindings[0].Keys.SequenceEqual(new[] { "Up", "Left" })
            && custom.KeyBindings[1].SingleInstance == true && custom.KeyBindings[1].WaitForProcess == "game.exe",
            "Keys accept a string or an array, and property names ignore case");
        var customMap = KeyBindingMap.Build(custom.KeyBindings);
        Check(customMap.Errors.Count == 0 && customMap.TryGetSingle(Key.Up, out var up) && up.Action == LauncherAction.PreviousTable,
            "Action names ignore case");
        Check(Config.Serialize(custom).Contains("\"FfmpegPath\": \"ffmpeg\""), "Unknown fields are preserved when config.json is rewritten");
        Check(Config.Deserialize("{ \"TablesDirectory\": \"D:\\\\Tables\" }").KeyBindings.Count == new Config().KeyBindings.Count,
            "A config without KeyBindings keeps the defaults");

        var oldFile = Path.Combine(Path.GetTempPath(), "LauncherChecks-config-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(oldFile, "{ \"FfmpegPath\": \"ffmpeg\", \"ReturnToLauncherHotkey\": \"F12+Enter\", \"KeyBindings\": [] }");
            Config.LoadAsync(oldFile).GetAwaiter().GetResult();
            var rewritten = File.ReadAllText(oldFile);
            Check(!rewritten.Contains("ReturnToLauncherHotkey") && rewritten.Contains("\"FfmpegPath\": \"ffmpeg\""),
                "Loading an old config.json drops the removed return hotkey and keeps other fields");
        }
        finally { File.Delete(oldFile); }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine($"PASS: {description}");
    }
}
