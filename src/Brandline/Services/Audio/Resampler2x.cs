namespace Brandline.Services.Audio;

/// <summary>
/// Umrechnung 16 kHz ↔ 8 kHz mit Tiefpass (gefensterter Sinc, 31 Taps), damit beim
/// G.711-Codec keine Aliasing-Artefakte entstehen. Hält den Filterzustand über Frames hinweg.
/// </summary>
public sealed class Resampler2x
{
    private static readonly float[] Taps = CreateLowPass(31, 3600.0 / 16000.0);
    private readonly float[] _history = new float[Taps.Length];
    private int _pos;

    /// <summary>16 kHz → 8 kHz.</summary>
    public short[] Down(short[] input)
    {
        var output = new short[input.Length / 2];
        for (var i = 0; i < input.Length; i++)
        {
            Push(input[i]);
            if ((i & 1) == 1) output[i / 2] = Clip(Filter());
        }
        return output;
    }

    /// <summary>8 kHz → 16 kHz (Nullen einfügen, filtern, Verstärkung ×2).</summary>
    public float[] Up(float[] input)
    {
        var output = new float[input.Length * 2];
        for (var i = 0; i < input.Length; i++)
        {
            Push(input[i]);
            output[2 * i] = 2 * Filter();
            Push(0);
            output[2 * i + 1] = 2 * Filter();
        }
        return output;
    }

    private void Push(float sample)
    {
        _history[_pos] = sample;
        _pos = (_pos + 1) % _history.Length;
    }

    private float Filter()
    {
        float acc = 0;
        var idx = _pos;
        for (var t = 0; t < Taps.Length; t++)
        {
            acc += Taps[t] * _history[idx];
            idx = (idx + 1) % _history.Length;
        }
        return acc;
    }

    private static short Clip(float v) => (short)Math.Clamp(MathF.Round(v), short.MinValue, short.MaxValue);

    private static float[] CreateLowPass(int length, double cutoff)
    {
        var taps = new float[length];
        var m = length - 1;
        double sum = 0;
        for (var n = 0; n < length; n++)
        {
            var x = n - m / 2.0;
            var sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            var window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * n / m);
            taps[n] = (float)(sinc * window);
            sum += taps[n];
        }
        for (var n = 0; n < length; n++) taps[n] /= (float)sum;
        return taps;
    }
}
