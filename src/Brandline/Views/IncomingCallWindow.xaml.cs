using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Brandline.Views;

/// <summary>Hinweisfenster unten rechts über der Taskleiste; stiehlt nicht den Tastaturfokus.</summary>
public partial class IncomingCallWindow : Window
{
    private readonly Action _answer;
    private readonly Action _reject;
    private readonly Action _silence;

    public IncomingCallWindow(string name, string detail, ImageSource? avatar, Action answer, Action reject, Action silence)
    {
        InitializeComponent();
        Avatar.DisplayName = name;
        Avatar.Image = avatar;
        NameText.Text = name;
        DetailText.Text = detail;
        DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        _answer = answer;
        _reject = reject;
        _silence = silence;
        // Außerhalb des Bildschirms beginnen, dann hereingleiten.
        Left = SystemParameters.WorkArea.Right - Width;
        Top = SystemParameters.WorkArea.Bottom;
        Loaded += (_, _) => SlideIn();
    }

    private void SlideIn()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth;
        var targetTop = area.Bottom - ActualHeight;
        BeginAnimation(TopProperty, new DoubleAnimation(area.Bottom, targetTop, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void OnAnswer(object sender, RoutedEventArgs e)
    {
        Close();
        _answer();
    }

    private void OnReject(object sender, RoutedEventArgs e)
    {
        Close();
        _reject();
    }

    private void OnSilence(object sender, RoutedEventArgs e)
    {
        _silence();
        SilenceButton.IsEnabled = false;
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => Close();
}
