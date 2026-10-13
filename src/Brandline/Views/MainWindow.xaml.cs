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
    private sealed record Suggestion(string Name, string Number, ImageSource? Image, string Detail);

    private readonly SipPhone _phone = new();
    private readonly ObservableCollection<Contact> _contacts;
    private readonly ICollectionView _contactView;
    private readonly ObservableCollection<CallRecord> _calls;
    private readonly DispatcherTimer _callTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly UpdateService _updates = new();
    private readonly Microsoft365Service _m365 = new();
    private readonly DispatcherTimer _m365Timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _m365Syncing;
    private UpdateInfo? _availableUpdate;
    private string? _announcedVersion;
    private readonly TrayIcon _tray;
    private GlobalHotkey? _hotkey;
    private IncomingCallWindow? _incomingWindow;
    private AppSettings _settings;
    private RegistrationState _registration = RegistrationState.Offline;
    private string _registrationText = "Nicht angemeldet";
    private bool _exitRequested;
    private bool _hideHintShown;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        Icon = TrayIcon.CreateWindowIcon();
        _settings = settings;

        // Eigene Kontakte und aus Microsoft 365 synchronisierte (getrennt gespeichert, hier gemeinsam angezeigt).
        _contacts = new ObservableCollection<Contact>(Storage.LoadContacts().Concat(Storage.LoadSyncedContacts()));
        _contactView = CollectionViewSource.GetDefaultView(_contacts);
        _contactView.SortDescriptions.Add(new SortDescription(nameof(Contact.Name), ListSortDirection.Ascending));
        _contactView.Filter = FilterContact;
        ContactList.ItemsSource = _contactView;

        _calls = new ObservableCollection<CallRecord>(Storage.LoadCalls().OrderByDescending(c => c.Start));
        CallList.ItemsSource = _calls;
        UpdateMissedBadge();

        BuildDialPad();
        DndToggle.IsChecked = settings.DoNotDisturb;
        HideCallerIdBox.IsChecked = settings.HideCallerId;
        // Symbol folgt jedem Wechsel – per Schaltfläche, Einstellungen oder Windows („wie Windows“).
        UpdateThemeButton();
        Themes.ThemeManager.Changed += UpdateThemeButton;

        _tray = new TrayIcon(ShowFromTray, () => SetDoNotDisturb(!_phone.DoNotDisturb), () => OnSettings(this, new RoutedEventArgs()),
            () => OnAbout(this, new RoutedEventArgs()),
            () => _ = ExitAsync());
        _tray.BalloonClicked += () => { ShowFromTray(); Tabs.SelectedItem = CallsTab; };
        _tray.SetDoNotDisturb(settings.DoNotDisturb);

        _phone.RegistrationChanged += (state, text) => Dispatcher.BeginInvoke(() => ShowRegistration(state, text));
        _phone.CallsChanged += info => Dispatcher.BeginInvoke(() => RefreshCalls(info));
        _phone.IncomingCall += (call, waiting) => Dispatcher.BeginInvoke(() => ShowIncoming(call, waiting));
        _phone.VoicemailChanged += status => Dispatcher.BeginInvoke(() => ShowVoicemail(status));
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

        // Update-Prüfung: erstmals eine Minute nach dem Start (Netz nach der Anmeldung abwarten), dann alle 12 Stunden.
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(12);
            if (_settings.CheckForUpdates) await CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();

        // Microsoft-365-Kontakte: 30 Sekunden nach dem Start, dann alle 6 Stunden.
        _m365Timer.Tick += async (_, _) =>
        {
            _m365Timer.Interval = TimeSpan.FromHours(6);
            await SyncMicrosoft365Async(manual: false);
        };
        _m365Timer.Start();
        UpdateM365Button();

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
        if (query.Length == 0 || exactMatch || _phone.Calls.Count >= 2)
        {
            SuggestionList.Visibility = Visibility.Collapsed;
            return;
        }

        bool NumberMatches(string number) => digits.Length >= 2 && PhoneNumber.ForDialing(number).Contains(digits);

        // Ein Vorschlag pro Rufnummer (bei Kontakten mit mehreren Nummern also mehrere).
        var fromContacts = _contacts
            .SelectMany(c => c.Numbers.Select(n => (Contact: c, Number: n)))
            .Where(x => x.Contact.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || x.Contact.Company.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || NumberMatches(x.Number.Number))
            .Select(x => new Suggestion(string.IsNullOrEmpty(x.Contact.Company) ? x.Contact.Name : $"{x.Contact.Name} ({x.Contact.Company})",
                x.Number.Number, x.Contact.AvatarImage, $"{x.Number.Number} · {x.Number.TypeLabel}"));
        var fromHistory = _calls
            .Where(c => c.Number.Length > 0 && (NumberMatches(c.Number) || c.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .Select(c => FindContact(c.Number) is { } known
                ? new Suggestion(known.Name, c.Number, known.AvatarImage, $"{c.Number} · {known.FindNumber(c.Number)?.TypeLabel}")
                : new Suggestion(c.Name.Length > 0 ? c.Name : "Anrufliste", c.Number, null, c.Number));

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
        if (_phone.Calls.Count >= 2 || _phone.RingingCall != null)
        {
            StatusText.Text = "Es sind bereits zwei Gespräche aktiv – bitte zuerst eines beenden.";
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
        // Läuft schon ein Gespräch, wird es gehalten (Rückfrage).
        await _phone.DialAsync(number, HideCallerIdBox.IsChecked == true);
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

    // ---------------- Gesprächssteuerung ----------------

    /// <summary>Im großen Gesprächsbereich angezeigtes Gespräch und das zweite (gehalten/anklopfend).</summary>
    private PhoneCall? _shownCall;
    private PhoneCall? _secondCall;

    private async void OnAnswer(object sender, RoutedEventArgs e)
    {
        if (_shownCall is { IsRinging: true } call) await _phone.AnswerAsync(call);
    }

    private void OnHangup(object sender, RoutedEventArgs e)
    {
        if (_shownCall != null) _phone.Hangup(_shownCall);
    }

    private void OnMute(object sender, RoutedEventArgs e) => _phone.ToggleMute();

    private void OnHold(object sender, RoutedEventArgs e)
    {
        if (_shownCall is not { } call) return;
        if (call.IsOnHold) _phone.Resume(call);
        else _phone.Hold(call);
    }

    private async void OnTransfer(object sender, RoutedEventArgs e)
    {
        if (_shownCall is not { IsConnected: true } call) return;
        var dialog = new TransferWindow(_contacts, DisplayNameFor(call.Remote)) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Number is not { } number) return;

        if (dialog.Consult)
        {
            // Rückfrage: aktuelles Gespräch wird gehalten, Ziel angerufen; danach „Verbinden“.
            await Dial(number);
        }
        else if (await _phone.BlindTransferAsync(call, number) is { } error)
        {
            StatusText.Text = error;
        }
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        if (await _phone.CompleteAttendedTransferAsync() is { } error) StatusText.Text = error;
        ConnectButton.IsEnabled = true;
    }

    private async void OnSecondPrimary(object sender, RoutedEventArgs e)
    {
        if (_secondCall is not { } call) return;
        if (call.IsRinging) await _phone.AnswerAsync(call);   // Anklopfen annehmen, aktuelles halten
        else if (call.IsOnHold) _phone.Resume(call);          // Makeln
    }

    private void OnSecondHangup(object sender, RoutedEventArgs e)
    {
        if (_secondCall != null) _phone.Hangup(_secondCall);
    }

    private void UpdateCallTimer()
    {
        if (_shownCall?.AnsweredAt is { } at) CallTimer.Text = (DateTime.Now - at).ToString(@"hh\:mm\:ss");
        if (_secondCall is { IsOnHold: true } held)
            SecondName.Text = $"{DisplayNameFor(held.Remote)} · {(DateTime.Now - (held.AnsweredAt ?? held.Start)).ToString(@"mm\:ss")}";

        if (_shownCall?.Quality is { } q)
        {
            QualityText.Text = (q.Encrypted ? "Verschlüsselt (SRTP) · " : "Unverschlüsselt · ") +
                               $"Jitter {q.JitterMs:0} ms · Verlust {q.LossPercent:0.0} % · Puffer {q.BufferMs} ms · " +
                               (q.EchoCancellation ? "Echounterdrückung an" : "Echounterdrückung aus");
            QualityText.SetResourceReference(TextBlock.ForegroundProperty, q.LossPercent > 5 || q.JitterMs > 60 ? "Accent" : "Muted");
        }
        else
        {
            QualityText.Text = "";
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
        if (_phone.RingingCall != null)
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
        _tray.SetStatus(status, _phone.Calls.Count > 0 ? "Im Gespräch" : _registrationText);
    }

    /// <summary>Gesprächsbereich aus dem aktuellen Stand neu aufbauen (bis zu zwei Gespräche).</summary>
    private void RefreshCalls(string info)
    {
        var calls = _phone.Calls;
        if (_phone.RingingCall == null) CloseIncomingWindow();
        UpdateTray();

        if (calls.Count == 0)
        {
            _shownCall = _secondCall = null;
            CallPanel.Visibility = Visibility.Collapsed;
            _callTimer.Stop();
            CallButton.IsEnabled = true;
            CallButtonText.Text = "Anrufen";
            QualityText.Text = "";
            HideCallerIdBox.IsChecked = _settings.HideCallerId; // gilt nur für einen Anruf
            if (!string.IsNullOrEmpty(info)) StatusText.Text = $"{DateTime.Now:HH:mm} {info}";
            return;
        }

        // Groß: aktives Gespräch, sonst der klingelnde, sonst der gehaltene Anruf.
        var shown = _phone.ActiveCall ?? _phone.RingingCall ?? _phone.HeldCall!;
        var second = calls.FirstOrDefault(c => c != shown);
        _shownCall = shown;
        _secondCall = second;

        CallPanel.Visibility = Visibility.Visible;
        SuggestionList.Visibility = Visibility.Collapsed;

        var number = shown.Remote.Number;
        var contact = number.Length > 0 ? FindContact(number) : null;
        CallName.Text = DisplayNameFor(shown.Remote);
        CallAvatar.DisplayName = contact?.Name ?? shown.Remote.DisplayName;
        CallAvatar.Image = contact?.AvatarImage;
        CallNumber.Text = number.Length == 0 ? "Rufnummer unterdrückt"
            : string.Join(" · ", new[] { number, contact?.FindNumber(number)?.TypeLabel }.Where(s => !string.IsNullOrEmpty(s)));
        CallNumber.Visibility = CallName.Text == number ? Visibility.Collapsed : Visibility.Visible;
        CallStateText.Text = string.Join(" · ", new[]
        {
            shown.Info,
            shown.Anonymous ? "Ihre Nummer wird unterdrückt" : null,
            shown.IsHeldByRemote ? "von der Gegenseite gehalten" : null,
            shown.ConsultationFor != null ? $"Rückfrage für {DisplayNameFor(shown.ConsultationFor.Remote)}" : null,
        }.Where(s => !string.IsNullOrEmpty(s)));
        if (shown.AnsweredAt == null) CallTimer.Text = "";

        var connected = shown.IsConnected;
        AnswerButton.Visibility = shown.IsRinging ? Visibility.Visible : Visibility.Collapsed;
        HangupButton.Content = shown.IsRinging ? "Ablehnen" : "Auflegen";
        MuteButton.Visibility = HoldButton.Visibility = TransferButton.Visibility =
            connected ? Visibility.Visible : Visibility.Collapsed;
        MuteButton.Content = shown.IsMuted ? "Stumm aus" : "Stumm";
        MuteButton.SetResourceReference(BackgroundProperty, shown.IsMuted ? "Highlight" : "Surface");
        HoldButton.Content = shown.IsOnHold ? "Fortsetzen" : "Halten";
        HoldButton.SetResourceReference(BackgroundProperty, shown.IsOnHold ? "Highlight" : "Surface");
        TransferButton.IsEnabled = !shown.IsOnHold && second == null;
        // „Verbinden“: gehaltenes Gespräch an das aktive (Rückfrage-)Gespräch übergeben.
        ConnectButton.Visibility = connected && !shown.IsOnHold && second is { IsOnHold: true }
            ? Visibility.Visible : Visibility.Collapsed;

        if (second != null)
        {
            SecondCallBar.Visibility = Visibility.Visible;
            if (second.IsRinging)
            {
                SecondIcon.Text = "";
                SecondTitle.Text = "Anklopfen";
                SecondName.Text = DisplayNameFor(second.Remote) + (second.Remote.Number.Length > 0 && DisplayNameFor(second.Remote) != second.Remote.Number ? $" · {second.Remote.Number}" : "");
                SecondPrimaryButton.Content = "Annehmen";
                SecondPrimaryButton.ToolTip = "Annehmen – das aktuelle Gespräch wird gehalten";
                SecondHangupButton.Content = "Ablehnen";
            }
            else
            {
                SecondIcon.Text = "";
                SecondTitle.Text = "Gehalten";
                SecondName.Text = DisplayNameFor(second.Remote);
                SecondPrimaryButton.Content = "Wechseln";
                SecondPrimaryButton.ToolTip = "Zum gehaltenen Gespräch wechseln (das aktuelle wird gehalten)";
                SecondHangupButton.Content = "Auflegen";
            }
        }
        else
        {
            SecondCallBar.Visibility = Visibility.Collapsed;
        }

        // Zweites Gespräch nur, wenn noch keine zwei laufen und keiner klingelt; dann als Rückfrage.
        CallButton.IsEnabled = calls.Count < 2 && _phone.RingingCall == null;
        CallButtonText.Text = _phone.ActiveCall is { IsConnected: true } ? "Rückfrage" : "Anrufen";

        if (!_callTimer.IsEnabled) _callTimer.Start();
        UpdateCallTimer();
        if (!string.IsNullOrEmpty(info)) StatusText.Text = info;
    }

    private string DisplayNameFor(CallParty? party)
    {
        var number = party?.Number ?? "";
        var contact = number.Length > 0 ? FindContact(number) : null;
        if (contact != null) return contact.Name;
        if (!string.IsNullOrWhiteSpace(party?.DisplayName)) return party.DisplayName;
        return number.Length > 0 ? number : "Unbekannt";
    }

    private void ShowIncoming(PhoneCall call, bool waiting)
    {
        CloseIncomingWindow();
        var caller = call.Remote;
        var name = DisplayNameFor(caller);
        var contact = caller.Number.Length > 0 ? FindContact(caller.Number) : null;
        var detail = caller.Number.Length == 0 ? "Rufnummer unterdrückt"
            : name == caller.Number ? ""
            : string.Join(" · ", new[] { caller.Number, contact?.FindNumber(caller.Number)?.TypeLabel, contact?.Company }
                .Where(s => !string.IsNullOrEmpty(s)));

        _incomingWindow = new IncomingCallWindow(name, detail, contact?.AvatarImage, waiting,
            answer: async () => { ShowFromTray(); await _phone.AnswerAsync(call); },
            reject: () => _phone.Hangup(call),
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

    // ---------------- Mailbox ----------------

    private int _announcedVoicemails;

    private void ShowVoicemail(VoicemailStatus status)
    {
        VoicemailButton.Visibility = status.Waiting || _settings.VoicemailNumber.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        VoicemailBadge.Visibility = status.New > 0 ? Visibility.Visible : Visibility.Collapsed;
        VoicemailCount.Text = status.New > 9 ? "9+" : status.New.ToString();
        VoicemailButton.ToolTip = status.New > 0
            ? $"{status.New} neue Sprachnachricht{(status.New == 1 ? "" : "en")} – Mailbox abhören"
            : "Mailbox abhören";
        if (status.New > _announcedVoicemails)
            _tray.ShowBalloon("Neue Sprachnachricht", $"Sie haben {status.New} neue Sprachnachricht{(status.New == 1 ? "" : "en")}.");
        _announcedVoicemails = status.New;
    }

    private async void OnVoicemail(object sender, RoutedEventArgs e)
    {
        if (_phone.VoicemailDialTarget() is { } target) await Dial(target);
        else StatusText.Text = "Mailbox-Nummer bitte in den Einstellungen (Konto) eintragen.";
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

    // ---------------- Hell/Dunkel ----------------

    /// <summary>Wechselt zwischen hellem und dunklem Design (auch aus „wie Windows“ heraus) und speichert die Wahl.</summary>
    private void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        _settings.Theme = Themes.ThemeManager.IsDark ? AppTheme.Light : AppTheme.Dark;
        Themes.ThemeManager.Apply(_settings.Theme);
        Storage.SaveSettings(_settings);
    }

    private void UpdateThemeButton()
    {
        var dark = Themes.ThemeManager.IsDark;
        ThemeButton.Content = dark ? "" : "";
        ThemeButton.ToolTip = dark ? "Helles Design" : "Dunkles Design";
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
        if (existing is { IsSynced: true })
        {
            MessageBox.Show(this, $"„{existing.Name}“ ist bereits im Adressbuch ({existing.SourceLabel}).", "Adressbuch",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new ContactWindow(existing ?? new Contact { Name = c.Name, Numbers = [new ContactNumber(c.Number)] }) { Owner = this };
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

    /// <summary>Kontakt zur Nummer – eigene Kontakte haben Vorrang vor synchronisierten.</summary>
    private Contact? FindContact(string number) =>
        _contacts.Where(c => c.HasNumber(number)).OrderBy(c => c.IsSynced).FirstOrDefault();

    private bool FilterContact(object item)
    {
        var q = SearchBox?.Text?.Trim() ?? "";
        if (q.Length == 0) return true;
        var c = (Contact)item;
        var digits = PhoneNumber.ForDialing(q);
        return c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || c.Company.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || (digits.Length > 0 && c.Numbers.Any(n => PhoneNumber.ForDialing(n.Number).Contains(digits)));
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => _contactView.Refresh();

    private Contact? SelectedContact => ContactList.SelectedItem as Contact;

    private async void OnCallContact(object sender, RoutedEventArgs e)
    {
        if (SelectedContact is { } c && await ChooseNumberAsync(c, sender as FrameworkElement) is { } number) await Dial(number);
    }

    /// <summary>
    /// Nummer eines Kontakts wählen: bei einer Nummer direkt, bei mehreren über ein Auswahlmenü an der Schaltfläche.
    /// </summary>
    private Task<string?> ChooseNumberAsync(Contact contact, FrameworkElement? placement)
    {
        if (contact.Numbers.Count <= 1) return Task.FromResult<string?>(contact.Number.Length > 0 ? contact.Number : null);

        var result = new TaskCompletionSource<string?>();
        var menu = new ContextMenu { PlacementTarget = placement ?? ContactList, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var n in contact.Numbers)
        {
            var item = new MenuItem
            {
                Header = $"{n.TypeLabel}:  {n.Number}",
                Icon = new TextBlock { Text = n.TypeIcon, FontFamily = (FontFamily)FindResource("IconFont") },
            };
            item.Click += (_, _) => result.TrySetResult(n.Number);
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(() => result.TrySetResult(null));
        menu.IsOpen = true;
        return result.Task;
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
        if (SelectedContact is not { } c || RefuseSynced(c)) return;
        var dialog = new ContactWindow(c) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _contacts[_contacts.IndexOf(c)] = dialog.Contact;
        SaveContacts();
    }

    private void OnDeleteContact(object sender, RoutedEventArgs e)
    {
        if (SelectedContact is not { } c || RefuseSynced(c)) return;
        if (MessageBox.Show(this, $"Kontakt „{c.Name}“ löschen?", "Adressbuch",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _contacts.Remove(c);
        Avatars.Delete(c.Avatar);
        SaveContacts();
    }

    /// <summary>Synchronisierte Kontakte werden in Microsoft 365 gepflegt und sind hier schreibgeschützt.</summary>
    private bool RefuseSynced(Contact c)
    {
        if (!c.IsSynced) return false;
        MessageBox.Show(this, $"„{c.Name}“ stammt aus dem {c.SourceLabel} und wird dort gepflegt. " +
                              "Änderungen erscheinen hier bei der nächsten Synchronisierung.",
            "Adressbuch", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
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
        var dialog = new SettingsWindow(_settings, _m365) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _settings = dialog.Settings;
        _settings.DoNotDisturb = _phone.DoNotDisturb;
        Storage.SaveSettings(_settings);
        ApplySystemIntegration(_settings);
        RegisterHotkey();
        StatusText.Text = "Einstellungen gespeichert.";
        UpdateM365Button();
        await _phone.StartAsync(_settings);
        // Neue Anmeldung, geänderte Quellen oder Abmeldung sofort übernehmen.
        if (Microsoft365Service.IsConfigured(_settings)) StatusText.Text = await SyncMicrosoft365Async(manual: true);
    }

    private async void OnSyncM365(object sender, RoutedEventArgs e)
    {
        M365SyncButton.IsEnabled = false;
        StatusText.Text = "Microsoft 365 wird synchronisiert …";
        StatusText.Text = await SyncMicrosoft365Async(manual: true);
        M365SyncButton.IsEnabled = true;
    }

    private void UpdateM365Button() =>
        M365SyncButton.Visibility = Microsoft365Service.IsConfigured(_settings) ? Visibility.Visible : Visibility.Collapsed;

    // ---------------- Microsoft 365 ----------------

    public Microsoft365Service Microsoft365 => _m365;

    /// <summary>
    /// Kontakte aus Microsoft 365 abrufen und die synchronisierten Einträge im Adressbuch ersetzen.
    /// Automatisch nur still (ohne Anmeldedialog); liefert eine Statusmeldung.
    /// </summary>
    public async Task<string> SyncMicrosoft365Async(bool manual)
    {
        if (!Microsoft365Service.IsConfigured(_settings)) return "Microsoft 365 ist nicht eingerichtet.";
        if (!_settings.M365SyncDirectory && !_settings.M365SyncOutlook)
        {
            ReplaceSyncedContacts([]);
            return "Keine Quelle ausgewählt – synchronisierte Kontakte entfernt.";
        }
        if (_m365Syncing) return "Synchronisierung läuft bereits …";

        _m365Syncing = true;
        try
        {
            var result = await Task.Run(() => _m365.SyncAsync(_settings));
            ReplaceSyncedContacts(result.Contacts);
            _settings.M365LastSync = DateTime.Now;
            Storage.SaveSettings(_settings);
            var text = $"Microsoft 365: {result.Directory} Kolleg:innen, {result.Outlook} Outlook-Kontakte";
            if (manual) StatusText.Text = text;
            return text;
        }
        catch (Microsoft.Identity.Client.MsalUiRequiredException)
        {
            // Abgemeldet: synchronisierte Daten nicht auf dem Rechner lassen.
            if (!_settings.M365AutoSignIn) ReplaceSyncedContacts([]);
            return "Bitte bei Microsoft 365 anmelden (Einstellungen → Microsoft 365).";
        }
        catch (Exception ex)
        {
            // Automatisch still (z. B. offline); manuell mit Grund.
            var text = $"Microsoft 365: Synchronisierung fehlgeschlagen – {ex.Message}";
            if (manual) StatusText.Text = text;
            return text;
        }
        finally
        {
            _m365Syncing = false;
        }
    }

    private void ReplaceSyncedContacts(List<Contact> synced)
    {
        foreach (var old in _contacts.Where(c => c.IsSynced).ToList()) _contacts.Remove(old);
        foreach (var c in synced) _contacts.Add(c);
        Storage.SaveSyncedContacts(synced);
        _contactView.Refresh();
    }

    // ---------------- Updates ----------------

    /// <summary>Sucht nach einer neuen Version; liefert eine Meldung für die manuelle Suche im Info-Fenster.</summary>
    public async Task<string> CheckForUpdatesAsync(bool manual)
    {
        try
        {
            var info = await _updates.CheckAsync(_settings.UpdateUrl);
            if (info == null) return $"brandline {UpdateService.CurrentVersion.ToString(3)} ist aktuell.";

            _availableUpdate = info;
            UpdateTitle.Text = $"brandline {info.Version} ist verfügbar";
            UpdateDetail.Text = string.IsNullOrWhiteSpace(info.Notes) ? $"Installiert: {UpdateService.CurrentVersion.ToString(3)}" : info.Notes;
            UpdateDetail.ToolTip = info.Notes;
            UpdateButtons.Visibility = Visibility.Visible;
            UpdateBanner.Visibility = Visibility.Visible;
            if (_announcedVersion != info.Version && !manual)
            {
                _announcedVersion = info.Version;
                _tray.ShowBalloon("Update verfügbar", $"brandline {info.Version} kann installiert werden.");
            }
            return $"Version {info.Version} ist verfügbar – siehe Hinweis im Hauptfenster.";
        }
        catch (Exception ex)
        {
            // Automatische Suche bleibt still (z. B. offline); bei der manuellen Suche Grund nennen.
            return $"Suche nach Updates nicht möglich: {ex.Message}";
        }
    }

    private void OnUpdateLater(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    private async void OnUpdateInstall(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not { } info) return;
        if (_phone.State != CallState.Idle)
        {
            StatusText.Text = "Das Update kann erst nach dem Gespräch installiert werden.";
            return;
        }

        UpdateButtons.Visibility = Visibility.Collapsed;
        try
        {
            var progress = new Progress<double>(p => UpdateDetail.Text = $"Wird heruntergeladen … {p:P0}");
            var msi = await _updates.DownloadAsync(_settings.UpdateUrl, info, progress);
            UpdateDetail.Text = "Installation startet – brandline wird danach neu gestartet.";
            UpdateService.StartInstall(msi);
            // Sauber abmelden und beenden; das Installationsprogramm startet brandline anschließend neu.
            await ExitAsync(force: true);
        }
        catch (Exception ex)
        {
            UpdateDetail.Text = ex.Message;
            UpdateButtons.Visibility = Visibility.Visible;
        }
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
        new AboutWindow(() => CheckForUpdatesAsync(manual: true)) { Owner = this }.ShowDialog();
    }

    public static void ApplySystemIntegration(AppSettings settings)
    {
        try
        {
            SystemIntegration.SetAutostart(settings.StartWithWindows);
            SystemIntegration.SetTelLinkRegistration(settings.RegisterTelLinks);
            SystemIntegration.RegisterOwnScheme();
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
        _updateTimer.Stop();
        _updates.Dispose();
        _m365Timer.Stop();
        _m365.Dispose();
        await _phone.DisposeAsync();
        _tray.Dispose();
        Close();
        Application.Current.Shutdown();
    }
}
