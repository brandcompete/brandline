using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Brandline.Models;
using Brandline.Services;
using Brandline.Services.Audio;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Brandline.Views;

public partial class SettingsWindow : Window
{
    private static readonly AudioDevice DefaultDevice = new("", AudioDevices.DefaultLabel);

    private readonly AppSettings _original;
    private readonly TonePlayer _testTone = new();
    private CancellationTokenSource? _testCts;
    private List<RingtoneItem> _ringtones = [];
    private bool _loading = true;

    /// <summary>Eintrag in der Klingelton-Liste; InPool = Teil der Auswahl für „Zufällig“.</summary>
    public sealed class RingtoneItem(RingtoneInfo info, bool inPool)
    {
        public string Id { get; } = info.Id;
        public string Name { get; } = info.Name;
        public string Kind { get; } = info.IsFile ? "eigene Datei" : "";
        public Visibility DeleteVisibility { get; } = info.IsFile ? Visibility.Visible : Visibility.Collapsed;
        public bool InPool { get; set; } = inPool;
    }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Icon = TrayIcon.CreateWindowIcon();
        _original = settings;

        UserBox.Text = settings.SipUser;
        ServerBox.Text = settings.Server;
        PortBox.Text = settings.Port.ToString();
        DisplayNameBox.Text = settings.DisplayName;
        TransportBox.ItemsSource = TransportChoices;
        TransportBox.SelectedItem = TransportChoices.First(t => t.Kind == settings.Transport);
        PasswordHint.Visibility = string.IsNullOrEmpty(settings.EncryptedPassword) ? Visibility.Collapsed : Visibility.Visible;

        Fill(RingBox, AudioDevices.Outputs(), DataFlow.Render, settings.RingDevice);
        Fill(SpeakerBox, AudioDevices.Outputs(), DataFlow.Render, settings.SpeakerDevice);
        Fill(MicBox, AudioDevices.Inputs(), DataFlow.Capture, settings.MicrophoneDevice);
        AecBox.IsChecked = settings.EchoCancellation;
        FillRingtones(settings.RingTone, settings.RandomRingTones);
        ThemeSystem.IsChecked = settings.Theme == AppTheme.System;
        ThemeLight.IsChecked = settings.Theme == AppTheme.Light;
        ThemeDark.IsChecked = settings.Theme == AppTheme.Dark;

        AutostartBox.IsChecked = settings.StartWithWindows;
        TelLinksBox.IsChecked = settings.RegisterTelLinks;
        HotkeyBox.Text = settings.DialHotkey;
        StunBox.IsChecked = settings.UseStun;
        StunServerBox.Text = settings.StunServer;
        SipLogBox.IsChecked = settings.EnableSipLog;

