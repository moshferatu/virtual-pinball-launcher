using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VirtualPinballLauncher;

public sealed class TableStats
{
    private const string TableStatsCsvFileName = "table-stats.csv";
    private const string TableStatsHeader = "Table Name,Times Played,Total Time Spent Playing,Last Date Played";
    private const string LastDateFormat = "MMMM d, yyyy";

    private List<TableStatsEntry>? cachedTableStats;
    private string? cachedTableStatsCsvPath;

    public List<TableStatsEntry> GetAllTableStats(string tablesDirectory)
    {
        var statsCsvPath = GetTableStatsCsvPath(tablesDirectory);
        return GetCachedTableStats(statsCsvPath);
    }

    public TableStatsEntry GetTableStatsForDisplay(string tablesDirectory, string tableName)
    {
        var statsCsvPath = GetTableStatsCsvPath(tablesDirectory);
        var allStats = GetCachedTableStats(statsCsvPath);
        return FindTableStats(allStats, tableName) ?? new TableStatsEntry(tableName);
    }

    public void UpdateTableStatsAfterPlay(string tablesDirectory, string tableName, TimeSpan elapsedPlayTime)
    {
        var statsCsvPath = GetTableStatsCsvPath(tablesDirectory);
        var allStats = LoadOrCreateTableStats(statsCsvPath);
        var tableStats = FindTableStats(allStats, tableName);

        if (tableStats is null)
        {
            tableStats = new TableStatsEntry(tableName);
            allStats.Add(tableStats);
        }

        tableStats.TimesPlayed += 1;
        tableStats.TotalTimeSpentPlaying += elapsedPlayTime;
        tableStats.LastDatePlayed = DateTime.Today;

        SaveTableStats(statsCsvPath, allStats);

        cachedTableStats = allStats;
        cachedTableStatsCsvPath = statsCsvPath;
    }

    public string FormatTableStatsForDisplay(TableStatsEntry tableStats)
    {
        var lastPlayedText = tableStats.LastDatePlayed.HasValue
            ? tableStats.LastDatePlayed.Value.ToString(LastDateFormat, CultureInfo.InvariantCulture)
            : "Never";

        return string.Join(Environment.NewLine, new[]
        {
            $"Times Played: {tableStats.TimesPlayed}",
            $"Total Time Played: {FormatDuration(tableStats.TotalTimeSpentPlaying)}",
            $"Last Played: {lastPlayedText}"
        });
    }

    private static string GetTableStatsCsvPath(string tablesDirectory)
    {
        var baseDirectory = string.IsNullOrWhiteSpace(tablesDirectory)
            ? AppContext.BaseDirectory
            : tablesDirectory;

        return Path.Combine(baseDirectory, TableStatsCsvFileName);
    }

    private static List<TableStatsEntry> LoadOrCreateTableStats(string csvPath)
    {
        if (!File.Exists(csvPath))
        {
            SaveTableStats(csvPath, new List<TableStatsEntry>());
            return new List<TableStatsEntry>();
        }

        var tableStats = new List<TableStatsEntry>();
        var firstNonEmptyLineFound = false;

        foreach (var line in File.ReadLines(csvPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!firstNonEmptyLineFound)
            {
                firstNonEmptyLineFound = true;
                if (string.Equals(line.Trim(), TableStatsHeader, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var fields = ParseCsvLine(line);
            if (fields.Count != 4)
                continue;

            var tableName = fields[0].Trim();
            if (string.IsNullOrWhiteSpace(tableName))
                continue;

            var timesPlayed = 0;
            if (int.TryParse(fields[1], out var parsedTimesPlayed) && parsedTimesPlayed >= 0)
                timesPlayed = parsedTimesPlayed;

            var totalPlayTime = TimeSpan.Zero;
            if (TimeSpan.TryParse(fields[2], CultureInfo.InvariantCulture, out var parsedPlayTime) && parsedPlayTime >= TimeSpan.Zero)
                totalPlayTime = parsedPlayTime;

            DateTime? lastDatePlayed = null;
            if (DateTime.TryParseExact(fields[3], LastDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedLastDate))
                lastDatePlayed = parsedLastDate;

            tableStats.Add(new TableStatsEntry(tableName)
            {
                TimesPlayed = timesPlayed,
                TotalTimeSpentPlaying = totalPlayTime,
                LastDatePlayed = lastDatePlayed
            });
        }

        return tableStats;
    }

    private List<TableStatsEntry> GetCachedTableStats(string csvPath)
    {
        if (cachedTableStats != null && string.Equals(cachedTableStatsCsvPath, csvPath, StringComparison.OrdinalIgnoreCase))
            return cachedTableStats;

        cachedTableStatsCsvPath = csvPath;
        cachedTableStats = LoadOrCreateTableStats(csvPath);
        return cachedTableStats;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static void SaveTableStats(string csvPath, List<TableStatsEntry> tableStats)
    {
        var lines = new List<string> { TableStatsHeader };

        foreach (var stats in tableStats.OrderBy(s => s.TableName, StringComparer.OrdinalIgnoreCase))
        {
            var lastDate = stats.LastDatePlayed.HasValue
                ? stats.LastDatePlayed.Value.ToString(LastDateFormat, CultureInfo.InvariantCulture)
                : string.Empty;

            lines.Add(string.Join(",",
                EscapeCsvValue(stats.TableName),
                stats.TimesPlayed.ToString(CultureInfo.InvariantCulture),
                EscapeCsvValue(stats.TotalTimeSpentPlaying.ToString("c", CultureInfo.InvariantCulture)),
                EscapeCsvValue(lastDate)));
        }

        File.WriteAllLines(csvPath, lines);
    }

    private static string EscapeCsvValue(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static TableStatsEntry? FindTableStats(List<TableStatsEntry> tableStats, string tableName)
    {
        return tableStats.FirstOrDefault(s => string.Equals(s.TableName, tableName, StringComparison.OrdinalIgnoreCase));
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

public sealed class TableStatsEntry
{
    public TableStatsEntry(string tableName)
    {
        TableName = tableName;
    }

    public string TableName { get; }
    public int TimesPlayed { get; set; }
    public TimeSpan TotalTimeSpentPlaying { get; set; }
    public DateTime? LastDatePlayed { get; set; }
}
