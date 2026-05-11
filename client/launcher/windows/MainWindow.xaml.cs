using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace OneAirLauncher;

public partial class MainWindow : Window
{
    /// <summary>null = mode "+ Nouveau compte" (inputs visibles), sinon login sélectionné.</summary>
    private string? _selectedAccountLogin;
    private bool _passwordVisible;

    /// <summary>Clé d'index dans `<connection.host>` (`AuthentificationFrame._allHostsInfos`).</summary>
    private const string ServerHostKey = "OneAir";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;

        UserField.TextChanged += (_, _) => UserPlaceholder.Visibility =
            string.IsNullOrEmpty(UserField.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var data = Settings.Data;
        RememberCheck.IsChecked = data.SaveLogin;

        var last = data.LastAccount;
        var acc = data.Accounts.FirstOrDefault(a => a.Login == last)
               ?? data.Accounts.FirstOrDefault();
        if (acc != null) SelectAccount(acc.Login);
        else SelectNewAccountMode();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void AccountSelector_MouseDown(object sender, MouseButtonEventArgs e)
    {
        AccountPopup.IsOpen = !AccountPopup.IsOpen;
        UpdateAccountSelectorVisuals();
        if (AccountPopup.IsOpen) RebuildAccountList();
        e.Handled = true;
    }

    private void AccountSelector_MouseEnter(object sender, MouseEventArgs e) =>
        UpdateAccountSelectorVisuals();

    private void AccountSelector_MouseLeave(object sender, MouseEventArgs e) =>
        UpdateAccountSelectorVisuals();

    private void UpdateAccountSelectorVisuals()
    {
        AccountSelector.BorderBrush = (AccountPopup.IsOpen || AccountSelector.IsMouseOver)
            ? (Brush)Application.Current.Resources["GoldBrush"]
            : (Brush)Application.Current.Resources["InputBorderBrush"];

        var anim = new DoubleAnimation
        {
            To = AccountPopup.IsOpen ? 180 : 0,
            Duration = TimeSpan.FromMilliseconds(120),
        };
        AccountChevronRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
    }

    private void RebuildAccountList()
    {
        AccountList.Children.Clear();
        var accounts = Settings.Data.Accounts;

        foreach (var acc in accounts)
        {
            var row = BuildAccountRow(acc.Login, isAddNew: false);
            AccountList.Children.Add(row);
        }

        if (accounts.Count > 0)
        {
            var sep = new Border
            {
                Height = 1,
                Background = (Brush)Application.Current.Resources["BorderBrush"],
                Margin = new Thickness(8, 4, 8, 4),
            };
            AccountList.Children.Add(sep);
        }

        AccountList.Children.Add(BuildAccountRow("", isAddNew: true));
    }

    private Border BuildAccountRow(string login, bool isAddNew)
    {
        var row = new Border
        {
            Height = 38,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Padding = new Thickness(8, 0, 8, 0),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });

        var avatar = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(14),
            Background = isAddNew
                ? new SolidColorBrush(Color.FromArgb(0x2e, 0xe5, 0xb4, 0x55))
                : new SolidColorBrush(Color.FromRgb(0x33, 0x45, 0x5c)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = isAddNew ? "+" : login.Substring(0, 1).ToUpperInvariant(),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = isAddNew
                    ? (Brush)Application.Current.Resources["GoldBrush"]
                    : (Brush)Application.Current.Resources["TextBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(avatar, 0);
        grid.Children.Add(avatar);

        var label = new TextBlock
        {
            Text = isAddNew ? "Nouveau compte" : login,
            FontSize = 13,
            FontWeight = isAddNew ? FontWeights.Medium : FontWeights.Normal,
            Foreground = isAddNew
                ? (Brush)Application.Current.Resources["GoldBrush"]
                : (Brush)Application.Current.Resources["TextBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        Button? trash = null;
        if (!isAddNew)
        {
            trash = new Button
            {
                Content = "🗑",
                Width = 24,
                Height = 24,
                FontSize = 13,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = (Brush)Application.Current.Resources["TextSoftBrush"],
                Visibility = Visibility.Hidden,
                Cursor = Cursors.Hand,
            };
            trash.Click += (_, e) =>
            {
                e.Handled = true;
                AccountPopup.IsOpen = false;
                ConfirmDelete(login);
            };
            Grid.SetColumn(trash, 2);
            grid.Children.Add(trash);
        }

        row.Child = grid;

        row.MouseEnter += (_, _) =>
        {
            row.Background = new SolidColorBrush(Color.FromArgb(0x1f, 0xe5, 0xb4, 0x55));
            if (trash != null) trash.Visibility = Visibility.Visible;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = Brushes.Transparent;
            if (trash != null) trash.Visibility = Visibility.Hidden;
        };
        row.MouseLeftButtonDown += (_, _) =>
        {
            AccountPopup.IsOpen = false;
            if (isAddNew) SelectNewAccountMode();
            else SelectAccount(login);
        };

        return row;
    }

    private void ConfirmDelete(string login)
    {
        var res = MessageBox.Show(this,
            $"Le mot de passe stocké sera effacé. Cette action est irréversible.",
            $"Supprimer le compte « {login} » ?",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (res != MessageBoxResult.OK) return;

        Settings.RemoveAccount(login);
        var first = Settings.Data.Accounts.FirstOrDefault();
        if (first != null) SelectAccount(first.Login);
        else SelectNewAccountMode();
    }

    private void SelectAccount(string login)
    {
        var acc = Settings.Data.Accounts.FirstOrDefault(a => a.Login == login);
        if (acc == null) { SelectNewAccountMode(); return; }

        _selectedAccountLogin = acc.Login;
        AccountText.Text = acc.Login;
        AccountText.Foreground = (Brush)Application.Current.Resources["TextBrush"];
        AccountIcon.Text = ""; // Contact
        UserField.Text = acc.Login;
        SetPasswordValue(acc.Password);
        Settings.Data.LastAccount = acc.Login;
        Settings.Save();
        InputsContainer.Visibility = Visibility.Collapsed;
    }

    private void SelectNewAccountMode()
    {
        _selectedAccountLogin = null;
        AccountText.Text = "+ Nouveau compte";
        AccountText.Foreground = (Brush)Application.Current.Resources["TextSoftBrush"];
        AccountIcon.Text = ""; // AddCircle
        UserField.Text = "";
        SetPasswordValue("");
        InputsContainer.Visibility = Visibility.Visible;
        UserField.Focus();
    }

    private string GetPasswordValue() =>
        _passwordVisible ? PassFieldVisible.Text : PassField.Password;

    private void SetPasswordValue(string v)
    {
        PassField.Password = v;
        PassFieldVisible.Text = v;
        UpdatePassPlaceholder();
    }

    private void PassField_Changed(object sender, RoutedEventArgs e)
    {
        if (!_passwordVisible) PassFieldVisible.Text = PassField.Password;
        UpdatePassPlaceholder();
    }

    private void UpdatePassPlaceholder() =>
        PassPlaceholder.Visibility =
            string.IsNullOrEmpty(GetPasswordValue()) ? Visibility.Visible : Visibility.Collapsed;

    private void PassToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_passwordVisible)
        {
            PassField.Password = PassFieldVisible.Text;
            PassField.Visibility = Visibility.Visible;
            PassFieldVisible.Visibility = Visibility.Collapsed;
            PassToggle.Content = "VOIR";
            PassField.Focus();
        }
        else
        {
            PassFieldVisible.Text = PassField.Password;
            PassField.Visibility = Visibility.Collapsed;
            PassFieldVisible.Visibility = Visibility.Visible;
            PassToggle.Content = "CACHER";
            PassFieldVisible.Focus();
            PassFieldVisible.CaretIndex = PassFieldVisible.Text.Length;
        }
        _passwordVisible = !_passwordVisible;
    }

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) PlayButton_Click(this, new RoutedEventArgs());
    }

    private enum StatusKind { Ok, Warn, Err }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusLabel.Text = text;
        StatusDot.Fill = kind switch
        {
            StatusKind.Ok => (Brush)Application.Current.Resources["GreenBrush"],
            StatusKind.Warn => (Brush)Application.Current.Resources["AmberBrush"],
            _ => (Brush)Application.Current.Resources["RedBrush"],
        };

        if (kind != StatusKind.Ok)
        {
            var sw = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3.5),
            };
            sw.Tick += (_, _) =>
            {
                sw.Stop();
                StatusLabel.Text = "En ligne";
                StatusDot.Fill = (Brush)Application.Current.Resources["GreenBrush"];
            };
            sw.Start();
        }
    }

    private void SavePrefs()
    {
        Settings.Data.SaveLogin = RememberCheck.IsChecked == true;

        if (_selectedAccountLogin == null && RememberCheck.IsChecked == true)
        {
            var login = UserField.Text.Trim();
            var pass = GetPasswordValue();
            if (!string.IsNullOrEmpty(login))
            {
                Settings.UpsertAccount(login, pass);
                _selectedAccountLogin = login;
            }
        }
        Settings.Save();
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        var user = UserField.Text.Trim();
        var pass = GetPasswordValue();
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            SetStatus("Identifiants requis", StatusKind.Err);
            return;
        }

        SavePrefs();
        var host = Settings.Data.Host;
        var port = int.TryParse(Settings.Data.Port, out var pp) ? pp : 5555;

        PlayLabel.Text = "CONNEXION…";
        PlayArrow.Visibility = Visibility.Collapsed;
        PlayButton.IsEnabled = false;
        SetStatus("Authentification…", StatusKind.Warn);

        var (ok, ms, err) = await TcpProbe.TestAsync(host, port);
        if (!ok)
        {
            PlayLabel.Text = "JOUER";
            PlayArrow.Visibility = Visibility.Visible;
            PlayButton.IsEnabled = true;
            SetStatus($"Injoignable : {err}", StatusKind.Err);
            return;
        }

        SetStatus($"OK · {ms}ms", StatusKind.Ok);
        await Task.Delay(400);

        try
        {
            // DofusLaunch.Launch quitte le process via Environment.Exit.
            DofusLaunch.Launch(host, port, ServerHostKey, user, pass);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, StatusKind.Err);
            PlayLabel.Text = "JOUER";
            PlayArrow.Visibility = Visibility.Visible;
            PlayButton.IsEnabled = true;
        }
    }

    private void Advanced_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AdvancedWindow { Owner = this };
        if (dlg.ShowDialog() == true)
            SetStatus("Paramètres enregistrés", StatusKind.Ok);
    }
}
