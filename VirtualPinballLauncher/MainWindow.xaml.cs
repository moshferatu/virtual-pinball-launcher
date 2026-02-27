using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;
using Cursors = System.Windows.Input.Cursors;

namespace VirtualPinballLauncher;

public partial class MainWindow : Window
{
    private const string TableStatsCsvFileName = "table-stats.csv";
    private const string TableStatsHeader = "Table Name,Times Played,Total Time Spent Playing,Last Date Played";
    private const string LastDateFormat = "MMMM d, yyyy";

    private Config config = null!;
    private List<TableItem> tableList = new();
    private int tableIndex = 0;

    private LibVLC vlcPlayer = null!;
    private MediaPlayer mediaPlayer = null!;
    private DmdWindow? dmdWindow;
    private BackglassWindow? backglassWindow;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

    private bool cursorHidden;

    private System.Windows.Threading.DispatcherTimer? navigationTimer;
    private long navigationGeneration;
    private List<TableStatsEntry>? cachedTableStats;
    private string? cachedTableStatsCsvPath;

    private CancellationTokenSource playbackCancellation = new();

    private void ForceHideCursor()
    {
        if (cursorHidden) return;

        // ShowCursor uses an internal display count.
        // Repeatedly call until the count is < 0 (meaning hidden).
        while (ShowCursor(false) >= 0) { }
        cursorHidden = true;
    }

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, __) =>
        {
            dmdWindow?.Close();
            backglassWindow?.Close();
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            vlcPlayer = new LibVLC("--no-video-title-show");
            mediaPlayer = new MediaPlayer(vlcPlayer);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "VLC Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        PreviewPlayer.MediaPlayer = mediaPlayer;

        WindowState = WindowState.Maximized;
        Topmost = true;

        Cursor = Cursors.None;
        ForceHideCursor();
        Activated += (_, __) => ForceHideCursor();

        EnsureDmdWindow();
        EnsureBackglassWindow();

        Focus();

        navigationTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(5)
        };
        navigationTimer.Tick += NavigationTimer_Tick;

        try
        {
            config = await Config.LoadAsync(Config.DefaultPath);
            tableList = TableScanner.Scan(config).ToList();

            if (tableList.Count == 0)
            {
                TableNameText.Text = "No VPX tables found. Check config.json TablesDirectory.";
                dmdWindow?.ShowTableProperties("No Table Selected", "");
                return;
            }

            tableIndex = 0;
            ShowCurrentFull();
        }
        catch (Exception ex)
        {
            TableNameText.Text = "Error loading config / scanning tables.";
            MessageBox.Show(ex.ToString(), "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EnsureDmdWindow()
    {
        if (dmdWindow != null)
            return;

        dmdWindow = new DmdWindow();
        dmdWindow.MoveToRightmostMonitor();
        dmdWindow.Show();
    }

    private void EnsureBackglassWindow()
    {
        if (backglassWindow != null)
            return;

        backglassWindow = new BackglassWindow();
        backglassWindow.MoveToSecondRightmostMonitor();
        backglassWindow.Show();
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (tableList.Count == 0) return;

        if (e.Delta < 0) Next();
        else Prev();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (tableList.Count == 0)
        {
            if (e.Key == Key.Escape) Close();
            return;
        }

        bool isRepeat = e.IsRepeat;

        switch (e.Key)
        {
            case Key.Down:
            case Key.Right:
            case Key.RightShift:
            case Key.PageDown:
                Next(isRepeat);
                break;

            case Key.Up:
            case Key.Left:
            case Key.LeftShift:
            case Key.PageUp:
                Prev(isRepeat);
                break;

            case Key.Enter:
            case Key.Space:
                LaunchSelected();
                break;

            case Key.R:
                _ = RecordSelectedPreviewAsync();
                break;

            case Key.Escape:
                Close();
                break;
        }
    }

    private void Window_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (tableList.Count == 0) return;

        switch (e.Key)
        {
            case Key.Down:
            case Key.Right:
            case Key.RightShift:
            case Key.PageDown:
            case Key.Up:
            case Key.Left:
            case Key.LeftShift:
            case Key.PageUp:
                if (navigationTimer?.IsEnabled != true)
                    ShowCurrentFull();
                break;
        }
    }

    private void Next(bool isRepeat = false)
    {
        tableIndex = (tableIndex + 1) % tableList.Count;
        ShowCurrent(isRepeat);
    }

    private void Prev(bool isRepeat = false)
    {
        tableIndex = (tableIndex - 1 + tableList.Count) % tableList.Count;
        ShowCurrent(isRepeat);
    }

    private void ShowCurrent(bool isRepeat = false)
    {   
        var table = tableList[tableIndex];
        TableNameText.Text = table.DisplayName;

        Interlocked.Increment(ref navigationGeneration);

        playbackCancellation.Cancel();

        if (navigationTimer != null && !isRepeat)
        {
            navigationTimer.Stop();
            navigationTimer.Start();
        }
    }

    private async void ShowCurrentFull()
    {
        var expectedGeneration = Interlocked.Read(ref navigationGeneration);

        var table = tableList[tableIndex];
        TableNameText.Text = table.DisplayName;

        var statsCsvPath = GetTableStatsCsvPath();
        var allStats = GetCachedTableStats(statsCsvPath);
        var tableStats = FindTableStats(allStats, table.DisplayName) ?? new TableStatsEntry(table.DisplayName);

        if (Interlocked.Read(ref navigationGeneration) != expectedGeneration)
            return;

        // Cancel any in-flight “play prep”
        playbackCancellation.Cancel();
        playbackCancellation.Dispose();
        playbackCancellation = new CancellationTokenSource();
        var token = playbackCancellation.Token;

        try
        {
            // Kick both preparations concurrently
            var playfieldTask = PlayPreviewAsync(table.PreviewVideoPath, token);
            var backglassTask = backglassWindow != null
                ? backglassWindow.PlayBackglassAsync(table.BackglassVideoPath, token)
                : Task.CompletedTask;

            dmdWindow?.ShowTableProperties(table.DisplayName, FormatTableStatsForDisplay(tableStats));
            await Task.WhenAll(playfieldTask, backglassTask);
        }
        catch (OperationCanceledException) { }
    }

    private async Task PlayPreviewAsync(string path, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            await Dispatcher.InvokeAsync(() => mediaPlayer.Stop());
            return;
        }

        token.ThrowIfCancellationRequested();

        await Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();

            mediaPlayer.EndReached -= OnEndReached;
            mediaPlayer.EndReached += OnEndReached;

            var m = new Media(vlcPlayer, new Uri(path));
            mediaPlayer.Play(m);
        }, token);
    }

    private void NavigationTimer_Tick(object? sender, EventArgs e)
    {
        navigationTimer?.Stop();
        ShowCurrentFull();
    }

    private string GetTableStatsCsvPath()
    {
        var baseDirectory = string.IsNullOrWhiteSpace(config.TablesDirectory)
            ? AppContext.BaseDirectory
            : config.TablesDirectory;

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
        var current = new System.Text.StringBuilder();
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

    private static string FormatTableStatsForDisplay(TableStatsEntry tableStats)
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

    private static string FormatDuration(TimeSpan duration)
    {
        var totalHours = (int)Math.Floor(duration.TotalHours);
        var totalMinutes = duration.Minutes;

        if (totalHours == 0 && totalMinutes == 0)
            return "0 minutes";

        if (totalHours == 0)
            return totalMinutes == 1
                ? "1 minute"
                : $"{totalMinutes} minutes";

        if (totalMinutes == 0)
            return totalHours == 1
                ? "1 hour"
                : $"{totalHours} hours";

        var hoursText = totalHours == 1 ? "1 hour" : $"{totalHours} hours";
        var minutesText = totalMinutes == 1 ? "1 minute" : $"{totalMinutes} minutes";

        return $"{hoursText} {minutesText}";
    }

    private void UpdateTableStatsAfterPlay(string tableName, TimeSpan elapsedPlayTime)
    {
        var statsCsvPath = GetTableStatsCsvPath();
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

        cachedTableStats = null;
        cachedTableStatsCsvPath = null;
    }

    private sealed class TableStatsEntry
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

    private void PlayPreview(string path)
    {
        // Detach any previous handler.
        mediaPlayer.EndReached -= OnEndReached;
        mediaPlayer.EndReached += OnEndReached;

        var media = new Media(vlcPlayer, new Uri(path));
        mediaPlayer.Play(media);
        media.Dispose();
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        // Don't block the media player thread.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                var table = tableList[tableIndex];
                if (!string.IsNullOrWhiteSpace(table.PreviewVideoPath) && File.Exists(table.PreviewVideoPath))
                    PlayPreview(table.PreviewVideoPath);
                else
                    mediaPlayer.Stop();
            });
        });
    }

    private void SetLoading(bool isLoading)
    {
        LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        IsHitTestVisible = !isLoading;

        // Keep the UI responsive and force a render right away.
        if (isLoading)
            Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { }));
    }

    private void LaunchSelected()
    {
        var table = tableList[tableIndex];
        if (!File.Exists(config.VpxExePath))
        {
            MessageBox.Show($"VPX executable not found:\n{config.VpxExePath}", "Launch Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!File.Exists(table.VpxPath))
        {
            MessageBox.Show($"Table file not found:\n{table.VpxPath}", "Launch Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = config.VpxExePath,
            Arguments = $"/play -\"{table.VpxPath}\" /minimized",
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(config.VpxExePath) ?? Environment.CurrentDirectory
        };

        try
        {
            SetLoading(true);

            var stopwatch = Stopwatch.StartNew();
            var proc = Process.Start(psi);

            if (proc != null)
            {
                Topmost = false;
                if (dmdWindow != null)
                {
                    dmdWindow.Topmost = false;
                    dmdWindow.SetLoading(true);
                }
                if (backglassWindow != null)
                {
                    backglassWindow.Topmost = false;
                    backglassWindow.SetLoading(true);
                }

                proc.EnableRaisingEvents = true;
                proc.Exited += (_, __) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        stopwatch.Stop();

                        try
                        {
                            UpdateTableStatsAfterPlay(table.DisplayName, stopwatch.Elapsed);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(ex.ToString(), "CSV Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }

                        SetLoading(false);
                        WindowState = WindowState.Maximized;
                        if (dmdWindow != null)
                        {
                            dmdWindow.Show();
                            dmdWindow.SetLoading(false);
                            dmdWindow.Topmost = true;
                        }
                        if (backglassWindow != null)
                        {
                            backglassWindow.SetLoading(false);
                            backglassWindow.Topmost = true;
                        }
                        ShowCurrentFull();
                        Activate();
                        Topmost = true;
                    });
                };

                // Poll for the player window and focus it
                _ = Task.Run(async () =>
                {
                    const string targetTitle = "Visual Pinball Player";
                    bool playfieldWindowFound = false;
                    IntPtr hwnd = IntPtr.Zero;

                    // Poll for up to ~30 seconds
                    for (int i = 0; i < 60; i++)
                    {
                        await Task.Delay(500);
                        if (proc.HasExited) return;

                        hwnd = FindWindow(null, targetTitle);
                        if (hwnd != IntPtr.Zero)
                        {
                            playfieldWindowFound = true;
                            break;
                        }
                    }

                    if (playfieldWindowFound)
                    {
                        // Small extra delay to let the window finish initializing
                        await Task.Delay(1000);
                        if (dmdWindow != null)
                            Dispatcher.Invoke(() => dmdWindow.Hide());
                        ShowWindow(hwnd, SW_RESTORE);
                        SetForegroundWindow(hwnd);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            SetLoading(false);
            MessageBox.Show(ex.ToString(), "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RecordSelectedPreviewAsync()
    {
        var table = tableList[tableIndex];

        // Run the same executable in record mode so there is no need to ship extra runtime files.
        var self = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(self) || !File.Exists(self))
        {
            MessageBox.Show("Could not determine executable path for record mode.", "Record Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            Topmost = false;

            if (backglassWindow != null)
                backglassWindow.Topmost = false;

            // 1) Record playfield video.
            await RunRecordProcessAsync(self, $"--record \"{table.VpxPath}\"");

            // 2) Record backglass video.
            if (!string.IsNullOrWhiteSpace(config.BackglassVideosDirectory))
                await RunRecordProcessAsync(self, $"--record-backglass \"{table.VpxPath}\"");

            WindowState = WindowState.Maximized;
            Activate();
            Topmost = true;
            if (backglassWindow != null)
                backglassWindow.Topmost = true;

            ShowCurrentFull();
        }
        catch (Exception ex)
        {
            WindowState = WindowState.Maximized;
            Activate();
            Topmost = true;
            MessageBox.Show(ex.ToString(), "Record Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static Task RunRecordProcessAsync(string exePath, string arguments)
    {
        var tcs = new TaskCompletionSource();
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false
        });

        if (proc is null)
        {
            tcs.SetResult();
            return tcs.Task;
        }

        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => tcs.SetResult();
        return tcs.Task;
    }
}
