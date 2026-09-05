using System.Linq;
using System.Windows;
using Forms = System.Windows.Forms;

namespace VirtualPinballLauncher;

public static class WindowUtils
{
    internal static void PreventOverlayActivation(this Window host, FrameworkElement content)
    {
        // VideoView moves its content into an owned Window when its template is
        // applied. Configure that window before the native video host shows it.
        var overlay = Window.GetWindow(content);
        if (overlay == null || overlay == host) return;
        overlay.ShowActivated = false;
        overlay.Focusable = false;
    }

    public static void MoveToMonitor(this Window window, int index)
    {
        var sorted = Forms.Screen.AllScreens
            .OrderBy(s => s.Bounds.Left)
            .ThenByDescending(s => s.Bounds.Width * s.Bounds.Height)
            .ToArray();

        if (sorted.Length == 0) return;

        var screen = sorted[Math.Clamp(index, 0, sorted.Length - 1)];

        window.WindowState = WindowState.Normal;
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        window.Left = screen.Bounds.Left;
        window.Top = screen.Bounds.Top;
        window.Width = screen.Bounds.Width;
        window.Height = screen.Bounds.Height;
    }
}
