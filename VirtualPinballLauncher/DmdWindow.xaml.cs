using System.Linq;
using System.Windows;
using Forms = System.Windows.Forms;

namespace VirtualPinballLauncher;

public partial class DmdWindow : Window
{
    public DmdWindow()
    {
        InitializeComponent();
    }

    public void MoveToPreferredMonitor(int preferredZeroBasedIndex)
    {
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0)
            return;

        var index = preferredZeroBasedIndex;
        if (index < 0) index = 0;
        if (index >= screens.Length) index = screens.Length - 1;

        var screen = screens[index];
        WindowState = WindowState.Normal;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = screen.Bounds.Left;
        Top = screen.Bounds.Top;
        Width = screen.Bounds.Width;
        Height = screen.Bounds.Height;
    }

    public void ShowTableProperties(string tableDisplayName, string propertiesDisplayText)
    {
        TableTitleText.Text = tableDisplayName;
        PropertiesText.Text = propertiesDisplayText;
    }

    public void SetLoading(bool isLoading)
    {
        LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;

        if (isLoading)
            Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { }));
    }

    public void MoveToRightmostMonitor()
    {
        var screen = Forms.Screen.AllScreens
            .OrderByDescending(s => s.Bounds.Left)   // rightmost first
            .ThenBy(s => s.Bounds.Width * s.Bounds.Height) // tie-break: smallest area
            .FirstOrDefault();

        if (screen is null) return;

        MoveToScreen(screen);
    }

    private void MoveToScreen(Forms.Screen screen)
    {
        WindowState = WindowState.Normal;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Left = screen.Bounds.Left;
        Top = screen.Bounds.Top;
        Width = screen.Bounds.Width;
        Height = screen.Bounds.Height;
    }
}
