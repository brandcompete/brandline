using System.Diagnostics;
using NAudio.Wave;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace Brandline.Services.Audio;

/// <summary>
/// Adaptiver Jitter-Puffer. Empfangene RTP-Pakete werden nach Sequenznummer einsortiert und
/// vom Wiedergabegerät im eigenen Takt abgeholt (16 kHz mono). Verspätete Pakete werden verworfen,
/// verlorene kurz verschleiert (letzten Frame leiser wiederholen). Die Zielverzögerung passt sich
/// der gemessenen Laufzeitschwankung an (RFC 3550).
/// </summary>
public sealed class JitterBuffer : ISampleProvider, IDisposable
{
    public const int OutputRate = 16000;
    private const int MinDelayMs = 40;
    private const int MaxDelayMs = 300;
    private const int MaxConcealedFrames = 3;

    private readonly object _sync = new();
    private readonly SortedDictionary<long, (AudioFormat Format, byte[] Payload)> _packets = new();
    private readonly AudioEncoder _decoder = new();
    private readonly Resampler2x _upsampler = new();

    // Sequenznummern-Verwaltung (16 Bit mit Überlauf → 64 Bit).
    private long _highestExt = -1;
    private ushort _highestSeq;
    private long _nextExt = -1;

    // Laufzeitschwankung nach RFC 3550.
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastArrivalMs;
    private uint _lastTimestamp;
    private double _jitterMs;

    // Wiedergabe.
    private bool _buffering = true;
    private float[] _current = [];
    private int _currentPos;
    private float[] _lastFrame = new float[320];
    private int _concealed;
    private double _frameMs = 20;

    // Statistik.
    private long _received;
    private long _lost;
    private long _late;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(OutputRate, 1);

    public bool Paused { get; set; }

    public double JitterMs { get { lock (_sync) return _jitterMs; } }

    public double LossPercent
    {
        get
        {
            lock (_sync)
            {
                var total = _received + _lost;
                return total == 0 ? 0 : 100.0 * (_lost + _late) / total;
            }
        }
    }

    public int TargetDelayMs { get { lock (_sync) return TargetDelay(); } }

    public void Add(ushort sequence, uint timestamp, AudioFormat format, byte[] payload)
    {
        if (payload.Length == 0) return;
        lock (_sync)
        {
            long ext;
            if (_highestExt < 0)
            {
                ext = sequence;
            }
            else
            {
                var delta = (short)(sequence - _highestSeq);
                if (Math.Abs((int)delta) > 1000)
                {
                    // Neuer Strom (z. B. nach Halten/Weiterleiten): neu beginnen.
                    ResetLocked();
                    ext = sequence;
                }
                else
                {
                    ext = _highestExt + delta;
                }
            }

            if (ext > _highestExt)
            {
                UpdateJitter(timestamp, format);
                _highestExt = ext;
                _highestSeq = sequence;
            }

            _received++;
            if (_nextExt >= 0 && ext < _nextExt)
            {
                _late++;
                return;
            }
            _packets[ext] = (format, payload);
        }
    }

    public int Read(Span<float> buffer)
    {
        lock (_sync)
        {
            var written = 0;
            while (written < buffer.Length)
            {
                if (_currentPos >= _current.Length)
                {
                    _current = NextFrame();
                    _currentPos = 0;
                }
                var n = Math.Min(buffer.Length - written, _current.Length - _currentPos);
                _current.AsSpan(_currentPos, n).CopyTo(buffer.Slice(written, n));
                _currentPos += n;
                written += n;
            }
            return buffer.Length;
        }
    }

    private float[] NextFrame()
    {
        var silence = new float[(int)(OutputRate * _frameMs / 1000)];
        if (Paused) return silence;

        if (_buffering)
        {
            if (_packets.Count == 0 || BufferedMs() < TargetDelay()) return silence;
            _buffering = false;
            _nextExt = _packets.Keys.First();
        }

        // Zu viel gepuffert (Gegenseite sendet schneller, oder nach Stau): auf Zielwert kürzen.
        while (BufferedMs() > TargetDelay() + 120 && _packets.Count > 1)
        {
            var oldest = _packets.Keys.First();
            _packets.Remove(oldest);
            _nextExt = Math.Max(_nextExt, oldest + 1);
        }

        if (_packets.Count == 0)
        {
            // Puffer leer gelaufen: kurz verschleiern, dann neu puffern.
            _buffering = true;
            return Conceal();
        }

        var first = _packets.Keys.First();
        if (first > _nextExt + 50) _nextExt = first; // großer Sprung → nicht ewig verschleiern

        if (_packets.Remove(_nextExt, out var packet))
        {
            _nextExt++;
            _concealed = 0;
            var frame = Decode(packet.Format, packet.Payload);
            _lastFrame = frame;
            _frameMs = frame.Length * 1000.0 / OutputRate;
            return frame;
        }

        // Paket fehlt, spätere sind schon da → verloren.
        _lost++;
        _nextExt++;
        return Conceal();
    }

    private float[] Conceal()
    {
        _concealed++;
        if (_concealed > MaxConcealedFrames) return new float[_lastFrame.Length];
        var gain = MathF.Pow(0.5f, _concealed);
        var frame = new float[_lastFrame.Length];
        for (var i = 0; i < frame.Length; i++) frame[i] = _lastFrame[i] * gain;
        return frame;
    }

    private float[] Decode(AudioFormat format, byte[] payload)
    {
        var pcm = _decoder.DecodeAudio(payload, format);
        var samples = new float[pcm.Length];
        for (var i = 0; i < pcm.Length; i++) samples[i] = pcm[i] / 32768f;
        return format.ClockRate == OutputRate ? samples : _upsampler.Up(samples);
    }

    private void UpdateJitter(uint timestamp, AudioFormat format)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        if (_lastArrivalMs > 0)
        {
            var tsDeltaMs = (int)(timestamp - _lastTimestamp) * 1000.0 / format.RtpClockRate;
            var d = Math.Abs((now - _lastArrivalMs) - tsDeltaMs);
            if (d < 2000) _jitterMs += (d - _jitterMs) / 16;
        }
        _lastArrivalMs = now;
        _lastTimestamp = timestamp;
    }

    private int TargetDelay() => (int)Math.Clamp(_frameMs + 3 * _jitterMs, MinDelayMs, MaxDelayMs);

    private double BufferedMs() => _packets.Count * _frameMs;

    private void ResetLocked()
    {
        _packets.Clear();
        _highestExt = -1;
        _nextExt = -1;
        _buffering = true;
        _lastArrivalMs = 0;
    }

    public void Dispose() => _decoder.Dispose();
}
