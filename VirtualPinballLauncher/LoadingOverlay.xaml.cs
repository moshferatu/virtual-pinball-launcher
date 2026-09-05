using System.Windows;

namespace VirtualPinballLauncher;

public partial class LoadingOverlay : System.Windows.Controls.UserControl
{
    public LoadingOverlay() => InitializeComponent();

    public void SetLoading(bool isLoading)
        => Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
}
