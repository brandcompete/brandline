using System.IO;
using System.Windows;
using System.Windows.Threading;
using Brandline.Services;
using Brandline.Views;

namespace Brandline;

/// <summary>
/// Start: nur eine Instanz pro Benutzer. Weitere Aufrufe (z. B. ein angeklickter tel:-Link) reichen
/// ihren Befehl an die laufende Instanz weiter und beenden sich sofort.
///
/// Argumente:  --background      Start ohne Fenster (Autostart), nur Symbol im Infobereich
///             tel:/callto:/sip:/brandline:  Nummer wählen
///             --dial &lt;Nummer&gt;  Nummer wählen
///             --exit                laufende Instanz beenden (Installer, vor Update/Deinstallation)
///             --uninstall-cleanup   wie --exit, zusätzlich Autostart und tel:-Links entfernen
/// </summary>
public partial class App : Application
{
    private readonly CancellationTokenSource _shutdown = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);

        // Befehle des Installers: ohne Fenster ausführen und sofort beenden.
        var cleanup = e.Args.Contains("--uninstall-cleanup", StringComparer.OrdinalIgnoreCase);
        if (cleanup || e.Args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            SystemIntegration.ExitRunningInstances(TimeSpan.FromSeconds(15));
            if (cleanup) SystemIntegration.RemoveUserIntegration();
            Shutdown();
            return;
        }

        var background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        var dialNumber = ParseDialArgument(e.Args);
        var command = dialNumber != null ? $"dial {dialNumber}" : "show";

        if (!SystemIntegration.TryBecomePrimaryInstance() && !ReplaceInstanceFromOtherFolder(background))
        {
            // Läuft schon: Befehl weitergeben (beim Autostart nichts tun).
            if (!background) SystemIntegration.SendToPrimaryInstance(command);
            Shutdown();
            return;
        }

        SystemIntegration.MigrateFromPreviousVersion();
        var settings = Storage.LoadSettings();
        settings.Upgrade();
        MigrateDefaults(settings);
        Themes.ThemeManager.Apply(settings.Theme);
        Views.MainWindow.ApplySystemIntegration(settings);

        var window = new Views.MainWindow(settings);
        MainWindow = window;
        if (!background) window.Show();

        SystemIntegration.ListenForCommands(
            cmd => Dispatcher.BeginInvoke(() => window.HandleCommand(cmd)), _shutdown.Token);

        await window.StartAsync();
        if (dialNumber != null) window.HandleCommand(command);
    }

    /// <summary>Bestehende Einstellungen einmal neu speichern, damit neu hinzugekommene Optionen mit Standardwert in der Datei stehen.</summary>
    private static void MigrateDefaults(Models.AppSettings settings)
    {
        if (!File.Exists(Path.Combine(Storage.Folder, "settings.json"))) return;
        Storage.SaveSettings(settings);
    }

    /// <summary>
    /// Läuft brandline aus einem anderen Ordner (typisch: alte, manuell kopierte Version neben der installierten),
    /// fragen, ob diese beendet und stattdessen diese Version gestartet werden soll.
    /// </summary>
    /// <returns>True, wenn diese Instanz jetzt die laufende ist.</returns>
    private static bool ReplaceInstanceFromOtherFolder(bool background)
    {
        // Beim Autostart keine Rückfrage – dann bleibt die bereits laufende Version aktiv.
        if (background || SystemIntegration.FindInstanceFromOtherFolder() is not { } other) return false;

        var ownVersion = typeof(App).Assembly.GetName().Version?.ToString(3);
        var answer = MessageBox.Show(
            $"Es läuft bereits eine andere brandline-Installation:\n{other.Path} (Version {other.Version})\n\n" +
            $"Diese beenden und stattdessen brandline {ownVersion} aus\n{SystemIntegration.ExePath}\nstarten?",
            SystemIntegration.AppName, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;

        SystemIntegration.ExitRunningInstances(TimeSpan.FromSeconds(5));
        return SystemIntegration.TryBecomePrimaryInstance();
    }

    private static string? ParseDialArgument(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--dial", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
            if (PhoneNumber.FromUri(args[i]) is { Length: > 0 } number) return number;
        }
        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, $"{SystemIntegration.AppName} – Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
