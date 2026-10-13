using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Brandline.Models;
using Brandline.Services.Audio;
using Microsoft.Win32;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace Brandline.Services;

public enum RegistrationState
{
    Offline,
    Registering,
    Registered,
    Failed
}

public enum CallState
{
    Idle,
    Dialing,
    RemoteRinging,
    Incoming,
    Connected
}

public sealed record CallParty(string Number, string DisplayName);

public sealed record CallQuality(double JitterMs, double LossPercent, int BufferMs, bool EchoCancellation, bool Encrypted);

/// <summary>Stand der Mailbox laut Server (SIP MWI).</summary>
public sealed record VoicemailStatus(bool Waiting, int New, int Old, string Account);

/// <summary>
/// Kapselt SIP-Registrierung und bis zu zwei gleichzeitige Gespräche (aktiv + gehalten bzw. anklopfend). Hält die Verbindung zum Server
/// aktiv (Keepalive) und meldet sich nach Netzwechsel oder Standby automatisch neu an.
/// Alle Ereignisse werden auf Hintergrund-Threads ausgelöst.
/// </summary>
public sealed class SipPhone : IAsyncDisposable
{
    private static readonly byte[] KeepAlivePayload = Encoding.ASCII.GetBytes("\r\n\r\n");
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(4);

    private readonly object _sync = new();
    private readonly TonePlayer _tones = new();
    private readonly SemaphoreSlim _startStop = new(1, 1);
    private readonly Timer _reconnectTimer;
    private Timer? _keepAliveTimer;

    private AppSettings _settings = new();
    private SIPTransport? _transport;
    private SIPChannel? _signalingChannel;
    private SIPEndPoint? _serverEndPoint;
    private SIPRegistrationUserAgent? _registration;
    private IPAddress? _publicIp;
    private bool _reconnectPending;

    private const int MaxCalls = 2;
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(30);
    private readonly List<PhoneCall> _calls = [];
    private SIPNotifierClient? _voicemailClient;
    private StreamWriter? _sipLog;

