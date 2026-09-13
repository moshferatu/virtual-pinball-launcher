using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using VirtualPinballLauncher;

/// <summary>
/// The launcher drops Topmost while another program has the screen and takes it back
/// afterwards. The main window owns the video view's transparent overlay window, so
/// the overlay must follow the main window through every cycle, not just the first.
/// </summary>
internal static class TopmostChecks
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    public static void Run()
    {
        var frame = new DispatcherFrame();
        Exception? failure = null;
        _ = Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try { await CheckAsync(); }
            catch (Exception ex) { failure = ex; }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        if (failure != null) throw new InvalidOperationException("Topmost cycle check failed", failure);
    }

    private static async Task CheckAsync()
    {
        await using var previews = await PreviewPlayback.CreateAsync();
        var main = new MainWindow(previews);
        var dmd = new DmdWindow();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        main.Closed += (_, _) => closed.TrySetResult();
        main.ContentRendered += (_, _) => rendered.TrySetResult();
        var loaded = typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
        main.Loaded -= (RoutedEventHandler)loaded.CreateDelegate(typeof(RoutedEventHandler), main);
        foreach (var window in new Window[] { main, dmd })
        {
            window.Width = 320;
            window.Height = 180;
            window.ShowInTaskbar = false;
        }

        try
        {
            main.Show();
            dmd.Show();
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await SettleAsync();

            var overlay = main.OwnedWindows.Cast<Window>().FirstOrDefault()
                ?? throw new InvalidOperationException("The video view did not create its owned overlay window");
            var list = (List<(Window Window, LoadingOverlay Overlay)>)typeof(MainWindow)
                .GetField("windows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            list.Add((main, (LoadingOverlay)main.FindName("LoadingOverlay")));
            list.Add((dmd, dmd.LoadingOverlay));
            typeof(MainWindow).GetField("dmdWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, dmd);

            Console.WriteLine($"STATE initial: main={IsTopmost(main)} overlay={IsTopmost(overlay)} dmd={IsTopmost(dmd)} overlayVisible={overlay.IsVisible} overlayOwnerIsMain={overlay.Owner == main}");
            main.Topmost = true;
            await SettleAsync();
            Console.WriteLine($"STATE after Topmost=true: main={IsTopmost(main)} overlay={IsTopmost(overlay)}");

            for (var cycle = 1; cycle <= 3; cycle++)
            {
                Invoke(main, "SuspendWindows");
                await SettleAsync();
                Check(!IsTopmost(main) && !IsTopmost(dmd) && !IsTopmost(overlay),
                    $"Cycle {cycle}: suspending clears topmost on the launcher windows and the owned video overlay");

                Invoke(main, "OnExternalWindowActivated");
                await SettleAsync();
                Invoke(main, "RestoreWindows");
                await SettleAsync();
                await Task.Delay(200);
                await SettleAsync();
                await Task.Delay(500);
                await SettleAsync();
                Check(dmd.IsVisible, $"Cycle {cycle}: restoring shows the DMD window again");
                if (WindowActivation.IsForeground(new WindowInteropHelper(main).Handle))
                    Check(IsTopmost(main) && IsTopmost(dmd) && IsTopmost(overlay),
                        $"Cycle {cycle}: restoring makes the launcher windows and the owned video overlay topmost again");
                else
                    Console.WriteLine($"SKIP: Cycle {cycle} topmost assertion (Windows kept the foreground elsewhere: main={IsTopmost(main)} overlay={IsTopmost(overlay)} dmd={IsTopmost(dmd)})");
            }
        }
        finally
        {
            main.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            dmd.Close();
        }
    }

    private static bool IsTopmost(Window window) =>
        (GetWindowLong(new WindowInteropHelper(window).Handle, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    private static void Invoke(MainWindow main, string name) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);

    private static async Task SettleAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine($"PASS: {description}");
    }
}
