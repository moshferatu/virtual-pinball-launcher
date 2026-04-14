using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Forms = System.Windows.Forms;

namespace VirtualPinballLauncher;

public partial class BackglassWindow : Window
{
    private LoopingVideoPlayer videoPlayer = null!;

    public BackglassWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            videoPlayer = new LoopingVideoPlayer(Dispatcher);
            BackglassPlayer.MediaPlayer = videoPlayer.MediaPlayer;
        };

        Closed += (_, _) => videoPlayer?.Dispose();
    }

    public Task PlayBackglassAsync(string? path, CancellationToken token)
        => videoPlayer.PlayAsync(path, token);

    public void StopVideo() => videoPlayer.Stop();

    public void SetLoading(bool isLoading)
    {
        LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;

        if (isLoading)
            Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { }));
    }

    public void MoveToSecondRightmostMonitor()
    {
        var sorted = Forms.Screen.AllScreens
            .OrderByDescending(s => s.Bounds.Left)
            .ThenBy(s => s.Bounds.Width * s.Bounds.Height)
            .ToArray();

        // Second-rightmost if available, otherwise fall back to rightmost.
        var screen = sorted.Length >= 2 ? sorted[1] : sorted.FirstOrDefault();

        if (screen is null) return;

        MoveToScreen(screen);
    }

    private void MoveToScreen(Forms.Screen screen)
    {
        WindowState = WindowState.Normal;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Left = screen.Bounds.Left;
        Top = screen.Bounds.Top;
        Width = screen.Bounds.Width;
        Height = screen.Bounds.Height;
    }
}
