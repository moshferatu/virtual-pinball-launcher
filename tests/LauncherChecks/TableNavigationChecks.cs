using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using VirtualPinballLauncher;

internal static class TableNavigationChecks
{
    public static void Run(string directory)
    {
        var frame = new DispatcherFrame();
        Exception? failure = null;
        _ = Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try { await CheckAsync(directory); }
            catch (Exception ex) { failure = ex; }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        if (failure != null) throw new InvalidOperationException("Table navigation check failed", failure);
    }

    private static async Task CheckAsync(string directory)
    {
        var tablesDirectory = Path.Combine(directory, "navigation");
        Directory.CreateDirectory(tablesDirectory);
        File.WriteAllText(Path.Combine(tablesDirectory, "table-stats.csv"),
            "Table Name,Times Played,Total Time Spent Playing,Last Date Played\n"
            + "First,2,01:05:00,\"September 4, 2026\"\n"
            + "Second,7,00:09:00,\"September 3, 2026\"\n");

        await using var previews = await PreviewPlayback.CreateAsync();
        var main = new MainWindow(previews);
        var dmd = new DmdWindow();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        main.Closed += (_, _) => closed.TrySetResult();
        // Keep the real views unshown so the check needs no cabinet configuration or focus.
        SetField(main, "config", new Config { TablesDirectory = tablesDirectory });
        SetField(main, "dmdWindow", dmd);
        var expected = new[]
        {
            (Name: "First", Plays: "2", Duration: "1 hour 5 minutes", Date: "September 4, 2026"),
            (Name: "Second", Plays: "7", Duration: "9 minutes", Date: "September 3, 2026"),
            (Name: "Unplayed", Plays: "0", Duration: "0 minutes", Date: "Never")
        };
        SetField(main, "tableList", expected.Select(table =>
            new TableItem(Path.Combine(tablesDirectory, table.Name + ".vpx"), table.Name, null, null)).ToList());

        void CheckSelection(int index)
        {
            var table = expected[index];
            if (((TextBlock)main.FindName("TableNameText")).Text != table.Name
                || ((TextBlock)dmd.FindName("TableTitleText")).Text != table.Name
                || ((Run)dmd.FindName("TimesPlayedValue")).Text != table.Plays
                || ((Run)dmd.FindName("TotalTimePlayedValue")).Text != table.Duration
                || ((Run)dmd.FindName("LastPlayedValue")).Text != table.Date)
                throw new InvalidOperationException($"DMD title and stats must immediately match selection: {table.Name}");
        }

        try
        {
            await (Task)Invoke(main, "RefreshPreviewAsync", false, false)!;
            CheckSelection(0);
            var index = 0;
            foreach (var direction in new[] { "Next", "Prev" })
            {
                // Repeat without yielding: every selection must update before the video delay,
                // including unplayed tables and wrapping around either end of the list.
                for (var press = 0; press < 8; press++)
                {
                    Invoke(main, direction, press != 0);
                    index = (index + (direction == "Next" ? 1 : expected.Length - 1)) % expected.Length;
                    CheckSelection(index);
                }
                Console.WriteLine($"PASS: Rapid {direction} navigation updates all DMD stats immediately, including unplayed tables and wraparound");
            }

            await Task.Delay(150);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            CheckSelection(index);
            Console.WriteLine("PASS: Delayed preview work leaves the DMD on the final selected table");
        }
        finally
        {
            main.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private static void SetField(MainWindow main, string name, object value) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, value);

    private static object? Invoke(MainWindow main, string name, params object[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, args);
}
