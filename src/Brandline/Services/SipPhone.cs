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

/// <summary>
/// Kapselt SIP-Registrierung und genau einen Anruf gleichzeitig. Hält die Verbindung zum Server
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

    private SIPUserAgent? _ua;
    private SIPServerUserAgent? _pendingIncoming;
    private VoIPMediaSession? _media;
    private CallAudio? _audio;
    private StreamWriter? _sipLog;
    private SIPUserAgent? _localHangup;

    // Für die Anrufliste.
    private CallDirection _direction;
    private DateTime _callStart;
    private DateTime? _answeredAt;
    private bool _rejectedByUser;

    public SipPhone()
    {
        _reconnectTimer = new Timer(_ => _ = ReconnectAsync());
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public event Action<RegistrationState, string>? RegistrationChanged;
    public event Action<CallState, string>? CallStateChanged;
    public event Action<CallParty>? IncomingCall;
    public event Action<CallRecord>? CallEnded;
    public event Action<string>? Info;
    public event Action<string>? Error;

    public CallState State { get; private set; } = CallState.Idle;
    public CallParty? Remote { get; private set; }
    public bool IsMuted => _audio?.IsMuted ?? false;
    public bool IsOnHold => _ua?.IsOnLocalHold ?? false;
    public bool DoNotDisturb { get; set; }
    public IPAddress? PublicIp => _publicIp;

    /// <summary>Signalisierung per TLS und Sprache per SRTP.</summary>
    public bool IsEncrypted => _settings.Transport == SipTransportKind.Tls;

    /// <summary>Nur für automatisierte Tests mit selbstsignierten Zertifikaten – in der App immer null.</summary>
    public static RemoteCertificateValidationCallback? CertificateValidationOverride { get; set; }

    public CallQuality? Quality => _audio is { } a && State == CallState.Connected
        ? new CallQuality(a.JitterMs, a.LossPercent, a.BufferDelayMs, a.EchoCancellationActive, _media?.IsSecureContextReady() == true)
        : null;

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
        Hangup();

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

    // ---------------- Ausgehende Anrufe ----------------

    public async Task DialAsync(string input)
    {
        var number = PhoneNumber.ForDialing(input);
        if (number.Length == 0) return;

        SIPUserAgent ua;
        lock (_sync)
        {
            if (_transport == null || _ua != null) return;
            ua = new SIPUserAgent(_transport, null);
            _ua = ua;
            Remote = new CallParty(number, "");
            BeginRecord(CallDirection.Outgoing);
            WireCall(ua);
        }

        var target = $"sip:{number}@{RegistrarHost()}";
        var from = string.IsNullOrWhiteSpace(_settings.DisplayName)
            ? $"<sip:{_settings.SipUser}@{_settings.Server}>"
            : $"\"{_settings.DisplayName.Replace("\"", "")}\" <sip:{_settings.SipUser}@{_settings.Server}>";

        var descriptor = new SIPCallDescriptor(
            _settings.SipUser, _settings.Password, target, from, target,
            null, null, null, SIPCallDirection.Out, SDP.SDP_MIME_CONTENTTYPE, null, null);

        ua.ClientCallTrying += (_, _) => SetState(CallState.Dialing, "Verbinde …");
        ua.ClientCallRinging += (_, response) =>
        {
            // Ohne Early Media (183 mit SDP) spielen wir den Freiton selbst.
            if (string.IsNullOrEmpty(response?.Body)) _tones.PlayRingback(_settings.SpeakerDevice);
            SetState(CallState.RemoteRinging, "Es klingelt …");
        };
        ua.ClientCallFailed += (_, error, response) => EndCall(ua, DescribeCallFailure(response, error));

        SetState(CallState.Dialing, "Wähle …");
        bool answered;
        try
        {
            answered = await ua.Call(descriptor, CreateMedia(), 120);
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Anruf fehlgeschlagen: {ex.Message}");
            answered = false;
        }

        if (answered)
        {
            _tones.Stop();
            _answeredAt = DateTime.Now;
            SetState(CallState.Connected, "Verbunden");
        }
        else
        {
            EndCall(ua, "Nicht verbunden");
        }
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
        // Anfragen innerhalb eines Dialogs (BYE, re-INVITE …) bearbeitet der SIPUserAgent des Anrufs.
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
        }
        return Task.CompletedTask;
    }

    private void HandleIncomingInvite(SIPRequest request)
    {
        var caller = ReadCaller(request);

        lock (_sync)
        {
            if (_transport == null) return;

            string? rejectNote = null;
            if (_ua != null) rejectNote = "während eines Gesprächs";
            else if (DoNotDisturb) rejectNote = "Nicht stören";

            if (rejectNote != null)
            {
                // Besetzt melden – Placetel leitet dann gemäß Routing weiter (z. B. Mailbox).
                var busyTx = new UASInviteTransaction(_transport, request, null, false);
                busyTx.SendFinalResponse(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.BusyHere, null));
                CallEnded?.Invoke(new CallRecord
                {
                    Direction = CallDirection.Incoming,
                    Outcome = _ua != null ? CallOutcome.Missed : CallOutcome.Rejected,
                    Number = caller.Number,
                    Name = caller.DisplayName,
                    Start = DateTime.Now,
                    Note = rejectNote,
                    Unseen = _ua != null,
                });
                return;
            }

            var ua = new SIPUserAgent(_transport, null);
            _ua = ua;
            Remote = caller;
            BeginRecord(CallDirection.Incoming);
            WireCall(ua);
            ua.ServerCallCancelled += (_, _) => EndCall(ua, "Verpasster Anruf");
            _pendingIncoming = ua.AcceptCall(request);
        }

        _tones.PlayRing(_settings.RingDevice, Ringtones.PickForCall(_settings.RingTone, _settings.RandomRingTones));
        SetState(CallState.Incoming, "Eingehender Anruf");
        IncomingCall?.Invoke(caller);
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

    public async Task AnswerAsync()
    {
        SIPUserAgent? ua;
        SIPServerUserAgent? uas;
        lock (_sync)
        {
            ua = _ua;
            uas = _pendingIncoming;
            _pendingIncoming = null;
        }
        if (ua == null || uas == null) return;

        _tones.Stop();
        SetState(CallState.Dialing, "Nehme an …");
        bool ok;
        try
        {
            ok = await ua.Answer(uas, CreateMedia());
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Annehmen fehlgeschlagen: {ex.Message}");
            ok = false;
        }

        if (ok)
        {
            _answeredAt = DateTime.Now;
            SetState(CallState.Connected, "Verbunden");
        }
        else
        {
            EndCall(ua, "Annehmen fehlgeschlagen");
        }
    }

    // ---------------- Während des Gesprächs ----------------

    public void Hangup()
    {
        SIPUserAgent? ua;
        SIPServerUserAgent? incoming;
        lock (_sync)
        {
            ua = _ua;
            incoming = _pendingIncoming;
            _pendingIncoming = null;
        }
        if (ua == null) return;

        try
        {
            if (incoming != null)
            {
                _rejectedByUser = true;
                incoming.Reject(SIPResponseStatusCodesEnum.BusyHere, null);
            }
            else if (ua.IsCallActive)
            {
                // Hangup() löst OnCallHungup aus; so wird das nicht als Auflegen der Gegenseite gemeldet.
                _localHangup = ua;
                ua.Hangup();
            }
            else if (ua.IsCalling || ua.IsRinging)
            {
                ua.Cancel();
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Auflegen: {ex.Message}");
        }
        EndCall(ua, incoming != null ? "Abgelehnt" : "Aufgelegt");
    }

    /// <summary>Klingelton abschalten, der Anruf klingelt beim Anrufer weiter.</summary>
    public void SilenceRinging() => _tones.Stop();

    public void ToggleMute()
    {
        if (_audio != null) _audio.IsMuted = !_audio.IsMuted;
    }

    public void ToggleHold()
    {
        var ua = _ua;
        if (ua == null || !ua.IsCallActive) return;
        if (ua.IsOnLocalHold) ua.TakeOffHold();
        else ua.PutOnHold();
    }

    public void SendDtmf(char key)
    {
        var ua = _ua;
        if (ua == null || State != CallState.Connected) return;
        byte? tone = key switch
        {
            >= '0' and <= '9' => (byte)(key - '0'),
            '*' => 10,
            '#' => 11,
            _ => null
        };
        if (tone != null) _ = ua.SendDtmf(tone.Value);
    }

    // ---------------- Interna ----------------

    private void BeginRecord(CallDirection direction)
    {
        _direction = direction;
        _callStart = DateTime.Now;
        _answeredAt = null;
        _rejectedByUser = false;
    }

    private void WireCall(SIPUserAgent ua)
    {
        ua.OnCallHungup += _ => EndCall(ua, _localHangup == ua ? "Aufgelegt" : "Gegenseite hat aufgelegt");
        ua.RemotePutOnHold += () => CallStateChanged?.Invoke(State, "Gegenseite hat gehalten");
        ua.RemoteTookOffHold += () => CallStateChanged?.Invoke(State, "Verbunden");
    }

    private VoIPMediaSession CreateMedia()
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

        lock (_sync)
        {
            _audio = audio;
            _media = media;
        }
        return media;
    }

    private void EndCall(SIPUserAgent ua, string? reason)
    {
        VoIPMediaSession? media;
        CallAudio? audio;
        CallParty? remote;
        lock (_sync)
        {
            if (_ua != ua) return;
            media = _media;
            audio = _audio;
            remote = Remote;
            _ua = null;
            _media = null;
            _audio = null;
            _pendingIncoming = null;
            _localHangup = null;
        }

        _tones.Stop();
        try { media?.Close("Anruf beendet"); } catch { /* bereits geschlossen */ }
        audio?.Dispose();
        // Close() statt Dispose(): Dispose() wartet in SIPSorcery 10 auf ein Semaphor, das nach
        // ausgehenden Anrufen nicht freigegeben wird, und blockiert dann dauerhaft.
        ua.Close();

        var outcome = _answeredAt != null
            ? CallOutcome.Answered
            : _direction == CallDirection.Outgoing ? CallOutcome.NotReached
            : _rejectedByUser ? CallOutcome.Rejected : CallOutcome.Missed;
        CallEnded?.Invoke(new CallRecord
        {
            Direction = _direction,
            Outcome = outcome,
            Number = remote?.Number ?? "",
            Name = remote?.DisplayName ?? "",
            Start = _callStart,
            Duration = _answeredAt is { } at ? DateTime.Now - at : TimeSpan.Zero,
            Note = outcome == CallOutcome.NotReached ? reason ?? "" : "",
            Unseen = outcome == CallOutcome.Missed,
        });

        SetState(CallState.Idle, reason ?? "");
        Remote = null;

        if (_reconnectPending) _reconnectTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    private void SetState(CallState state, string info)
    {
        State = state;
        CallStateChanged?.Invoke(state, info);
    }

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
