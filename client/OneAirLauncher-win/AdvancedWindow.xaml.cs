using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace OneAirLauncher;

public partial class AdvancedWindow : Window
{
    public AdvancedWindow()
    {
        InitializeComponent();
        IpField.Text = Settings.Data.Host;
        PortField.Text = Settings.Data.Port;
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var host = IpField.Text.Trim();
        if (!int.TryParse(PortField.Text.Trim(), out var port) || port <= 0 || string.IsNullOrEmpty(host))
        {
            ResultLabel.Text = "✗ IP/port invalide";
            ResultLabel.Foreground = (Brush)Application.Current.Resources["RedBrush"];
            return;
        }
        ResultLabel.Text = $"Test en cours sur {host}:{port}…";
        ResultLabel.Foreground = (Brush)Application.Current.Resources["TextSoftBrush"];
        TestButton.IsEnabled = false;
        var (ok, ms, err) = await TcpProbe.TestAsync(host, port);
        TestButton.IsEnabled = true;
        if (ok)
        {
            ResultLabel.Text = $"✓ Connexion OK · {ms} ms";
            ResultLabel.Foreground = (Brush)Application.Current.Resources["GreenBrush"];
        }
        else
        {
            ResultLabel.Text = $"✗ {err}";
            ResultLabel.Foreground = (Brush)Application.Current.Resources["RedBrush"];
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Settings.Data.Host = IpField.Text.Trim();
        if (int.TryParse(PortField.Text.Trim(), out var p) && p > 0)
            Settings.Data.Port = p.ToString();
        Settings.Save();
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
