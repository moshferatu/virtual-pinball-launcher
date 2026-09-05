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
using Forms = System.Windows.Forms;

namespace VirtualPinballLauncher;

public partial class MainWindow : Window
{
    private Config config = null!;
    private List<TableItem> tableList = new();
    private int tableIndex = 0;
    private readonly TableStats tableStats = new();
    private bool sortByPopularity = true;
    private readonly Random random = new();

    private LoopingVideoPlayer videoPlayer = null!;
    private DmdWindow? dmdWindow;
    private BackglassWindow? backglassWindow;
    private VolumeOverlayWindow? volumeOverlayWindow;
    private readonly List<(Window Window, LoadingOverlay Overlay)> windows = new();

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

    private string? previewTablePath;

    private CancellationTokenSource playbackCancellation = new();
    private SoundPlayer? flipperSound;

    private bool launchInProgress;
    private bool isClosed;

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
            isClosed = true;
            StopPreviews();
            playbackCancellation.Dispose();
            videoPlayer?.Dispose();
            flipperSound?.Dispose();
            dmdWindow?.Close();
            backglassWindow?.Close();
            volumeOverlayWindow?.Close();
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            videoPlayer = new LoopingVideoPlayer(Dispatcher);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "VLC Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        PreviewPlayer.MediaPlayer = videoPlayer.MediaPlayer;

        var wavPath = Path.Combine(AppContext.BaseDirectory, "flipper.wav");
        if (File.Exists(wavPath))
        {
            flipperSound = new SoundPlayer(wavPath);
            await Task.Run(() => flipperSound.Load());
        }

        if (isClosed) return;
        WindowState = WindowState.Maximized;
        Topmost = true;

        Cursor = Cursors.None;
        ForceHideCursor();
        Activated += (_, __) => ForceHideCursor();

        dmdWindow = new DmdWindow();
        dmdWindow.MoveToMonitor(Forms.Screen.AllScreens.Length - 1);
        dmdWindow.Show();

        backglassWindow = new BackglassWindow();
        backglassWindow.MoveToMonitor(Forms.Screen.AllScreens.Length - 2);
        backglassWindow.Show();

        volumeOverlayWindow = new VolumeOverlayWindow();
        volumeOverlayWindow.MoveToMonitor(0);
        volumeOverlayWindow.Show();

        windows.Add((this, LoadingOverlay));
        windows.Add((dmdWindow, dmdWindow.LoadingOverlay));
        windows.Add((backglassWindow, backglassWindow.LoadingOverlay));

        Focus();

