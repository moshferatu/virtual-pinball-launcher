using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using VirtualPinballLauncher.Input;

namespace ReturnToLauncher;

/// <summary>
/// An optional windowless helper for the desktop: it waits for the chord named by
/// ReturnToLauncherHotkey in config.json (F12+Enter by default) and starts the launcher,
/// or brings it to the front if it is already running. Put a shortcut to it in the
/// Startup folder to have the hotkey available from logon.
///
/// ReturnToLauncher.exe [--config path\to\config.json] [--launcher path\to\VirtualPinballLauncher.exe] [--once]
/// </summary>
internal static class Program
{
    private const string DefaultHotkey = "F12+Enter";
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    private static int Main(string[] args)
    {
        var configPath = Argument(args, "--config") ?? Path.Combine(AppContext.BaseDirectory, "config.json");
        var launcherPath = Argument(args, "--launcher") ?? Path.Combine(AppContext.BaseDirectory, "VirtualPinballLauncher.exe");
        var once = args.Any(a => string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase));

        string hotkeyText;
        try
        {
            hotkeyText = ReadHotkey(configPath);
        }
        catch (Exception ex)
        {
            return Fail(2, $"Could not read {configPath}:\n{ex.Message}");
        }

        if (!KeyChord.TryParse(hotkeyText, out var chord, out var error))
            return Fail(2, $"ReturnToLauncherHotkey in config.json is invalid.\n{error}");
        if (!chord.IsChord)
            return Fail(2, $"ReturnToLauncherHotkey must be a two-key chord such as F12+Enter, not '{hotkeyText}'.");

        // One listener per session is plenty; a second copy just leaves.
        using var singleInstance = new Mutex(true, @"Local\VirtualPinballLauncher.ReturnToLauncher", out var isFirst);
        if (!isFirst) return 0;

        using var finished = new ManualResetEventSlim();
        var listener = new ChordListener(new[] { chord });
        listener.ChordPressed += _ =>
        {
            StartOrActivateLauncher(launcherPath);
            if (once) finished.Set();
        };

        using var hook = new LowLevelKeyboardHook(listener.Handle);
        if (!hook.Start())
            return Fail(3, "Windows refused the keyboard hook, so the return hotkey is unavailable.");

        finished.Wait();
        return 0;
    }

    private static void StartOrActivateLauncher(string launcherPath)
    {
        var name = Path.GetFileNameWithoutExtension(launcherPath);
        var sessionId = Process.GetCurrentProcess().SessionId;
        foreach (var running in Process.GetProcessesByName(name).Where(p => p.SessionId == sessionId))
        {
            using (running)
            {
                var window = running.MainWindowHandle;
                if (window == IntPtr.Zero) continue;
                if (IsIconic(window)) ShowWindow(window, SW_RESTORE);
                SetForegroundWindow(window);
                return;
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = launcherPath,
                WorkingDirectory = Path.GetDirectoryName(launcherPath) ?? AppContext.BaseDirectory,
                UseShellExecute = false
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Could not start the launcher: {ex}");
            MessageBox.Show($"Could not start the launcher:\n{launcherPath}\n\n{ex.Message}",
                "Return To Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string ReadHotkey(string configPath)
    {
        if (!File.Exists(configPath)) return DefaultHotkey;
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("ReturnToLauncherHotkey", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString() ?? DefaultHotkey;
        }
        return DefaultHotkey;
    }

    private static string? Argument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static int Fail(int code, string message)
    {
        Trace.TraceError(message);
        MessageBox.Show(message, "Return To Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        return code;
    }
}
