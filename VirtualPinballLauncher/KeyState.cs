using System.Runtime.InteropServices;

namespace VirtualPinballLauncher;

/// <summary>
/// Reads the physical state of a key straight from Windows, independent of which
/// window has focus. Used wherever a remembered key-down must not be trusted for
/// long, because releases can go missing while a game switches display modes.
/// </summary>
public static class KeyState
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public static bool IsPhysicallyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
