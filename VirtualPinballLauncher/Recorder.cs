using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace VirtualPinballLauncher;

public static class Recorder
{
    public static async Task RecordAsync(Config config, string vpxPath)
    {
        if (!File.Exists(config.VpxExePath))
            throw new FileNotFoundException("VPX executable not found.", config.VpxExePath);

        if (!File.Exists(vpxPath))
            throw new FileNotFoundException("VPX table not found.", vpxPath);

        if (string.IsNullOrWhiteSpace(config.VideosDirectory))
            throw new ArgumentException("VideosDirectory is not set in config.json.", nameof(config.VideosDirectory));

        bool recordBackglass = !string.IsNullOrWhiteSpace(config.BackglassVideosDirectory);

        System.Drawing.Rectangle? backglassBounds = null;
        if (recordBackglass)
        {
            backglassBounds = ScreenSize.GetBackglassMonitorBounds();
            if (backglassBounds is null)
                throw new InvalidOperationException("No backglass monitor could be detected.");
        }

        Directory.CreateDirectory(config.VideosDirectory);
        if (recordBackglass)
            Directory.CreateDirectory(config.BackglassVideosDirectory!);

        // 1) Launch the VPX table once.
        var vpx = Process.Start(new ProcessStartInfo
        {
            FileName = config.VpxExePath,
            Arguments = $"/play -\"{vpxPath}\" /minimized",
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(config.VpxExePath) ?? Environment.CurrentDirectory
        });

        // Give the table time to come to the foreground.
        await Task.Delay(TimeSpan.FromSeconds(config.RecordDelaySeconds));

        if (vpx == null)
            throw new Exception("Failed to start VPX process.");

        var tableName = Path.GetFileNameWithoutExtension(vpxPath);
        var extension = string.IsNullOrWhiteSpace(config.VideoExtension) ? ".mp4" : config.VideoExtension;
        int videoLength = config.RecordSeconds;

        // 2) Build and start both ffmpeg captures in parallel.
        var playfieldFile = Path.Combine(config.VideosDirectory, tableName + extension);
        if (File.Exists(playfieldFile))
            File.Delete(playfieldFile);

        var playfieldArgs =
            $"-y -f gdigrab -framerate 120 " +
            $"-video_size {ScreenSize.PlayfieldWidth}x{ScreenSize.PlayfieldHeight} " +
            $"-i desktop -t {videoLength} " +
            $"-vf \"scale=in_range=pc:out_range=tv\" " +
            $"-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p " +
            $"-colorspace bt709 -color_primaries bt709 -color_trc bt709 " +
            $"\"{playfieldFile}\"";

        var ffmpegPlayfield = Process.Start(new ProcessStartInfo
        {
            FileName = config.FfmpegPath,
            Arguments = playfieldArgs,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new Exception("Failed to start ffmpeg process for playfield. Check FfmpegPath in config.json.");

        Process? ffmpegBackglass = null;
        if (recordBackglass)
        {
            var rect = backglassBounds!.Value;
            var backglassFile = Path.Combine(config.BackglassVideosDirectory!, tableName + extension);
            if (File.Exists(backglassFile))
                File.Delete(backglassFile);

            var backglassArgs =
                $"-y -f gdigrab -framerate 120 " +
                $"-offset_x {rect.Left} -offset_y {rect.Top} " +
                $"-video_size {rect.Width}x{rect.Height} " +
                $"-i desktop -t {videoLength} " +
                $"-vf \"scale=in_range=pc:out_range=tv\" " +
                $"-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p " +
                $"-colorspace bt709 -color_primaries bt709 -color_trc bt709 " +
                $"\"{backglassFile}\"";

            ffmpegBackglass = Process.Start(new ProcessStartInfo
            {
                FileName = config.FfmpegPath,
                Arguments = backglassArgs,
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new Exception("Failed to start ffmpeg process for backglass. Check FfmpegPath in config.json.");
        }

        // 3) Wait for both captures to finish in parallel.
        var tasks = new System.Collections.Generic.List<Task> { ffmpegPlayfield.WaitForExitAsync() };
        if (ffmpegBackglass != null)
            tasks.Add(ffmpegBackglass.WaitForExitAsync());

        await Task.WhenAll(tasks);

        await StopVpxAndRelatedAsync(vpx);
    }

    private static async Task StopVpxAndRelatedAsync(Process vpx)
    {
        // VPX itself
        TryCloseThenKill(vpx, timeoutMs: 1500);

        // PinUp processes that commonly linger after VPX is killed
        // (Process names: no ".exe")
        var pupProcessNames = new[]
        {
            "PinUpPlayer",
            "PinUpDisplay",
        };

        // Give VPX a moment to cascade-close children before we sweep.
        await Task.Delay(750);

        foreach (var name in pupProcessNames)
            KillAllByName(name, timeoutMs: 1500);
    }

    private static void KillAllByName(string processName, int timeoutMs)
    {
        Process[] procs;
        try
        {
            procs = Process.GetProcessesByName(processName);
        }
        catch
        {
            return;
        }

        foreach (var p in procs)
            TryCloseThenKill(p, timeoutMs);
    }

    private static void TryCloseThenKill(Process p, int timeoutMs)
    {
        try
        {
            if (p.HasExited) return;

            // Attempt graceful shutdown if it has a main window
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                    p.CloseMainWindow();
            }
            catch
            {
                // ignore
            }

            if (!p.WaitForExit(timeoutMs))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore any race conditions/access issues
        }
        finally
        {
            try { p.Dispose(); } catch { /* ignore */ }
        }
    }
}