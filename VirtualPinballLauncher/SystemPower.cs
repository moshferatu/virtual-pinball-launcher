using System.Diagnostics;
using System.IO;

namespace VirtualPinballLauncher;

/// <summary>Backs the ShutdownPc and RestartPc key binding actions.</summary>
public static class SystemPower
{
    public static void PowerOff(bool restart)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            Arguments = restart ? "/r /t 0" : "/s /t 0",
            UseShellExecute = false,
            CreateNoWindow = true
        })?.Dispose();
    }
}
