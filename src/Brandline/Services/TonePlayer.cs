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

        private readonly double _onSeconds;
        private readonly double _periodSeconds;
        private readonly float _amplitude;
        private readonly Func<double, double> _frequencyAt;
        private long _sample;
        private double _phase;

        private ToneProvider(double on, double period, float amplitude, Func<double, double> frequencyAt)
        {
            _onSeconds = on;
            _periodSeconds = period;
            _amplitude = amplitude;
            _frequencyAt = frequencyAt;
        }

        /// <summary>Deutscher Freiton: 425 Hz, 1 s an, 4 s aus.</summary>
        public static ToneProvider Ringback() => new(1.0, 5.0, 0.2f, _ => 425);

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

        public int Read(Span<float> buffer)
        {
            for (var i = 0; i < buffer.Length; i++, _sample++)
            {
                var t = (double)_sample / Rate % _periodSeconds;
                if (t >= _onSeconds)
                {
                    buffer[i] = 0;
                    _phase = 0;
                    continue;
                }

                var envelope = Math.Min(1.0, Math.Min(t, _onSeconds - t) / Fade);
                _phase += 2 * Math.PI * _frequencyAt(t) / Rate;
                buffer[i] = (float)(Math.Sin(_phase) * envelope) * _amplitude;
            }
            return buffer.Length;
        }
    }
}
