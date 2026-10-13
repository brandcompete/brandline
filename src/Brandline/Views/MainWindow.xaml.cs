using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Brandline.Models;
using Brandline.Services;

namespace Brandline.Views;

public partial class MainWindow : Window
{
    private sealed record Suggestion(string Name, string Number, ImageSource? Image);

    private readonly SipPhone _phone = new();
    private readonly ObservableCollection<Contact> _contacts;
    private readonly ICollectionView _contactView;
    private readonly ObservableCollection<CallRecord> _calls;
    private readonly DispatcherTimer _callTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TrayIcon _tray;
    private GlobalHotkey? _hotkey;
    private IncomingCallWindow? _incomingWindow;
    private AppSettings _settings;
    private RegistrationState _registration = RegistrationState.Offline;
    private string _registrationText = "Nicht angemeldet";
    private DateTime _connectedAt;
    private bool _exitRequested;
    private bool _hideHintShown;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        Icon = TrayIcon.CreateWindowIcon();
        _settings = settings;

        _contacts = new ObservableCollection<Contact>(Storage.LoadContacts());
        _contactView = CollectionViewSource.GetDefaultView(_contacts);
        _contactView.SortDescriptions.Add(new SortDescription(nameof(Contact.Name), ListSortDirection.Ascending));
        _contactView.Filter = FilterContact;
        ContactList.ItemsSource = _contactView;

        _calls = new ObservableCollection<CallRecord>(Storage.LoadCalls().OrderByDescending(c => c.Start));
        CallList.ItemsSource = _calls;
        UpdateMissedBadge();

        BuildDialPad();
        DndToggle.IsChecked = settings.DoNotDisturb;

        _tray = new TrayIcon(ShowFromTray, () => SetDoNotDisturb(!_phone.DoNotDisturb), () => OnSettings(this, new RoutedEventArgs()),
            () => OnAbout(this, new RoutedEventArgs()),
            () => _ = ExitAsync());
        _tray.BalloonClicked += () => { ShowFromTray(); Tabs.SelectedItem = CallsTab; };
        _tray.SetDoNotDisturb(settings.DoNotDisturb);

        _phone.RegistrationChanged += (state, text) => Dispatcher.BeginInvoke(() => ShowRegistration(state, text));
        _phone.CallStateChanged += (state, info) => Dispatcher.BeginInvoke(() => ShowCallState(state, info));
        _phone.IncomingCall += caller => Dispatcher.BeginInvoke(() => ShowIncoming(caller));
        _phone.CallEnded += record => Dispatcher.BeginInvoke(() => AddCallRecord(record));
        _phone.Info += msg => Dispatcher.BeginInvoke(() => StatusText.Text = msg);
        _phone.Error += msg => Dispatcher.BeginInvoke(() => StatusText.Text = msg);
        _callTimer.Tick += (_, _) => UpdateCallTimer();

