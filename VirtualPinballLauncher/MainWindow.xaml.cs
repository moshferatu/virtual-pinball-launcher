using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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

    private readonly PreviewPlayback previews;
    private DmdWindow? dmdWindow;
    private BackglassWindow? backglassWindow;
    private VolumeOverlayWindow? volumeOverlayWindow;
    private readonly List<(Window Window, LoadingOverlay Overlay)> windows = new();

    // Defaults apply until config.json is loaded, so the window answers keys even in checks that skip loading.
    private KeyBindingMap keyBindings = KeyBindingMap.Build(Config.DefaultKeyBindings());
    private KeyBindingHost? bindingHost;

    // Actions that still make sense while a table or another program has the screen.
    private static readonly HashSet<LauncherAction> ActionsAllowedDuringLaunch = new()
    {
        LauncherAction.VolumeUp, LauncherAction.VolumeDown, LauncherAction.KillTable,
        LauncherAction.ShutdownPc, LauncherAction.RestartPc
    };

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
    private bool readyToClose;
    private Task soundLoad = Task.CompletedTask;

    private void ForceHideCursor()
    {
        if (cursorHidden) return;

        // ShowCursor uses an internal display count.
        // Repeatedly call until the count is < 0 (meaning hidden).
        while (ShowCursor(false) >= 0) { }
        cursorHidden = true;
    }

    public MainWindow(PreviewPlayback previews)
    {
        this.previews = previews;
        InitializeComponent();
        PreviewPlayer.MediaPlayer = previews.Playfield.MediaPlayer;
        PreviewPlayer.Loaded += (_, _) => this.PreventOverlayActivation(LoadingOverlay);
        ContentRendered += (_, _) => RequestInputFocus();
    }

    private void RequestInputFocus()
    {
        // VideoView creates a separate overlay window during loading. Let those
        // Loaded/render callbacks finish before choosing the keyboard input window.
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (isClosed || launchInProgress || !IsVisible || !IsEnabled) return;
            Activate();
            Keyboard.Focus(this);
        }, DispatcherPriority.ContextIdle);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (readyToClose) return;
        e.Cancel = true;
        if (isClosed) return;
        isClosed = true;
        await Task.Yield();

        try
        {
            await StopPreviewsAsync();
            await soundLoad;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Launcher shutdown failed: {ex}");
        }
        finally
        {
            bindingHost?.Dispose();
            PreviewPlayer.MediaPlayer = null;
            backglassWindow?.DetachPlayer();
            playbackCancellation.Dispose();
            flipperSound?.Dispose();
            dmdWindow?.Close();
            backglassWindow?.Close();
            volumeOverlayWindow?.Close();
            readyToClose = true;
            Close();
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var wavPath = Path.Combine(AppContext.BaseDirectory, "flipper.wav");
        if (File.Exists(wavPath))
        {
            flipperSound = new SoundPlayer(wavPath);
            soundLoad = Task.Run(() => flipperSound.Load());
            await soundLoad;
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

        backglassWindow = new BackglassWindow(previews.Backglass);
        backglassWindow.MoveToMonitor(Forms.Screen.AllScreens.Length - 2);
        backglassWindow.Show();

        volumeOverlayWindow = new VolumeOverlayWindow();
        volumeOverlayWindow.MoveToMonitor(0);
        volumeOverlayWindow.Show();

        windows.Add((this, LoadingOverlay));
        windows.Add((dmdWindow, dmdWindow.LoadingOverlay));
        windows.Add((backglassWindow, backglassWindow.LoadingOverlay));

        var problems = new List<string>();
        try
        {
            config = await Config.LoadAsync(Config.DefaultPath);
            if (isClosed) return;

            InstallKeyBindings(config.KeyBindings, problems);

            tableList = TableScanner.Scan(config).ToList();
            SortTableList();

            if (tableList.Count == 0)
            {
                TableNameText.Text = "No VPX tables found. Check config.json TablesDirectory.";
                dmdWindow?.ShowTableProperties("No Table Selected", null);
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
        finally
        {
            if (problems.Count > 0 && !isClosed)
                MessageBox.Show(string.Join("\n\n", problems), "Key Binding Problems", MessageBoxButton.OK, MessageBoxImage.Warning);
            RequestInputFocus();
        }
    }

    private void InstallKeyBindings(IEnumerable<KeyBinding> bindings, List<string> problems)
    {
        keyBindings = KeyBindingMap.Build(bindings);
        problems.AddRange(keyBindings.Errors);

        bindingHost?.Dispose();
        bindingHost = new KeyBindingHost(keyBindings, Dispatcher);
        bindingHost.ChordTriggered += binding => _ = HandleChordAsync(binding);
        bindingHost.ModifierTapped += key => _ = HandleModifierTapAsync(key);
        if (!bindingHost.Start())
            problems.Add("Windows refused the keyboard hook, so key chords such as F12+1 will not work until the launcher is restarted.");
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (isClosed || launchInProgress || tableList.Count == 0) return;

        if (e.Delta < 0) Next();
        else Prev();
    }

    private async void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (isClosed) return;
        // Any key that reaches this window passed the global hook first, unless the hook is gone.
        bindingHost?.NoteWindowKeyEvent();
        if (launchInProgress) return;
        if (!keyBindings.TryGetSingle(e.Key, out var binding)) return;

        // While a chord modifier is held, its partner keys belong to the chord, not to the launcher.
        if (bindingHost?.HookActive == true && (bindingHost.IsModifierHeld || keyBindings.IsModifier(e.Key))) return;

        if (tableList.Count == 0 && binding.Action != LauncherAction.Exit) return;
        await ExecuteAsync(binding, e.IsRepeat);
    }

    private async Task HandleModifierTapAsync(Key key)
    {
        if (isClosed || launchInProgress || !IsActive) return;
        if (tableList.Count == 0) return;
        if (keyBindings.TryGetSingle(key, out var binding))
            await ExecuteAsync(binding);
    }

    private async Task HandleChordAsync(ResolvedBinding binding)
    {
        if (isClosed) return;
        if (launchInProgress && !ActionsAllowedDuringLaunch.Contains(binding.Action)) return;
        if (tableList.Count == 0 && binding.Action is LauncherAction.NextTable or LauncherAction.PreviousTable
            or LauncherAction.LaunchTable or LauncherAction.RandomTable or LauncherAction.ToggleSort or LauncherAction.RecordPreview)
            return;
        await ExecuteAsync(binding);
    }

    private async Task ExecuteAsync(ResolvedBinding binding, bool isRepeat = false)
    {
        switch (binding.Action)
        {
            case LauncherAction.NextTable:
                Next(isRepeat);
                break;
            case LauncherAction.PreviousTable:
                Prev(isRepeat);
                break;
            case LauncherAction.LaunchTable:
                await LaunchSelectedAsync();
                break;
            case LauncherAction.RandomTable:
                if (!isRepeat) SelectRandom();
                break;
            case LauncherAction.ToggleSort:
                if (!isRepeat) ToggleSortMode();
                break;
            case LauncherAction.RecordPreview:
                await RecordSelectedPreviewAsync();
                break;
            case LauncherAction.Exit:
                if (!isRepeat) await ExitAsync();
                break;
            case LauncherAction.Run:
                await RunProgramAsync(binding.Source);
                break;
            case LauncherAction.KillTable:
                KillTable();
                break;
            case LauncherAction.VolumeUp:
                volumeOverlayWindow?.VolumeUp();
                break;
            case LauncherAction.VolumeDown:
                volumeOverlayWindow?.VolumeDown();
                break;
            case LauncherAction.ShutdownPc:
                SystemPower.PowerOff(restart: false);
                break;
            case LauncherAction.RestartPc:
                SystemPower.PowerOff(restart: true);
                break;
        }
    }

    private Task ExitAsync()
    {
        if (!isClosed) Close();
        return Task.CompletedTask;
    }

    private void KillTable()
    {
        if (config == null || string.IsNullOrWhiteSpace(config.VpxExePath)) return;
        foreach (var process in ExternalProgram.FindProcesses(Path.GetFileName(config.VpxExePath)))
        {
            using (process)
            {
                try { process.Kill(); }
                catch (Exception ex) { Trace.TraceWarning($"Could not end VPX: {ex.Message}"); }
            }
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
            // Keep the DMD in sync with every selection, even while a flipper is held.
            var stats = tableStats.GetTableStatsForDisplay(config.TablesDirectory, table.DisplayName);
            dmdWindow?.ShowTableProperties(table.DisplayName, stats);

            // Wait for navigation to settle before starting either decoder.
            if (delay)
                await Task.Delay(100, token);
            token.ThrowIfCancellationRequested();

            if (!force && previewTablePath == table.VpxPath) return;

            previewTablePath = null;
            await Task.WhenAll(
                previews.Playfield.PlayAsync(table.PreviewVideoPath, token),
                previews.Backglass.PlayAsync(table.BackglassVideoPath, token));
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

    private Task StopPreviewsAsync()
    {
        previewTablePath = null;
        playbackCancellation.Cancel();
        return Task.WhenAll(previews.Playfield.StopAsync(), previews.Backglass.StopAsync());
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
        RequestInputFocus();
    }

    /// <summary>Once another program owns the screen, free the DMD display and leave the previews ready underneath.</summary>
    private void OnExternalWindowActivated()
    {
        if (isClosed) return;
        dmdWindow?.Hide();
        foreach (var (_, overlay) in windows)
            overlay.SetLoading(false);
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
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                OnExternalWindowActivated();
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>Runs a program bound with the Run action, the same way a table is launched.</summary>
    private async Task RunProgramAsync(KeyBinding binding)
    {
        if (launchInProgress || isClosed) return;

        var spec = ExternalProgramSpec.From(binding);
        if (!File.Exists(spec.Command))
        {
            MessageBox.Show($"Program not found for {string.Join(", ", binding.Keys)}:\n{spec.Command}", "Launch Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        launchInProgress = true;
        try
        {
            await RefreshPreviewAsync();
            if (isClosed) return;
            LauncherLog.Info($"Run: {spec.Command} {spec.Arguments}");
            // The new process inherits the right to take the foreground from this one.
            WindowActivation.AllowAnyProcessToTakeForeground();
            SuspendWindows();
            await ExternalProgram.RunAsync(spec, () => Dispatcher.Invoke(OnExternalWindowActivated));
            LauncherLog.Info("Run: program finished.");
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
            SuspendWindows();
            await StopPreviewsAsync();
            if (isClosed) return;
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
