using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace VirtualPinballLauncher;

static class ScreenSize
{
    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);

    // The primary monitor is assumed to be the playfield.
    const int SM_CXSCREEN = 0; // Primary monitor width
    const int SM_CYSCREEN = 1; // Primary monitor height

    public static int PlayfieldWidth  => GetSystemMetrics(SM_CXSCREEN);
    public static int PlayfieldHeight => GetSystemMetrics(SM_CYSCREEN);

    public static Rectangle? GetBackglassMonitorBounds()
    {
        var sorted = Forms.Screen.AllScreens
            .OrderByDescending(s => s.Bounds.Left)
            .ThenBy(s => s.Bounds.Width * s.Bounds.Height)
            .ToArray();

        var screen = sorted.Length >= 2 ? sorted[1] : sorted.FirstOrDefault();
        return screen?.Bounds;
    }
}