using System.Diagnostics;
using System.IO;

using ScreenRecorderLib;

namespace VirtualPinballLauncher;

public static class Recorder
{
    public static async Task RecordAsync(Config config, string vpxPath)
    {
        if (!File.Exists(config.VpxExePath))
            throw new FileNotFoundException("VPX executable not found.", config.VpxExePath);

        if (!File.Exists(vpxPath))
            throw new FileNotFoundException("VPX table not found.", vpxPath);

        if (string.IsNullOrWhiteSpace(config.PreviewsDirectory))
            throw new ArgumentException("PreviewsDirectory is not set in config.json.", nameof(config.PreviewsDirectory));

        string backglassDeviceName = WindowUtils.ScreenAt(WindowUtils.BackglassMonitor)?.DeviceName
            ?? throw new InvalidOperationException("No backglass monitor could be detected.");

        Directory.CreateDirectory(config.PlayfieldPreviewsDirectory);
        Directory.CreateDirectory(config.BackglassPreviewsDirectory);

        var vpx = Process.Start(new ProcessStartInfo
        {
            FileName = config.VpxExePath,
            Arguments = $"/play -\"{vpxPath}\" /minimized",
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(config.VpxExePath) ?? Environment.CurrentDirectory
        });

        if (vpx == null)
            throw new InvalidOperationException("Failed to start VPX process.");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(config.RecordDelaySeconds));

            var (playfieldFile, backglassFile) = config.PreviewVideoPaths(Path.GetFileNameWithoutExtension(vpxPath));

            await RecordedPreview.RecordPairAsync(playfieldFile, backglassFile,
                (playfieldTemp, backglassTemp) => RecordScreensAsync(config, backglassDeviceName, playfieldTemp, backglassTemp));
        }
        finally
        {
            await StopVpxAndRelatedAsync(vpx);
        }
    }

    private static async Task RecordScreensAsync(Config config, string backglassDeviceName,
        string playfieldFile, string backglassFile)
    {
        using var playfieldRecorder = CreateScreenRecorder(DisplayRecordingSource.MainMonitor);
        using var backglassRecorder = CreateScreenRecorder(new DisplayRecordingSource(backglassDeviceName));
        var playfieldTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backglassTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        playfieldRecorder.OnRecordingComplete += (s, e) => playfieldTcs.TrySetResult(true);
        playfieldRecorder.OnRecordingFailed += (s, e) =>
            playfieldTcs.TrySetException(new Exception($"Playfield recording failed: {e.Error}"));

        backglassRecorder.OnRecordingComplete += (s, e) => backglassTcs.TrySetResult(true);
        backglassRecorder.OnRecordingFailed += (s, e) =>
            backglassTcs.TrySetException(new Exception($"Backglass recording failed: {e.Error}"));

        playfieldRecorder.Record(playfieldFile);
        backglassRecorder.Record(backglassFile);

        await Task.Delay(TimeSpan.FromSeconds(config.RecordSeconds));
        playfieldRecorder.Stop();
        backglassRecorder.Stop();
        await Task.WhenAll(playfieldTcs.Task, backglassTcs.Task);
        // Dispose both recorders before returning to publish their output files.
    }

    private static ScreenRecorderLib.Recorder CreateScreenRecorder(DisplayRecordingSource displaySource)
    {
        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions
            {
                RecordingSources = new List<RecordingSourceBase> { displaySource }
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder
                {
                    BitrateMode = H264BitrateControlMode.Quality,
                    EncoderProfile = H264Profile.Main,
                },
                IsHardwareEncodingEnabled = true,
                Quality = 100,
                Bitrate = 8000 * 1000,
                Framerate = 60,
                IsFixedFramerate = false,
                IsMp4FastStartEnabled = true,
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = false
            },
            MouseOptions = new MouseOptions
            {
                IsMousePointerEnabled = false
            }
        };

        return ScreenRecorderLib.Recorder.CreateRecorder(options);
    }

    private static async Task StopVpxAndRelatedAsync(Process vpx)
    {
        TryCloseThenKill(vpx, timeoutMs: 1500);

        var pupProcesses = new[]
        {
            "PinUpPlayer",
            "PinUpDisplay",
        };

        await Task.Delay(750);

        foreach (var process in pupProcesses)
            KillAllByName(process, timeoutMs: 1500);
    }

    private static void KillAllByName(string processName, int timeoutMs)
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName(processName); } catch { return; }

        foreach (var process in processes)
            TryCloseThenKill(process, timeoutMs);
    }

    private static void TryCloseThenKill(Process process, int timeoutMs)
    {
        try
        {
            if (process.HasExited) return;

            if (process.MainWindowHandle != IntPtr.Zero)
                process.CloseMainWindow();

            if (!process.WaitForExit(timeoutMs))
                process.Kill(entireProcessTree: true);
        }
        catch { /* ignore */ }
        finally
        {
            try { process.Dispose(); } catch { /* ignore */ }
        }
    }
}
