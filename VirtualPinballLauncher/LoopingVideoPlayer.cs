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
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            currentPath = null;
            await dispatcher.InvokeAsync(() => mediaPlayer.Stop());
            return;
        }

        token.ThrowIfCancellationRequested();

        var media = new Media(libVlc, new Uri(path));

        await dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            currentPath = path;
            mediaPlayer.Play(media);
            media.Dispose();
        });
    }

    public void Stop()
    {
        currentPath = null;
        mediaPlayer.Stop();
    }

    public void Dispose()
    {
        mediaPlayer.EndReached -= OnEndReached;
        mediaPlayer.Stop();
        mediaPlayer.Dispose();
        libVlc.Dispose();
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        // Don't block the media player thread.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            dispatcher.BeginInvoke(() =>
            {
                var path = currentPath;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    var media = new Media(libVlc, new Uri(path));
                    mediaPlayer.Play(media);
                    media.Dispose();
                }
                else
                {
                    mediaPlayer.Stop();
                }
            });
        });
    }
}
