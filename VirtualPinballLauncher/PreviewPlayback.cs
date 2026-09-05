using LibVLCSharp.Shared;

namespace VirtualPinballLauncher;

// Owned by App until both video views have closed and all native work has drained.
public sealed class PreviewPlayback : IAsyncDisposable
{
    private readonly LibVLC libVlc;
    private readonly object sync = new();
    private Task? disposal;

    public LoopingVideoPlayer Playfield { get; }
    public LoopingVideoPlayer Backglass { get; }

    private PreviewPlayback()
    {
        libVlc = new LibVLC(
            "--no-video-title-show",
            "--no-drop-late-frames",
            "--no-skip-frames",
            "--clock-jitter=0");
        try
        {
            Playfield = new LoopingVideoPlayer(libVlc);
            Backglass = new LoopingVideoPlayer(libVlc);
        }
        catch
        {
            // Construction runs on a worker and no views are attached yet.
            try { Playfield?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            finally { libVlc.Dispose(); }
            throw;
        }
    }

    public static Task<PreviewPlayback> CreateAsync() => Task.Run(() => new PreviewPlayback());

    public ValueTask DisposeAsync()
    {
        lock (sync)
            return new ValueTask(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await Task.WhenAll(Playfield.DisposeAsync().AsTask(), Backglass.DisposeAsync().AsTask())
                .ConfigureAwait(false);
        }
        finally
        {
            await Task.Run(libVlc.Dispose).ConfigureAwait(false);
        }
    }
}
