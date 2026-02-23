using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace VirtualPinballLauncher;

public static class Recorder
{
    public static async Task RecordPreviewAsync(Config config, string vpxPath)
    {
        if (!File.Exists(config.VpxExePath))
            throw new FileNotFoundException("VPX executable not found.", config.VpxExePath);

        if (!File.Exists(vpxPath))
            throw new FileNotFoundException("VPX table not found.", vpxPath);

        if (string.IsNullOrWhiteSpace(config.VideosDirectory))
            throw new ArgumentException("VideosDirectory is not set in config.json.", nameof(config.VideosDirectory));

        Directory.CreateDirectory(config.VideosDirectory);

        // 1) Launch the VPX table.
        var vpx = Process.Start(new ProcessStartInfo
        {
            FileName = config.VpxExePath,
            Arguments = $"/play -\"{vpxPath}\" /minimized",
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(config.VpxExePath) ?? Environment.CurrentDirectory
        });

        // Give the table playfield time to come to the foreground.
        await Task.Delay(TimeSpan.FromSeconds(10));

        if (vpx == null)
            throw new Exception("Failed to start VPX process.");

        // 2) Record the playfield.
        int video_width = ScreenSize.PlayfieldWidth;
        int video_height = ScreenSize.PlayfieldHeight;
        int video_length = config.RecordSeconds;

        var tableName = Path.GetFileNameWithoutExtension(vpxPath);
        var video_file = Path.Combine(
            config.VideosDirectory,
            tableName + (string.IsNullOrWhiteSpace(config.VideoExtension) ? ".mp4" : config.VideoExtension)
        );

        if (File.Exists(video_file))
            File.Delete(video_file);

        var ffmpeg_arguments =
            $"-y -f gdigrab -framerate 30 " +
            $"-video_size {video_width}x{video_height} " +
            $"-i desktop -t {video_length} " +
            $"-vf \"scale=in_range=pc:out_range=tv\" " +
            $"-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p " +
            $"-colorspace bt709 -color_primaries bt709 -color_trc bt709 " +
            $"\"{video_file}\"";

        var ffmpeg = Process.Start(new ProcessStartInfo
        {
            FileName = config.FfmpegPath,
            Arguments = ffmpeg_arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (ffmpeg == null)
            throw new Exception("Failed to start ffmpeg process. Check FfmpegPath in config.json.");

        await ffmpeg.WaitForExitAsync();

        await StopVpxAndRelatedAsync(vpx);
    }

    public static async Task RecordBackglassPreviewAsync(Config config, string vpxPath)
    {
        if (!File.Exists(config.VpxExePath))
            throw new FileNotFoundException("VPX executable not found.", config.VpxExePath);

        if (!File.Exists(vpxPath))
            throw new FileNotFoundException("VPX table not found.", vpxPath);

        if (string.IsNullOrWhiteSpace(config.BackglassVideosDirectory))
            throw new ArgumentException("BackglassVideosDirectory is not set in config.json.", nameof(config.BackglassVideosDirectory));

        var bounds = ScreenSize.GetBackglassMonitorBounds();
        if (bounds is null)
            throw new InvalidOperationException("No backglass monitor could be detected.");

        Directory.CreateDirectory(config.BackglassVideosDirectory);

        // 1) Launch the VPX table.
        var vpx = Process.Start(new ProcessStartInfo
        {
            FileName = config.VpxExePath,
            Arguments = $"/play -\"{vpxPath}\" /minimized",
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(config.VpxExePath) ?? Environment.CurrentDirectory
        });

        // Give the table time to come to the foreground.
        await Task.Delay(TimeSpan.FromSeconds(10));

        if (vpx == null)
            throw new Exception("Failed to start VPX process.");

        // 2) Record the backglass monitor.
        var rect = bounds.Value;
        int video_width = rect.Width;
        int video_height = rect.Height;
        int video_length = config.RecordSeconds;

        var tableName = Path.GetFileNameWithoutExtension(vpxPath);
        var video_file = Path.Combine(
            config.BackglassVideosDirectory,
            tableName + (string.IsNullOrWhiteSpace(config.VideoExtension) ? ".mp4" : config.VideoExtension)
        );

        if (File.Exists(video_file))
            File.Delete(video_file);

        var ffmpeg_arguments =
            $"-y -f gdigrab -framerate 30 " +
            $"-offset_x {rect.Left} -offset_y {rect.Top} " +
            $"-video_size {video_width}x{video_height} " +
            $"-i desktop -t {video_length} " +
            $"-vf \"scale=in_range=pc:out_range=tv\" " +
            $"-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p " +
            $"-colorspace bt709 -color_primaries bt709 -color_trc bt709 " +
            $"\"{video_file}\"";

        var ffmpeg = Process.Start(new ProcessStartInfo
        {
            FileName = config.FfmpegPath,
            Arguments = ffmpeg_arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (ffmpeg == null)
            throw new Exception("Failed to start ffmpeg process. Check FfmpegPath in config.json.");

        await ffmpeg.WaitForExitAsync();

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

            // Optional/common helpers (uncomment if you see these linger in your setup):
            // "PinUpMenu",
            // "PinUpPopper",
            // "PuPServer",   // some installs use this name
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