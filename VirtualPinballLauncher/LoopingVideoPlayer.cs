using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace VirtualPinballLauncher;

public sealed class LoopingVideoPlayer : IDisposable
{
    private readonly LibVLC libVlc;
    private readonly MediaPlayer mediaPlayer;
    private readonly Dispatcher dispatcher;
    private volatile string? currentPath;
    private bool disposed;

    public MediaPlayer MediaPlayer => mediaPlayer;

    public LoopingVideoPlayer(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        libVlc = new LibVLC(
            "--no-video-title-show",
            "--no-drop-late-frames",
            "--no-skip-frames",
            "--clock-jitter=0");
        mediaPlayer = new MediaPlayer(libVlc);
        mediaPlayer.EndReached += OnEndReached;
    }

    public async Task PlayAsync(string? path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (disposed) return;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Stop();
                return;
            }

            using var media = new Media(libVlc, new Uri(path));
            currentPath = path;
            mediaPlayer.Play(media);
        });
    }

    public void Stop()
    {
        if (disposed) return;
        currentPath = null;
        mediaPlayer.Stop();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        currentPath = null;
        mediaPlayer.EndReached -= OnEndReached;
        mediaPlayer.Stop();
        mediaPlayer.Dispose();
        libVlc.Dispose();
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        // Don't block the media player thread.
        var path = currentPath;
        dispatcher.BeginInvoke(() =>
        {
            if (disposed || path != currentPath) return;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                using var media = new Media(libVlc, new Uri(path));
                mediaPlayer.Play(media);
            }
        });
    }
}
