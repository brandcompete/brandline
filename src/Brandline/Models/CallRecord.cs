namespace Brandline.Models;

public enum CallDirection
{
    Incoming,
    Outgoing
}

public enum CallOutcome
{
    /// <summary>Gespräch kam zustande.</summary>
    Answered,

    /// <summary>Eingehend, nicht angenommen (Anrufer hat aufgelegt, oder wir waren besetzt).</summary>
    Missed,

    /// <summary>Eingehend, von uns abgelehnt (auch „Nicht stören“).</summary>
    Rejected,

    /// <summary>Ausgehend, Gegenseite nicht erreicht.</summary>
    NotReached
}

public sealed class CallRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public CallDirection Direction { get; set; }
    public CallOutcome Outcome { get; set; }
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime Start { get; set; }
    public TimeSpan Duration { get; set; }
    public string Note { get; set; } = "";

    /// <summary>Verpasster Anruf, den der Benutzer noch nicht in der Anrufliste gesehen hat.</summary>
    public bool Unseen { get; set; }

    public string Symbol => (Direction, Outcome) switch
    {
        (_, CallOutcome.Missed) => "✗",
        (_, CallOutcome.Rejected) => "⊘",
        (CallDirection.Incoming, _) => "↙",
        _ => "↗"
    };

    public string Display => string.IsNullOrWhiteSpace(Name) ? (Number.Length > 0 ? Number : "Unbekannt") : Name;

    public string Description => (Direction, Outcome) switch
    {
        (_, CallOutcome.Missed) => "Verpasst",
        (_, CallOutcome.Rejected) => "Abgelehnt",
        (CallDirection.Incoming, _) => "Eingehend",
        (CallDirection.Outgoing, CallOutcome.NotReached) => "Nicht erreicht",
        _ => "Ausgehend"
    } + (Note.Length > 0 ? $" – {Note}" : "");

    public string When => Start.Date == DateTime.Today
        ? Start.ToString("HH:mm")
        : Start.Date == DateTime.Today.AddDays(-1) ? "Gestern " + Start.ToString("HH:mm") : Start.ToString("dd.MM. HH:mm");

    public string DurationText => Outcome == CallOutcome.Answered ? Duration.ToString(Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss") : "";
}
