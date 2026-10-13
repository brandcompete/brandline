using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Brandline.Services.Audio;

/// <summary>Mikrofonquelle, liefert 20-ms-Frames mit 16 kHz mono (320 Samples).</summary>
public interface IMicCapture : IDisposable
{
    const int SampleRate = 16000;
    const int FrameSamples = 320;

    event Action<short[]>? FrameReady;
    event Action<string>? Failed;

    void Start();
}

/// <summary>Sammelt beliebig große Blöcke und gibt sie als 20-ms-Frames weiter.</summary>
internal sealed class FrameAccumulator(Action<short[]> emit)
{
    private readonly short[] _frame = new short[IMicCapture.FrameSamples];
    private int _fill;

    public void Add(ReadOnlySpan<short> samples)
    {
        foreach (var s in samples)
        {
            _frame[_fill++] = s;
            if (_fill == _frame.Length)
            {
                emit((short[])_frame.Clone());
                _fill = 0;
            }
        }
    }
}

/// <summary>
/// WASAPI-Aufnahme im Kommunikationsmodus (Rückfall, wenn das Voice-Capture-DSP nicht verfügbar ist).
/// Unter Windows 11 wendet das System dabei seine eigene Sprachverarbeitung an, sofern der Treiber sie anbietet.
/// </summary>
public sealed class WasapiMicCapture : IMicCapture
{
    private readonly MMDevice _device;
    private readonly FrameAccumulator _frames;
    private WasapiRecorder? _capture;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider? _chain;
    private float[] _read = new float[1024];
    private short[] _pcm = new short[1024];

    public WasapiMicCapture(string storedDeviceId)
    {
        _device = AudioDevices.Open(DataFlow.Capture, storedDeviceId);
        _frames = new FrameAccumulator(f => FrameReady?.Invoke(f));
    }

    public event Action<short[]>? FrameReady;
    public event Action<string>? Failed;

    public void Start()
    {
        _capture = new WasapiRecorderBuilder()
            .WithDevice(_device)
            .WithSharedMode()
            .WithEventSync()
            .WithBufferLength(20)
            .WithCommunicationsMode()
            .Build();
        _buffer = new BufferedWaveProvider(_capture.WaveFormat) { ReadFully = false, DiscardOnBufferOverflow = true };

        ISampleProvider chain = _buffer.ToSampleProvider();
        if (chain.WaveFormat.Channels > 1) chain = new DownmixToMono(chain);
        if (chain.WaveFormat.SampleRate != IMicCapture.SampleRate)
            chain = new WdlResamplingSampleProvider(chain, IMicCapture.SampleRate);
        _chain = chain;

        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) Failed?.Invoke($"Mikrofon: {e.Exception.Message}");
        };
        _capture.StartRecording();
    }

    private void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        _buffer!.AddSamples(data);
        int read;
        while ((read = _chain!.Read(_read)) > 0)
        {
            if (_pcm.Length < read) _pcm = new short[read];
            for (var i = 0; i < read; i++)
                _pcm[i] = (short)Math.Clamp(_read[i] * 32767f, short.MinValue, short.MaxValue);
            _frames.Add(_pcm.AsSpan(0, read));
        }
    }

    public void Dispose()
    {
        if (_capture != null)
        {
            _capture.DataAvailable -= OnData;
            try { _capture.StopRecording(); } catch { /* Gerät evtl. entfernt */ }
            _capture.Dispose();
        }
        _device.Dispose();
    }

    private sealed class DownmixToMono(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _multi = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(Span<float> buffer)
        {
            var needed = buffer.Length * _channels;
            if (_multi.Length < needed) _multi = new float[needed];
            var read = source.Read(_multi.AsSpan(0, needed)) / _channels;
            for (var i = 0; i < read; i++)
            {
                float sum = 0;
                for (var c = 0; c < _channels; c++) sum += _multi[i * _channels + c];
                buffer[i] = sum / _channels;
            }
            return read;
        }
    }
}