    public SipPhone()
    {
        _reconnectTimer = new Timer(_ => _ = ReconnectAsync());
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public event Action<RegistrationState, string>? RegistrationChanged;
    /// <summary>Gespräche haben sich geändert; Text = kurze Meldung für die Statuszeile.</summary>
    public event Action<string>? CallsChanged;
    /// <summary>Neuer eingehender Anruf; true = Anklopfen (während eines aktiven Gesprächs).</summary>
    public event Action<PhoneCall, bool>? IncomingCall;
    public event Action<VoicemailStatus>? VoicemailChanged;
    public event Action<CallRecord>? CallEnded;
    public event Action<string>? Info;
    public event Action<string>? Error;

    public bool DoNotDisturb { get; set; }
    public IPAddress? PublicIp => _publicIp;

    /// <summary>Signalisierung per TLS und Sprache per SRTP.</summary>
    public bool IsEncrypted => _settings.Transport == SipTransportKind.Tls;

    /// <summary>Nur für automatisierte Tests mit selbstsignierten Zertifikaten – in der App immer null.</summary>
    public static RemoteCertificateValidationCallback? CertificateValidationOverride { get; set; }


    // ---------------- Registrierung ----------------

    public async Task StartAsync(AppSettings settings)
    {
        await _startStop.WaitAsync();
        try
        {
            await StopCoreAsync();
            _settings = settings.Clone();
            DoNotDisturb = settings.DoNotDisturb;

            if (!settings.IsComplete)
            {
                RegistrationChanged?.Invoke(RegistrationState.Offline, "Bitte SIP-Zugangsdaten in den Einstellungen eintragen.");
                return;
            }

            var transport = new SIPTransport();
            _signalingChannel = settings.Transport switch
            {
                // TLS: Serverzertifikat streng gegen den Windows-Zertifikatsspeicher prüfen.
                SipTransportKind.Tls => new SIPTLSChannel(new IPEndPoint(IPAddress.Any, 0), false, ValidateServerCertificate),
                SipTransportKind.Tcp => new SIPTCPChannel(new IPEndPoint(IPAddress.Any, 0)),
                _ => new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0)),
            };
            transport.AddSIPChannel(_signalingChannel);
            transport.SIPTransportRequestReceived += OnTransportRequest;
            if (settings.EnableSipLog) AttachSipLog(transport);
            _transport = transport;

            // Contact mit Benutzerteil (sip:777…@ip:port) wie bei üblichen Softphones; ohne ihn stellt
            // Placetel eingehende Anrufe nicht zu, obwohl die Registrierung erfolgreich ist.
            var registration = new SIPRegistrationUserAgent(
                transport, settings.SipUser, settings.Password, RegistrarHost(), settings.RegisterExpirySeconds,
                sendUsernameInContactHeader: true);
            registration.RegistrationSuccessful += (_, _) =>
                RegistrationChanged?.Invoke(RegistrationState.Registered,
                    $"Angemeldet als {settings.SipUser}" + (IsEncrypted ? " · verschlüsselt" : ""));
            registration.RegistrationTemporaryFailure += (_, response, message) =>
                RegistrationChanged?.Invoke(RegistrationState.Failed, DescribeRegistrationError(response, message) + " – neuer Versuch läuft …");
            registration.RegistrationFailed += (_, response, message) =>
                RegistrationChanged?.Invoke(RegistrationState.Failed, DescribeRegistrationError(response, message));
            _registration = registration;

            RegistrationChanged?.Invoke(RegistrationState.Registering, $"Verbinde mit {settings.Server} …");
            registration.Start();
            StartVoicemailSubscription(transport);

            // Keepalive auch bei TCP/TLS: hält die Verbindung im Router offen, über die Placetel Anrufe zustellt.
            if (settings.KeepAliveSeconds > 0)
            {
                var period = TimeSpan.FromSeconds(settings.KeepAliveSeconds);
                _keepAliveTimer = new Timer(_ => _ = SendKeepAliveAsync(), null, period, period);
            }
            if (settings.UseStun) _ = DetectPublicIpAsync(settings.StunServer);
        }
        finally
        {
            _startStop.Release();
        }
    }

    public async Task StopAsync()
    {
        await _startStop.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _startStop.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        HangupAll();
        _voicemailClient?.Stop();
        _voicemailClient = null;

        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;

        var registration = _registration;
        var transport = _transport;
        _registration = null;
        _transport = null;
        _signalingChannel = null;
        _serverEndPoint = null;
        _publicIp = null;

        if (registration != null)
        {
            registration.Stop(sendZeroExpiryRegister: true);
            // Dem Abmelde-REGISTER kurz Zeit geben, bevor der Transport geschlossen wird.
            await Task.Delay(400);
        }
        if (transport != null)
        {
            transport.SIPTransportRequestReceived -= OnTransportRequest;
            transport.Shutdown();
        }
        _sipLog?.Dispose();
        _sipLog = null;

        RegistrationChanged?.Invoke(RegistrationState.Offline, "Nicht angemeldet");
    }

    private string RegistrarHost() => _settings.Transport switch
    {
        // Bei TLS Port immer angeben (Placetel: 5061).
        SipTransportKind.Tls => $"{_settings.Server}:{_settings.Port};transport=tls",
        SipTransportKind.Tcp => (_settings.Port == 5060 ? _settings.Server : $"{_settings.Server}:{_settings.Port}") + ";transport=tcp",
        _ => _settings.Port == 5060 ? _settings.Server : $"{_settings.Server}:{_settings.Port}",
    };

    private bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (CertificateValidationOverride != null) return CertificateValidationOverride(sender, certificate, chain, errors);
        if (errors == SslPolicyErrors.None) return true;

        // Verbindung ablehnen – sonst könnte sich jemand im Netz als Placetel ausgeben.
        var detail = errors switch
        {
            SslPolicyErrors.RemoteCertificateNameMismatch => "ausgestellt für einen anderen Servernamen",
            SslPolicyErrors.RemoteCertificateChainErrors => "nicht vertrauenswürdig oder abgelaufen",
            SslPolicyErrors.RemoteCertificateNotAvailable => "kein Zertifikat erhalten",
            _ => errors.ToString()
        };
        Error?.Invoke($"Sicherheitszertifikat von {_settings.Server} abgelehnt ({detail}) – keine Verbindung.");
        LogLine($"TLS: Zertifikat abgelehnt ({errors}) – {certificate?.Subject}");
        return false;
    }

    private static string DescribeRegistrationError(SIPResponse? response, string? message)
    {
        if (response == null) return string.IsNullOrWhiteSpace(message) ? "Server nicht erreichbar" : message;
        return response.StatusCode switch
        {
            401 or 403 or 407 => "Anmeldung abgelehnt – SIP-Benutzername oder Passwort prüfen",
            404 => "SIP-Benutzer beim Anbieter unbekannt",
            _ => $"Anmeldung fehlgeschlagen ({response.StatusCode} {response.ReasonPhrase})"
        };
    }

    // ---------------- Verbindung stabil halten ----------------

    /// <summary>
    /// Hält die NAT-Zuordnung im Router offen, damit eingehende Anrufe auch nach Minuten ohne
    /// Verkehr noch ankommen (RFC 5626, CRLF-Keepalive).
    /// </summary>
    private async Task SendKeepAliveAsync()
    {
        try
        {
            var transport = _transport;
            var channel = _signalingChannel;
            if (transport == null || channel == null) return;

            _serverEndPoint ??= await transport.ResolveSIPUriAsync(SIPURI.ParseSIPURIRelaxed("sip:" + RegistrarHost()));
            if (_serverEndPoint == null) return;
            await transport.SendRawAsync(channel.ListeningSIPEndPoint, _serverEndPoint, KeepAlivePayload);
        }
        catch
        {
            // Beim nächsten Mal neu auflösen (z. B. nach Netzwechsel).
            _serverEndPoint = null;
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => ScheduleReconnect("Netzwerk geändert");

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) ScheduleReconnect("Aus dem Standby aufgewacht");
    }

    private void ScheduleReconnect(string reason)
    {
        if (!_settings.IsComplete) return;
        Info?.Invoke($"{reason} – melde neu an …");
        // Mehrere Ereignisse kurz hintereinander zusammenfassen; dem Netz Zeit geben, sich einzurichten.
        _reconnectTimer.Change(ReconnectDelay, Timeout.InfiniteTimeSpan);
    }

    private async Task ReconnectAsync()
    {
        if (State != CallState.Idle)
        {
            // Laufendes Gespräch nicht abbrechen – danach neu anmelden.
            _reconnectPending = true;
            return;
        }
        _reconnectPending = false;
        try
        {
            // Zwischenzeitlich umgeschaltetes „Nicht stören“ beibehalten.
            var settings = _settings.Clone();
            settings.DoNotDisturb = DoNotDisturb;
            await StartAsync(settings);
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Neuanmeldung fehlgeschlagen: {ex.Message}");
        }
    }

    private async Task DetectPublicIpAsync(string stunServer)
    {
        try
        {
            var parts = stunServer.Split(':');
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 3478;
            var ip = await Task.Run(() => STUNClient.GetPublicIPAddress(parts[0], port));
            _publicIp = ip;
            Info?.Invoke(ip != null ? $"Öffentliche IP (STUN): {ip}" : "STUN: keine Antwort");
        }
        catch (Exception ex)
        {
            Info?.Invoke($"STUN fehlgeschlagen: {ex.Message}");
        }
    }

    // ---------------- Anrufe: Übersicht ----------------

    /// <summary>Alle Gespräche (höchstens zwei): aktiv, gehalten, klingelnd/anklopfend.</summary>
    public IReadOnlyList<PhoneCall> Calls
    {
        get { lock (_sync) return _calls.ToList(); }
    }

    /// <summary>Das Gespräch, das gerade Mikrofon und Lautsprecher hat (nicht gehalten, nicht klingelnd).</summary>
    public PhoneCall? ActiveCall
    {
        get { lock (_sync) return _calls.FirstOrDefault(c => !c.IsOnHold && !c.IsRinging); }
    }

    public PhoneCall? HeldCall
    {
        get { lock (_sync) return _calls.FirstOrDefault(c => c.IsOnHold); }
    }

    public PhoneCall? RingingCall
    {
        get { lock (_sync) return _calls.FirstOrDefault(c => c.IsRinging); }
    }

    /// <summary>Zusammengefasster Zustand (für einfache Abfragen wie „läuft gerade ein Gespräch?“).</summary>
    public CallState State
    {
        get
        {
            lock (_sync)
            {
                if (_calls.Count == 0) return CallState.Idle;
                return _calls.FirstOrDefault(c => !c.IsOnHold && !c.IsRinging)?.State
                       ?? (_calls.Any(c => c.IsRinging) ? CallState.Incoming : CallState.Connected);
            }
        }
    }

    public bool IsMuted => ActiveCall?.IsMuted ?? false;
    public CallQuality? Quality => ActiveCall?.Quality;

    // ---------------- Ausgehende Anrufe ----------------

    /// <summary>
    /// Wählt eine Nummer. Läuft bereits ein Gespräch, wird es automatisch gehalten (Rückfrage).
    /// </summary>
    /// <param name="anonymous">Eigene Rufnummer unterdrücken (SIP-Header „Privacy: id“).</param>
    public async Task<PhoneCall?> DialAsync(string input, bool anonymous = false)
    {
        var number = PhoneNumber.ForDialing(input);
        if (number.Length == 0) return null;

        PhoneCall call;
        PhoneCall? toHold;
        lock (_sync)
        {
            if (_transport == null || _calls.Count >= MaxCalls || _calls.Any(c => c.IsRinging)) return null;
            toHold = _calls.FirstOrDefault(c => !c.IsOnHold);
            call = new PhoneCall(new SIPUserAgent(_transport, null), CallDirection.Outgoing, new CallParty(number, ""))
            {
                Anonymous = anonymous,
                Info = "Wähle …",
                ConsultationFor = toHold,
            };
            _calls.Add(call);
        }
        if (toHold != null) Hold(toHold);
        WireCall(call);

        var ua = call.Ua;
        var target = $"sip:{number}@{RegistrarHost()}";
        var from = string.IsNullOrWhiteSpace(_settings.DisplayName) || anonymous
            ? $"<sip:{_settings.SipUser}@{_settings.Server}>"
            : $"\"{_settings.DisplayName.Replace("\"", "")}\" <sip:{_settings.SipUser}@{_settings.Server}>";
        // RFC 3323/3325: Der Anbieter soll die Rufnummer gegenüber dem Angerufenen nicht anzeigen.
        var headers = anonymous ? new List<string> { "Privacy: id" } : null;

        var descriptor = new SIPCallDescriptor(
            _settings.SipUser, _settings.Password, target, from, target,
            null, headers, null, SIPCallDirection.Out, SDP.SDP_MIME_CONTENTTYPE, null, null);

        ua.ClientCallTrying += (_, _) => Update(call, CallState.Dialing, "Verbinde …");
        ua.ClientCallRinging += (_, response) =>
        {
            // Ohne Early Media (183 mit SDP) spielen wir den Freiton selbst.
            call.LocalRingback = string.IsNullOrEmpty(response?.Body);
            Update(call, CallState.RemoteRinging, "Es klingelt …");
        };
        ua.ClientCallFailed += (_, error, response) => EndCall(call, DescribeCallFailure(response, error));

        Changed(call.Info);
        bool answered;
        try
        {
            answered = await ua.Call(descriptor, CreateMedia(call), 120);
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Anruf fehlgeschlagen: {ex.Message}");
            answered = false;
        }

        if (answered)
        {
            call.AnsweredAt = DateTime.Now;
            call.LocalRingback = false;
            Update(call, CallState.Connected, "Verbunden");
        }
        else
        {
            EndCall(call, "Nicht verbunden");
        }
        return call;
    }

    private static string DescribeCallFailure(SIPResponse? response, string? error)
    {
        if (response == null) return string.IsNullOrWhiteSpace(error) ? "Nicht verbunden" : error;
        return response.StatusCode switch
        {
            404 or 484 => "Nummer nicht erreichbar",
            480 => "Teilnehmer nicht erreichbar",
            486 or 600 => "Besetzt",
            487 => "Abgebrochen",
            403 => "Anruf vom Anbieter abgelehnt",
            603 => "Abgelehnt",
            _ => $"{response.StatusCode} {response.ReasonPhrase}"
        };
    }

    // ---------------- Eingehende Anrufe ----------------

    private Task OnTransportRequest(SIPEndPoint localEndPoint, SIPEndPoint remoteEndPoint, SIPRequest request)
    {
        // Anfragen innerhalb eines Dialogs (BYE, re-INVITE, REFER-NOTIFY …) bearbeitet der SIPUserAgent des Anrufs.
        if (!string.IsNullOrEmpty(request.Header.To?.ToTag)) return Task.CompletedTask;

        switch (request.Method)
        {
            case SIPMethodsEnum.INVITE:
                HandleIncomingInvite(request);
                break;
            case SIPMethodsEnum.OPTIONS:
                // Erreichbarkeitsprüfung des Servers beantworten.
                _ = _transport?.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null));
                break;
            case SIPMethodsEnum.NOTIFY when request.Header.Event?.StartsWith("message-summary", StringComparison.OrdinalIgnoreCase) == true:
                // Manche Anlagen melden neue Sprachnachrichten ohne vorheriges SUBSCRIBE.
                OnVoicemailNotification(request.Body);
                _ = _transport?.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null));
                break;
        }
        return Task.CompletedTask;
    }

    private void HandleIncomingInvite(SIPRequest request)
    {
        var caller = ReadCaller(request);
        PhoneCall call;
        bool waiting;

        lock (_sync)
        {
            if (_transport == null) return;

            string? rejectNote = null;
            if (DoNotDisturb) rejectNote = "Nicht stören";
            else if (_calls.Count >= MaxCalls || _calls.Any(c => c.IsRinging)) rejectNote = "besetzt (zwei Gespräche)";

            if (rejectNote != null)
            {
                // Besetzt melden – Placetel leitet dann gemäß Routing weiter (z. B. Mailbox).
                var busyTx = new UASInviteTransaction(_transport, request, null, false);
                busyTx.SendFinalResponse(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.BusyHere, null));
                var missed = !DoNotDisturb;
                CallEnded?.Invoke(new CallRecord
                {
                    Direction = CallDirection.Incoming,
                    Outcome = missed ? CallOutcome.Missed : CallOutcome.Rejected,
                    Number = caller.Number,
                    Name = caller.DisplayName,
                    Start = DateTime.Now,
                    Note = rejectNote,
                    Unseen = missed,
                });
                return;
            }

            // Anklopfen, wenn bereits ein Gespräch aktiv ist (nicht nur gehalten).
            waiting = _calls.Any(c => !c.IsOnHold);
            call = new PhoneCall(new SIPUserAgent(_transport, null), CallDirection.Incoming, caller)
            {
                State = CallState.Incoming,
                Info = waiting ? "Anklopfen" : "Eingehender Anruf",
            };
            _calls.Add(call);
        }

        WireCall(call);
        call.Ua.ServerCallCancelled += (_, _) => EndCall(call, "Verpasster Anruf");
        call.PendingIncoming = call.Ua.AcceptCall(request);
        Changed(call.Info);
        IncomingCall?.Invoke(call, waiting);
    }

    private static CallParty ReadCaller(SIPRequest request)
    {
        // P-Asserted-Identity enthält bei Placetel/Carriern oft die geprüfte Nummer.
        var pai = request.Header.UnknownHeaders
            .FirstOrDefault(h => h.StartsWith("P-Asserted-Identity:", StringComparison.OrdinalIgnoreCase));
        SIPUserField? asserted = null;
        if (pai != null)
        {
            try { asserted = SIPUserField.ParseSIPUserField(pai[(pai.IndexOf(':') + 1)..].Trim()); }
            catch { /* ungültiger Header – ignorieren */ }
        }

        var from = request.Header.From;
        var number = asserted?.URI?.User ?? from?.FromURI?.User ?? "";
        if (number.Equals("anonymous", StringComparison.OrdinalIgnoreCase)) number = "";
        var name = (from?.FromName ?? asserted?.Name ?? "").Trim('"', ' ');
        if (name == number) name = "";
        return new CallParty(number, name);
    }

    /// <summary>Klingelnden/anklopfenden Anruf annehmen; ein laufendes Gespräch wird dabei gehalten.</summary>
    public async Task AnswerAsync(PhoneCall? call = null)
    {
        call ??= RingingCall;
        if (call?.PendingIncoming is not { } uas) return;
        call.PendingIncoming = null;

        var active = ActiveCall;
        if (active != null) Hold(active);

        Update(call, CallState.Dialing, "Nehme an …");
        bool ok;
        try
        {
            ok = await call.Ua.Answer(uas, CreateMedia(call));
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Annehmen fehlgeschlagen: {ex.Message}");
            ok = false;
        }

        if (ok)
        {
            call.AnsweredAt = DateTime.Now;
            Update(call, CallState.Connected, "Verbunden");
        }
        else
        {
            EndCall(call, "Annehmen fehlgeschlagen");
        }
    }

    // ---------------- Während des Gesprächs ----------------

    /// <summary>
    /// Auflegen bzw. ablehnen. Ohne Angabe: das aktive Gespräch, sonst der klingelnde Anruf.
    /// Ein gehaltenes Gespräch bleibt gehalten (der Benutzer setzt es bewusst fort).
    /// </summary>
    public void Hangup(PhoneCall? call = null)
    {
        call ??= ActiveCall ?? RingingCall;
        if (call == null) return;

        var incoming = call.PendingIncoming;
        call.PendingIncoming = null;
        Action? signal = null;
        if (incoming != null)
        {
            call.RejectedByUser = true;
            signal = () => incoming.Reject(SIPResponseStatusCodesEnum.BusyHere, null);
        }
        else if (call.Ua.IsCallActive)
        {
            // LocalHangup: OnCallHungup soll das nicht als Auflegen der Gegenseite melden.
            call.LocalHangup = true;
            signal = call.Ua.Hangup;
        }
        else if (call.Ua.IsCalling || call.Ua.IsRinging)
        {
            signal = call.Ua.Cancel;
        }
        EndCall(call, incoming != null ? "Abgelehnt" : "Aufgelegt", signal);
    }

    private void HangupAll()
    {
        foreach (var call in Calls) Hangup(call);
    }

    /// <summary>Gespräch halten: Gegenseite hört Wartemusik/Stille, Mikrofon und Lautsprecher werden frei.</summary>
    public void Hold(PhoneCall call)
    {
        if (call.IsOnHold || !call.Ua.IsCallActive) return;
        call.Ua.PutOnHold();
        call.Audio?.SuspendDevices();
        call.IsOnHold = true;
        call.Info = "Gehalten";
        Changed($"{Describe(call)} gehalten");
    }

    /// <summary>Gehaltenes Gespräch fortsetzen; ein anderes aktives Gespräch wird dabei gehalten (Makeln).</summary>
    public void Resume(PhoneCall call)
    {
        if (!call.IsOnHold) return;
        var other = ActiveCall;
        if (other != null && other != call)
        {
            if (other.IsConnected) Hold(other);
            else return; // Verbindungsaufbau läuft noch – nicht dazwischenfunken
        }
        call.Audio?.ResumeDevices();
        call.Ua.TakeOffHold();
        call.IsOnHold = false;
        call.Info = "Verbunden";
        Changed($"{Describe(call)} fortgesetzt");
    }

    /// <summary>Zwischen aktivem und gehaltenem Gespräch wechseln.</summary>
    public void Swap()
    {
        if (HeldCall is { } held) Resume(held);
    }

    /// <summary>Halten des aktiven Gesprächs ein-/ausschalten (eine Leitung).</summary>
    public void ToggleHold()
    {
        if (ActiveCall is { IsConnected: true } active) Hold(active);
        else if (HeldCall is { } held) Resume(held);
    }

    /// <summary>Klingelton abschalten, der Anruf klingelt beim Anrufer weiter.</summary>
    public void SilenceRinging()
    {
        _silenced = true;
        _tones.Stop();
        _tone = ToneKind.None;
    }

    public void ToggleMute()
    {
        if (ActiveCall?.Audio is { } audio)
        {
            audio.IsMuted = !audio.IsMuted;
            Changed(audio.IsMuted ? "Stummgeschaltet" : "Mikrofon an");
        }
    }

    public void SendDtmf(char key)
    {
        if (ActiveCall is not { IsConnected: true } call) return;
        byte? tone = key switch
        {
            >= '0' and <= '9' => (byte)(key - '0'),
            '*' => 10,
            '#' => 11,
            _ => null
        };
        if (tone != null) _ = call.Ua.SendDtmf(tone.Value);
    }

    // ---------------- Weiterleiten ----------------

    /// <summary>
    /// Direkt weiterleiten (Blind Transfer, SIP REFER): die Gegenseite wird mit der Zielnummer verbunden,
    /// unser Gespräch endet. Liefert eine Fehlermeldung oder null bei Erfolg.
    /// </summary>
    public async Task<string?> BlindTransferAsync(PhoneCall call, string input)
    {
        var number = PhoneNumber.ForDialing(input);
        if (number.Length == 0) return "Bitte eine Zielnummer angeben.";
        if (!call.IsConnected) return "Nur ein verbundenes Gespräch kann weitergeleitet werden.";

        if (call.IsOnHold) Resume(call);
        Update(call, call.State, $"Leite weiter an {number} …");
        try
        {
            var destination = SIPURI.ParseSIPURIRelaxed($"sip:{number}@{RegistrarHost()}");
            var ok = await call.Ua.BlindTransfer(destination, TransferTimeout, CancellationToken.None,
                null, _settings.SipUser, _settings.Password);
            if (!ok) return FailTransfer(call, "Weiterleiten fehlgeschlagen – das Ziel hat nicht angenommen.");

            call.TransferredTo = number;
            call.LocalHangup = true;
            EndCall(call, $"Weitergeleitet an {number}", call.Ua.IsCallActive ? call.Ua.Hangup : null);
            return null;
        }
        catch (Exception ex)
        {
            return FailTransfer(call, $"Weiterleiten fehlgeschlagen: {ex.Message}");
        }
    }

    /// <summary>
    /// Mit Rückfrage weiterleiten (Attended Transfer): das gehaltene Gespräch wird mit dem aktiven
    /// Rückfragegespräch verbunden (REFER mit Replaces), beide Leitungen von uns enden.
    /// </summary>
    public async Task<string?> CompleteAttendedTransferAsync()
    {
        var held = HeldCall;
        var consult = ActiveCall;
        if (held == null || consult is not { IsConnected: true }) return "Für das Verbinden werden ein gehaltenes und ein aktives Gespräch benötigt.";

        Update(consult, consult.State, "Verbinde beide Gespräche …");
        try
        {
            var ok = await held.Ua.AttendedTransfer(consult.Ua.Dialogue, TransferTimeout, CancellationToken.None,
                null, _settings.SipUser, _settings.Password);
            if (!ok) return FailTransfer(consult, "Verbinden fehlgeschlagen – bitte erneut versuchen oder direkt weiterleiten.");

            held.TransferredTo = consult.Remote.Number;
            consult.TransferredTo = held.Remote.Number;
            foreach (var call in new[] { held, consult })
            {
                call.LocalHangup = true;
                EndCall(call, "Verbunden und übergeben", call.Ua.IsCallActive ? call.Ua.Hangup : null);
            }
            return null;
        }
        catch (Exception ex)
        {
            return FailTransfer(consult, $"Verbinden fehlgeschlagen: {ex.Message}");
        }
    }

    private string FailTransfer(PhoneCall call, string message)
    {
        Update(call, call.State, call.IsConnected ? "Verbunden" : call.Info);
        Error?.Invoke(message);
        return message;
    }

    // ---------------- Sprachnachrichten (MWI) ----------------

    public VoicemailStatus Voicemail { get; private set; } = new(false, 0, 0, "");

    private void StartVoicemailSubscription(SIPTransport transport)
    {
        try
        {
            var aor = SIPURI.ParseSIPURIRelaxed($"sip:{_settings.SipUser}@{RegistrarHost()}");
            var client = new SIPNotifierClient(transport, null, SIPEventPackagesEnum.MessageSummary, aor,
                _settings.SipUser, _settings.Server, _settings.Password, 3600, null);
            client.NotificationReceived += (_, body) => OnVoicemailNotification(body);
            client.SubscriptionFailed += (_, status, reason) => LogLine($"MWI: Abonnement abgelehnt ({(int)status} {reason})");
            client.Start();
            _voicemailClient = client;
        }
        catch (Exception ex)
        {
            LogLine($"MWI: {ex.Message}");
        }
    }

    /// <summary>
    /// RFC 3842 „simple-message-summary“, z. B.:
    /// Messages-Waiting: yes / Message-Account: sip:*55@fpbx.de / Voice-Message: 2/8 (0/0)
    /// </summary>
    private void OnVoicemailNotification(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        bool waiting = false;
        int newCount = 0, oldCount = 0;
        var account = "";
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Messages-Waiting", StringComparison.OrdinalIgnoreCase))
                waiting = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
            else if (name.Equals("Message-Account", StringComparison.OrdinalIgnoreCase))
                account = value;
            else if (name.Equals("Voice-Message", StringComparison.OrdinalIgnoreCase))
            {
                var counts = value.Split(' ')[0].Split('/');
                if (counts.Length == 2) { int.TryParse(counts[0], out newCount); int.TryParse(counts[1], out oldCount); }
            }
        }
        if (waiting && newCount == 0) newCount = 1; // „yes“ ohne Anzahl
        Voicemail = new VoicemailStatus(waiting, newCount, oldCount, account);
        VoicemailChanged?.Invoke(Voicemail);
    }

    /// <summary>Nummer zum Abhören der Mailbox: Einstellung, sonst die vom Server gemeldete Adresse.</summary>
    public string? VoicemailDialTarget()
    {
        if (!string.IsNullOrWhiteSpace(_settings.VoicemailNumber)) return _settings.VoicemailNumber;
        if (Voicemail.Account.Length == 0) return null;
        try { return SIPURI.ParseSIPURIRelaxed(Voicemail.Account).User; }
        catch { return null; }
    }

    // ---------------- Interna ----------------

    private void WireCall(PhoneCall call)
    {
        call.Ua.OnCallHungup += _ => EndCall(call, call.LocalHangup ? "Aufgelegt"
            : call.TransferredTo != null ? $"Weitergeleitet an {call.TransferredTo}" : "Gegenseite hat aufgelegt");
        call.Ua.RemotePutOnHold += () => { call.IsHeldByRemote = true; Changed($"{Describe(call)} hat gehalten"); };
        call.Ua.RemoteTookOffHold += () => { call.IsHeldByRemote = false; Changed($"{Describe(call)} ist zurück"); };
    }

    private VoIPMediaSession CreateMedia(PhoneCall call)
    {
        var audio = new CallAudio(_settings.MicrophoneDevice, _settings.SpeakerDevice, _settings.EchoCancellation);
        audio.OnAudioSourceError += msg => Error?.Invoke(msg);
        audio.OnAudioSinkError += msg => Error?.Invoke(msg);
        audio.Warning += msg => Info?.Invoke(msg);

        var config = new VoIPMediaSessionConfig
        {
            MediaEndPoint = new MediaEndPoints { AudioSource = audio, AudioSink = audio },
            // SRTP nur zusammen mit TLS: der Schlüssel steht im SDP und wäre sonst mitlesbar.
            RtpSecureMediaOption = IsEncrypted ? RtpSecureMediaOptionEnum.SdpCryptoNegotiation : RtpSecureMediaOptionEnum.None,
        };
        var media = new PhoneMediaSession(config, () => _publicIp)
        {
            // Hinter NAT kommt RTP oft von einer anderen Adresse als im SDP angegeben.
            AcceptRtpFromAny = true
        };
        media.OnRtpPacketReceived += (_, mediaType, packet) =>
        {
            if (mediaType == SDPMediaTypesEnum.audio) audio.ReceiveRtp(packet);
        };

        call.Audio = audio;
        call.Media = media;
        return media;
    }

    /// <param name="signal">
    /// SIP-Abschluss (BYE/CANCEL/Ablehnen). Läuft wie das Freigeben der Audiogeräte im Hintergrund:
    /// SIPSorcery wartet beim Auflegen auf die Bestätigung der Gegenseite (bis zu 30 s) – das Fenster
    /// soll dabei nicht hängen.
    /// </param>
    private void EndCall(PhoneCall call, string? reason, Action? signal = null)
    {
        lock (_sync)
        {
            if (!_calls.Remove(call)) return;
            foreach (var other in _calls.Where(c => c.ConsultationFor == call)) other.ConsultationFor = null;
        }

        // Reihenfolge einhalten (erst BYE, dann Medien schließen): Parallel dazu blockiert SIPSorcery
        // sonst den Empfang, wenn gerade noch eine Antwort auf ein Re-INVITE (Halten/Fortsetzen) eintrifft.
        _ = Task.Run(() =>
        {
            try { signal?.Invoke(); }
            catch (Exception ex) { LogLine($"Auflegen {call.Remote.Number}: {ex.Message}"); }
            try { call.Media?.Close("Anruf beendet"); } catch { /* bereits geschlossen */ }
            try { call.Audio?.Dispose(); } catch (Exception ex) { LogLine($"Audio freigeben: {ex.Message}"); }
            // Close() statt Dispose(): Dispose() wartet in SIPSorcery 10 auf ein Semaphor, das nach
            // ausgehenden Anrufen nicht freigegeben wird, und blockiert dann dauerhaft.
            call.Ua.Close();
        });

        var outcome = call.AnsweredAt != null
            ? CallOutcome.Answered
            : call.Direction == CallDirection.Outgoing ? CallOutcome.NotReached
            : call.RejectedByUser ? CallOutcome.Rejected : CallOutcome.Missed;
        CallEnded?.Invoke(new CallRecord
        {
            Direction = call.Direction,
            Outcome = outcome,
            Number = call.Remote.Number,
            Name = call.Remote.DisplayName,
            Start = call.Start,
            Duration = call.AnsweredAt is { } at ? DateTime.Now - at : TimeSpan.Zero,
            Note = call.TransferredTo != null ? $"weitergeleitet an {call.TransferredTo}"
                : outcome == CallOutcome.NotReached ? reason ?? "" : "",
            Unseen = outcome == CallOutcome.Missed,
        });

        call.State = CallState.Idle;
        call.Info = reason ?? "";
        var remaining = Calls;
        Changed(remaining.Count > 0 && remaining.All(c => c.IsOnHold)
            ? $"{reason} – {Describe(remaining[0])} wartet (gehalten)"
            : reason ?? "");

        if (remaining.Count == 0 && _reconnectPending) _reconnectTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    private void Update(PhoneCall call, CallState state, string info)
    {
        call.State = state;
        call.Info = info;
        Changed(info);
    }

    /// <summary>Gesprächsliste hat sich geändert – Töne anpassen und Oberfläche benachrichtigen.</summary>
    private void Changed(string info)
    {
        UpdateTones();
        CallsChanged?.Invoke(info);
    }

    private enum ToneKind { None, Ring, CallWaiting, Ringback }
    private ToneKind _tone;
    private bool _silenced;

    /// <summary>Klingelton, Anklopfton oder Freiton passend zum aktuellen Zustand – nie mehrere gleichzeitig.</summary>
    private void UpdateTones()
    {
        var calls = Calls;
        var ringing = calls.FirstOrDefault(c => c.IsRinging);
        var hasActive = calls.Any(c => !c.IsOnHold && !c.IsRinging);
        var wanted = ringing != null
            ? (hasActive ? ToneKind.CallWaiting : ToneKind.Ring)
            : calls.Any(c => !c.IsOnHold && c.State == CallState.RemoteRinging && c.LocalRingback) ? ToneKind.Ringback
            : ToneKind.None;

        if (ringing == null) _silenced = false;
        if (_silenced && wanted is ToneKind.Ring or ToneKind.CallWaiting) wanted = ToneKind.None;
        if (wanted == _tone) return;

        _tone = wanted;
        switch (wanted)
        {
            case ToneKind.Ring: _tones.PlayRing(_settings.RingDevice, Ringtones.PickForCall(_settings.RingTone, _settings.RandomRingTones)); break;
            case ToneKind.CallWaiting: _tones.PlayCallWaiting(_settings.SpeakerDevice); break;
            case ToneKind.Ringback: _tones.PlayRingback(_settings.SpeakerDevice); break;
            default: _tones.Stop(); break;
        }
    }

    private static string Describe(PhoneCall call) =>
        string.IsNullOrWhiteSpace(call.Remote.DisplayName)
            ? (call.Remote.Number.Length > 0 ? call.Remote.Number : "Anrufer")
            : call.Remote.DisplayName;

    private void AttachSipLog(SIPTransport transport)
    {
        Directory.CreateDirectory(Storage.Folder);
        var path = Path.Combine(Storage.Folder, "sip.log");
        // Protokoll begrenzen: ab 10 MB alte Datei als sip.old.log behalten und neu beginnen.
        if (File.Exists(path) && new FileInfo(path).Length > 10 * 1024 * 1024)
            File.Move(path, Path.Combine(Storage.Folder, "sip.old.log"), overwrite: true);

        var writer = new StreamWriter(path, append: true) { AutoFlush = true };
        _sipLog = writer;
        writer.WriteLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} Start, Transport {_settings.Transport}, Server {RegistrarHost()}");

        void Write(string direction, SIPEndPoint local, SIPEndPoint remote, object message)
        {
            lock (writer) writer.WriteLine($"--- {DateTime.Now:HH:mm:ss.fff} {direction} {local} <-> {remote}\r\n{message}");
        }

        transport.SIPRequestOutTraceEvent += (l, r, m) => Write("OUT", l, r, m);
        transport.SIPRequestInTraceEvent += (l, r, m) => Write("IN", l, r, m);
        transport.SIPResponseOutTraceEvent += (l, r, m) => Write("OUT", l, r, m);
        transport.SIPResponseInTraceEvent += (l, r, m) => Write("IN", l, r, m);
        // Auch Nachrichten protokollieren, die nicht gelesen werden konnten (sonst gingen sie unbemerkt verloren).
        transport.SIPBadRequestInTraceEvent += (l, r, message, error, raw) => Write($"IN UNGÜLTIG ({error})", l, r, raw ?? message);
        transport.SIPBadResponseInTraceEvent += (l, r, message, error, raw) => Write($"IN UNGÜLTIG ({error})", l, r, raw ?? message);
    }

    public async ValueTask DisposeAsync()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        await _reconnectTimer.DisposeAsync();
        await StopAsync();
        _tones.Dispose();
    }

    /// <summary>Setzt bei Bedarf die per STUN ermittelte öffentliche IP ins SDP ein.</summary>
    private void LogLine(string text)
    {
        var writer = _sipLog;
        if (writer != null) lock (writer) writer.WriteLine($"--- {DateTime.Now:HH:mm:ss.fff} {text}");
    }

    private sealed class PhoneMediaSession(VoIPMediaSessionConfig config, Func<IPAddress?> publicIp)
        : VoIPMediaSession(config)
    {
        public override SDP CreateOffer(IPAddress connectionAddress) =>
            base.CreateOffer(connectionAddress ?? publicIp()!);

        public override SDP CreateAnswer(IPAddress connectionAddress) =>
            base.CreateAnswer(connectionAddress ?? publicIp()!);
    }
}