        Loaded += (_, _) => { UserBox.Focus(); _loading = false; };
        Closed += (_, _) =>
        {
            _testCts?.Cancel();
            _testTone.Dispose();
            // Abgebrochen: Vorschau des Designs zurücknehmen.
            if (DialogResult != true && SelectedTheme != _original.Theme) Themes.ThemeManager.Apply(_original.Theme);
        };
    }

    public AppSettings Settings { get; private set; } = new();

    public sealed record TransportChoice(SipTransportKind Kind, string Label, int DefaultPort);

    private static readonly TransportChoice[] TransportChoices =
    [
        new(SipTransportKind.Tls, "TLS – verschlüsselt (empfohlen)", 5061),
        new(SipTransportKind.Udp, "UDP", 5060),
        new(SipTransportKind.Tcp, "TCP", 5060),
    ];

    /// <summary>Port mitziehen, solange er noch auf dem Standardwert der vorherigen Auswahl steht.</summary>
    private void OnTransportChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TransportBox.SelectedItem is not TransportChoice now) return;
        var before = e.RemovedItems.OfType<TransportChoice>().FirstOrDefault();
        if (before != null && PortBox.Text.Trim() == before.DefaultPort.ToString())
            PortBox.Text = now.DefaultPort.ToString();
    }

    private static void Fill(ComboBox box, List<AudioDevice> devices, DataFlow flow, string stored)
    {
        var items = new List<AudioDevice> { DefaultDevice };
        items.AddRange(devices);
        box.ItemsSource = items;
        var id = AudioDevices.Resolve(flow, stored);
        box.SelectedItem = items.FirstOrDefault(d => d.Id == id) ?? DefaultDevice;
    }

    private static string Selected(ComboBox box) => (box.SelectedItem as AudioDevice)?.Id ?? "";

    // ---------------- Klingeltöne ----------------

    private void FillRingtones(string selectedId, IReadOnlyCollection<string> pool)
    {
        var all = Ringtones.All();
        _ringtones = all.Select(r => new RingtoneItem(r, pool.Contains(r.Id))).ToList();
        RingtoneList.ItemsSource = _ringtones;

        var choices = new List<RingtoneInfo> { new(Ringtones.RandomId, "Zufällig (aus der Auswahl unten)") };
        choices.AddRange(all);
        RingtoneBox.ItemsSource = choices;
        RingtoneBox.SelectedItem = choices.FirstOrDefault(r => r.Id == selectedId) ?? choices[1];
        UpdateRandomHint();
    }

    private string SelectedRingtone => (RingtoneBox.SelectedItem as RingtoneInfo)?.Id ?? Ringtones.DefaultId;
    private List<string> RandomPool => _ringtones.Where(r => r.InPool).Select(r => r.Id).ToList();

    private void OnRingtoneModeChanged(object sender, SelectionChangedEventArgs e) => UpdateRandomHint();

    private void UpdateRandomHint() =>
        RandomHint.Visibility = SelectedRingtone == Ringtones.RandomId ? Visibility.Visible : Visibility.Collapsed;

    private void OnAddRingtones(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Eigene Klingeltöne hinzufügen",
            Filter = "Audiodateien (*.mp3;*.wav;*.wma;*.m4a;*.aac)|*.mp3;*.wav;*.wma;*.m4a;*.aac",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var pool = RandomPool;
        var failed = new List<string>();
        foreach (var file in dialog.FileNames)
        {
            try { pool.Add(Ringtones.Import(file)); } // neue Dateien gleich in die Zufallsauswahl
            catch { failed.Add(System.IO.Path.GetFileName(file)); }
        }
        FillRingtones(SelectedRingtone, pool);
        if (failed.Count > 0)
            MessageBox.Show(this, "Diese Dateien können nicht abgespielt werden:\n" + string.Join("\n", failed), "Klingeltöne",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnDeleteRingtone(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RingtoneItem item) return;
        if (MessageBox.Show(this, $"Klingelton „{item.Name}“ entfernen?", "Klingeltöne",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _testTone.Stop();
        Ringtones.Delete(item.Id);
        var selected = SelectedRingtone == item.Id ? Ringtones.DefaultId : SelectedRingtone;
        FillRingtones(selected, RandomPool.Where(id => id != item.Id).ToList());
    }

    private void OnPreviewRingtone(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RingtoneItem item)
            PlayTest(() => _testTone.PlayRing(Selected(RingBox), item.Id), seconds: 5);
    }

    // ---------------- Design ----------------

    private AppTheme SelectedTheme =>
        ThemeDark.IsChecked == true ? AppTheme.Dark : ThemeLight.IsChecked == true ? AppTheme.Light : AppTheme.System;

    /// <summary>Design sofort anzeigen; bei „Abbrechen“ wird es zurückgesetzt.</summary>
    private void OnThemePreview(object sender, RoutedEventArgs e)
    {
        if (!_loading) Themes.ThemeManager.Apply(SelectedTheme);
    }
    private void OnTestSpeaker(object sender, RoutedEventArgs e) => PlayTest(() => _testTone.PlayRingback(Selected(SpeakerBox)));

    private async void PlayTest(Action start, int seconds = 3)
    {
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource();
        start();
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token); }
        catch (TaskCanceledException) { return; }
        _testTone.Stop();
    }

    private void OnOpenDefaultApps(object sender, RoutedEventArgs e)
    {
        // Registrierung vorziehen, damit die App in der Liste schon auftaucht.
        if (TelLinksBox.IsChecked == true) SystemIntegration.SetTelLinkRegistration(true);
        SystemIntegration.OpenDefaultAppsSettings();
    }

    /// <summary>Nimmt die gedrückte Tastenkombination auf, statt Text einzutippen.</summary>
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Delete or Key.Back)
        {
            HotkeyBox.Text = "";
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.Tab) return;
        if (Keyboard.Modifiers == ModifierKeys.None) return;

        try
        {
            HotkeyBox.Text = new KeyGesture(key, Keyboard.Modifiers).GetDisplayStringForCulture(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (NotSupportedException)
        {
            // Kombination von WPF nicht unterstützt – ignorieren.
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var user = UserBox.Text.Trim();
        var server = ServerBox.Text.Trim();

        // "777123456789@fpbx.de" direkt aus dem Placetel-Portal einfügen erlauben.
        if (user.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)) user = user[4..];
        var at = user.IndexOf('@');
        if (at > 0)
        {
            if (server.Length == 0 || server == "fpbx.de") server = user[(at + 1)..];
            user = user[..at];
        }

        string? problem = null;
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
            problem = "Bitte einen gültigen Port angeben (Standard: 5060).";
        else if (user.Length == 0 || server.Length == 0)
            problem = "Bitte SIP-Benutzername und Server angeben.";
        else if (PasswordBox.Password.Length == 0 && string.IsNullOrEmpty(_original.EncryptedPassword))
            problem = "Bitte das SIP-Passwort angeben.";
        else if (HotkeyBox.Text.Length > 0 && !GlobalHotkey.TryParse(HotkeyBox.Text, out _, out _))
            problem = "Das Tastenkürzel braucht mindestens eine Zusatztaste (Strg, Alt, Umschalt).";
        else if (SelectedRingtone == Ringtones.RandomId && RandomPool.Count == 0)
            problem = "Für „Zufällig“ bitte mindestens einen Klingelton in der Auswahl ankreuzen.";

        if (problem != null)
        {
            MessageBox.Show(this, problem, "Einstellungen", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var s = _original.Clone();
        s.SipUser = user;
        s.Server = server;
        s.Port = port;
        s.Transport = (TransportBox.SelectedItem as TransportChoice)?.Kind ?? SipTransportKind.Udp;
        s.DisplayName = DisplayNameBox.Text.Trim();
        s.RingDevice = Selected(RingBox);
        s.RingTone = SelectedRingtone;
        s.RandomRingTones = RandomPool;
        s.Theme = SelectedTheme;
        s.SpeakerDevice = Selected(SpeakerBox);
        s.MicrophoneDevice = Selected(MicBox);
        s.EchoCancellation = AecBox.IsChecked == true;
        s.StartWithWindows = AutostartBox.IsChecked == true;
        s.RegisterTelLinks = TelLinksBox.IsChecked == true;
        s.DialHotkey = HotkeyBox.Text.Trim();
        s.UseStun = StunBox.IsChecked == true;
        s.StunServer = StunServerBox.Text.Trim();
        s.EnableSipLog = SipLogBox.IsChecked == true;
        if (PasswordBox.Password.Length > 0) s.Password = PasswordBox.Password;

        Settings = s;
        DialogResult = true;
    }
}
