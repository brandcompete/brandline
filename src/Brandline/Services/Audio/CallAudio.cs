using System.Net;
using NAudio.CoreAudioApi;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace Brandline.Services.Audio;

/// <summary>
/// Audio eines Gesprächs: Mikrofon (mit Echounterdrückung, falls möglich) → Codec → RTP,
/// RTP → Jitter-Puffer → Lautsprecher. Wird von der SIPSorcery-Mediensitzung gesteuert.
/// </summary>
public sealed class CallAudio : IAudioSource, IAudioSink, IDisposable
{
    // Reihenfolge = Präferenz bei der SDP-Aushandlung: G.722 (HD), dann G.711 a-law/µ-law.
    private static readonly AudioFormat[] SupportedFormats =
    [
        new(SDPWellKnownMediaFormatsEnum.G722),
        new(SDPWellKnownMediaFormatsEnum.PCMA),
        new(SDPWellKnownMediaFormatsEnum.PCMU),
    ];

    private readonly string _microphoneId;
    private readonly string _speakerId;
    private readonly bool _echoCancellation;
    private readonly AudioEncoder _encoder = new();
    private readonly Resampler2x _downsampler = new();
    private readonly JitterBuffer _jitter = new();
    private readonly object _sync = new();

    private List<AudioFormat> _formats = [.. SupportedFormats];
    private AudioFormat _sourceFormat = SupportedFormats[0];
    private DevicePlayer? _player;
    private IMicCapture? _mic;
    private bool _sourcePaused;
    private bool _closed;
    private bool _suspended;

    public CallAudio(string microphoneId, string speakerId, bool echoCancellation)
    {
        _microphoneId = microphoneId;
        _speakerId = speakerId;
        _echoCancellation = echoCancellation;
    }

    public bool IsMuted { get; set; }

    /// <summary>True, wenn die Windows-Echounterdrückung aktiv ist.</summary>
    public bool EchoCancellationActive { get; private set; }

    public double JitterMs => _jitter.JitterMs;
    public double LossPercent => _jitter.LossPercent;
    public int BufferDelayMs => _jitter.TargetDelayMs;

    public event Action<string>? Warning;
    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
    public event SourceErrorDelegate? OnAudioSourceError;
    public event SourceErrorDelegate? OnAudioSinkError;
#pragma warning disable CS0067 // Von der Schnittstelle verlangt, hier nicht benötigt.
    public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady;
    public event RawAudioSampleDelegate? OnAudioSourceRawSample;
#pragma warning restore CS0067

    // ---------------- Empfang ----------------

    /// <summary>Wird mit jedem empfangenen RTP-Paket der Mediensitzung aufgerufen.</summary>
    public void ReceiveRtp(RTPPacket packet)
    {
        // Andere Payload-Typen (z. B. DTMF-Ereignisse) ignorieren.
        if (TryGetFormat(packet.Header.PayloadType, out var format))
            _jitter.Add(packet.Header.SequenceNumber, packet.Header.Timestamp, format, packet.Payload);
    }

    // Die Mediensitzung liefert dekodierte Frames ohne Sequenznummer; wir verwenden stattdessen
    // die RTP-Pakete (ReceiveRtp), damit der Jitter-Puffer umsortieren und Verluste erkennen kann.
    public void GotEncodedMediaFrame(EncodedAudioFrame encodedMediaFrame) { }

