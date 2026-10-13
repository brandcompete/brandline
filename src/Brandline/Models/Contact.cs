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
