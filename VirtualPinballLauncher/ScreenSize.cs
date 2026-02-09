using System.Runtime.InteropServices;

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
}