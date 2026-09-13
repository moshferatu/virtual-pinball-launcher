using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using VirtualPinballLauncher;

internal static class WindowFocusChecks
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

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
        if (failure != null) throw new InvalidOperationException("Window input focus check failed", failure);
    }

    private static async Task CheckAsync(string directory)
    {
        await using var previews = await PreviewPlayback.CreateAsync();
        var main = new MainWindow(previews);
        var dmd = new DmdWindow();
        var backglass = new BackglassWindow(previews.Backglass);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        main.Closed += (_, _) => closed.TrySetResult();
        main.ContentRendered += (_, _) => rendered.TrySetResult();

        // Use the real views and input handlers in small windows. Skip cabinet setup,
        // cursor hiding, audio hooks, and the user's config/VPX installation.
        var loaded = typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
        main.Loaded -= (RoutedEventHandler)loaded.CreateDelegate(typeof(RoutedEventHandler), main);
        SetField(main, "config", new Config { TablesDirectory = directory });
        SetField(main, "tableList", new List<TableItem>
        {
            new(System.IO.Path.Combine(directory, "First.vpx"), "First", null, null),
            new(System.IO.Path.Combine(directory, "Second.vpx"), "Second", null, null)
        });
        foreach (var window in new Window[] { main, dmd, backglass })
        {
            window.Width = 320;
            window.Height = 180;
            window.ShowInTaskbar = false;
        }

        try
        {
            main.Loaded += (_, _) =>
            {
                dmd.Show();
                backglass.Show();
            };
            main.Show();
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await SettleAsync();
            CheckInputFocus(main, "Startup gives the launcher native and WPF keyboard focus after secondary views load");

            // Deliver a cabinet flipper key to the HWND that actually owns focus.
            // Unlike RaiseEvent, this exercises native-to-WPF keyboard routing.
            var inputWindow = GetFocus();
            var key = new IntPtr(KeyInterop.VirtualKeyFromKey(Key.RightShift));
            if (!PostMessage(inputWindow, 0x0100, key, new IntPtr(1))
                || !PostMessage(inputWindow, 0x0101, key, new IntPtr(unchecked((int)0xC0000001))))
                throw new InvalidOperationException("Could not deliver the test cabinet button");
            await SettleAsync();
            if (((TextBlock)main.FindName("TableNameText")).Text != "Second")
                throw new InvalidOperationException("The cabinet button did not navigate to the next table");
            Console.WriteLine("PASS: A cabinet button navigates tables without first clicking the launcher");

            dmd.Hide();
            backglass.Hide();
            dmd.Show();
            backglass.Show();
            await SettleAsync();
            CheckInputFocus(main, "Showing secondary views again leaves launcher input focused");

            var game = new Window { Title = "Test game input", Width = 320, Height = 180, ShowInTaskbar = false };
            try
            {
                SetField(main, "launchInProgress", true);
                game.Show();
                Keyboard.Focus(game);
                RequestInputFocus(main);
                await SettleAsync();
                if (GetFocus() != new WindowInteropHelper(game).Handle || Keyboard.FocusedElement != game)
                    throw new InvalidOperationException("Queued launcher focus work interrupted game input");
                Console.WriteLine("PASS: Queued launcher focus work leaves input with a running game");

                SetField(main, "launchInProgress", false);
                RequestInputFocus(main);
                await SettleAsync();
                CheckInputFocus(main, "Returning to the launcher restores native and WPF keyboard focus");

                // The picker must prefer a program's real window over a small helper window.
                var helper = new Window { Title = "Helper", Width = 40, Height = 30, ShowInTaskbar = false };
                helper.Show();
                await SettleAsync();
                var picked = WindowActivation.FindMainWindow(pid => pid == Environment.ProcessId, out var pickedPid);
                helper.Close();
                if (pickedPid != Environment.ProcessId || picked == new WindowInteropHelper(helper).Handle)
                    throw new InvalidOperationException("Main window detection picked a small helper window");
                Console.WriteLine("PASS: Main window detection prefers the largest visible window of the process");

            }
            finally
            {
                SetField(main, "launchInProgress", false);
                game.Close();
            }
        }
        finally
        {
            main.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            backglass.DetachPlayer();
            backglass.Close();
            dmd.Close();
        }
    }

    private static async Task SettleAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static void CheckInputFocus(MainWindow main, string description)
    {
        var hwnd = new WindowInteropHelper(main).Handle;
        if (GetFocus() != hwnd || Keyboard.FocusedElement != main)
            throw new InvalidOperationException($"{description}: expected HWND {hwnd}, foreground {GetForegroundWindow()}, native focus {GetFocus()}, WPF focus {Keyboard.FocusedElement}, active {main.IsActive}");
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var foregroundProcess);
        if (foregroundProcess == Environment.ProcessId && foreground != hwnd)
            throw new InvalidOperationException("A secondary test window took foreground activation from the launcher");
        if (foregroundProcess != Environment.ProcessId)
            Console.WriteLine("SKIP: Desktop foreground assertion (Windows did not foreground the automated test process)");
        Console.WriteLine($"PASS: {description}");
    }

    private static void SetField(MainWindow window, string name, object value) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static void RequestInputFocus(MainWindow window) =>
        typeof(MainWindow).GetMethod("RequestInputFocus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
}
