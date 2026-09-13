using System;
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
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int BarCount = 20;
    private const float VolumeStep = 1f / BarCount;

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

    public VolumeOverlayWindow()
    {
        InitializeComponent();
        BuildBars();
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => UpdateBarDimensions();
        Closed += OnClosed;
    }

    private void BuildBars()
    {
        for (int i = 0; i < BarCount; i++)
        {
            var rect = new Rectangle
            {
                Stroke = Brushes.Yellow,
                RadiusX = 2,
                RadiusY = 2
            };
            bars[i] = rect;
            VolumeBars.Children.Add(rect);
        }
    }

    private void UpdateBarDimensions()
    {
        if (ActualWidth <= 0) return;

        // The overlay fills the monitor, so ActualWidth is the screen width. Aim
        // for the bar row to span ~75% of that, then split evenly across all bars.
        double slot = ActualWidth * 0.75 / BarCount;
        double margin = slot * 0.05;
        double barWidth = slot - 2 * margin;
        double barHeight = barWidth * 2.0;
        double stroke = barWidth / 15.0;

        foreach (var bar in bars)
        {
            bar.Width = barWidth;
            bar.Height = barHeight;
            bar.Margin = new Thickness(margin, 0, margin, 0);
            bar.StrokeThickness = stroke;
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

    /// <summary>Raises the master volume one bar; bound through KeyBindings (VolumeUp).</summary>
    public void VolumeUp() => AdjustVolume(+VolumeStep);

    /// <summary>Lowers the master volume one bar; bound through KeyBindings (VolumeDown).</summary>
    public void VolumeDown() => AdjustVolume(-VolumeStep);

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
        device?.Dispose();
        deviceEnumerator?.Dispose();
    }
}