        SourceInitialized += (_, _) =>
        {
            _hotkey = new GlobalHotkey(this, () => _ = DialSelectionAsync());
            RegisterHotkey();
        };
    }

    /// <summary>Startet die Anmeldung; beim ersten Start ohne Zugangsdaten öffnen sich die Einstellungen.</summary>
    public async Task StartAsync()
    {
        // Fensterhandle für das globale Tastenkürzel auch dann anlegen, wenn das Fenster verborgen startet.
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();

        if (!_settings.IsComplete)
        {
            ShowFromTray();
            StatusText.Text = "Willkommen! Bitte zuerst die SIP-Zugangsdaten eintragen.";
            OnSettings(this, new RoutedEventArgs());
            return;
        }
        await _phone.StartAsync(_settings);
    }

    /// <summary>Befehl einer weiteren Instanz, z. B. aus einem tel:-Link: "show" oder "dial &lt;Nummer&gt;".</summary>
    public void HandleCommand(string command)
    {
        if (command.Equals("exit", StringComparison.OrdinalIgnoreCase))
        {
            // Vom Installer (Update/Deinstallation): ohne Rückfrage beenden.
            _ = ExitAsync(force: true);
            return;
        }
        ShowFromTray();
        if (command.StartsWith("dial ", StringComparison.OrdinalIgnoreCase))
            _ = Dial(command[5..].Trim());
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    // ---------------- Telefon ----------------

    private void BuildDialPad()
    {
        foreach (var key in "123456789*0#")
        {
            var button = new Button { Content = key.ToString(), Style = (Style)FindResource("DialKey"), Focusable = false };
            button.Click += (_, _) => OnDialKey(key);
            DialPad.Children.Add(button);
        }
    }

    private void OnDialKey(char key)
    {
        if (_phone.State == CallState.Connected)
        {
            _phone.SendDtmf(key);
            StatusText.Text = $"Tonwahl gesendet: {key}";
            return;
        }
        NumberBox.Text += key;
        NumberBox.CaretIndex = NumberBox.Text.Length;
    }

    private void OnBackspace(object sender, RoutedEventArgs e)
    {
        if (NumberBox.Text.Length > 0) NumberBox.Text = NumberBox.Text[..^1];
    }

    private void OnNumberKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                OnCall(sender, e);
                break;
            case Key.Down when SuggestionList.Visibility == Visibility.Visible:
                e.Handled = true;
                SuggestionList.SelectedIndex = 0;
                (SuggestionList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                break;
            case Key.Escape:
                SuggestionList.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void OnNumberChanged(object sender, TextChangedEventArgs e)
    {
        var text = NumberBox.Text;
        var contact = FindContact(text);
        NumberHint.Text = contact != null ? contact.Name : "";
        UpdateSuggestions(text, exactMatch: contact != null);
    }

    private void UpdateSuggestions(string query, bool exactMatch)
    {
        query = query.Trim();
        var digits = PhoneNumber.ForDialing(query);
        if (query.Length == 0 || exactMatch || _phone.State != CallState.Idle)
        {
            SuggestionList.Visibility = Visibility.Collapsed;
            return;
        }

        bool NumberMatches(string number) => digits.Length >= 2 && PhoneNumber.ForDialing(number).Contains(digits);

        var fromContacts = _contacts
            .Where(c => c.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || c.Company.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || NumberMatches(c.Number))
            .Select(c => new Suggestion(string.IsNullOrEmpty(c.Company) ? c.Name : $"{c.Name} ({c.Company})", c.Number, c.AvatarImage));
        var fromHistory = _calls
            .Where(c => c.Number.Length > 0 && (NumberMatches(c.Number) || c.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .Select(c => FindContact(c.Number) is { } known
                ? new Suggestion(known.Name, c.Number, known.AvatarImage)
                : new Suggestion(c.Name.Length > 0 ? c.Name : "Anrufliste", c.Number, null));

        var suggestions = fromContacts.Concat(fromHistory)
            .DistinctBy(s => PhoneNumber.Canonical(s.Number))
            .Take(6)
            .ToList();
        SuggestionList.ItemsSource = suggestions;
        SuggestionList.Visibility = suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSuggestionClick(object sender, MouseButtonEventArgs e) => UseSuggestion(dial: true);

    private void OnSuggestionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            UseSuggestion(dial: true);
        }
        else if (e.Key == Key.Escape)
        {
            SuggestionList.Visibility = Visibility.Collapsed;
            NumberBox.Focus();
        }
    }

    private void UseSuggestion(bool dial)
    {
        if (SuggestionList.SelectedItem is not Suggestion s) return;
        SuggestionList.Visibility = Visibility.Collapsed;
        NumberBox.Text = s.Number;
        NumberBox.CaretIndex = NumberBox.Text.Length;
        if (dial) _ = Dial(s.Number);
    }

    private async void OnCall(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NumberBox.Text) && LastDialed() is { } last)
        {
            // Klassische Wahlwiederholung: leeres Feld + Anrufen holt die letzte Nummer.
            NumberBox.Text = last;
            NumberBox.CaretIndex = last.Length;
            StatusText.Text = "Wahlwiederholung – nochmals „Anrufen“ zum Wählen.";
            return;
        }

        // Name eingegeben? Dann den ersten Vorschlag wählen.
        if (PhoneNumber.ForDialing(NumberBox.Text).Length == 0 && SuggestionList.Items.Count > 0
            && SuggestionList.Visibility == Visibility.Visible)
        {
            SuggestionList.SelectedIndex = 0;
            UseSuggestion(dial: true);
            return;
        }
        await Dial(NumberBox.Text);
    }

    private async void OnRedial(object sender, RoutedEventArgs e)
    {
        if (LastDialed() is { } last) await Dial(last);
        else StatusText.Text = "Noch keine Nummer gewählt.";
    }

    private string? LastDialed() =>
        _calls.FirstOrDefault(c => c.Direction == CallDirection.Outgoing && c.Number.Length > 0)?.Number;

    private async Task Dial(string number)
    {
        if (PhoneNumber.ForDialing(number).Length == 0)
        {
            StatusText.Text = "Bitte eine Nummer eingeben.";
            return;
        }
        if (_phone.State != CallState.Idle)
        {
            StatusText.Text = "Es läuft bereits ein Gespräch.";
            return;
        }
        if (!_settings.IsComplete)
        {
            StatusText.Text = "Bitte zuerst die SIP-Zugangsdaten eintragen.";
            return;
        }
        Tabs.SelectedIndex = 0;
        SuggestionList.Visibility = Visibility.Collapsed;
        NumberBox.Text = number;
        await _phone.DialAsync(number);
    }

    /// <summary>Globales Tastenkürzel: markierte Nummer aus einer beliebigen App wählen.</summary>
    private async Task DialSelectionAsync()
    {
        var text = await GlobalHotkey.GetSelectedTextAsync();
        var number = text == null ? null : PhoneNumber.FromUri(text) ?? PhoneNumber.Extract(text);
        if (number == null)
        {
            _tray.ShowBalloon(SystemIntegration.AppName, "Keine Telefonnummer markiert.");
            return;
        }
        ShowFromTray();
        await Dial(number);
    }

    private async void OnAnswer(object sender, RoutedEventArgs e) => await _phone.AnswerAsync();
    private void OnHangup(object sender, RoutedEventArgs e) => _phone.Hangup();

    private void OnMute(object sender, RoutedEventArgs e)
    {
        _phone.ToggleMute();
        MuteButton.Content = _phone.IsMuted ? "Stumm aus" : "Stumm";
        MuteButton.SetResourceReference(BackgroundProperty, _phone.IsMuted ? "Highlight" : "Surface");
    }

    private void OnHold(object sender, RoutedEventArgs e)
    {
        _phone.ToggleHold();
        HoldButton.Content = _phone.IsOnHold ? "Fortsetzen" : "Halten";
        HoldButton.SetResourceReference(BackgroundProperty, _phone.IsOnHold ? "Highlight" : "Surface");
        CallStateText.Text = _phone.IsOnHold ? "Gehalten" : "Verbunden";
    }

    private void UpdateCallTimer()
    {
        CallTimer.Text = (DateTime.Now - _connectedAt).ToString(@"hh\:mm\:ss");
        if (_phone.Quality is { } q)
        {
            QualityText.Text = (q.Encrypted ? "Verschlüsselt (SRTP) · " : "Unverschlüsselt · ") +
                               $"Jitter {q.JitterMs:0} ms · Verlust {q.LossPercent:0.0} % · Puffer {q.BufferMs} ms · " +
                               (q.EchoCancellation ? "Echounterdrückung an" : "Echounterdrückung aus");
            QualityText.SetResourceReference(TextBlock.ForegroundProperty, q.LossPercent > 5 || q.JitterMs > 60 ? "Accent" : "Muted");
        }
    }

    // ---------------- Status ----------------

    private void ShowRegistration(RegistrationState state, string text)
    {
        _registration = state;
        _registrationText = text;
        RegText.Text = text;
        RegText.ToolTip = text;
        LockIcon.Visibility = state == RegistrationState.Registered && _phone.IsEncrypted ? Visibility.Visible : Visibility.Collapsed;
        RegDot.Fill = new SolidColorBrush(state switch
        {
            RegistrationState.Registered => Color.FromRgb(0x22, 0xA4, 0x47),
            RegistrationState.Registering => Color.FromRgb(0xE8, 0xA3, 0x17),
            RegistrationState.Failed => Color.FromRgb(0xF2, 0xC9, 0x4C),
            _ => Color.FromRgb(0x8A, 0x8A, 0x99)
        });
        UpdateTray();
    }

    private void UpdateTray()
    {
        if (_phone.State == CallState.Incoming)
        {
            _tray.SetStatus(TrayStatus.Ringing, "Eingehender Anruf");
            return;
        }
        if (_phone.DoNotDisturb && _registration == RegistrationState.Registered)
        {
            _tray.SetStatus(TrayStatus.DoNotDisturb, "Nicht stören");
            return;
        }
        var status = _registration switch
        {
            RegistrationState.Registered => TrayStatus.Online,
            RegistrationState.Registering => TrayStatus.Registering,
            RegistrationState.Failed => TrayStatus.Error,
            _ => TrayStatus.Offline
        };
        _tray.SetStatus(status, _phone.State == CallState.Connected ? "Im Gespräch" : _registrationText);
    }

    private void ShowCallState(CallState state, string info)
    {
        if (state != CallState.Incoming) CloseIncomingWindow();
        UpdateTray();

        if (state == CallState.Idle)
        {
            CallPanel.Visibility = Visibility.Collapsed;
            _callTimer.Stop();
            CallButton.IsEnabled = true;
            QualityText.Text = "";
            if (!string.IsNullOrEmpty(info)) StatusText.Text = $"{DateTime.Now:HH:mm} {info}";
            return;
        }

        var remote = _phone.Remote;
        var number = remote?.Number ?? "";

        CallPanel.Visibility = Visibility.Visible;
        CallButton.IsEnabled = false;
        SuggestionList.Visibility = Visibility.Collapsed;
        CallStateText.Text = info;
        CallName.Text = DisplayNameFor(remote);
        var callContact = number.Length > 0 ? FindContact(number) : null;
        CallAvatar.DisplayName = callContact?.Name ?? remote?.DisplayName ?? "";
        CallAvatar.Image = callContact?.AvatarImage;
        CallNumber.Text = number.Length > 0 ? number : "Rufnummer unterdrückt";
        CallNumber.Visibility = CallName.Text == number ? Visibility.Collapsed : Visibility.Visible;

        AnswerButton.Visibility = state == CallState.Incoming ? Visibility.Visible : Visibility.Collapsed;
        HangupButton.Content = state == CallState.Incoming ? "Ablehnen" : "Auflegen";
        MuteButton.Visibility = HoldButton.Visibility =
            state == CallState.Connected ? Visibility.Visible : Visibility.Collapsed;

        if (state == CallState.Connected && !_callTimer.IsEnabled)
        {
            _connectedAt = DateTime.Now;
            CallTimer.Text = "00:00:00";
            _callTimer.Start();
            MuteButton.Content = "Stumm";
            HoldButton.Content = "Halten";
            MuteButton.SetResourceReference(BackgroundProperty, "Surface");
            HoldButton.SetResourceReference(BackgroundProperty, "Surface");
        }
        else if (state != CallState.Connected)
        {
            CallTimer.Text = "";
        }
    }

    private string DisplayNameFor(CallParty? party)
    {
        var number = party?.Number ?? "";
        var contact = number.Length > 0 ? FindContact(number) : null;
        if (contact != null) return contact.Name;
        if (!string.IsNullOrWhiteSpace(party?.DisplayName)) return party.DisplayName;
        return number.Length > 0 ? number : "Unbekannt";
    }

    private void ShowIncoming(CallParty caller)
    {
        CloseIncomingWindow();
        var name = DisplayNameFor(caller);
        var contact = caller.Number.Length > 0 ? FindContact(caller.Number) : null;
        var detail = caller.Number.Length == 0 ? "Rufnummer unterdrückt"
            : name == caller.Number ? ""
            : contact is { Company.Length: > 0 } ? $"{caller.Number} · {contact.Company}" : caller.Number;

        _incomingWindow = new IncomingCallWindow(name, detail, contact?.AvatarImage,
            answer: async () => { ShowFromTray(); await _phone.AnswerAsync(); },
            reject: _phone.Hangup,
            silence: _phone.SilenceRinging);
        _incomingWindow.Closed += (_, _) => _incomingWindow = null;
        _incomingWindow.Show();
        UpdateTray();
    }

    private void CloseIncomingWindow()
    {
        _incomingWindow?.Close();
        _incomingWindow = null;
    }

    // ---------------- Nicht stören ----------------

    private void OnDndClick(object sender, RoutedEventArgs e) => SetDoNotDisturb(DndToggle.IsChecked == true);

    private void SetDoNotDisturb(bool on)
    {
        _phone.DoNotDisturb = on;
        _settings.DoNotDisturb = on;
        Storage.SaveSettings(_settings);
        DndToggle.IsChecked = on;
        _tray.SetDoNotDisturb(on);
        StatusText.Text = on ? "Nicht stören ist an – eingehende Anrufe werden abgewiesen." : "Nicht stören ist aus.";
        UpdateTray();
    }

    // ---------------- Anrufliste ----------------

    private void AddCallRecord(CallRecord record)
    {
        if (string.IsNullOrEmpty(record.Name) && FindContact(record.Number) is { } contact)
            record.Name = contact.Name;
        if (Tabs.SelectedItem == CallsTab && IsVisible) record.Unseen = false;

        _calls.Insert(0, record);
        while (_calls.Count > Storage.MaxCallRecords) _calls.RemoveAt(_calls.Count - 1);
        Storage.SaveCalls(_calls);
        UpdateMissedBadge();

        if (record.Outcome == CallOutcome.Missed)
        {
            var note = record.Note.Length > 0 ? $" ({record.Note})" : "";
            _tray.ShowBalloon("Verpasster Anruf", $"{record.Display}{note} um {record.Start:HH:mm}");
        }
    }

    private void UpdateMissedBadge()
    {
        var missed = _calls.Count(c => c.Unseen);
        CallsTabHeader.Text = missed > 0 ? $"Anrufliste ({missed})" : "Anrufliste";
        if (missed > 0) CallsTabHeader.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        else CallsTabHeader.ClearValue(TextBlock.ForegroundProperty);
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs || Tabs.SelectedItem != CallsTab || !_calls.Any(c => c.Unseen)) return;
        foreach (var c in _calls) c.Unseen = false;
        Storage.SaveCalls(_calls);
        UpdateMissedBadge();
    }

    private CallRecord? SelectedCall => CallList.SelectedItem as CallRecord;

    private async void OnCallBack(object sender, RoutedEventArgs e)
    {
        if (SelectedCall is { Number.Length: > 0 } c) await Dial(c.Number);
    }

    private void OnCallListDoubleClick(object sender, MouseButtonEventArgs e) => OnCallBack(sender, e);

    private void OnHistoryToContact(object sender, RoutedEventArgs e)
    {
        if (SelectedCall is not { Number.Length: > 0 } c) return;
        var existing = FindContact(c.Number);
        var dialog = new ContactWindow(existing ?? new Contact { Name = c.Name, Number = c.Number }) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (existing != null) _contacts[_contacts.IndexOf(existing)] = dialog.Contact;
        else _contacts.Add(dialog.Contact);
        SaveContacts();
    }

    private void OnDeleteHistory(object sender, RoutedEventArgs e)
    {
        if (SelectedCall is not { } c) return;
        _calls.Remove(c);
        Storage.SaveCalls(_calls);
        UpdateMissedBadge();
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        if (_calls.Count == 0) return;
        if (MessageBox.Show(this, "Gesamte Anrufliste löschen?", "Anrufliste",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _calls.Clear();
        Storage.SaveCalls(_calls);
        UpdateMissedBadge();
    }

    // ---------------- Adressbuch ----------------

    private Contact? FindContact(string number) =>
        _contacts.FirstOrDefault(c => PhoneNumber.SameNumber(c.Number, number));

    private bool FilterContact(object item)
    {
        var q = SearchBox?.Text?.Trim() ?? "";
        if (q.Length == 0) return true;
        var c = (Contact)item;
        var digits = PhoneNumber.ForDialing(q);
        return c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || c.Company.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || (digits.Length > 0 && PhoneNumber.ForDialing(c.Number).Contains(digits));
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => _contactView.Refresh();

    private Contact? SelectedContact => ContactList.SelectedItem as Contact;

    private async void OnCallContact(object sender, RoutedEventArgs e)
    {
        if (SelectedContact is { } c) await Dial(c.Number);
    }

    private void OnContactDoubleClick(object sender, MouseButtonEventArgs e) => OnCallContact(sender, e);

    private void OnNewContact(object sender, RoutedEventArgs e)
    {
        var dialog = new ContactWindow(new Contact()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _contacts.Add(dialog.Contact);
        SaveContacts();
        ContactList.SelectedItem = dialog.Contact;
    }

    private void OnEditContact(object sender, RoutedEventArgs e)
    {
        if (SelectedContact is not { } c) return;
        var dialog = new ContactWindow(c) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _contacts[_contacts.IndexOf(c)] = dialog.Contact;
        SaveContacts();
    }

    private void OnDeleteContact(object sender, RoutedEventArgs e)
    {
        if (SelectedContact is not { } c) return;
        if (MessageBox.Show(this, $"Kontakt „{c.Name}“ löschen?", "Adressbuch",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _contacts.Remove(c);
        Avatars.Delete(c.Avatar);
        SaveContacts();
    }

    private void SaveContacts()
    {
        Storage.SaveContacts(_contacts);
        _contactView.Refresh();
    }

    // ---------------- Einstellungen & Beenden ----------------

    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        if (_phone.State != CallState.Idle)
        {
            StatusText.Text = "Einstellungen können erst nach dem Gespräch geändert werden.";
            return;
        }
        ShowFromTray();
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _settings = dialog.Settings;
        _settings.DoNotDisturb = _phone.DoNotDisturb;
        Storage.SaveSettings(_settings);
        ApplySystemIntegration(_settings);
        RegisterHotkey();
        StatusText.Text = "Einstellungen gespeichert.";
        await _phone.StartAsync(_settings);
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
        new AboutWindow { Owner = this }.ShowDialog();
    }

    public static void ApplySystemIntegration(AppSettings settings)
    {
        try
        {
            SystemIntegration.SetAutostart(settings.StartWithWindows);
            SystemIntegration.SetTelLinkRegistration(settings.RegisterTelLinks);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Windows-Integration konnte nicht eingerichtet werden: {ex.Message}", SystemIntegration.AppName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RegisterHotkey()
    {
        if (_hotkey == null) return;
        if (!_hotkey.Register(_settings.DialHotkey))
            StatusText.Text = $"Tastenkürzel „{_settings.DialHotkey}“ ist ungültig oder von einer anderen App belegt.";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_exitRequested) return;

        // Schließen = in den Infobereich minimieren, damit Anrufe weiter ankommen.
        e.Cancel = true;
        Hide();
        if (!_hideHintShown)
        {
            _hideHintShown = true;
            _tray.ShowBalloon(SystemIntegration.AppName, "Läuft im Hintergrund weiter. Beenden über das Symbol im Infobereich.");
        }
    }

    public async Task ExitAsync(bool force = false)
    {
        if (_exitRequested) return;
        if (!force && _phone.State != CallState.Idle &&
            MessageBox.Show(this, "Es läuft ein Gespräch. Trotzdem beenden?", SystemIntegration.AppName,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _exitRequested = true;
        IsEnabled = false;
        StatusText.Text = "Melde ab …";
        CloseIncomingWindow();
        _hotkey?.Dispose();
        await _phone.DisposeAsync();
        _tray.Dispose();
        Close();
        Application.Current.Shutdown();
    }
}
