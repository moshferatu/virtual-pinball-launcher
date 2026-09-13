using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VirtualPinballLauncher.Input;

/// <summary>
/// A WH_KEYBOARD_LL hook running on its own thread with its own message loop, so a
/// busy UI thread can never stall the callback and get the hook silently removed.
/// The handler runs on the hook thread and must return quickly.
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly Func<int, bool, bool> handler;
    private readonly ManualResetEventSlim started = new();
    private HookProc? hookProc; // kept alive for the lifetime of the hook
    private IntPtr hookHandle;
    private Thread? thread;
    private uint threadId;
    private bool disposed;

    /// <param name="handler">Receives (virtualKey, isDown) and returns true to swallow the event.</param>
    public LowLevelKeyboardHook(Func<int, bool, bool> handler)
    {
        this.handler = handler;
    }

    public bool IsActive => hookHandle != IntPtr.Zero;

    /// <summary>When the hook last delivered a key event (UTC ticks), or 0 if never. Lets a host detect a silently removed hook.</summary>
    public long LastEventTicks => Volatile.Read(ref lastEventTicks);
    private long lastEventTicks;

    /// <summary>Installs the hook. Returns false if Windows refused it.</summary>
    public bool Start()
    {
        if (thread != null) throw new InvalidOperationException("The hook was already started.");
        thread = new Thread(Run) { IsBackground = true, Name = "LowLevelKeyboardHook" };
        thread.Start();
        started.Wait();
        return IsActive;
    }

    private void Run()
    {
        threadId = GetCurrentThreadId();
        hookProc = Callback;
        hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
        if (hookHandle == IntPtr.Zero)
            Trace.TraceError($"SetWindowsHookEx failed with error {Marshal.GetLastWin32Error()}.");
        started.Set();
        if (hookHandle == IntPtr.Zero) return;

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(hookHandle);
        hookHandle = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = (int)wParam;
            var isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
            var isUp = message is WM_KEYUP or WM_SYSKEYUP;
            Volatile.Write(ref lastEventTicks, DateTime.UtcNow.Ticks);
            if (isDown || isUp)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                try
                {
                    if (handler((int)info.vkCode, isDown))
                        return new IntPtr(1);
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"Keyboard hook handler failed: {ex}");
                }
            }
        }
        return CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (thread == null) return;
        if (IsActive) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(2));
        started.Dispose();
    }
}
