using System.IO;
using System.Text;
using LibVLCSharp.Shared;
using VirtualPinballLauncher;

internal static class MediaPlaybackChecks
{
    public static async Task RunAsync(string directory)
    {
        var path = Path.Combine(directory, "silent-preview.wav");
        WriteSilentWave(path);

        // Exercise native playback and looping without opening windows or playing sound.
        using var libVlc = await Task.Run(() => new LibVLC("--aout=dummy", "--vout=dummy", "--quiet"));
        await CheckNavigationAsync(libVlc, directory);
        await using var playfield = new LoopingVideoPlayer(libVlc);
        await using var backglass = new LoopingVideoPlayer(libVlc);
        using var navigation = new CancellationTokenSource();
        var playfieldStarted = Signal();
        var backglassStarted = Signal();
        var playfieldLooped = Signal();
        var backglassLooped = Signal();
        var playfieldStarts = 0;
        var backglassStarts = 0;
        playfield.MediaPlayer.Playing += (_, _) =>
        {
            playfieldStarted.TrySetResult();
            if (Interlocked.Increment(ref playfieldStarts) >= 2) playfieldLooped.TrySetResult();
        };
        backglass.MediaPlayer.Playing += (_, _) =>
        {
            backglassStarted.TrySetResult();
            if (Interlocked.Increment(ref backglassStarts) >= 2) backglassLooped.TrySetResult();
        };

        await Task.WhenAll(playfield.PlayAsync(path, navigation.Token), backglass.PlayAsync(path, navigation.Token));
        await Task.WhenAll(playfieldStarted.Task, backglassStarted.Task).WaitAsync(TimeSpan.FromSeconds(15));
        navigation.Cancel();
        await Task.WhenAll(playfieldLooped.Task, backglassLooped.Task).WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("PASS: Both players share VLC and keep looping after a navigation token is cancelled");

        // Superseded selections and disposal can arrive while an older native call is active.
        var selections = Enumerable.Range(0, 20)
            .Select(_ => playfield.PlayAsync(path, CancellationToken.None)).ToArray();
        await playfield.DisposeAsync();
        await Task.WhenAll(selections).WaitAsync(TimeSpan.FromSeconds(15));
        await playfield.DisposeAsync();
        var stillLooping = Signal();
        backglass.MediaPlayer.Playing += (_, _) => stillLooping.TrySetResult();
        await stillLooping.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("PASS: Disposing a player with queued selections leaves the other player's VLC usable");

        await backglass.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Console.WriteLine("PASS: Awaiting stop releases preview file handles before recording");

        await using var previews = await PreviewPlayback.CreateAsync();
        await Task.WhenAll(previews.Playfield.StopAsync(), previews.Backglass.StopAsync());
        await previews.DisposeAsync();
        Console.WriteLine("PASS: Application-owned playback initializes and disposes both players");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task CheckNavigationAsync(LibVLC libVlc, string directory)
    {
        var first = Path.Combine(directory, "first-preview.wav");
        var second = Path.Combine(directory, "second-preview.wav");
        WriteSilentWave(first, seconds: 30);
        File.Copy(first, second);
        await using var player = new LoopingVideoPlayer(libVlc);
        var stops = 0;
        player.MediaPlayer.Stopped += (_, _) => Interlocked.Increment(ref stops);

        async Task PlayAndWaitAsync(string path)
        {
            var playing = Signal();
            EventHandler<EventArgs> handler = (_, _) => playing.TrySetResult();
            player.MediaPlayer.Playing += handler;
            try
            {
                await player.PlayAsync(path, CancellationToken.None);
                await playing.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally { player.MediaPlayer.Playing -= handler; }
        }

        await PlayAndWaitAsync(first);
        Interlocked.Exchange(ref stops, 0);
        await PlayAndWaitAsync(second);
        await PlayAndWaitAsync(first);
        if (Volatile.Read(ref stops) != 0)
            throw new InvalidOperationException("Changing valid previews must not explicitly stop playback between selections");
        Console.WriteLine("PASS: Table navigation replaces playing media without a stopped state between selections");

        await player.PlayAsync(Path.Combine(directory, "missing-preview.wav"), CancellationToken.None);
        if (player.MediaPlayer.IsPlaying || Volatile.Read(ref stops) == 0)
            throw new InvalidOperationException("A missing preview must stop the previous table's playback");
        Console.WriteLine("PASS: A table without a preview still stops the previous table's playback");
    }

    private static void WriteSilentWave(string path, int seconds = 1)
    {
        const int sampleRate = 8000;
        var dataLength = sampleRate * 2 * seconds; // Mono, 16-bit PCM silence.
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
    }
}
