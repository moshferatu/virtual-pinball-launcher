using System.IO;
using System.Windows;
using VirtualPinballLauncher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LauncherChecks-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var stats = new TableStats();
            Check(stats.GetAllTableStats(directory).Count == 0, "New stats file is empty");
            Check(stats.GetTableStatsForDisplay(directory, "Missing").TimesPlayed == 0,
                "Missing tables have default stats");

            const string name = "Table, with \"quotes\"";
            stats.UpdateTableStatsAfterPlay(directory, name, TimeSpan.FromMinutes(5));
            stats.UpdateTableStatsAfterPlay(directory, name.ToUpperInvariant(), TimeSpan.FromMinutes(7));
            var entry = stats.GetTableStatsForDisplay(directory, name.ToLowerInvariant());
            Check(entry.TimesPlayed == 2 && entry.TotalTimeSpentPlaying == TimeSpan.FromMinutes(12),
                "Case-insensitive updates refresh the cached index");
            Check(stats.GetAllTableStats(directory).Count == 1, "Case variants share an entry");
            entry = new TableStats().GetTableStatsForDisplay(directory, name);
            Check(entry.TimesPlayed == 2 && entry.LastDatePlayed == DateTime.Today,
                "Quoted CSV names and dates survive save/reload");

            var csvPath = Path.Combine(directory, "table-stats.csv");
            File.AppendAllText(csvPath, "Duplicate,3,00:03:00,\nDUPLICATE,9,00:09:00,\n");
            stats = new TableStats();
            Check(stats.GetTableStatsForDisplay(directory, "duplicate").TimesPlayed == 3,
                "Duplicate names retain the first matching row");
            stats.UpdateTableStatsAfterPlay(directory, "duplicate", TimeSpan.FromMinutes(1));
            Check(File.ReadAllLines(csvPath).Length == 4, "Saving preserves existing duplicate rows");
            Check(stats.GetAllTableStats(directory)["DUPLICATE"].TimesPlayed == 4,
                "Indexed updates use the first duplicate row");

            var otherDirectory = Path.Combine(directory, "other");
            Directory.CreateDirectory(otherDirectory);
            Check(stats.GetAllTableStats(otherDirectory).Count == 0, "Switching directories resets the cache");
            Check(stats.GetAllTableStats(directory).Count == 2, "Switching back reloads the right stats");

            var overlay = new LoadingOverlay();
            Check(overlay.Visibility == Visibility.Collapsed, "Overlay starts collapsed");
            overlay.SetLoading(true);
            Check(overlay.Visibility == Visibility.Visible, "Loading shows the overlay");
            overlay.SetLoading(false);
            Check(overlay.Visibility == Visibility.Collapsed, "Loading completion hides the overlay");
            Console.WriteLine("All launcher regression checks passed.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine($"PASS: {description}");
    }
}
