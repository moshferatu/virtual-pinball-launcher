using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using VirtualPinballLauncher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LauncherChecks-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var stats = new TableStats();
            Check(stats.GetAllTableStats(directory).Count == 0, "New stats file is empty");
            Check(stats.GetTableStatsForDisplay(directory, "Missing").TimesPlayed == 0,
                "Missing tables have default stats");

            const string name = "Table, with \"quotes\"";
            stats.UpdateTableStatsAfterPlay(directory, name, TimeSpan.FromMinutes(5));
            stats.UpdateTableStatsAfterPlay(directory, name.ToUpperInvariant(), TimeSpan.FromMinutes(7));
            var entry = stats.GetTableStatsForDisplay(directory, name.ToLowerInvariant());
            Check(entry.TimesPlayed == 2 && entry.TotalTimeSpentPlaying == TimeSpan.FromMinutes(12),
                "Case-insensitive updates refresh the cached index");
            Check(stats.GetAllTableStats(directory).Count == 1, "Case variants share an entry");
            entry = new TableStats().GetTableStatsForDisplay(directory, name);
            Check(entry.TimesPlayed == 2 && entry.LastDatePlayed == DateTime.Today,
                "Quoted CSV names and dates survive save/reload");

            var csvPath = Path.Combine(directory, "table-stats.csv");
            File.AppendAllText(csvPath, "Duplicate,3,00:03:00,\nDUPLICATE,9,00:09:00,\n");
            stats = new TableStats();
            Check(stats.GetTableStatsForDisplay(directory, "duplicate").TimesPlayed == 3,
                "Duplicate names retain the first matching row");
            stats.UpdateTableStatsAfterPlay(directory, "duplicate", TimeSpan.FromMinutes(1));
            Check(File.ReadAllLines(csvPath).Length == 4, "Saving preserves existing duplicate rows");
            Check(stats.GetAllTableStats(directory)["DUPLICATE"].TimesPlayed == 4,
                "Indexed updates use the first duplicate row");

            var otherDirectory = Path.Combine(directory, "other");
            Directory.CreateDirectory(otherDirectory);
            Check(stats.GetAllTableStats(otherDirectory).Count == 0, "Switching directories resets the cache");
            Check(stats.GetAllTableStats(directory).Count == 2, "Switching back reloads the right stats");

            var overlay = new LoadingOverlay();
            Check(overlay.Visibility == Visibility.Collapsed, "Overlay starts collapsed");
            overlay.SetLoading(true);
            Check(overlay.Visibility == Visibility.Visible, "Loading shows the overlay");
            overlay.SetLoading(false);
            Check(overlay.Visibility == Visibility.Collapsed, "Loading completion hides the overlay");

            CheckDmdDisplay();
            KeyBindingChecks.Run();
            VolumeRepeatChecks.Run();
            ExternalProgramChecks.RunAsync(directory).GetAwaiter().GetResult();
            CheckMediaQueueAsync().GetAwaiter().GetResult();
            CheckRecordingFilesAsync(directory).GetAwaiter().GetResult();
            MediaPlaybackChecks.RunAsync(directory).GetAwaiter().GetResult();
            TableNavigationChecks.Run(directory);
            CheckWindowShutdown();
            if (args.Contains("--window-focus")) WindowFocusChecks.Run(directory);
            if (args.Contains("--window-focus") || args.Contains("--topmost")) TopmostChecks.Run();
            Console.WriteLine("All launcher regression checks passed.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckWindowShutdown()
    {
        var frame = new DispatcherFrame();
        Exception? failure = null;
        _ = Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try
            {
                await using var previews = await PreviewPlayback.CreateAsync();
                var window = new MainWindow(previews);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                // Keep the window unshown: exercise WPF attachment/cleanup without taking over the desktop.
                window.Close();
                Check(!closed.Task.IsCompleted, "Window close yields while playback shuts down");
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Check(((LibVLCSharp.WPF.VideoView)window.FindName("PreviewPlayer")).MediaPlayer == null,
                    "WPF close detaches the video view before shared playback disposal");
            }
            catch (Exception ex) { failure = ex; }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        if (failure != null) throw new InvalidOperationException("WPF shutdown check failed", failure);
    }

    private static void CheckDmdDisplay()
    {
        var dmd = new DmdWindow();
        try
        {
            var properties = (TextBlock)dmd.FindName("PropertiesText");
            var inlines = properties.Inlines.ToArray();
            dmd.ShowTableProperties("Example", new TableStatsEntry("Example")
            {
                TimesPlayed = 2,
                TotalTimeSpentPlaying = TimeSpan.FromMinutes(61),
                LastDatePlayed = new DateTime(2026, 9, 4)
            });
            var text = new TextRange(properties.ContentStart, properties.ContentEnd).Text;
            Check(text == "Times Played: 2\r\nTotal Time Played: 1 hour 1 minute\r\nLast Played: September 4, 2026",
                "Structured DMD stats preserve labels, spacing, duration, and date formatting");
            dmd.ShowTableProperties("Unplayed", new TableStatsEntry("Unplayed"));
            Check(((Run)dmd.FindName("LastPlayedValue")).Text == "Never"
                && ((Run)dmd.FindName("TotalTimePlayedValue")).Text == "0 minutes",
                "Unplayed DMD stats replace the previous table's values");
            Check(properties.Inlines.SequenceEqual(inlines), "DMD updates reuse the same inline elements");
            dmd.ShowTableProperties("No Table Selected", null);
            Check(properties.Visibility == Visibility.Collapsed, "Empty selections hide the DMD stats");
        }
        finally { dmd.Close(); }
    }

    private static async Task CheckMediaQueueAsync()
    {
        var queue = new MediaOperationQueue();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();
        var callerThread = Environment.CurrentManagedThreadId;
        var first = queue.Enqueue(() =>
        {
            Check(Environment.CurrentManagedThreadId != callerThread, "Media work runs off the calling UI thread");
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            calls.Add("play");
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var cancelled = queue.Enqueue(() => calls.Add("cancelled"), cancellation.Token);
        var failed = queue.Enqueue(() => throw new IOException("Simulated media failure"));
        var stopped = queue.Enqueue(() => calls.Add("stop"));
        var disposed = queue.CompleteAsync(() => calls.Add("dispose"));
        var disposedAgain = queue.CompleteAsync(() => calls.Add("duplicate dispose"));
        await queue.Enqueue(() => calls.Add("late playback"));
        Check(!stopped.IsCompleted && !disposed.IsCompleted, "Stop and disposal wait for pending playback");
        cancellation.Cancel();
        release.Set();
        await first;
        await ExpectFailureAsync<OperationCanceledException>(() => cancelled);
        await ExpectFailureAsync<IOException>(() => failed);
        await Task.WhenAll(stopped, disposed, disposedAgain).WaitAsync(TimeSpan.FromSeconds(10));
        Check(calls.SequenceEqual(new[] { "play", "stop", "dispose" }),
            "Media operations remain ordered through cancellation, failure, and repeated disposal");
    }

    private static async Task CheckRecordingFilesAsync(string directory)
    {
        var playfield = Path.Combine(directory, "playfield.mp4");
        var backglass = Path.Combine(directory, "backglass.mp4");
        File.WriteAllText(playfield, "old playfield");
        File.WriteAllText(backglass, "old backglass");

        foreach (var failureStage in new[] { 0, 1, 2 })
        {
            await ExpectFailureAsync<IOException>(() => RecordedPreview.RecordPairAsync(playfield, backglass, (first, second) =>
            {
                if (failureStage >= 1) File.WriteAllText(first, "partial playfield");
                if (failureStage >= 2) File.WriteAllText(second, "partial backglass");
                throw new IOException("Simulated recorder initialization/capture failure");
            }));
            Check(File.ReadAllText(playfield) == "old playfield" && File.ReadAllText(backglass) == "old backglass",
                $"Recording failure at stage {failureStage} preserves both original previews");
        }

        await ExpectFailureAsync<IOException>(() => RecordedPreview.RecordPairAsync(playfield, backglass, (first, second) =>
        {
            File.WriteAllText(first, "new playfield");
            File.WriteAllText(second, "");
            return Task.CompletedTask;
        }));
        Check(File.ReadAllText(playfield) == "old playfield" && File.ReadAllText(backglass) == "old backglass",
            "An empty output prevents either preview from being replaced");

        await RecordedPreview.RecordPairAsync(playfield, backglass, (first, second) =>
        {
            Check(Path.GetDirectoryName(first) == directory && Path.GetDirectoryName(second) == directory
                && Path.GetExtension(first) == ".mp4", "Temporary captures keep the destination directory and video extension");
            File.WriteAllText(first, "new playfield");
            File.WriteAllText(second, "new backglass");
            Check(File.ReadAllText(playfield) == "old playfield" && File.ReadAllText(backglass) == "old backglass",
                "Existing previews remain intact until both recordings complete");
            return Task.CompletedTask;
        });
        Check(File.ReadAllText(playfield) == "new playfield" && File.ReadAllText(backglass) == "new backglass",
            "Successful recording replaces both previews");
        Check(!Directory.EnumerateFiles(directory, "*.recording.mp4").Any(),
            "Temporary captures are cleaned up after success and failure");

        using var pending = new RecordedPreview(playfield);
        File.WriteAllText(pending.TemporaryPath, "replacement");
        using (File.Open(playfield, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var rejected = false;
            try { pending.Commit(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
            Check(rejected && File.ReadAllText(playfield) == "new playfield",
                "A locked destination survives a failed replacement");
        }
    }

    private static async Task ExpectFailureAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine($"PASS: {description}");
    }
}
