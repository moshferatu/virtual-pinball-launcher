using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>
/// Drives the real ReturnToLauncher.exe with injected keystrokes. Opt in with
/// --return-hotkey: it presses F12 and F12+Enter on this machine and starts a stub in
/// place of the launcher, so run it with nothing important focused.
/// </summary>
internal static class ReturnHotkeyChecks
{
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
        private readonly long padding; // MOUSEINPUT is larger than KEYBDINPUT
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static async Task RunAsync(string directory)
    {
        var companion = Path.Combine(AppContext.BaseDirectory, "ReturnToLauncher.exe");
        if (!File.Exists(companion))
            throw new FileNotFoundException("ReturnToLauncher.exe was not built next to the checks.", companion);

        var marker = Path.Combine(directory, "launched.txt");
        var stub = Path.Combine(directory, "launcher-stub.cmd");
        File.WriteAllText(stub, "@echo off\r\necho %*> \"" + marker + "\"\r\n");
        var config = Path.Combine(directory, "return-config.json");
        File.WriteAllText(config, "{ \"ReturnToLauncherHotkey\": \"F12+Enter\" }");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = companion,
            Arguments = $"--config \"{config}\" --launcher \"{stub}\" --once",
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start ReturnToLauncher.exe");

        try
        {
            await Task.Delay(1500); // let the hook install
            Check(!process.HasExited, "The companion stays resident while waiting for the hotkey");

            Press(0x7B); // a bare F12 tap must not trigger it (and passes through to whatever is focused)
            await Task.Delay(500);
            Check(!File.Exists(marker), "F12 alone does not start the launcher");

            Chord(0x7B, 0x0D); // F12+Enter
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!File.Exists(marker) && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            Check(File.Exists(marker), "F12+Enter starts the launcher");
            Check(process.WaitForExit(5000), "With --once the helper exits after starting the launcher");
        }
        finally
        {
            if (!process.HasExited) process.Kill();
        }
    }

    private static void Press(ushort key)
    {
        Send(key, false);
        Send(key, true);
    }

    private static void Chord(ushort modifier, ushort key)
    {
        Send(modifier, false);
        Thread.Sleep(50);
        Send(key, false);
        Send(key, true);
        Thread.Sleep(50);
        Send(modifier, true);
    }

    private static void Send(ushort key, bool up)
    {
        var input = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = key, dwFlags = up ? KEYEVENTF_KEYUP : 0 } };
        if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException($"SendInput failed: {Marshal.GetLastWin32Error()}");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine($"PASS: {description}");
    }
}
