using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Brushes = System.Windows.Media.Brushes;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace VirtualPinballLauncher;

public partial class VolumeOverlayWindow : Window
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID_VOLUME_DOWN = 0xB001;
    private const int HOTKEY_ID_VOLUME_UP = 0xB002;
    private const uint MOD_CONTROL = 0x0002;
    private const uint VK_LEFT = 0x25;
    private const uint VK_RIGHT = 0x27;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int BarCount = 20;
    private const float VolumeStep = 1f / BarCount;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private MMDeviceEnumerator? deviceEnumerator;
    private MMDevice? device;
    private IntPtr hwnd;
    private HwndSource? hwndSource;
    private DispatcherTimer? hideTimer;
    private readonly Rectangle[] bars = new Rectangle[BarCount];

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

        hwndSource = HwndSource.FromHwnd(hwnd);
        hwndSource?.AddHook(WndProc);

        RegisterHotKey(hwnd, HOTKEY_ID_VOLUME_DOWN, MOD_CONTROL, VK_LEFT);
        RegisterHotKey(hwnd, HOTKEY_ID_VOLUME_UP, MOD_CONTROL, VK_RIGHT);

        deviceEnumerator = new MMDeviceEnumerator();
        device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        hideTimer.Tick += (_, _) =>
        {
            hideTimer!.Stop();
            VolumePanel.Visibility = Visibility.Collapsed;
        };

        UpdateBars(GetCurrentVolume());
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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;

        int id = wParam.ToInt32();
        if (id == HOTKEY_ID_VOLUME_DOWN)
        {
            AdjustVolume(-VolumeStep);
            handled = true;
        }
        else if (id == HOTKEY_ID_VOLUME_UP)
        {
            AdjustVolume(+VolumeStep);
            handled = true;
        }
        return IntPtr.Zero;
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
        hideTimer?.Stop();
        hideTimer?.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(hwnd, HOTKEY_ID_VOLUME_DOWN);
            UnregisterHotKey(hwnd, HOTKEY_ID_VOLUME_UP);
        }
        hwndSource?.RemoveHook(WndProc);
        device?.Dispose();
        deviceEnumerator?.Dispose();
    }
}
