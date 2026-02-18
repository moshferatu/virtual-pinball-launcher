using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using LibVLCSharp.Shared;
using Forms = System.Windows.Forms;

namespace VirtualPinballLauncher;

public partial class BackglassWindow : Window
{
    private LibVLC vlcPlayer = null!;
    private MediaPlayer mediaPlayer = null!;
    private volatile string? currentVideoPath;

    public BackglassWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            vlcPlayer = new LibVLC("--no-video-title-show");
            mediaPlayer = new MediaPlayer(vlcPlayer);
            BackglassPlayer.MediaPlayer = mediaPlayer;
        };

        Closed += (_, _) =>
        {
            mediaPlayer.Stop();
            mediaPlayer.Dispose();
            vlcPlayer.Dispose();
        };
    }

    public void PlayVideo(string path)
    {
        if (mediaPlayer is null || vlcPlayer is null)
            return;

        currentVideoPath = path;

        mediaPlayer.EndReached -= OnEndReached;
        mediaPlayer.EndReached += OnEndReached;

        var media = new Media(vlcPlayer, new Uri(path));
        mediaPlayer.Play(media);
        media.Dispose();
    }

    public async Task PlayBackglassAsync(string path, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            await Dispatcher.InvokeAsync(() => mediaPlayer.Stop());
            return;
        }

        var media = new Media(vlcPlayer, new Uri(path));

        token.ThrowIfCancellationRequested();

        await Dispatcher.InvokeAsync(() =>
        {
            currentVideoPath = path;
            mediaPlayer.EndReached -= OnEndReached;
            mediaPlayer.EndReached += OnEndReached;
            mediaPlayer.Play(media);
            media.Dispose();
        });
    }

    public void StopVideo()
    {
        currentVideoPath = null;
        mediaPlayer.Stop();
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!string.IsNullOrWhiteSpace(currentVideoPath) && File.Exists(currentVideoPath))
                    PlayVideo(currentVideoPath);
                else
                    mediaPlayer.Stop();
            });
        });
    }

    public void SetLoading(bool isLoading)
    {
        LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;

        if (isLoading)
            Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { }));
    }

    public void MoveToPreferredMonitor(int preferredZeroBasedIndex)
    {
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0)
            return;

        var index = preferredZeroBasedIndex;
        if (index < 0) index = 0;
        if (index >= screens.Length) index = screens.Length - 1;

        MoveToScreen(screens[index]);
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
