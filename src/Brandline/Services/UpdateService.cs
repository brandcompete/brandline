using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brandline.Services;

/// <summary>Inhalt von latest.json auf dem Update-Server (erzeugt von tools\Build-Installer.ps1).</summary>
public sealed class UpdateInfo
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    /// <summary>Dateiname oder vollständige HTTPS-Adresse des MSI; relativ zur Adresse von latest.json.</summary>
    [JsonPropertyName("msi")] public string Msi { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("published")] public string Published { get; set; } = "";

    [JsonIgnore] public Version? ParsedVersion => System.Version.TryParse(Version, out var v) ? v : null;
}

/// <summary>
/// Sucht per HTTPS nach einer neueren Version, lädt das MSI herunter, prüft die SHA-256-Prüfsumme und
/// startet die Installation. Nach der Installation wird brandline automatisch neu gestartet.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly HttpClient _http;

    public UpdateService(HttpMessageHandler? handler = null)
    {
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"brandline/{CurrentVersion}");
    }

    public static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Liefert die Versionsinfo, wenn sie neuer ist als die laufende Version, sonst null.</summary>
    /// <exception cref="InvalidOperationException">Adresse ungültig oder kein HTTPS.</exception>
    public async Task<UpdateInfo?> CheckAsync(string updateUrl, CancellationToken cancel = default)
    {
        var uri = RequireHttps(updateUrl);
        using var response = await _http.GetAsync(uri, cancel);
        response.EnsureSuccessStatusCode();
        var info = await JsonSerializer.DeserializeAsync<UpdateInfo>(await response.Content.ReadAsStreamAsync(cancel), cancellationToken: cancel)
                   ?? throw new InvalidOperationException("Versionsdatei ist leer.");

        if (info.ParsedVersion is not { } version) throw new InvalidOperationException($"Ungültige Versionsangabe „{info.Version}“.");
        if (string.IsNullOrWhiteSpace(info.Msi) || info.Sha256.Length != 64)
            throw new InvalidOperationException("Versionsdatei unvollständig (MSI oder Prüfsumme fehlt).");

        // Nur Haupt-, Neben- und Patchnummer vergleichen (die Assembly-Version hat vier Stellen).
        return Normalize(version) > Normalize(CurrentVersion) ? info : null;
    }

    /// <summary>Lädt das MSI herunter und prüft die Prüfsumme. Liefert den lokalen Pfad.</summary>
    public async Task<string> DownloadAsync(string updateUrl, UpdateInfo info, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var msiUri = RequireHttps(new Uri(RequireHttps(updateUrl), info.Msi).AbsoluteUri);
        var folder = Path.Combine(Path.GetTempPath(), "brandline-update");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"brandline-{info.Version}-x64.msi");

        using (var response = await _http.GetAsync(msiUri, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var file = File.Create(target);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                if (total > 0) progress?.Report((double)done / total.Value);
            }
        }

        // Prüfsumme muss stimmen – sonst wird nichts installiert (beschädigter oder manipulierter Download).
        string actual;
        await using (var check = File.OpenRead(target))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(check, cancel));
        if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new InvalidOperationException("Prüfsumme des Downloads stimmt nicht – Update abgebrochen.");
        }
        return target;
    }

    /// <summary>
    /// Startet die Installation (mit Fortschrittsanzeige und Windows-Rückfrage für Administratorrechte) und
    /// danach brandline neu. Läuft über cmd.exe mit den Rechten des Benutzers, damit die neu gestartete App
    /// nicht mit Administratorrechten läuft. Das Installationspaket beendet die laufende App selbst.
    /// </summary>
    public static void StartInstall(string msiPath)
    {
        var exe = SystemIntegration.ExePath;
        // „start /wait“: erst nach Ende der Installation die (dann neue) brandline.exe starten.
        var command = $"/c start \"\" /wait msiexec /i \"{msiPath}\" /passive /norestart & start \"\" \"{exe}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    private static Uri RequireHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new InvalidOperationException($"Ungültige Update-Adresse „{url}“.");
        return RequireHttps(uri);
    }

    private static Uri RequireHttps(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps ? uri : throw new InvalidOperationException("Updates werden nur über HTTPS geladen.");

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public void Dispose() => _http.Dispose();
}
