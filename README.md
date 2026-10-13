# brandline

SIP-Softphone der brandcompete GmbH für Windows 10/11 (WPF, .NET 10), vorkonfiguriert für Placetel.

## Funktionen

**Telefonieren**
- Anmeldung am SIP-Server, Standard **verschlüsselt**: SIP über **TLS** (`fpbx.de:5061`) und Sprache per **SRTP**;
  Serverzertifikat wird streng geprüft. UDP/TCP (5060, unverschlüsselt) weiterhin wählbar
- Anrufen über Nummernfeld, Tastatur (Enter), Adressbuch, Anrufliste oder Wahlwiederholung
- Vorschläge beim Tippen (Name, Firma oder Teil der Nummer) aus Adressbuch und Anrufliste
- Eingehende Anrufe: Benachrichtigungsfenster unten rechts mit Name/Nummer, Annehmen/Ablehnen, Klingelton stumm
- Halten, Stummschalten, Tonwahl (DTMF)
- **Zwei Leitungen**: Anklopfen (Hinweisfenster + Anklopfton), Annehmen hält das laufende Gespräch,
  Makeln, einzeln auflegen; ein dritter Anruf bekommt „Besetzt“ (erscheint als verpasst)
- **Weiterleiten**: direkt (REFER) oder mit Rückfrage – Ziel zuerst anrufen, dann „Verbinden“ (REFER mit Replaces)
- **Mailbox-Anzeige** (MWI, `message-summary`): Symbol mit Zahl neuer Nachrichten, Klick ruft die Mailbox an
- **Rufnummer unterdrücken** pro Anruf (Häkchen unter dem Nummernfeld, Vorgabe in den Einstellungen; `Privacy: id`)
- **Nicht stören**: eingehende Anrufe werden abgewiesen (Placetel leitet z. B. zur Mailbox)
- **Anrufliste** mit verpassten Anrufen (rot, Zähler im Reiter, Hinweis im Infobereich)
- **Klingeltöne**: neun eingebaute, beliebig viele eigene MP3/WAV/WMA/M4A/AAC-Dateien, Probehören;
  **„Zufällig“** spielt bei jedem Anruf einen Ton aus einer wählbaren Auswahl (nie zweimal hintereinander denselben)
- **Adressbuch** mit Avatar (Bild wird quadratisch zugeschnitten, sonst farbige Initialen) und beliebig vielen
  Nummern je Kontakt (Mobil/Festnetz, optional Bezeichnung); beim Anrufen wird bei mehreren Nummern gefragt

**Erscheinungsbild**
- brandcompete-Design: Markenrot `#A91212`, Anthrazit `#1A1A2E`, Wortmarke „■ **brand**line“,
  Hausschrift **Rubik** eingebettet (SIL Open Font License, `src/Brandline/Fonts/OFL-Rubik.txt`)
- **Hell**, **Dunkel** oder **wie Windows** (folgt der Windows-Einstellung auch im laufenden Betrieb)
- Fluent-Design von .NET für alle Standard-Steuerelemente; deren Windows-Akzentfarbe wird automatisch
  durch Markenrot ersetzt
- „Über brandline“ mit Angaben zur brandcompete GmbH

**Microsoft 365**
- **Firmenverzeichnis** (Kolleginnen und Kollegen mit Geschäfts-/Mobilnummer und **Profilfoto**) und **persönliche
  Outlook-Kontakte** im Adressbuch und bei eingehenden Anrufen; Abgleich beim Start und alle 6 Stunden
- Anmeldung über den Windows-Anmeldebroker (MSAL/WAM) – auf Entra-verbundenen PCs automatisch mit dem Windows-Konto
- Synchronisierte Einträge sind mit einer Wolke markiert und schreibgeschützt (in Microsoft 365 gepflegt);
  eigene Kontakte haben bei gleicher Nummer Vorrang. Abmelden entfernt die synchronisierten Daten vom Rechner

**Windows-Integration**
- Läuft im **Infobereich** (Tray), Symbolfarbe zeigt den Status; Schließen minimiert nur
- **Autostart** mit der Windows-Anmeldung (im Hintergrund)
- **tel:/callto:-Links** (Outlook-Signaturen, Browser, Teams …) öffnen das Softphone
- **Tastenkürzel** (Standard `Strg+Alt+D`): Nummer in beliebiger App markieren → wird gewählt
- Nur eine Instanz; ein zweiter Start oder ein Link reicht den Befehl an die laufende Instanz weiter

