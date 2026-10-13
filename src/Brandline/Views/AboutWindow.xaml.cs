using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace Brandline.Views;

public partial class AboutWindow : Window
{
    private readonly Func<Task<string>>? _checkForUpdates;

    public AboutWindow(Func<Task<string>>? checkForUpdates = null)
    {
        InitializeComponent();
        Icon = TrayIcon.CreateWindowIcon();
        _checkForUpdates = checkForUpdates;
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3)}";
        CopyrightText.Text = $"© {DateTime.Now.Year} brandcompete GmbH";
        CheckUpdatesButton.Visibility = checkForUpdates == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        if (_checkForUpdates == null) return;
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatus.Text = "Suche …";
        UpdateStatus.Text = await _checkForUpdates();
        CheckUpdatesButton.IsEnabled = true;
    }

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
