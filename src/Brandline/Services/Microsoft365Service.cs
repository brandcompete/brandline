using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Brandline.Models;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Brandline.Services;

public sealed record M365SyncResult(List<Contact> Contacts, int Directory, int Outlook, int Photos);

/// <summary>
/// Anmeldung bei Microsoft 365 (MSAL mit Windows-Anmeldebroker WAM) und Abruf der Kontakte über Microsoft Graph.
/// Das Token wird verschlüsselt (DPAPI) unter %APPDATA%\Brandline\msal.cache abgelegt.
/// Benötigte delegierte Berechtigungen: User.Read, Contacts.Read, User.Read.All (Administratorzustimmung).
/// </summary>
public sealed class Microsoft365Service : IDisposable
{
    private static readonly string[] Scopes = ["User.Read", "Contacts.Read", "User.Read.All"];

    private readonly HttpClient _http;
    private IPublicClientApplication? _app;
    private string? _appKey;

    public Microsoft365Service(HttpMessageHandler? handler = null)
    {
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(2);
    }

    public static bool IsConfigured(AppSettings s) =>
        Guid.TryParse(s.M365ClientId, out _) && !string.IsNullOrWhiteSpace(s.M365TenantId);

    // ---------------- Anmeldung ----------------

    private async Task<IPublicClientApplication> GetAppAsync(AppSettings s)
    {
        if (!IsConfigured(s)) throw new InvalidOperationException("Microsoft 365 ist nicht eingerichtet (Client-ID/Mandanten-ID fehlt).");
        var key = $"{s.M365ClientId}|{s.M365TenantId}";
        if (_app != null && _appKey == key) return _app;

        var app = PublicClientApplicationBuilder.Create(s.M365ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, s.M365TenantId)
            .WithDefaultRedirectUri()
            // Windows-Anmeldebroker: einmaliges Anmelden mit dem Windows-/Entra-Konto, ohne Passwortdialog im Browser.
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "brandline" })
            .Build();

        Directory.CreateDirectory(Storage.Folder);
        var cache = await MsalCacheHelper.CreateAsync(
            new StorageCreationPropertiesBuilder("msal.cache", Storage.Folder).Build());
        cache.RegisterCache(app.UserTokenCache);

        _app = app;
        _appKey = key;
        return app;
    }

    /// <summary>Angemeldetes Konto (aus dem Cache), sonst null.</summary>
    public async Task<string?> GetSignedInUserAsync(AppSettings s)
    {
        if (!IsConfigured(s)) return null;
        var accounts = await (await GetAppAsync(s)).GetAccountsAsync();
        return accounts.FirstOrDefault()?.Username;
    }

    /// <summary>Interaktive Anmeldung; <paramref name="window"/> ist das Fenster, über dem der Dialog erscheint.</summary>
    public async Task<string> SignInAsync(AppSettings s, IntPtr window)
    {
        var app = await GetAppAsync(s);
        var result = await app.AcquireTokenInteractive(Scopes)
            .WithParentActivityOrWindow(window)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync();
        return result.Account.Username;
    }

    public async Task SignOutAsync(AppSettings s)
    {
        if (!IsConfigured(s)) return;
        var app = await GetAppAsync(s);
        foreach (var account in await app.GetAccountsAsync()) await app.RemoveAsync(account);
    }

    /// <summary>
    /// Token ohne Rückfrage: angemeldetes Konto aus dem Cache oder – falls erlaubt – das Windows-Konto.
    /// </summary>
    /// <exception cref="MsalUiRequiredException">Anmeldung durch den Benutzer nötig.</exception>
    private async Task<string> GetTokenSilentAsync(AppSettings s)
    {
        var app = await GetAppAsync(s);
        var account = (await app.GetAccountsAsync()).FirstOrDefault();
        if (account == null && !s.M365AutoSignIn)
            throw new MsalUiRequiredException("no_account", "Nicht bei Microsoft 365 angemeldet.");
        var result = await app.AcquireTokenSilent(Scopes, account ?? PublicClientApplication.OperatingSystemAccount).ExecuteAsync();
        return result.AccessToken;
    }

    // ---------------- Kontakte ----------------

    /// <summary>Kontakte laden; wirft <see cref="MsalUiRequiredException"/>, wenn eine Anmeldung nötig ist.</summary>
    public Task<M365SyncResult> SyncAsync(AppSettings s, CancellationToken cancel = default) =>
        LoadContactsAsync(() => GetTokenSilentAsync(s), s.M365SyncDirectory, s.M365SyncOutlook, loadPhotos: true, cancel);

    /// <summary>Graph-Abruf, getrennt von der Anmeldung (für Tests mit eigenem Token und HttpMessageHandler).</summary>
    public async Task<M365SyncResult> LoadContactsAsync(Func<Task<string>> token, bool directory, bool outlook,
        bool loadPhotos, CancellationToken cancel = default)
    {
        var contacts = new List<Contact>();
        int directoryCount = 0, outlookCount = 0, photos = 0;

        if (directory)
        {
            const string url = "https://graph.microsoft.com/v1.0/users?$top=999&$select=id,displayName,companyName,department,jobTitle,businessPhones,mobilePhone,accountEnabled,userType";
            foreach (var user in await GetAllAsync(url, token, cancel))
            {
                // Deaktivierte Konten und Gäste auslassen.
                if (user.TryGetProperty("accountEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.False) continue;
                if (Str(user, "userType") == "Guest") continue;

                var id = Str(user, "id");
                if (Map(user, "m365-directory", Str(user, "companyName"), Join(Str(user, "jobTitle"), Str(user, "department"))) is not { } person)
                    continue;

                if (loadPhotos && await LoadPhotoAsync($"https://graph.microsoft.com/v1.0/users/{id}/photos/96x96/$value", $"m365-{id}.png", token, cancel) is { } avatar)
                {
                    photos++;
                    person.Avatar = avatar;
                }
                contacts.Add(person);
                directoryCount++;
            }
        }

        if (outlook)
        {
            const string url = "https://graph.microsoft.com/v1.0/me/contacts?$top=999&$select=id,displayName,companyName,jobTitle,businessPhones,mobilePhone,homePhones";
            foreach (var contact in await GetAllAsync(url, token, cancel))
            {
                if (Map(contact, "m365-outlook", Str(contact, "companyName"), Str(contact, "jobTitle")) is not { } person) continue;
                contacts.Add(person);
                outlookCount++;
            }
        }

        return new M365SyncResult(contacts, directoryCount, outlookCount, photos);
    }

    /// <summary>
    /// Ein Kontakt pro Person mit allen Rufnummern: Mobil, Geschäftlich (Festnetz), Privat (Festnetz).
    /// Null, wenn keine Rufnummer vorhanden ist.
    /// </summary>
    private static Contact? Map(JsonElement item, string source, string company, string note)
    {
        var name = Str(item, "displayName");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var person = new Contact { Name = name, Company = company, Note = note, Source = source, ExternalId = Str(item, "id") };

        void Add(string number, PhoneType type, string label = "")
        {
            if (PhoneNumber.ForDialing(number).Length < 3 || person.HasNumber(number)) return;
            person.Numbers.Add(new ContactNumber(number.Trim(), type, label));
        }

        Add(Str(item, "mobilePhone"), PhoneType.Mobile);
        foreach (var n in Strings(item, "businessPhones")) Add(n, PhoneType.Landline);
        foreach (var n in Strings(item, "homePhones")) Add(n, PhoneType.Landline, "privat");
        return person.Numbers.Count > 0 ? person : null;
    }
    /// <summary>Alle Seiten einer Graph-Liste abrufen (@odata.nextLink).</summary>
    private async Task<List<JsonElement>> GetAllAsync(string url, Func<Task<string>> token, CancellationToken cancel)
    {
        var items = new List<JsonElement>();
        string? next = url;
        while (next != null)
        {
            using var response = await SendAsync(next, token, cancel);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException("Keine Berechtigung – in Entra ID fehlt die Administratorzustimmung für die App „brandline“.");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            if (doc.RootElement.TryGetProperty("value", out var value))
                items.AddRange(value.EnumerateArray().Select(e => e.Clone()));
            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
        return items;
    }

    private async Task<string?> LoadPhotoAsync(string url, string fileName, Func<Task<string>> token, CancellationToken cancel)
    {
        // Fotos höchstens einmal pro Woche neu laden.
        var path = Path.Combine(Avatars.Folder, fileName);
        if (File.Exists(path) && File.GetLastWriteTime(path) > DateTime.Now.AddDays(-7)) return fileName;
        try
        {
            using var response = await SendAsync(url, token, cancel);
            if (!response.IsSuccessStatusCode) return File.Exists(path) ? fileName : null; // 404 = kein Foto
            return Avatars.ImportBytes(await response.Content.ReadAsByteArrayAsync(cancel), fileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>GET mit Bearer-Token; bei Drosselung (429/503) einmal nach der angegebenen Wartezeit wiederholen.</summary>
    private async Task<HttpResponseMessage> SendAsync(string url, Func<Task<string>> token, CancellationToken cancel)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token());
            var response = await _http.SendAsync(request, cancel);
            if (attempt > 0 || (response.StatusCode != HttpStatusCode.TooManyRequests && response.StatusCode != HttpStatusCode.ServiceUnavailable))
                return response;
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
            response.Dispose();
            await Task.Delay(wait < TimeSpan.FromSeconds(60) ? wait : TimeSpan.FromSeconds(60), cancel);
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static IEnumerable<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "")
            : [];

    private static string Join(params string[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    public void Dispose() => _http.Dispose();
}