    public void GotAudioRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[] payload)
    {
        if (TryGetFormat(payloadID, out var format)) _jitter.Add((ushort)seqnum, timestamp, format, payload);
    }

    private static bool TryGetFormat(int payloadType, out AudioFormat format)
    {
        foreach (var f in SupportedFormats)
        {
            if (f.FormatID != payloadType) continue;
            format = f;
            return true;
        }
        format = default!;
        return false;
    }

    public List<AudioFormat> GetAudioSinkFormats() => _formats;
    public void SetAudioSinkFormat(AudioFormat audioFormat) { }

    public Task StartAudioSink()
    {
        Start();
        return Task.CompletedTask;
    }

    public Task PauseAudioSink()
    {
        _jitter.Paused = true;
        return Task.CompletedTask;
    }

    public Task ResumeAudioSink()
    {
        _jitter.Paused = false;
        return Task.CompletedTask;
    }

    public Task CloseAudioSink()
    {
        Dispose();
        return Task.CompletedTask;
    }

    // ---------------- Senden ----------------

    public List<AudioFormat> GetAudioSourceFormats() => _formats;
    public void SetAudioSourceFormat(AudioFormat audioFormat) => _sourceFormat = audioFormat;
    public void RestrictFormats(Func<AudioFormat, bool> filter) => _formats = _formats.Where(filter).ToList();

    public Task StartAudio()
    {
        _sourcePaused = false;
        Start();
        return Task.CompletedTask;
    }

    public Task PauseAudio()
    {
        _sourcePaused = true;
        return Task.CompletedTask;
    }

    public Task ResumeAudio()
    {
        _sourcePaused = false;
        return Task.CompletedTask;
    }

    public Task CloseAudio()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public bool IsAudioSourcePaused() => _sourcePaused;
    public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;
    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample) { }

    private void OnMicrophoneFrame(short[] frame16k)
    {
        if (_sourcePaused) return;
        var format = _sourceFormat;

        var pcm = IsMuted ? new short[frame16k.Length] : frame16k;
        if (format.ClockRate == 8000) pcm = _downsampler.Down(pcm);

        var encoded = _encoder.EncodeAudio(pcm, format);
        var durationRtpUnits = (uint)((long)pcm.Length * format.RtpClockRate / format.ClockRate);
        OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, encoded);
    }

    // ---------------- Geräte ----------------

    /// <summary>Startet erst die Wiedergabe (die Echounterdrückung braucht sie als Referenz), dann das Mikrofon.</summary>
    private void Start()
    {
        lock (_sync)
        {
            if (_closed || _suspended || _player != null) return;

            try
            {
                _player = new DevicePlayer(_speakerId, _jitter, latencyMilliseconds: 40);
            }
            catch (Exception ex)
            {
                OnAudioSinkError?.Invoke($"Lautsprecher konnte nicht geöffnet werden: {ex.Message}");
            }

            _mic = OpenMicrophone();
        }
    }

    private IMicCapture? OpenMicrophone()
    {
        if (_echoCancellation && _player != null)
        {
            try
            {
                using var micDevice = AudioDevices.Open(DataFlow.Capture, _microphoneId);
                var aec = new EchoCancellingCapture(micDevice.ID, _player.Device.ID);
                aec.FrameReady += OnMicrophoneFrame;
                // Fällt das DSP während des Gesprächs aus, ohne Unterbrechung auf das normale Mikrofon wechseln.
                // Eigener Task, weil das Ereignis aus dem Aufnahme-Thread kommt, den Dispose() beenden muss.
                aec.Failed += msg => Task.Run(() => FallBackToPlainMicrophone(msg));
                aec.Start();
                EchoCancellationActive = true;
                return aec;
            }
            catch (Exception ex)
            {
                Warning?.Invoke($"Echounterdrückung nicht verfügbar ({ex.Message}) – nutze normales Mikrofon.");
            }
        }
        return OpenPlainMicrophone();
    }

    private void FallBackToPlainMicrophone(string reason)
    {
        lock (_sync)
        {
            if (_closed || _suspended) return;
            _mic?.Dispose();
            EchoCancellationActive = false;
            _mic = OpenPlainMicrophone();
        }
        Warning?.Invoke($"{reason} – nutze normales Mikrofon.");
    }

    private IMicCapture? OpenPlainMicrophone()
    {
        try
        {
            var mic = new WasapiMicCapture(_microphoneId);
            mic.FrameReady += OnMicrophoneFrame;
            mic.Failed += msg => OnAudioSourceError?.Invoke(msg);
            mic.Start();
            return mic;
        }
        catch (Exception ex)
        {
            OnAudioSourceError?.Invoke($"Mikrofon konnte nicht geöffnet werden: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gehaltenes Gespräch: Mikrofon und Lautsprecher freigeben, damit ein zweites Gespräch sie nutzen kann
    /// (die Echounterdrückung darf nur einmal pro Gerät laufen). Die Sitzung selbst bleibt bestehen.
    /// </summary>
    public void SuspendDevices()
    {
        lock (_sync) _suspended = true;
        ReleaseDevices();
    }

    /// <summary>
    /// Geräte außerhalb der Sperre schließen: NAudio wartet beim Beenden auf den Aufnahme-Thread, was bei
    /// gerade erst gestarteten Geräten dauern kann. Hielte das die Sperre, stünde der SIP-Empfang
    /// (Start() aus der Antwort auf ein Re-INVITE) so lange still.
    /// </summary>
    private void ReleaseDevices()
    {
        IMicCapture? mic;
        DevicePlayer? player;
        lock (_sync)
        {
            mic = _mic;
            _mic = null;
            player = _player;
            _player = null;
        }
        mic?.Dispose();
        player?.Dispose();
    }

    /// <summary>Gespräch fortsetzen: Geräte wieder öffnen.</summary>
    public void ResumeDevices()
    {
        lock (_sync) _suspended = false;
        Start();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
        }
        ReleaseDevices();
        _encoder.Dispose();
        _jitter.Dispose();
    }
}