**Sprachqualität und Stabilität**
- Audio über **WASAPI** (Kategorie „Kommunikation“), Geräte mit vollem Namen und stabiler ID
- **Echo- und Rauschunterdrückung** über das in Windows eingebaute Voice-Capture-DSP (Windows 10 und 11);
  fällt sie aus, wird ohne Unterbrechung auf das normale Mikrofon gewechselt
- **Adaptiver Jitter-Puffer** (40–300 ms) mit Umsortierung und Verlustverschleierung
- Anzeige von Jitter, Paketverlust und Puffer während des Gesprächs
- **NAT-Keepalive** alle 25 s, damit eingehende Anrufe auch nach langer Ruhe ankommen
- **Automatische Neuanmeldung** nach Netzwechsel (WLAN/LAN/VPN) und Aufwachen aus dem Standby
- Optional **STUN** (öffentliche IP im SDP) bei einseitigem Ton hinter NAT
- Codecs: G.722 (HD), G.711 a-law, G.711 µ-law

## Starten

```bash
dotnet run --project src/Brandline
```

Eine einzelne `.exe` erzeugen (benötigt die .NET 10 Desktop Runtime; für die Verteilung besser den Installer nutzen):

```bash
dotnet publish src/Brandline/Brandline.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Argumente: `--background` (ohne Fenster starten), `--dial <Nummer>` oder ein `tel:`-Link,
`--exit` (laufende Instanz beenden) und `--uninstall-cleanup` (zusätzlich Autostart/tel:-Links entfernen) – die
letzten beiden nutzt der Installer.

## Installer (MSI) für die Verteilung

```bash
powershell -ExecutionPolicy Bypass -File tools\Build-Installer.ps1
```

Ergebnis: `artifacts\brandline-<Version>-x64.msi` (≈ 62 MB, installiert ≈ 180 MB; .NET-Laufzeit enthalten –
Zielrechner brauchen nichts). Die Laufzeit wird als normale Dateien installiert, nicht als Single-File – sonst würden
Teile beim ersten Start nach `%TEMP%` entpackt, was AppLocker/Virenscanner auf Firmenrechnern oft blockieren.
Die Version kommt aus `<Version>` in `src\Brandline\Brandline.csproj`; für ein Update dort hochzählen, dann ersetzt
das neue MSI die alte Version automatisch.

| Was | Wie |
|-----|-----|
| Installationsort | im Installer wählbar, Standard `C:\Program Files\brandcompete\brandline`; Updates übernehmen den gewählten Ordner (pro Computer, Startmenü-Eintrag, „Apps & Features“) |
| Rechte | Installation braucht einmalig **Administratorrechte** (UAC) – wegen der Firewall-Regel |
| Firewall | Eingehende Regel „brandline (SIP-Softphone)“ – ohne sie kommen eingehende Anrufe nicht an |
| Update / Deinstallation | laufende App wird vorher sauber beendet (Abmeldung bei Placetel); bei Deinstallation werden Autostart und tel:-Links des Benutzers entfernt |
| Vorgaben | `defaults.json` im Installationsordner = Startwerte für neue Benutzer (Server, Port, Design …), keine Zugangsdaten |
| Lizenzen | `THIRD-PARTY-NOTICES.txt` (erzeugt mit `tools\Generate-ThirdPartyNotices.ps1`), Installer-Lizenztext `installer\License.rtf` (**Entwurf, rechtlich prüfen**) |
| Bilder | `installer\Assets\*.bmp`, erzeugt mit `tools\New-InstallerArt.ps1` |

Stille Installation (z. B. per Skript): `msiexec /i brandline-0.6.0-x64.msi /qn` – optional mit `INSTALLFOLDER="D:\Apps\brandline"`

### Automatische Updates

brandline sucht eine Minute nach dem Start und danach alle 12 Stunden nach einer neuen Version (abschaltbar in
Einstellungen → Allgemein; manuell über „Über brandline → Nach Updates suchen“). Ist eine neuere Version da, erscheint
ein Hinweis im Hauptfenster; „Installieren“ lädt das MSI, **prüft die SHA-256-Prüfsumme**, startet die Installation
(Fortschrittsanzeige + Windows-Rückfrage für Administratorrechte) und danach brandline neu. Während eines Gesprächs
wird kein Update gestartet. Es wird nur über **HTTPS** geladen.

**Neue Version veröffentlichen:**
1. `<Version>` in `src\Brandline\Brandline.csproj` hochzählen.
2. `powershell -ExecutionPolicy Bypass -File tools\Build-Installer.ps1 -Notes "Was ist neu"`
3. Auf den Update-Server laden (gleicher Ordner): **zuerst** `artifacts\brandline-<Version>-x64.msi`,
   **dann** `artifacts\latest.json` – sonst finden Clients kurz eine Version, deren MSI noch fehlt.

Die Adresse der Versionsdatei steht in `installer\defaults.json` (`UpdateUrl`, Standard
`https://download.brandcompete.com/brandline/latest.json`) und ist pro Benutzer in den Einstellungen änderbar.
Der Server sollte `latest.json` nicht lange zwischenspeichern (kurzes `Cache-Control`).