        try
        {
            config = await Config.LoadAsync(Config.DefaultPath);
            if (isClosed) return;
            tableList = TableScanner.Scan(config).ToList();
            SortTableList();

            if (tableList.Count == 0)
            {
                TableNameText.Text = "No VPX tables found. Check config.json TablesDirectory.";
                dmdWindow?.ShowTableProperties("No Table Selected", "");
                return;
            }

            tableIndex = 0;
            await RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            TableNameText.Text = "Error loading config / scanning tables.";
            MessageBox.Show(ex.ToString(), "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (isClosed || launchInProgress || tableList.Count == 0) return;

        if (e.Delta < 0) Next();
        else Prev();
    }

    private async void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (tableList.Count == 0)
        {
            if (e.Key == Key.Escape) Close();
            return;
        }

        if (isClosed || launchInProgress) return;

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
            case Key.D1:
            case Key.NumPad1:
                await LaunchSelectedAsync();
                break;

            case Key.R:
                await RecordSelectedPreviewAsync();
                break;

            case Key.F12:
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

    private void SortTableList()
    {
        if (sortByPopularity)
        {
            var allStats = tableStats.GetAllTableStats(config.TablesDirectory);
            tableList = tableList
                .OrderByDescending(t => allStats.TryGetValue(t.DisplayName, out var stats) ? stats.TotalTimeSpentPlaying : TimeSpan.Zero)
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
        if (isClosed || launchInProgress || tableList.Count == 0) return;

        var currentTableName = tableList[tableIndex].DisplayName;
        sortByPopularity = !sortByPopularity;
        SortTableList();

        tableIndex = tableList.FindIndex(t => string.Equals(t.DisplayName, currentTableName, StringComparison.OrdinalIgnoreCase));
        if (tableIndex < 0) tableIndex = 0;

        _ = RefreshPreviewAsync();
    }

    private void SelectRandom()
    {
        tableIndex = random.Next(tableList.Count);
        flipperSound?.Play();
        _ = RefreshPreviewAsync();
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
        if (!isRepeat)
            flipperSound?.Play();
        _ = RefreshPreviewAsync(delay: true);
    }

    private async Task RefreshPreviewAsync(bool delay = false, bool force = false)
    {
        if (isClosed || tableList.Count == 0) return;

        var table = tableList[tableIndex];
        TableNameText.Text = table.DisplayName;
        playbackCancellation.Cancel();
        playbackCancellation.Dispose();
        playbackCancellation = new CancellationTokenSource();
        var token = playbackCancellation.Token;

        try
        {
            // Wait for navigation to settle before starting either decoder.
            if (delay)
                await Task.Delay(100, token);
            token.ThrowIfCancellationRequested();

            var stats = tableStats.GetTableStatsForDisplay(config.TablesDirectory, table.DisplayName);
            dmdWindow?.ShowTableProperties(table.DisplayName, tableStats.FormatTableStatsForDisplay(stats));
            if (!force && previewTablePath == table.VpxPath) return;

            previewTablePath = null;
            await Task.WhenAll(
                videoPlayer.PlayAsync(table.PreviewVideoPath, token),
                backglassWindow?.PlayBackglassAsync(table.BackglassVideoPath, token) ?? Task.CompletedTask);
            token.ThrowIfCancellationRequested();
            previewTablePath = table.VpxPath;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!isClosed && !token.IsCancellationRequested)
                MessageBox.Show(ex.ToString(), "Preview Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopPreviews()
    {
        previewTablePath = null;
        playbackCancellation.Cancel();
        videoPlayer?.Stop();
        backglassWindow?.StopVideo();
    }

    private void SuspendWindows()
    {
        foreach (var (window, overlay) in windows)
        {
            window.Topmost = false;
            overlay.SetLoading(true);
        }
    }

    private void RestoreWindows()
    {
        if (isClosed) return;
        WindowState = WindowState.Maximized;
        foreach (var (window, overlay) in windows)
        {
            window.Show();
            overlay.SetLoading(false);
            window.Topmost = true;
        }
        Activate();
    }

    private async Task LaunchSelectedAsync()
    {
        if (launchInProgress) return;

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

        launchInProgress = true;

        try
        {
            // Finish any pending navigation refresh before handing off to VPX.
            // Keep both previews playing underneath it so returning needs no reload.
            await RefreshPreviewAsync();
            if (isClosed) return;
            SuspendWindows();

            var stopwatch = Stopwatch.StartNew();
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start VPX.");
            using var focusCancellation = new CancellationTokenSource();
            var focusTask = FocusPlayerWindowAsync(process, focusCancellation.Token);
            try
            {
                await process.WaitForExitAsync();
            }
            finally
            {
                focusCancellation.Cancel();
                await focusTask;
            }
            stopwatch.Stop();

            try
            {
                tableStats.UpdateTableStatsAfterPlay(config.TablesDirectory, table.DisplayName, stopwatch.Elapsed);
                if (sortByPopularity)
                {
                    SortTableList();
                    tableIndex = Math.Max(0, tableList.FindIndex(t =>
                        string.Equals(t.DisplayName, table.DisplayName, StringComparison.OrdinalIgnoreCase)));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "CSV Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            launchInProgress = false;
            RestoreWindows();
            await RefreshPreviewAsync();
        }
    }

    private async Task FocusPlayerWindowAsync(Process process, CancellationToken token)
    {
        try
        {
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(500, token);
                if (isClosed || process.HasExited) return;
                var hwnd = FindWindow(null, "Visual Pinball Player");
                if (hwnd == IntPtr.Zero) continue;

                await Task.Delay(1000, token);
                if (isClosed || process.HasExited) return;
                dmdWindow?.Hide();
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                // Leave the previews unobscured behind VPX, ready for its exit.
                foreach (var (_, overlay) in windows)
                    overlay.SetLoading(false);
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task RecordSelectedPreviewAsync()
    {
        if (isClosed || launchInProgress) return;
        var table = tableList[tableIndex];

        // Run the same executable in record mode so there is no need to ship extra runtime files.
        var self = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(self) || !File.Exists(self))
        {
            MessageBox.Show("Could not determine executable path for record mode.", "Record Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        launchInProgress = true;
        try
        {
            // Release preview file handles before the recorder replaces the videos.
            StopPreviews();
            SuspendWindows();
            await RunRecordProcessAsync(self, $"--record \"{table.VpxPath}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Record Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            launchInProgress = false;
            RestoreWindows();
            await RefreshPreviewAsync(force: true);
        }
    }

    private static async Task RunRecordProcessAsync(string exePath, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start the recorder.");

        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Recording failed (exit code {process.ExitCode}).");
    }
}
