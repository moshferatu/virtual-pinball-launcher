using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace VirtualPinballLauncher;

public partial class BackglassWindow : Window
{
    private LoopingVideoPlayer videoPlayer = null!;

    public BackglassWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            videoPlayer = new LoopingVideoPlayer(Dispatcher);
            BackglassPlayer.MediaPlayer = videoPlayer.MediaPlayer;
        };

        Closed += (_, _) => videoPlayer?.Dispose();
    }

    public Task PlayBackglassAsync(string? path, CancellationToken token)
        => videoPlayer.PlayAsync(path, token);

    public void StopVideo() => videoPlayer?.Stop();
}
