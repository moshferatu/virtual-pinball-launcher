using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
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
    private Config config = null!;
    private List<TableItem> tableList = new();
    private int tableIndex = 0;
    private readonly TableStats tableStats = new();
    private bool sortByPopularity = true;
    private readonly Random random = new();

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

    private CancellationTokenSource playbackCancellation = new();
    private SoundPlayer? flipperSound;

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

        var wavPath = Path.Combine(AppContext.BaseDirectory, "flipper.wav");
        if (File.Exists(wavPath))
        {
            flipperSound = new SoundPlayer(wavPath);
            await Task.Run(() => flipperSound.Load());
        }

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
            SortTableList();

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

            case Key.D1:
            case Key.NumPad1:
                if (!isRepeat) SelectRandom();
                break;

            case Key.LeftCtrl:
            case Key.RightCtrl:
                if (!isRepeat) ToggleSortMode();
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

    private void SortTableList()
    {
        if (sortByPopularity)
        {
            var allStats = tableStats.GetAllTableStats(config.TablesDirectory);
            tableList = tableList
                .OrderByDescending(t => allStats
                    .FirstOrDefault(s => string.Equals(s.TableName, t.DisplayName, StringComparison.OrdinalIgnoreCase))
                    ?.TotalTimeSpentPlaying ?? TimeSpan.Zero)
                .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            tableList = tableList.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private void ToggleSortMode()
    {
        if (tableList.Count == 0) return;

        var currentTableName = tableList[tableIndex].DisplayName;
        sortByPopularity = !sortByPopularity;
        SortTableList();

        tableIndex = tableList.FindIndex(t => string.Equals(t.DisplayName, currentTableName, StringComparison.OrdinalIgnoreCase));
        if (tableIndex < 0) tableIndex = 0;

        ShowCurrentFull();
    }

    private void SelectRandom()
    {
        tableIndex = random.Next(tableList.Count);
        Task.Run(() => flipperSound?.Play());
        ShowCurrentFull();
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

        if (!isRepeat)
            Task.Run(() => flipperSound?.Play());

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

        var tableStatsEntry = tableStats.GetTableStatsForDisplay(config.TablesDirectory, table.DisplayName);

        if (Interlocked.Read(ref navigationGeneration) != expectedGeneration)
            return;

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

            dmdWindow?.ShowTableProperties(table.DisplayName, tableStats.FormatTableStatsForDisplay(tableStatsEntry));
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
                            tableStats.UpdateTableStatsAfterPlay(config.TablesDirectory, table.DisplayName, stopwatch.Elapsed);

                            if (sortByPopularity)
                            {
                                SortTableList();
                                tableIndex = tableList.FindIndex(t => string.Equals(t.DisplayName, table.DisplayName, StringComparison.OrdinalIgnoreCase));
                                if (tableIndex < 0) tableIndex = 0;
                            }
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

            // Stop playback before recording so LibVLC releases its file handles.
            // Without this, the recorder subprocess's File.Delete calls trigger a
            // native LibVLC crash when it tries to read a file that has been deleted
            // out from under it.
            playbackCancellation.Cancel();
            mediaPlayer.EndReached -= OnEndReached;
            mediaPlayer.Stop();
            backglassWindow?.StopVideo();

            // Record playfield and backglass videos in parallel (single VPX session).
            await RunRecordProcessAsync(self, $"--record \"{table.VpxPath}\"");

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