**Noch offen:** Code-Signatur (sonst warnt SmartScreen „Unbekannter Herausgeber“), Test auf sauberen Windows-10/11-Rechnern.

## Microsoft 365 einrichten (einmalig, Entra-Administrator)

1. entra.microsoft.com → Identität → Anwendungen → App-Registrierungen → **Neue Registrierung**:
   Name `brandline`, **nur Konten in diesem Organisationsverzeichnis**.
2. **Authentifizierung** → Plattform **Mobilgerät- und Desktopanwendungen** mit den Umleitungs-URIs
   `ms-appx-web://microsoft.aad.brokerplugin/<Client-ID>` und `http://localhost`.
3. **API-Berechtigungen** → Microsoft Graph, delegiert: `User.Read`, `Contacts.Read`, `User.Read.All`
   → **Administratorzustimmung erteilen** (`User.Read.All` für die Rufnummern aus dem Firmenverzeichnis).
4. Client-ID und Mandanten-ID in `installer\defaults.json` (`M365ClientId`, `M365TenantId`) eintragen und das MSI neu bauen.

Das Token liegt verschlüsselt (DPAPI) unter `%APPDATA%\Brandline\msal.cache`, die Kontakte in `m365-contacts.json`.

## Outlook-Add-in

Schaltfläche **„Anrufen“** (Gruppe „brandline“) beim Lesen von E-Mails und Terminen – im klassischen und im neuen
Outlook für Windows sowie in Outlook im Web. Der Aufgabenbereich zeigt Absender bzw. Organisator und alle
Telefonnummern aus Text und Ort (mit Art: Telefon/Mobil/Fax; Datum, IBAN, Register- und Belegnummern werden
ignoriert), dazu ein Feld für eine beliebige Nummer. Er lässt sich anheften und folgt dann der gewählten Nachricht.

**So kommt der Klick zu brandline:** über einen Link `brandline:<Nummer>`, den Windows an die App übergibt
(brandline registriert das Schema bei jedem Start im Benutzerprofil). Ein Aufruf an `localhost` geht nicht mehr –
Edge/WebView2 sperrt seit Version 143 Anfragen von Webseiten ins lokale Netz („Local Network Access“), und
Add-ins können das nicht freischalten.

**Einrichten (einmalig):**
1. `tools\Build-Installer.ps1` legt die Dateien unter `artifacts\outlook\` ab. Den Inhalt nach
   `https://download.brandcompete.com/brandline/outlook/` hochladen (`taskpane.html` muss dort erreichbar sein).
2. Microsoft 365 Admin Center → **Einstellungen → Integrierte Apps → Benutzerdefinierte Apps hochladen** →
   Typ „Office-Add-in“, `outlook-addin\manifest.xml` hochladen, Benutzer zuweisen (z. B. ganze Organisation).
   Bis das Add-in in Outlook erscheint, kann es einige Stunden dauern.
3. Zum Ausprobieren vorab nur für sich selbst: in Outlook **Add-ins abrufen → Meine Add-ins →
   Benutzerdefiniertes Add-in hinzufügen → Aus Datei** und `manifest.xml` wählen.

