using System.Text.Json.Serialization;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace VirtualPinballLauncher;

/// <summary>
/// Colors and placement of the launcher's overlays. Colors accept anything WPF can parse,
/// such as "Gold", "#FFCC00" or "#DDFF0000" (with alpha). A value that can't be parsed
/// falls back to its default.
/// </summary>
public sealed class Appearance
{
    private const string DefaultAccentColor = "Red";
    private const string DefaultTitleColor = "Yellow";
    private const string DefaultOverlayBackground = "#DD000000";
    private const string DefaultDmdLabelColor = "DimGray";
    private const string DefaultDmdValueColor = "White";
    private const double DefaultOverlayBottomMargin = 200;

    /// <summary>Top and bottom borders of the table name and volume overlays.</summary>
    public string AccentColor { get; set; } = DefaultAccentColor;

    /// <summary>Table name on the playfield and DMD, and the lit volume bars.</summary>
    public string TitleColor { get; set; } = DefaultTitleColor;

    /// <summary>Panel behind the table name. The volume panel uses it fully opaque so the name can't show through.</summary>
    public string OverlayBackground { get; set; } = DefaultOverlayBackground;

    /// <summary>Stat labels on the DMD, such as "Times Played:".</summary>
    public string DmdLabelColor { get; set; } = DefaultDmdLabelColor;

    /// <summary>Stat values on the DMD.</summary>
    public string DmdValueColor { get; set; } = DefaultDmdValueColor;

    /// <summary>Distance from the bottom of the playfield screen to the table name and volume overlays.</summary>
    public double OverlayBottomMargin { get; set; } = DefaultOverlayBottomMargin;

    [JsonIgnore] public Brush AccentBrush => Frozen(Parse(AccentColor, DefaultAccentColor));
    [JsonIgnore] public Brush TitleBrush => Frozen(Parse(TitleColor, DefaultTitleColor));
    [JsonIgnore] public Brush OverlayBackgroundBrush => Frozen(Parse(OverlayBackground, DefaultOverlayBackground));
    [JsonIgnore] public Brush DmdLabelBrush => Frozen(Parse(DmdLabelColor, DefaultDmdLabelColor));
    [JsonIgnore] public Brush DmdValueBrush => Frozen(Parse(DmdValueColor, DefaultDmdValueColor));

    /// <summary>The overlay background with no transparency.</summary>
    [JsonIgnore]
    public Brush OpaqueOverlayBackgroundBrush
    {
        get
        {
            var color = Parse(OverlayBackground, DefaultOverlayBackground);
            color.A = 0xFF;
            return Frozen(color);
        }
    }

    /// <summary>The title color dimmed to a quarter of its opacity, for unlit volume bars.</summary>
    [JsonIgnore]
    public Brush DimTitleBrush
    {
        get
        {
            var color = Parse(TitleColor, DefaultTitleColor);
            color.A = (byte)(color.A / 4);
            return Frozen(color);
        }
    }

    [JsonIgnore]
    public double SafeOverlayBottomMargin =>
        double.IsFinite(OverlayBottomMargin) && OverlayBottomMargin >= 0 ? OverlayBottomMargin : DefaultOverlayBottomMargin;

    /// <summary>Parses a WPF color string, or returns the fallback when it isn't one.</summary>
    public static Color Parse(string? value, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            try
            {
                if (ColorConverter.ConvertFromString(value.Trim()) is Color color) return color;
            }
            catch (FormatException)
            {
            }
        }
        return (Color)ColorConverter.ConvertFromString(fallback);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
