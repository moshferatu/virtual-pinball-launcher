using System.Diagnostics;
using System.IO;

namespace VirtualPinballLauncher;

/// <summary>
/// Routes Trace output to launcher.log next to the executable (or the local app data
/// folder if that is read-only). The file is truncated at every start, so it holds one
/// session: key chords, program launches, window activation, and focus steps.
/// </summary>
public static class LauncherLog
{
    public static string? Path { get; private set; }

    public static void Install()
    {
        foreach (var candidate in new[]
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "launcher.log"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VirtualPinballLauncher", "launcher.log")
        })
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(candidate)!);
                var writer = new StreamWriter(candidate, append: false) { AutoFlush = true };
                Trace.Listeners.Add(new TextWriterTraceListener(writer) { TraceOutputOptions = TraceOptions.DateTime });
                Trace.AutoFlush = true;
                Path = candidate;
                Info($"Log started. Process {Environment.ProcessId}, args: {string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // try the next location
            }
        }
    }

    public static void Info(string message) =>
        Trace.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
}
