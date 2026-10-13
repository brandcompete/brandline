using System.Text.Json.Serialization;
using System.Windows.Media;
using Brandline.Services;

namespace Brandline.Models;

public enum PhoneType
{
    Mobile,
    Landline
}

/// <summary>Eine Rufnummer eines Kontakts.</summary>
public sealed class ContactNumber
{
    public ContactNumber() { }

    public ContactNumber(string number, PhoneType type = PhoneType.Mobile, string label = "")
    {
        Number = number;
        Type = type;
        Label = label;
    }

    public string Number { get; set; } = "";
    public PhoneType Type { get; set; } = PhoneType.Mobile;

    /// <summary>Optionaler Zusatz, z. B. „privat“.</summary>
    public string Label { get; set; } = "";

    /// <summary>Symbol aus „Segoe Fluent Icons“: Handy bzw. Festnetztelefon.</summary>
    [JsonIgnore]
    public string TypeIcon => Type == PhoneType.Mobile ? "" : "";

    [JsonIgnore]
    public string TypeLabel => (Type == PhoneType.Mobile ? "Mobil" : "Festnetz") + (Label.Length > 0 ? $" ({Label})" : "");
}

public sealed class Contact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Company { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>Alle Rufnummern; die erste ist die Hauptnummer.</summary>
    public List<ContactNumber> Numbers { get; set; } = [];

    /// <summary>
    /// Herkunft: leer = eigener Kontakt; „m365-directory“ (Firmenverzeichnis) bzw. „m365-outlook“
    /// (persönliche Outlook-Kontakte) = aus Microsoft 365 synchronisiert, dort gepflegt und hier schreibgeschützt.
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>Kennung in Microsoft 365 (Objekt-ID), nur bei synchronisierten Kontakten.</summary>
    public string ExternalId { get; set; } = "";

    /// <summary>Dateiname des Avatars im Ordner %APPDATA%\Brandline\avatars, leer = Initialen.</summary>
    public string Avatar { get; set; } = "";

    // ---------- Altdaten bis Version 0.7: genau eine Nummer pro Kontakt ----------

    [JsonPropertyName("Number"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyNumber { get; set; }

    [JsonPropertyName("NumberType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PhoneType? LegacyNumberType { get; set; }

    /// <summary>Alte Einzelnummer in die Nummernliste übernehmen (beim Laden).</summary>
    public Contact Upgrade()
    {
        if (Numbers.Count == 0 && !string.IsNullOrWhiteSpace(LegacyNumber))
            Numbers.Add(new ContactNumber(LegacyNumber, LegacyNumberType ?? PhoneType.Mobile));
        LegacyNumber = null;
        LegacyNumberType = null;
        return this;
    }

    // ---------- Abgeleitet (nicht gespeichert) ----------

    [JsonIgnore] public ContactNumber? Primary => Numbers.FirstOrDefault();

    /// <summary>Hauptnummer.</summary>
    [JsonIgnore] public string Number => Primary?.Number ?? "";

    [JsonIgnore] public string TypeIcon => Primary?.TypeIcon ?? "";
    [JsonIgnore] public string TypeLabel => Primary?.TypeLabel ?? "";

    /// <summary>Für die Liste: Hauptnummer, ggf. mit Anzahl weiterer Nummern.</summary>
    [JsonIgnore] public string NumberSummary => Numbers.Count > 1 ? $"{Number}  +{Numbers.Count - 1}" : Number;

    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            var numbers = Numbers.Count > 1 ? $"{Numbers.Count} Nummern" : TypeLabel;
            return string.IsNullOrWhiteSpace(Company) ? numbers : $"{Company} · {numbers}";
        }
    }

    public ContactNumber? FindNumber(string number) => Numbers.FirstOrDefault(n => PhoneNumber.SameNumber(n.Number, number));
    public bool HasNumber(string number) => FindNumber(number) != null;

    [JsonIgnore]
    public ImageSource? AvatarImage => Avatars.Load(Avatar);

    [JsonIgnore]
    public bool IsSynced => Source.Length > 0;

    [JsonIgnore]
    public string SourceLabel => Source switch
    {
        "m365-directory" => "Firmenverzeichnis (Microsoft 365)",
        "m365-outlook" => "Outlook-Kontakt (Microsoft 365)",
        _ => ""
    };
}
