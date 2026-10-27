using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace VirtualPinballLauncher;

public partial class App : System.Windows.Application
{
    // Held for the life of the process so a second copy can recognise the first.
    private static Mutex? instanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // CLI Mode: Record playfield and backglass videos for a specific table.
        // VirtualPinballLauncher.exe --record "D:\Pinball\Tables\Table.vpx"
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--record", StringComparison.OrdinalIgnoreCase))
        {
            string vpxPath = e.Args[1];
            try
            {
                var config = await Config.LoadAsync(Config.DefaultPath);
                await Recorder.RecordAsync(config, vpxPath);
                Shutdown(0);
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Record Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
        }

        // One launcher per session: a second copy leaves quietly.
        instanceMutex = new Mutex(true, @"Local\VirtualPinballLauncher.Instance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown(0);
            return;
        }

        // Keep the dispatcher alive until the views close and native playback is disposed.
        var exitCode = 0;
        try
        {
            await using var previews = await PreviewPlayback.CreateAsync();
            var window = new MainWindow(previews);
            MainWindow = window;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Launcher Error", MessageBoxButton.OK, MessageBoxImage.Error);
            exitCode = 1;
        }

        Shutdown(exitCode);
    }
}
