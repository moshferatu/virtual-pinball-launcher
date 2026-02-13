using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace VirtualPinballLauncher;

public partial class MainWindow : Window
{
    private Config config = null!;
    private List<TableItem> tableList = new();
    private int tableIndex = 0;

    private LibVLC vlcPlayer = null!;
    private MediaPlayer mediaPlayer = null!;

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

    private bool cursorHidden;

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

        Focus();

        try
        {
            config = await Config.LoadAsync(Config.DefaultPath);
            tableList = TableScanner.Scan(config).ToList();

            if (tableList.Count == 0)
            {
                TableNameText.Text = "No VPX tables found. Check config.json TablesDirectory.";
                return;
            }

            tableIndex = 0;
            ShowCurrent();
        }
        catch (Exception ex)
        {
            TableNameText.Text = "Error loading config / scanning tables.";
            MessageBox.Show(ex.ToString(), "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (tableList.Count == 0) return;

        if (e.Delta < 0) Next();
        else Prev();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (tableList.Count == 0)
        {
            if (e.Key == Key.Escape) Close();
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
            case Key.Right:
            case Key.RightShift:
            case Key.PageDown:
                Next();
                break;

            case Key.Up:
            case Key.Left:
            case Key.LeftShift:
            case Key.PageUp:
                Prev();
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

    private void Next()
    {
        tableIndex = (tableIndex + 1) % tableList.Count;
        ShowCurrent();
    }

    private void Prev()
    {
        tableIndex = (tableIndex - 1 + tableList.Count) % tableList.Count;
        ShowCurrent();
    }

    private void ShowCurrent()
    {
        var table = tableList[tableIndex];
        TableNameText.Text = table.DisplayName;

        if (!string.IsNullOrWhiteSpace(table.PreviewVideoPath) && File.Exists(table.PreviewVideoPath))
        {
            try
            {
                PlayPreview(table.PreviewVideoPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            mediaPlayer.Stop();
        }
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

            var proc = Process.Start(psi);

            if (proc != null)
            {
                Topmost = false;
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, __) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        SetLoading(false);
                        WindowState = WindowState.Maximized;
                        Activate();
                        Topmost = true;
                    });
                };
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
            // Kick off record mode in a separate process to not block UI.
            var psi = new ProcessStartInfo
            {
                FileName = self,
                Arguments = $"--record \"{table.VpxPath}\"",
                UseShellExecute = false
            };
            var proc = Process.Start(psi);

            if (proc != null)
            {
                Topmost = false;
                WindowState = WindowState.Minimized;
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, __) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        WindowState = WindowState.Maximized;
                        Activate();
                        Topmost = true;
                        ShowCurrent();
                    });
                };
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Record Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        await Task.CompletedTask;
    }
}