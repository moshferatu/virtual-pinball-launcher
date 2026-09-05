using System.Windows;

namespace VirtualPinballLauncher;

public partial class BackglassWindow : Window
{
    public BackglassWindow(LoopingVideoPlayer videoPlayer)
    {
        InitializeComponent();
        BackglassPlayer.MediaPlayer = videoPlayer.MediaPlayer;
    }

    public void DetachPlayer() => BackglassPlayer.MediaPlayer = null;
}
