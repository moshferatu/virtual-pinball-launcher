using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Brushes = System.Windows.Media.Brushes;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace VirtualPinballLauncher;

public partial class VolumeOverlayWindow : Window
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_CONTROL = 0x11;
    private const int VK_LEFT = 0x25;
    private const int VK_RIGHT = 0x27;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int BarCount = 20;
    private const float VolumeStep = 1f / BarCount;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    private MMDeviceEnumerator? deviceEnumerator;
    private MMDevice? device;
    private IntPtr hwnd;
    private DispatcherTimer? hideTimer;
    private readonly Rectangle[] bars = new Rectangle[BarCount];

    // Keep the delegate alive so the GC doesn't collect it while the hook is active.
    private LowLevelKeyboardProc? hookProc;
    private IntPtr hookHandle;

    public VolumeOverlayWindow()
    {
        InitializeComponent();
        BuildBars();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    private void BuildBars()
    {
        for (int i = 0; i < BarCount; i++)
        {
            var rect = new Rectangle
            {
                Width = 26,
                Height = 56,
                Margin = new Thickness(3, 0, 3, 0),
                Stroke = Brushes.Yellow,
                StrokeThickness = 2,
                RadiusX = 2,
                RadiusY = 2
            };
            bars[i] = rect;
            VolumeBars.Children.Add(rect);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        hwnd = helper.Handle;

        // Click-through, no activation, hide from Alt-Tab.
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        deviceEnumerator = new MMDeviceEnumerator();
        device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        hideTimer.Tick += (_, _) =>
        {
            hideTimer!.Stop();
            VolumePanel.Visibility = Visibility.Collapsed;
        };

        InstallKeyboardHook();
        UpdateBars(GetCurrentVolume());
    }

    private void InstallKeyboardHook()
    {
        hookProc = HookCallback;

        // WH_KEYBOARD_LL needs a valid module handle; the EXE's module works fine.
        using var curProcess = Process.GetCurrentProcess();
        var moduleName = curProcess.MainModule?.ModuleName;
        var hMod = moduleName != null ? GetModuleHandle(moduleName) : IntPtr.Zero;

        hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, hMod, 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                if (ctrl)
                {
                    if (kb.vkCode == VK_LEFT)
                    {
                        Dispatcher.BeginInvoke(() => AdjustVolume(-VolumeStep));
                        return new IntPtr(1);
                    }
                    if (kb.vkCode == VK_RIGHT)
                    {
                        Dispatcher.BeginInvoke(() => AdjustVolume(+VolumeStep));
                        return new IntPtr(1);
                    }
                }
            }
        }
        return CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    private float GetCurrentVolume()
    {
        return device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0f;
    }

    private void SetVolume(float level)
    {
        if (device == null) return;
        device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(level, 0f, 1f);
    }

    private void AdjustVolume(float delta)
    {
        // Snap to the nearest 5% step before adjusting so the bars line up cleanly
        // even if the system volume started at an off-grid value.
        var current = GetCurrentVolume();
        var snapped = (float)Math.Round(current / VolumeStep) * VolumeStep;
        var next = Math.Clamp(snapped + delta, 0f, 1f);
        SetVolume(next);
        UpdateBars(next);
        ShowOverlayBriefly();
    }

    private void UpdateBars(float volume)
    {
        int filled = (int)Math.Round(volume / VolumeStep);
        for (int i = 0; i < BarCount; i++)
        {
            bars[i].Fill = i < filled ? Brushes.Yellow : Brushes.Transparent;
        }
    }

    private void ShowOverlayBriefly()
    {
        VolumePanel.Visibility = Visibility.Visible;

        // Force the overlay to the front of the topmost band so it sits above the
        // (also-topmost) launcher window without using an Owner relationship —
        // owned windows get demoted when MainWindow drops Topmost during VPX play.
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        hideTimer?.Stop();
        hideTimer?.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hookHandle);
            hookHandle = IntPtr.Zero;
        }
        device?.Dispose();
        deviceEnumerator?.Dispose();
    }
}
