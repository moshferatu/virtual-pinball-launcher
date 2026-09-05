using System.Diagnostics;
using System.IO;
using LibVLCSharp.Shared;

namespace VirtualPinballLauncher;

public sealed class LoopingVideoPlayer : IAsyncDisposable
{
    private readonly LibVLC libVlc;
    private readonly MediaPlayer mediaPlayer;
    private readonly MediaOperationQueue operations = new();
    private volatile Playback? currentPlayback;
    private long generation;

    private sealed record Playback(string Path, long Generation);

    public MediaPlayer MediaPlayer => mediaPlayer;

    public LoopingVideoPlayer(LibVLC libVlc)
    {
        this.libVlc = libVlc;
        mediaPlayer = new MediaPlayer(libVlc);
        mediaPlayer.EndReached += OnEndReached;
    }

    public Task PlayAsync(string? path, CancellationToken token)
    {
        var request = Interlocked.Increment(ref generation);
        return operations.Enqueue(() =>
        {
            if (request != Volatile.Read(ref generation)) return;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                StopCore();
                return;
            }

            token.ThrowIfCancellationRequested();
            if (request != Volatile.Read(ref generation)) return;
            StartPlayback(new Playback(path, request));
        }, token);
    }

    public Task StopAsync()
    {
        Interlocked.Increment(ref generation);
        return operations.Enqueue(StopCore);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref generation);
        return new ValueTask(operations.CompleteAsync(() =>
        {
            mediaPlayer.EndReached -= OnEndReached;
            try { StopCore(); }
            finally { mediaPlayer.Dispose(); }
        }));
    }

    private void StopCore()
    {
        // Clear the session before stopping so a final native callback cannot restart it.
        currentPlayback = null;
        mediaPlayer.Stop();
    }

    private void StartPlayback(Playback playback)
    {
        using var media = new Media(libVlc, new Uri(playback.Path));
        // Replace media directly: an explicit Stop tears down the video output and flashes.
        // Ignore callbacks from the outgoing media while VLC replaces it.
        currentPlayback = null;
        if (!mediaPlayer.Play(media))
            throw new IOException($"Could not play preview: {playback.Path}");
        currentPlayback = playback;
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        var playback = currentPlayback;
        if (playback != null)
            _ = RestartAsync(playback);
    }

    private async Task RestartAsync(Playback playback)
    {
        try
        {
            // Never call VLC from its event thread. Ignore loops from an older selection.
            await operations.Enqueue(() =>
            {
                if (playback != currentPlayback || playback.Generation != Volatile.Read(ref generation)) return;

                if (File.Exists(playback.Path)) StartPlayback(playback);
                else StopCore();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Preview loop failed: {ex}");
        }
    }
}
