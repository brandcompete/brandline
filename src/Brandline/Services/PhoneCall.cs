using Brandline.Models;
using Brandline.Services.Audio;
using SIPSorcery.Media;
using SIPSorcery.SIP.App;

namespace Brandline.Services;

/// <summary>
/// Ein einzelnes Gespräch. <see cref="SipPhone"/> verwaltet bis zu zwei davon gleichzeitig: ein aktives und
/// ein gehaltenes (Rückfrage, Makeln) bzw. ein aktives und ein anklopfendes.
/// Eigenschaften werden von SipPhone gesetzt; die Oberfläche liest sie nur.
/// </summary>
public sealed class PhoneCall
{
    internal PhoneCall(SIPUserAgent ua, CallDirection direction, CallParty remote)
    {
        Ua = ua;
        Direction = direction;
        Remote = remote;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public CallDirection Direction { get; }
    public CallParty Remote { get; internal set; }
    public CallState State { get; internal set; } = CallState.Dialing;

    /// <summary>Kurzer Status für die Anzeige („Es klingelt …“, „Verbunden“ …).</summary>
    public string Info { get; internal set; } = "";

    public DateTime Start { get; } = DateTime.Now;
    public DateTime? AnsweredAt { get; internal set; }

    /// <summary>Von uns gehalten.</summary>
    public bool IsOnHold { get; internal set; }

    /// <summary>Von der Gegenseite gehalten.</summary>
    public bool IsHeldByRemote { get; internal set; }

    /// <summary>Mit unterdrückter Rufnummer gewählt.</summary>
    public bool Anonymous { get; internal set; }

    /// <summary>Rückfragegespräch: der gehaltene Anruf, der an dieses Gespräch übergeben werden kann.</summary>
    public PhoneCall? ConsultationFor { get; internal set; }

    public bool IsMuted => Audio?.IsMuted ?? false;
    public bool IsConnected => State == CallState.Connected;
    public bool IsRinging => State == CallState.Incoming;

    public CallQuality? Quality => Audio is { } a && State == CallState.Connected && !IsOnHold
        ? new CallQuality(a.JitterMs, a.LossPercent, a.BufferDelayMs, a.EchoCancellationActive, Media?.IsSecureContextReady() == true)
        : null;

    internal SIPUserAgent Ua { get; }
    internal SIPServerUserAgent? PendingIncoming { get; set; }
    internal VoIPMediaSession? Media { get; set; }
    internal CallAudio? Audio { get; set; }
    internal bool LocalRingback { get; set; }
    internal bool LocalHangup { get; set; }
    internal bool RejectedByUser { get; set; }
    internal string? TransferredTo { get; set; }
}
