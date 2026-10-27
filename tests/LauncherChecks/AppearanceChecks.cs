using System.IO;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using VirtualPinballLauncher;

internal static class AppearanceChecks
{
    public static void Run(string directory)
    {
        CheckConfig(directory);
        CheckColors();
        CheckWindows();
    }

    private static void CheckConfig(string directory)
    {
        var json = Config.Serialize(new Config());
        Check(json.Contains("\"Appearance\": {") && json.Contains("\"AccentColor\": \"Red\"")
            && json.Contains("\"OverlayBottomMargin\": 200"),
            "Default config.json includes the Appearance section");

        var partial = Config.Deserialize("""{ "Appearance": { "AccentColor": "#00FF00" } }""").Appearance;
        Check(partial.AccentColor == "#00FF00" && partial.TitleColor == "Yellow" && partial.DmdLabelColor == "DimGray",
            "A partial Appearance section keeps defaults for the missing settings");
        Check(Config.Deserialize("""{ "Appearance": null }""").Appearance.TitleColor == "Yellow",
            "A null Appearance section falls back to defaults");

        var oldFile = Path.Combine(directory, "appearance-old-config.json");
        File.WriteAllText(oldFile, """{ "TablesDirectory": "D:\\Tables", "KeyBindings": [] }""");
        Config.LoadAsync(oldFile).GetAwaiter().GetResult();
        Check(File.ReadAllText(oldFile).Contains("\"Appearance\""),
            "Loading an older config.json writes the Appearance section into it");
    }

    private static void CheckColors()
    {
        var appearance = new Appearance
        {
            TitleColor = "#80FFCC00",
            OverlayBackground = "#40102030",
            AccentColor = "not a color",
            DmdValueColor = "",
            OverlayBottomMargin = -5
        };
        Check(Color(appearance.AccentBrush) == Colors.Red && Color(appearance.DmdValueBrush) == Colors.White,
            "Invalid or empty colors fall back to their defaults");
        Check(Color(appearance.TitleBrush) == System.Windows.Media.Color.FromArgb(0x80, 0xFF, 0xCC, 0x00),
            "Hex colors with alpha are parsed");
        Check(Color(appearance.DimTitleBrush).A == 0x20, "Unlit volume bars use a quarter of the title color's opacity");
        Check(Color(appearance.OpaqueOverlayBackgroundBrush) == System.Windows.Media.Color.FromRgb(0x10, 0x20, 0x30),
            "The volume panel uses the overlay background without transparency");
        Check(appearance.SafeOverlayBottomMargin == 200, "A negative overlay margin falls back to the default");
        Check(Color(new Appearance { TitleColor = " gold " }.TitleBrush) == Colors.Gold, "Named colors are parsed");
    }

    private static void CheckWindows()
    {
        var appearance = new Appearance
        {
            AccentColor = "Blue",
            TitleColor = "Lime",
            OverlayBackground = "#80000080",
            DmdLabelColor = "Gray",
            DmdValueColor = "Orange",
            OverlayBottomMargin = 120
        };

        var dmd = new DmdWindow();
        try
        {
            Check(Color(((TextBlock)dmd.FindName("TableTitleText")).Foreground) == Colors.Yellow
                && Color(((Run)dmd.FindName("LastPlayedLabel")).Foreground) == Colors.DimGray,
                "The DMD starts with the default colors");
            dmd.ApplyAppearance(appearance);
            Check(Color(((TextBlock)dmd.FindName("TableTitleText")).Foreground) == Colors.Lime
                && Color(((TextBlock)dmd.FindName("PropertiesText")).Foreground) == Colors.Orange
                && Color(((Run)dmd.FindName("TimesPlayedLabel")).Foreground) == Colors.Gray
                && Color(((Run)dmd.FindName("LastPlayedLabel")).Foreground) == Colors.Gray,
                "The DMD applies the configured title, label, and value colors");
        }
        finally { dmd.Close(); }

        var volume = new VolumeOverlayWindow();
        try
        {
            volume.ApplyAppearance(appearance);
            var panel = (Border)volume.FindName("VolumePanel");
            var bars = (StackPanel)volume.FindName("VolumeBars");
            Check(Color(panel.BorderBrush) == Colors.Blue && Color(panel.Background) == Colors.Navy
                && panel.Margin.Bottom == 120,
                "The volume overlay applies the accent, opaque background, and bottom margin");
            Check(bars.Children.OfType<System.Windows.Shapes.Rectangle>().All(line => Color(line.Fill).A == 0xFF / 4),
                "Unlit volume bars use the dimmed title color");
        }
        finally { volume.Close(); }
    }

    private static System.Windows.Media.Color Color(Brush brush) => ((SolidColorBrush)brush).Color;

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine($"PASS: {description}");
    }
}
