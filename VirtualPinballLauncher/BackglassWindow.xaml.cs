using System.Windows;

namespace VirtualPinballLauncher;

public partial class BackglassWindow : Window
{
    public BackglassWindow(LoopingVideoPlayer videoPlayer)
    {
        InitializeComponent();
        BackglassPlayer.MediaPlayer = videoPlayer.MediaPlayer;
        BackglassPlayer.Loaded += (_, _) => this.PreventOverlayActivation(LoadingOverlay);
    }

    public void DetachPlayer() => BackglassPlayer.MediaPlayer = null;
}
