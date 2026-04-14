using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace VirtualPinballLauncher;

public partial class DmdWindow : Window
{
    public DmdWindow()
    {
        InitializeComponent();
    }

    private static readonly SolidColorBrush LabelBrush = new(Colors.DimGray);
    private static readonly SolidColorBrush ValueBrush = new(Colors.White);

    public void ShowTableProperties(string tableDisplayName, string propertiesDisplayText)
    {
        TableTitleText.Text = tableDisplayName;
        PropertiesText.Inlines.Clear();

        if (string.IsNullOrEmpty(propertiesDisplayText))
            return;

        var lines = propertiesDisplayText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var colonIndex = lines[i].IndexOf(": ");
            if (colonIndex >= 0)
            {
                PropertiesText.Inlines.Add(new Run(lines[i][..(colonIndex + 2)]) { Foreground = LabelBrush });
                PropertiesText.Inlines.Add(new Run(lines[i][(colonIndex + 2)..]) { Foreground = ValueBrush });
            }
            else
            {
                PropertiesText.Inlines.Add(new Run(lines[i]) { Foreground = ValueBrush });
            }

            if (i < lines.Length - 1)
                PropertiesText.Inlines.Add(new LineBreak());
        }
    }
}
