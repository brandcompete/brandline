using Brandline.Services.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Brandline.Services;

/// <summary>Spielt Klingelton (eingehend, siehe <see cref="Ringtones"/>) bzw. Freiton (ausgehend) in Schleife auf einem wählbaren Gerät.</summary>
public sealed class TonePlayer : IDisposable
{
    private readonly object _sync = new();
    private DevicePlayer? _player;

    public void PlayRing(string deviceId, string ringtoneId) =>
        Play(deviceId, Ringtones.Create(ringtoneId), AudioStreamCategory.Alerts);
    public void PlayRingback(string deviceId) => Play(deviceId, ToneProvider.Ringback(), AudioStreamCategory.Communications);
    public void PlayCallWaiting(string deviceId) => Play(deviceId, ToneProvider.CallWaiting(), AudioStreamCategory.Communications);

    public void Stop()
    {
        lock (_sync)
        {
            _player?.Dispose();
            _player = null;
        }
    }

    private void Play(string deviceId, ISampleProvider tone, AudioStreamCategory category)
    {
        lock (_sync)
        {
            Stop();
            try
            {
                _player = new DevicePlayer(deviceId, tone, latencyMilliseconds: 100, category);
            }
            catch
            {
                // Ohne Ton weitermachen, der Anruf selbst darf daran nicht scheitern.
                _player = null;
            }
        }
    }

    public void Dispose() => Stop();

    private sealed class ToneProvider : ISampleProvider
    {
        private const int Rate = 16000;
        private const double Fade = 0.01;

        private readonly (double Start, double Length)[] _bursts;
        private readonly double _periodSeconds;
        private readonly float _amplitude;
        private readonly double _frequency;
        private long _sample;
        private double _phase;

        private ToneProvider((double Start, double Length)[] bursts, double period, float amplitude, double frequency)
        {
            _bursts = bursts;
            _periodSeconds = period;
            _amplitude = amplitude;
            _frequency = frequency;
        }

        /// <summary>Deutscher Freiton: 425 Hz, 1 s an, 4 s aus.</summary>
        public static ToneProvider Ringback() => new([(0, 1.0)], 5.0, 0.2f, 425);

        /// <summary>Anklopfton: 425 Hz, 2 × 200 ms mit 200 ms Pause, dann 5 s Ruhe – leise, läuft im Gespräch mit.</summary>
        public static ToneProvider CallWaiting() => new([(0, 0.2), (0.4, 0.2)], 5.6, 0.12f, 425);

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

        public int Read(Span<float> buffer)
        {
            for (var i = 0; i < buffer.Length; i++, _sample++)
            {
                var t = (double)_sample / Rate % _periodSeconds;
                var burst = Array.FindIndex(_bursts, b => t >= b.Start && t < b.Start + b.Length);
                if (burst < 0)
                {
                    buffer[i] = 0;
                    _phase = 0;
                    continue;
                }

                var (start, length) = _bursts[burst];
                var inBurst = t - start;
                var envelope = Math.Min(1.0, Math.Min(inBurst, length - inBurst) / Fade);
                _phase += 2 * Math.PI * _frequency / Rate;
                buffer[i] = (float)(Math.Sin(_phase) * envelope) * _amplitude;
            }
            return buffer.Length;
        }
    }
}