Manifest-Änderungen (Symbole, Texte, URLs): `<Version>` im Manifest erhöhen und erneut hochladen; die `<Id>` nie
ändern. Änderungen an `taskpane.*` werden nur hochgeladen. Symbole: `tools\New-OutlookIcons.ps1`.
Test der Nummernerkennung: `node outlook-addin/test/extract.test.js`.

## Hinweise zur Einrichtung

- **Autostart / tel:-Links** werden beim Start der App für den aktuellen Pfad der `.exe` eingetragen
  (nur im Benutzerprofil, keine Adminrechte). Die `.exe` daher an ihren endgültigen Ort legen und von dort starten,
  am besten auf ein lokales Laufwerk (z. B. `%LOCALAPPDATA%\Programs\brandline`), nicht auf ein Netzlaufwerk.
- **tel:-Links**: einmalig in Windows unter *Einstellungen → Apps → Standard-Apps* bei „TEL“ und „CALLTO“
  brandline auswählen (Schaltfläche dafür in den Einstellungen der App).
- **Warum kein Windows-Dienst?** Dienste laufen in einer abgeschotteten Sitzung ohne Desktop und ohne Zugriff auf
  die Audiogeräte des Benutzers – sie könnten weder klingeln noch ein Fenster zeigen. Daher Autostart im Infobereich.

## Daten

Alles liegt unter `%APPDATA%\Brandline` (Daten der Vorversion aus `%APPDATA%\BcSoftphone` werden beim ersten Start übernommen):

| Datei / Ordner  | Inhalt                                                                                        |
|-----------------|-----------------------------------------------------------------------------------------------|
| `settings.json` | Einstellungen; das Passwort ist mit Windows-DPAPI an das Benutzerkonto gebunden verschlüsselt |
| `contacts.json` | Adressbuch                                                                                    |
| `calls.json`    | Anrufliste (max. 500 Einträge)                                                                |
| `ringtones\`    | importierte eigene Klingeltöne                                                                |
| `avatars\`      | Kontaktbilder (160 × 160 px, PNG)                                                             |
| `sip.log`       | SIP-Protokoll, nur wenn aktiviert (ab 10 MB wird nach `sip.old.log` rotiert)                  |

## Aufbau

| Datei                                   | Zweck                                                        |
|-----------------------------------------|--------------------------------------------------------------|
| `Services/SipPhone.cs`                  | Registrierung, Anrufsteuerung (2 Leitungen, Weiterleiten), Mailbox-Abo, Keepalive, Neuanmeldung, STUN |
| `Services/PhoneCall.cs`                 | Zustand eines einzelnen Gesprächs                            |
| `Services/Audio/CallAudio.cs`           | Gesprächsaudio: Mikrofon → Codec → RTP → Jitter-Puffer → Lautsprecher |
| `Services/Audio/EchoCancellingCapture.cs` | Windows Voice-Capture-DSP (Echo-/Rauschunterdrückung) per COM |
| `Services/Audio/JitterBuffer.cs`        | Adaptiver Jitter-Puffer                                      |
| `Services/Audio/Ringtones.cs`           | Eingebaute und eigene Klingeltöne, Zufallsauswahl            |
| `Services/Avatars.cs`                   | Kontaktbilder zuschneiden, speichern, Initialen              |
| `Themes/*`                              | Paletten Hell/Dunkel, Control-Styles, ThemeManager           |
| `Services/SystemIntegration.cs`         | Autostart, tel:-Registrierung, Einzelinstanz                 |
| `Services/GlobalHotkey.cs`              | Systemweites Tastenkürzel, Markierung auslesen               |
| `Views/*`                               | Hauptfenster, Anruf-Hinweis, Tray-Symbol, Einstellungen, Kontakt, Über |

## Fehlersuche

- **Anmeldung schlägt fehl (401/403):** SIP-Benutzername und -Passwort im Placetel-Portal prüfen.
- **Kein Ton in eine Richtung:** Windows-Firewall-Abfrage beim ersten Start bestätigen; ggf. STUN oder TCP aktivieren.
- **Echo beim Gegenüber:** Echo- und Rauschunterdrückung in den Einstellungen (Audio) aktiv lassen.
- **Details:** in den Einstellungen „SIP-Protokoll schreiben“ aktivieren und `sip.log` ansehen.
