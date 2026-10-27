using System.Runtime.InteropServices;
using System.Text;

namespace VirtualPinballLauncher;

/// <summary>
/// Brings a top-level window to the foreground and verifies that it got there.
/// Windows refuses SetForegroundWindow to a process that was not the last one the
/// user interacted with, so the usual workarounds are applied in turn: attach to
/// the current foreground thread's input queue, then synthesize a no-op mouse move
/// so this process counts as having received the last input event. (An Alt press is
/// the other classic trick, but a bare Alt puts a window into menu mode and can eat
/// the next keystrokes, so it is not used here.)
/// </summary>
public static class WindowActivation
{
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_CLOAKED = 14;
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const int ASFW_ANY = -1;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int dwProcessId);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    public static int ForegroundProcessId
    {
        get
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(foreground, out var pid);
            return pid;
        }
    }

    public static bool IsForeground(IntPtr hwnd) => hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;

    /// <summary>
    /// Lets the next process this one starts (or any process) take the foreground even if
    /// Windows would otherwise refuse it. Only works while this process holds the foreground.
    /// </summary>
    public static bool AllowAnyProcessToTakeForeground() => AllowSetForegroundWindow(ASFW_ANY);

    /// <summary>
    /// Moves the window to the front of the always-on-top layer without activating it.
    /// Calling this for several windows in turn stacks the last one in front.
    /// </summary>
    public static void BringToFrontOfTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Tries to make the window the foreground window. Returns whether it is afterwards.</summary>
    public static bool TryActivate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        else ShowWindow(hwnd, SW_SHOW);

        if (SetForegroundWindow(hwnd) && IsForeground(hwnd)) return true;

        // Borrow the foreground thread's input queue so the request is treated as coming from it.
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        if (foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true))
        {
            try
            {
                SetForegroundWindow(hwnd);
                BringWindowToTop(hwnd);
            }
            finally
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
            if (IsForeground(hwnd)) return true;
        }

        // Last resort: a zero-distance mouse move makes this process the last one to receive input.
        var nudge = new[] { new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MOVE } } };
        SendInput(1, nudge, Marshal.SizeOf<INPUT>());
        SetForegroundWindow(hwnd);
        SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        return IsForeground(hwnd);
    }

    /// <summary>
    /// Finds the most likely main window among the given processes: visible, not a tool,
    /// console, cloaked, or no-activate window, and the largest on screen. Games often
    /// create hidden helper windows first, so size is the tie-breaker that matters.
    /// </summary>
    public static IntPtr FindMainWindow(Func<int, bool> processMatches, out int processId)
    {
        var best = IntPtr.Zero;
        var bestPid = 0;
        long bestArea = -1;
        var className = new StringBuilder(64);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((exStyle & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || !processMatches(pid)) return true;
            if (!GetWindowRect(hwnd, out var rect)) return true;
            long area = (long)Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top);
            if (area <= 0) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            className.Clear();
            GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() == "ConsoleWindowClass") return true;
            if (area > bestArea)
            {
                bestArea = area;
                best = hwnd;
                bestPid = pid;
            }
            return true;
        }, IntPtr.Zero);
        processId = bestPid;
        return best;
    }

    /// <summary>Hides visible console windows that belong to matching processes. Returns how many were hidden.</summary>
    public static int HideConsoleWindows(Func<int, bool> processMatches)
    {
        var hidden = 0;
        var className = new StringBuilder(64);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            className.Clear();
            GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() != "ConsoleWindowClass") return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || !processMatches(pid)) return true;
            ShowWindow(hwnd, SW_HIDE);
            hidden++;
            return true;
        }, IntPtr.Zero);
        return hidden;
    }
}
