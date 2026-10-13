using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Brandline.Services.Audio;

/// <summary>
/// Spielt eine Mono-Quelle über WASAPI (gemeinsamer Modus, Kategorie „Kommunikation“) ab und passt
/// Abtastrate und Kanalzahl selbst an das Mischformat des Geräts an.
/// </summary>
public sealed class DevicePlayer : IDisposable
{
    private readonly WasapiPlayer _player;

    public DevicePlayer(string storedDeviceId, ISampleProvider monoSource, int latencyMilliseconds = 60,
        AudioStreamCategory category = AudioStreamCategory.Communications)
    {
        Device = AudioDevices.Open(DataFlow.Render, storedDeviceId);
        WaveFormat mix;
        using (var client = Device.CreateAudioClient()) mix = client.MixFormat;

        ISampleProvider chain = monoSource;
        if (chain.WaveFormat.SampleRate != mix.SampleRate)
            chain = new WdlResamplingSampleProvider(chain, mix.SampleRate);
        if (mix.Channels > 1)
            chain = new MonoToMultiChannelProvider(chain, mix.Channels);

        _player = new WasapiPlayerBuilder()
            .WithDevice(Device)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(latencyMilliseconds)
            .WithCategory(category)
            .Build();
        _player.Init(new SampleToWaveProvider(chain));
        _player.Play();
    }

    /// <summary>Tatsächlich verwendetes Gerät (nach Rückfall auf das Standardgerät).</summary>
    public MMDevice Device { get; }

    public void Dispose()
    {
        try { _player.Stop(); } catch { /* Gerät evtl. entfernt */ }
        _player.Dispose();
        Device.Dispose();
    }

    private sealed class MonoToMultiChannelProvider(ISampleProvider source, int channels) : ISampleProvider
    {
        private float[] _mono = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);

        public int Read(Span<float> buffer)
        {
            var frames = buffer.Length / channels;
            if (_mono.Length < frames) _mono = new float[frames];
            var read = source.Read(_mono.AsSpan(0, frames));
            for (var i = 0; i < read; i++)
            for (var c = 0; c < channels; c++)
                buffer[i * channels + c] = _mono[i];
            return read * channels;
        }
    }
}
