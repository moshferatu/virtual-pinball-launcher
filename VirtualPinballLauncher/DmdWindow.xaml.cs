using System.Globalization;
using System.Windows;

namespace VirtualPinballLauncher;

public partial class DmdWindow : Window
{
    public DmdWindow()
    {
        InitializeComponent();
        ApplyAppearance(new Appearance());
    }

    public void ApplyAppearance(Appearance appearance)
    {
        TableTitleText.Foreground = appearance.TitleBrush;
        PropertiesText.Foreground = appearance.DmdValueBrush;
        var labelBrush = appearance.DmdLabelBrush;
        TimesPlayedLabel.Foreground = labelBrush;
        TotalTimePlayedLabel.Foreground = labelBrush;
        LastPlayedLabel.Foreground = labelBrush;
    }

    public void ShowTableProperties(string tableDisplayName, TableStatsEntry? stats)
    {
        TableTitleText.Text = tableDisplayName;
        PropertiesText.Visibility = stats == null ? Visibility.Collapsed : Visibility.Visible;
        TimesPlayedValue.Text = stats?.TimesPlayed.ToString(CultureInfo.CurrentCulture) ?? "";
        TotalTimePlayedValue.Text = stats == null ? "" : FormatDuration(stats.TotalTimeSpentPlaying);
        LastPlayedValue.Text = stats == null ? "" :
            stats.LastDatePlayed?.ToString(TableStats.LastDateFormat, CultureInfo.InvariantCulture) ?? "Never";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var totalHours = (int)Math.Floor(duration.TotalHours);
        var totalMinutes = duration.Minutes;

        if (totalHours == 0)
            return Plural(totalMinutes, "minute");

        if (totalMinutes == 0)
            return Plural(totalHours, "hour");

        return $"{Plural(totalHours, "hour")} {Plural(totalMinutes, "minute")}";
    }

    private static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
}
