using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brandline.Models;

namespace Brandline.Services;

/// <summary>Speichert Einstellungen und Adressbuch als JSON unter %APPDATA%\Brandline.</summary>
public static class Storage
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Brandline");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Einstellungen des Benutzers. Beim allerersten Start dienen die Vorgaben aus defaults.json neben der
    /// .exe (vom Installer abgelegt, von der IT anpassbar) als Ausgangswert – Zugangsdaten stehen dort nie.
    /// </summary>
    public static AppSettings LoadSettings()
    {
        var preset = LoadDefaults();
        if (Load<AppSettings>("settings.json") is { } settings)
        {
            // Bestehende Benutzer: zentral vorgegebene Werte übernehmen, die neu hinzugekommen sind und noch fehlen
            // (z. B. Microsoft-365-Anbindung nach einem Update) – eigene Eingaben werden nie überschrieben.
            if (preset != null && settings.M365ClientId.Length == 0 && settings.M365TenantId.Length == 0)
            {
                settings.M365ClientId = preset.M365ClientId;
                settings.M365TenantId = preset.M365TenantId;
            }
            return settings;
        }
        return preset ?? new AppSettings();
    }

    /// <summary>Vorgaben aus defaults.json im Installationsordner (ohne Zugangsdaten), sonst null.</summary>
    private static AppSettings? LoadDefaults()
    {
        var defaults = Path.Combine(AppContext.BaseDirectory, "defaults.json");
        if (!File.Exists(defaults)) return null;
        try
        {
            var preset = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(defaults), Json);
            if (preset != null) preset.EncryptedPassword = "";
            return preset;
        }
        catch (JsonException)
        {
            return null; // Fehlerhafte Vorgabedatei ignorieren.
        }
    }
    public static void SaveSettings(AppSettings settings) => Save("settings.json", settings);

    public static List<Contact> LoadContacts() => Load<List<Contact>>("contacts.json") ?? [];
    /// <summary>Speichert nur eigene Kontakte; synchronisierte stehen in m365-contacts.json.</summary>
    public static void SaveContacts(IEnumerable<Contact> contacts) => Save("contacts.json", contacts.Where(c => !c.IsSynced).ToList());

    public static List<Contact> LoadSyncedContacts() => Load<List<Contact>>("m365-contacts.json") ?? [];
    public static void SaveSyncedContacts(IEnumerable<Contact> contacts) => Save("m365-contacts.json", contacts.ToList());

    public const int MaxCallRecords = 500;
    public static List<CallRecord> LoadCalls() => Load<List<CallRecord>>("calls.json") ?? [];
    public static void SaveCalls(IEnumerable<CallRecord> calls) => Save("calls.json", calls.Take(MaxCallRecords).ToList());

    private static T? Load<T>(string file)
    {
        var path = Path.Combine(Folder, file);
        if (!File.Exists(path)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            // Defekte Datei nicht überschreiben, sondern zur Seite legen.
            File.Copy(path, path + ".broken", overwrite: true);
            return default;
        }
    }

    private static void Save<T>(string file, T value)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, file);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Verschlüsselt Geheimnisse an das Windows-Benutzerkonto gebunden (DPAPI).</summary>
public static class Secret
{
    // Nicht ändern: bereits gespeicherte Passwörter sind damit verschlüsselt (Name stammt aus der Vorversion).
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BcSoftphone.v1");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string Unprotect(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }
}
