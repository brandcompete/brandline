using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Microsoft.Win32;

namespace Brandline.Services;

/// <summary>Autostart, tel:-Link-Registrierung und Einzelinstanz (alles im Benutzerprofil, keine Adminrechte nötig).</summary>
public static class SystemIntegration
{
    public const string AppName = "brandline";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Brandline";
    private const string ProgId = "Brandline.Url";
    private const string CapabilitiesKey = @"Software\Brandline\Capabilities";
    private static readonly string[] Protocols = ["tel", "callto"];

    public static string ExePath => Environment.ProcessPath ?? "";

    // ---------------- Autostart ----------------

    public static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{ExePath}\" --background");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    // ---------------- tel:/callto: ----------------

    /// <summary>
    /// Meldet die App bei Windows als mögliche Standard-App für tel:/callto: an. Welche App tatsächlich
    /// öffnet, entscheidet der Benutzer in den Windows-Einstellungen (Standard-Apps).
    /// </summary>
    public static void SetTelLinkRegistration(bool enabled)
    {
        var command = $"\"{ExePath}\" \"%1\"";
        if (enabled)
        {
            using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                progId.SetValue("", $"URL:{AppName}");
                progId.SetValue("URL Protocol", "");
                using var icon = progId.CreateSubKey("DefaultIcon");
                icon.SetValue("", $"\"{ExePath}\",0");
                using var cmd = progId.CreateSubKey(@"shell\open\command");
                cmd.SetValue("", command);
            }

            using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
            {
                caps.SetValue("ApplicationName", AppName);
                caps.SetValue("ApplicationDescription", "SIP-Softphone von brandcompete");
                using var urls = caps.CreateSubKey("URLAssociations");
                foreach (var p in Protocols) urls.SetValue(p, ProgId);
            }

            using (var apps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                apps.SetValue(AppName, CapabilitiesKey);

            // Falls noch gar keine App für das Protokoll zuständig ist, greift dieser Eintrag direkt.
            foreach (var p in Protocols)
            {
                using var proto = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{p}");
                if (proto.GetValue("URL Protocol") != null && !IsOurs(proto)) continue;
                proto.SetValue("", $"URL:{p}");
                proto.SetValue("URL Protocol", "");
                using var cmd = proto.CreateSubKey(@"shell\open\command");
                cmd.SetValue("", command);
            }
        }
        else
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Brandline", throwOnMissingSubKey: false);
            using (var apps = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
                apps?.DeleteValue(AppName, throwOnMissingValue: false);
            foreach (var p in Protocols)
            {
                using var proto = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{p}");
                if (proto != null && IsOurs(proto))
                    Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{p}", throwOnMissingSubKey: false);
            }
        }
    }

    // ---------------- brandline: ----------------

    /// <summary>Eigenes Link-Schema, das immer brandline öffnet.</summary>
    public const string OwnScheme = "brandline";

    /// <summary>
    /// Registriert brandline:&lt;Nummer&gt; (z. B. aus dem Outlook-Add-in). Anders als bei tel: muss dafür
    /// keine Standard-App gewählt werden – sonst landete der Klick womöglich bei Teams.
    /// </summary>
    public static void RegisterOwnScheme()
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{OwnScheme}");
        key.SetValue("", $"URL:{AppName}");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{ExePath}\",0");
        using var cmd = key.CreateSubKey(@"shell\open\command");
        cmd.SetValue("", $"\"{ExePath}\" \"%1\"");
    }

    private static void RemoveOwnScheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{OwnScheme}");
        if (key != null && IsOurs(key))
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{OwnScheme}", throwOnMissingSubKey: false);
    }

    private static bool IsOurs(RegistryKey protocolKey, string marker = "brandline")
    {
        using var cmd = protocolKey.OpenSubKey(@"shell\open\command");
        return cmd?.GetValue("") is string s && s.Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- Übernahme aus der Vorversion „bc softphone“ ----------------

    private const string LegacyName = "BcSoftphone";

    /// <summary>
    /// Übernimmt Daten (Einstellungen, Adressbuch, Anrufliste, Klingeltöne) aus %APPDATA%\BcSoftphone
    /// und entfernt die Windows-Einträge der Vorversion (Autostart, tel:-Registrierung).
    /// </summary>
    public static void MigrateFromPreviousVersion()
    {
        var oldFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyName);
        if (Directory.Exists(oldFolder) && !Directory.Exists(Storage.Folder))
        {
            CopyDirectory(oldFolder, Storage.Folder);
            try { Directory.Delete(oldFolder, recursive: true); }
            catch { /* z. B. noch geöffnetes sip.log der alten Version – Kopie genügt */ }
        }

        try
        {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                run?.DeleteValue(LegacyName, throwOnMissingValue: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{LegacyName}.Url", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\{LegacyName}", throwOnMissingSubKey: false);
            using (var apps = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
                apps?.DeleteValue("bc softphone", throwOnMissingValue: false);
            foreach (var p in Protocols)
            {
                using var proto = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{p}");
                if (proto != null && IsOurs(proto, LegacyName))
                    Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{p}", throwOnMissingSubKey: false);
            }
        }
        catch
        {
            // Aufräumen ist optional.
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            try { File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false); }
            catch (IOException) { /* gesperrte Protokolldatei o. Ä. überspringen */ }
        }
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    /// <summary>Öffnet die Windows-Einstellungen, in denen die Standard-App für tel: gewählt wird.</summary>
    public static void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(AppName)}") { UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
    }

    // ---------------- Einzelinstanz ----------------

    // Mutex pro Benutzer und Sitzung; die Pipe nur pro Benutzer, damit auch der Installer (der in einer
    // anderen Sitzung laufen kann) die laufende App erreicht.
    private static string MutexName => $"Brandline-{Environment.UserName}-{Process.GetCurrentProcess().SessionId}";
    private static string PipeName => $"Brandline-{Environment.UserName}";
    private static Mutex? _mutex;

    /// <summary>True, wenn dies die erste laufende Instanz für diesen Benutzer ist.</summary>
    public static bool TryBecomePrimaryInstance()
    {
        // Eigenen Griff aus einem früheren Versuch freigeben, sonst bleibt das Mutex-Objekt bestehen.
        _mutex?.Dispose();
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
        return created;
    }

    /// <summary>
    /// Läuft brandline bereits aus einem anderen Ordner (z. B. eine manuell kopierte Altversion)?
    /// Dann würde diese Instanz nur das alte Fenster nach vorne holen – der Aufrufer kann nachfragen.
    /// </summary>
    public static (string Path, string Version)? FindInstanceFromOtherFolder()
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExePath)))
        {
            if (process.Id == Environment.ProcessId) continue;
            try
            {
                var path = process.MainModule?.FileName;
                if (path == null || string.Equals(path, ExePath, StringComparison.OrdinalIgnoreCase)) continue;
                var version = FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+')[0] ?? "?";
                return (path, version);
            }
            catch
            {
                // Prozess eines anderen Benutzers oder bereits beendet.
            }
        }
        return null;
    }

    /// <summary>
    /// Für den Installer (Update/Deinstallation): laufende brandline-Instanzen sauber beenden (inkl. Abmelden
    /// bei Placetel) und notfalls nach Ablauf der Wartezeit hart beenden, damit keine Dateien gesperrt bleiben.
    /// </summary>
    public static void ExitRunningInstances(TimeSpan timeout)
    {
        SendToPrimaryInstance("exit");

        var self = Environment.ProcessId;
        Process[] Others() => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExePath))
            .Where(p => p.Id != self).ToArray();

        var deadline = DateTime.UtcNow + timeout;
        while (Others().Length > 0 && DateTime.UtcNow < deadline) Thread.Sleep(250);
        foreach (var p in Others())
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { /* bereits beendet oder fremder Benutzer */ }
        }
    }

    /// <summary>Entfernt die Einträge im Benutzerprofil (Autostart, tel:-Links) – bei der Deinstallation.</summary>
    public static void RemoveUserIntegration()
    {
        try { SetAutostart(false); } catch { /* ignorieren */ }
        try { SetTelLinkRegistration(false); } catch { /* ignorieren */ }
        try { RemoveOwnScheme(); } catch { /* ignorieren */ }
    }

    /// <summary>Schickt einen Befehl ("show" oder "dial &lt;Nummer&gt;") an die bereits laufende Instanz.</summary>
    public static bool SendToPrimaryInstance(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client);
            writer.WriteLine(command);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Nimmt Befehle weiterer Instanzen entgegen (z. B. aus einem angeklickten tel:-Link).</summary>
    public static void ListenForCommands(Action<string> onCommand, CancellationToken cancel)
    {
        Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(cancel);
                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync(cancel);
                    if (!string.IsNullOrWhiteSpace(line)) onCommand(line.Trim());
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    await Task.Delay(500, CancellationToken.None);
                }
            }
        }, cancel);
    }
}
