using System.Text.Json.Serialization;
using System.Windows.Media;
using Brandline.Services;

namespace Brandline.Models;

public enum PhoneType
{
    Mobile,
    Landline
}

public sealed class Contact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Number { get; set; } = "";
    public PhoneType NumberType { get; set; } = PhoneType.Mobile;
    public string Company { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>
    /// Herkunft: leer = eigener Kontakt; „m365-directory“ (Firmenverzeichnis) bzw. „m365-outlook“
    /// (persönliche Outlook-Kontakte) = aus Microsoft 365 synchronisiert, dort gepflegt und hier schreibgeschützt.
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>Kennung in Microsoft 365 (Objekt-ID + Nummernart), nur bei synchronisierten Kontakten.</summary>
    public string ExternalId { get; set; } = "";

    [JsonIgnore]
    public bool IsSynced => Source.Length > 0;

    [JsonIgnore]
    public string SourceLabel => Source switch
    {
        "m365-directory" => "Firmenverzeichnis (Microsoft 365)",
        "m365-outlook" => "Outlook-Kontakt (Microsoft 365)",
        _ => ""
    };

    /// <summary>Dateiname des Avatars im Ordner %APPDATA%\Brandline\avatars, leer = Initialen.</summary>
    public string Avatar { get; set; } = "";

    [JsonIgnore]
    public ImageSource? AvatarImage => Avatars.Load(Avatar);

    /// <summary>Symbol aus „Segoe Fluent Icons“: Handy bzw. Festnetztelefon.</summary>
    [JsonIgnore]
    public string TypeIcon => NumberType == PhoneType.Mobile ? "" : "";

    [JsonIgnore]
    public string TypeLabel => NumberType == PhoneType.Mobile ? "Mobil" : "Festnetz";

    [JsonIgnore]
    public string Subtitle => string.IsNullOrWhiteSpace(Company) ? TypeLabel : $"{Company} · {TypeLabel}";
}
