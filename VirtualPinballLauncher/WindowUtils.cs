using System.Linq;
using System.Windows;
using Forms = System.Windows.Forms;

namespace VirtualPinballLauncher;

public static class WindowUtils
{
    /// <summary>The DMD is the rightmost monitor.</summary>
    public static int DmdMonitor => Forms.Screen.AllScreens.Length - 1;

    /// <summary>The backglass is the monitor just left of the DMD.</summary>
    public static int BackglassMonitor => Forms.Screen.AllScreens.Length - 2;

    internal static void PreventOverlayActivation(this Window host, FrameworkElement content)
    {
        // VideoView moves its content into an owned Window when its template is
        // applied. Configure that window before the native video host shows it.
        var overlay = Window.GetWindow(content);
        if (overlay == null || overlay == host) return;
        overlay.ShowActivated = false;
        overlay.Focusable = false;
    }

    /// <summary>
    /// The monitor at an index counted from the left (the larger one first when two share
    /// a left edge), clamped to the monitors present. Null only when there are no monitors.
    /// </summary>
    public static Forms.Screen? ScreenAt(int index)
    {
        var sorted = Forms.Screen.AllScreens
            .OrderBy(s => s.Bounds.Left)
            .ThenByDescending(s => s.Bounds.Width * s.Bounds.Height)
            .ToArray();

        return sorted.Length == 0 ? null : sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    public static void MoveToMonitor(this Window window, int index)
    {
        if (ScreenAt(index) is not { } screen) return;

        window.WindowState = WindowState.Normal;
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        window.Left = screen.Bounds.Left;
        window.Top = screen.Bounds.Top;
        window.Width = screen.Bounds.Width;
        window.Height = screen.Bounds.Height;
    }
}
