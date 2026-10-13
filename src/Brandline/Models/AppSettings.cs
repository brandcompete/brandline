using System.Text.Json.Serialization;
using Brandline.Services;

namespace Brandline.Models;

public enum SipTransportKind
{
    Udp,
    Tcp,

    /// <summary>Verschlüsselt: SIP über TLS (Placetel: Port 5061), Sprache per SRTP.</summary>
    Tls
}

public enum AppTheme
{
    /// <summary>Folgt der Windows-Einstellung „App-Modus“.</summary>
    System,
    Light,
    Dark
}

public sealed class AppSettings
{
    /// <summary>SIP-Benutzername, bei Placetel z. B. "777123456789" (ohne "@fpbx.de").</summary>
    public string SipUser { get; set; } = "";

    /// <summary>Mit DPAPI (CurrentUser) verschlüsseltes Passwort, Base64.</summary>
    public string EncryptedPassword { get; set; } = "";

    public string Server { get; set; } = "fpbx.de";
    public int Port { get; set; } = 5061;
    public SipTransportKind Transport { get; set; } = SipTransportKind.Tls;
    public string DisplayName { get; set; } = "";
    public int RegisterExpirySeconds { get; set; } = 300;

    /// <summary>WASAPI-Geräte-IDs, leer = Windows-Standardgerät für Kommunikation.</summary>
    public string RingDevice { get; set; } = "";
    public string SpeakerDevice { get; set; } = "";
    public string MicrophoneDevice { get; set; } = "";

    /// <summary>
    /// Klingelton: ID eines eingebauten Tons (z. B. "classic"), einer eigenen Datei ("file:Name.mp3")
    /// oder "random" für einen zufälligen Ton aus <see cref="RandomRingTones"/> bei jedem Anruf.
    /// </summary>
    public string RingTone { get; set; } = "classic";

    /// <summary>Auswahl für den Zufallsmodus.</summary>
    public List<string> RandomRingTones { get; set; } = ["classic", "marimba", "chime", "kalimba"];

    /// <summary>Nur noch zum Einlesen alter Einstellungen (eine einzelne eigene Datei).</summary>
    public string RingToneFile { get; set; } = "";

    /// <summary>Erscheinungsbild.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Echo- und Rauschunterdrückung von Windows verwenden.</summary>
    public bool EchoCancellation { get; set; } = true;

    /// <summary>Mit der Windows-Anmeldung im Hintergrund starten.</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>App als Ziel für tel:/callto:-Links anbieten.</summary>
    public bool RegisterTelLinks { get; set; } = true;

    /// <summary>Systemweites Kürzel: markierte Nummer in beliebiger App wählen. Leer = aus.</summary>
    public string DialHotkey { get; set; } = "Ctrl+Alt+D";

    /// <summary>Eingehende Anrufe sofort abweisen.</summary>
    public bool DoNotDisturb { get; set; }

    /// <summary>Öffentliche IP per STUN ermitteln und im SDP angeben (nur bei Ton-Problemen hinter NAT nötig).</summary>
    public bool UseStun { get; set; }
    public string StunServer { get; set; } = "stun.l.google.com:19302";

    /// <summary>Abstand der NAT-Keepalives an den SIP-Server (UDP), 0 = aus.</summary>
    public int KeepAliveSeconds { get; set; } = 25;

    /// <summary>Schreibt alle SIP-Nachrichten nach %APPDATA%\Brandline\sip.log (zur Fehlersuche).</summary>
    public bool EnableSipLog { get; set; }

    /// <summary>Automatisch nach neuen Versionen suchen (beim Start und alle 12 Stunden).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Adresse der Versionsdatei (nur HTTPS). Wird über installer\defaults.json vorgegeben.</summary>
    public string UpdateUrl { get; set; } = "https://download.brandcompete.com/brandline/latest.json";

    [JsonIgnore]
    public string Password
    {
        get => Secret.Unprotect(EncryptedPassword);
        set => EncryptedPassword = Secret.Protect(value);
    }

    [JsonIgnore]
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(SipUser) &&
        !string.IsNullOrWhiteSpace(Server) &&
        !string.IsNullOrEmpty(EncryptedPassword);

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.RandomRingTones = [.. RandomRingTones];
        return copy;
    }

    /// <summary>Übernimmt Werte aus älteren Versionen der Einstellungsdatei.</summary>
    public void Upgrade()
    {
        // Vorversion: eine eigene Datei mit absolutem Pfad und RingTone = "custom".
        if (RingTone == "custom")
        {
            RingTone = RingToneFile.Length > 0 ? "file:" + System.IO.Path.GetFileName(RingToneFile) : "classic";
            RingToneFile = "";
        }
    }
}
