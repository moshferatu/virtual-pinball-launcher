using System;
using System.Threading.Tasks;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace VirtualPinballLauncher;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        // Launcher Mode: Show the main window.
        new MainWindow().Show();
    }
}
