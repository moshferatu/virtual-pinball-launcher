using System.Diagnostics;
using System.IO;
using VirtualPinballLauncher;

internal static class ExternalProgramChecks
{
    public static async Task RunAsync(string directory)
    {
        // A launcher batch file that starts the real work detached and exits at once,
        // the same shape as a cabinet .cmd that starts a game with "start".
        var child = Path.Combine(directory, "child.cmd");
        File.WriteAllText(child, "@echo off\r\nping -n 3 127.0.0.1 >nul\r\n");
        var wrapper = Path.Combine(directory, "wrapper.cmd");
        // "start" on a .cmd would keep a shell open with /K; start the child like a program instead.
        File.WriteAllText(wrapper, $"@echo off\r\nstart \"\" /b cmd /c \"{child}\"\r\nexit /b 0\r\n");

        var stopwatch = Stopwatch.StartNew();
        await ExternalProgram.RunAsync(new ExternalProgramSpec { Command = wrapper, HideConsole = true, ActivationTimeout = TimeSpan.FromSeconds(1) });
        stopwatch.Stop();
        Check(stopwatch.Elapsed >= TimeSpan.FromSeconds(1.5),
            $"Run waits for everything a batch file started, not just the batch file ({stopwatch.Elapsed.TotalSeconds:0.0}s)");

        var quick = Path.Combine(directory, "quick.cmd");
        File.WriteAllText(quick, "@echo off\r\nexit /b 0\r\n");
        stopwatch.Restart();
        await ExternalProgram.RunAsync(new ExternalProgramSpec { Command = quick, HideConsole = true, ActivationTimeout = TimeSpan.FromSeconds(1) });
        Check(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "Run returns as soon as the process tree is empty");

        var withArguments = Path.Combine(directory, "args.cmd");
        var marker = Path.Combine(directory, "args.txt");
        File.WriteAllText(withArguments, "@echo off\r\necho %1 %2> \"" + marker + "\"\r\n");
        await ExternalProgram.RunAsync(new ExternalProgramSpec
        {
            Command = withArguments, Arguments = "--cabinet second", WorkingDirectory = directory, HideConsole = true,
            ActivationTimeout = TimeSpan.FromSeconds(1)
        });
        Check(File.Exists(marker) && File.ReadAllText(marker).Trim() == "--cabinet second", "Run passes Arguments through to the program");

        var missing = false;
        try { await ExternalProgram.RunAsync(new ExternalProgramSpec { Command = Path.Combine(directory, "missing.exe") }); }
        catch (FileNotFoundException) { missing = true; }
        Check(missing, "A missing program is reported rather than silently ignored");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine($"PASS: {description}");
    }
}
