using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Brush = System.Windows.Media.Brush;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace VirtualPinballLauncher;

public partial class VolumeOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    // One line per percent so every 0-100 value has its own mark.
    private const int LineCount = 100;
    private const float VolumeStep = 1f / LineCount;

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

    // Lines above the current level use a dimmed title color so the whole scale stays visible.
    private Brush filledBrush = null!;
    private Brush unfilledBrush = null!;

    private MMDeviceEnumerator? deviceEnumerator;
    private MMDevice? device;
    private IntPtr hwnd;
    private DispatcherTimer? hideTimer;
    private readonly Rectangle[] lines = new Rectangle[LineCount];

    // Hold-to-repeat state: the step being repeated, whether the key is still
    // physically down, and how long it has been held (which sets the repeat rate).
    private readonly DispatcherTimer repeatTimer = new();
    private readonly Stopwatch holdStopwatch = new();
    private Func<bool>? holdStillDown;
    private float holdDelta;
    private TimeSpan holdLastTick;
    private double holdStepsOwed;

    public VolumeOverlayWindow()
    {
        InitializeComponent();
        BuildLines();
        ApplyAppearance(new Appearance());
        repeatTimer.Tick += (_, _) => OnRepeatTick();
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => UpdateLineDimensions();
        Closed += OnClosed;
    }

    private void BuildLines()
    {
        for (int i = 0; i < LineCount; i++)
        {
            var line = new Rectangle { VerticalAlignment = VerticalAlignment.Center };
            lines[i] = line;
            VolumeBars.Children.Add(line);
        }
    }

    public void ApplyAppearance(Appearance appearance)
    {
        // Opaque so the table name overlay underneath never shows through.
        VolumePanel.Background = appearance.OpaqueOverlayBackgroundBrush;
        VolumePanel.BorderBrush = appearance.AccentBrush;
        VolumePanel.Margin = new Thickness(0, 0, 0, appearance.SafeOverlayBottomMargin);
        filledBrush = appearance.TitleBrush;
        unfilledBrush = appearance.DimTitleBrush;
        UpdateLines(GetCurrentVolume());
    }

    private void UpdateLineDimensions()
    {
        if (ActualWidth <= 0) return;

        // The overlay fills the monitor, so ActualWidth is the screen width. Aim
        // for the row of lines to span ~75% of that, then give every line an equal
        // slot. Each line's thickness ramps up with its position so the row reads
        // as a wedge: hairline at 1%, nearly filling its slot at 100%.
        double rowWidth = ActualWidth * 0.75;
        double slot = rowWidth / LineCount;
        double minThickness = Math.Max(1.0, slot * 0.15);
        double maxThickness = slot * 0.85;
        double height = rowWidth * 0.08;

        for (int i = 0; i < LineCount; i++)
        {
            double t = (double)i / (LineCount - 1);
            double thickness = minThickness + (maxThickness - minThickness) * t;
            double sideMargin = (slot - thickness) / 2;

            var line = lines[i];
            line.Width = thickness;
            line.Height = height;
            line.Margin = new Thickness(sideMargin, 0, sideMargin, 0);
            line.RadiusX = thickness / 2;
            line.RadiusY = thickness / 2;
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

        UpdateLines(GetCurrentVolume());
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

    /// <summary>
    /// Raises the master volume one percent, then keeps raising it, faster and faster,
    /// for as long as <paramref name="stillDown"/> reports the key held. Bound through
    /// KeyBindings (VolumeUp).
    /// </summary>
    public void VolumeUp(Func<bool>? stillDown = null) => BeginHold(+VolumeStep, stillDown);

    /// <summary>Lowers the master volume one percent and keeps lowering it while held; see <see cref="VolumeUp"/>.</summary>
    public void VolumeDown(Func<bool>? stillDown = null) => BeginHold(-VolumeStep, stillDown);

    private void BeginHold(float delta, Func<bool>? stillDown)
    {
        AdjustVolume(delta);

        // Polling the physical key state, rather than waiting for a release event, means
        // a release the launcher never hears about (a game grabbing the keyboard, a hook
        // briefly gone) can never leave the volume running away.
        EndHold();
        if (stillDown == null) return;
        holdDelta = delta;
        holdStillDown = stillDown;
        holdLastTick = TimeSpan.Zero;
        holdStepsOwed = 0;
        holdStopwatch.Restart();
        repeatTimer.Interval = VolumeRepeat.Interval(TimeSpan.Zero);
        repeatTimer.Start();
    }

    private void OnRepeatTick()
    {
        if (holdStillDown?.Invoke() != true)
        {
            EndHold();
            return;
        }

        // The first repeat is always exactly one step; after that the rate and the real
        // time between ticks decide how many, so a late tick catches up rather than lagging.
        var held = holdStopwatch.Elapsed;
        var steps = holdLastTick == TimeSpan.Zero ? 1 : VolumeRepeat.StepsDue(held, held - holdLastTick, ref holdStepsOwed);
        holdLastTick = held;
        if (steps > 0) AdjustVolume(steps * holdDelta);
        repeatTimer.Interval = VolumeRepeat.Interval(held);
    }

    private void EndHold()
    {
        repeatTimer.Stop();
        holdStopwatch.Reset();
        holdStillDown = null;
    }

    private void AdjustVolume(float delta)
    {
        // Snap to a whole percent before adjusting so the lines line up cleanly
        // even if the system volume started at a fractional value.
        var current = GetCurrentVolume();
        var snapped = (float)Math.Round(current / VolumeStep) * VolumeStep;
        var next = Math.Clamp(snapped + delta, 0f, 1f);
        SetVolume(next);
        UpdateLines(next);
        ShowOverlayBriefly();
    }

    private void UpdateLines(float volume)
    {
        int filled = (int)Math.Round(volume / VolumeStep);
        for (int i = 0; i < LineCount; i++)
        {
            lines[i].Fill = i < filled ? filledBrush : unfilledBrush;
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
        EndHold();
        hideTimer?.Stop();
        device?.Dispose();
        deviceEnumerator?.Dispose();
    }
}
