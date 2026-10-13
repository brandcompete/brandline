using System.IO;
using NAudio.Wave;

namespace Brandline.Services.Audio;

public sealed record RingtoneInfo(string Id, string Name, bool IsFile = false);

/// <summary>
/// Eingebaute Klingeltöne (zur Laufzeit synthetisiert, daher ohne Lizenzfragen), beliebig viele eigene
/// Audiodateien im Ordner %APPDATA%\Brandline\ringtones und der Zufallsmodus.
/// IDs: eingebaut z. B. "classic", eigene Datei "file:Name.mp3", Zufall "random".
/// </summary>
public static class Ringtones
{
    public const string RandomId = "random";
    public const string DefaultId = "classic";
    private const string FilePrefix = "file:";
    private const int Rate = 16000;
    private static readonly string[] AudioExtensions = [".mp3", ".wav", ".wma", ".m4a", ".aac"];
    private static string? _lastRandom;

    public static IReadOnlyList<RingtoneInfo> BuiltIn { get; } =
    [
        new("classic", "Klassisch (Triller)"),
        new("retro", "Altes Telefon"),
        new("marimba", "Marimba"),
        new("kalimba", "Kalimba"),
        new("chime", "Glocke"),
        new("sonar", "Sonar"),
        new("synth", "Synth-Arpeggio"),
        new("digital", "Digital"),
        new("soft", "Sanft"),
    ];

    public static string Folder => Path.Combine(Storage.Folder, "ringtones");

    /// <summary>Alle verfügbaren Klingeltöne: eingebaute und importierte Dateien.</summary>
    public static List<RingtoneInfo> All() => [.. BuiltIn, .. Files()];

    public static IEnumerable<RingtoneInfo> Files()
    {
        if (!Directory.Exists(Folder)) yield break;
        foreach (var file in Directory.GetFiles(Folder).Order())
        {
            if (!AudioExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
            var name = Path.GetFileName(file);
            yield return new RingtoneInfo(FilePrefix + name, Path.GetFileNameWithoutExtension(name), IsFile: true);
        }
    }

    public static bool Exists(string id) => id.StartsWith(FilePrefix)
        ? File.Exists(PathOf(id))
        : BuiltIn.Any(r => r.Id == id);

    private static string PathOf(string fileId) => Path.Combine(Folder, fileId[FilePrefix.Length..]);

    /// <summary>
    /// Ton für einen eingehenden Anruf. Im Zufallsmodus wird aus der Auswahl gezogen, ohne denselben
    /// Ton zweimal hintereinander zu spielen.
    /// </summary>
    public static string PickForCall(string ringTone, IReadOnlyList<string> randomPool)
    {
        if (ringTone != RandomId) return Exists(ringTone) ? ringTone : DefaultId;

        var candidates = randomPool.Where(Exists).Distinct().ToList();
        if (candidates.Count == 0) return DefaultId;
        if (candidates.Count > 1 && _lastRandom != null) candidates.Remove(_lastRandom);
        _lastRandom = candidates[Random.Shared.Next(candidates.Count)];
        return _lastRandom;
    }

    /// <summary>Liefert den Klingelton als endlos laufende Mono-Quelle.</summary>
    public static ISampleProvider Create(string id)
    {
        if (id.StartsWith(FilePrefix) && File.Exists(PathOf(id)))
        {
            try { return new LoopingFileProvider(PathOf(id)); }
            catch { /* Datei defekt oder Format nicht unterstützt → eingebauter Ton */ }
        }
        return new LoopingBufferProvider(Render(id));
    }

    /// <summary>Kopiert eine eigene Datei in den App-Ordner und liefert ihre Klingelton-ID.</summary>
    public static string Import(string sourceFile)
    {
        using (new AudioFileReader(sourceFile)) { } // wirft, wenn das Format nicht lesbar ist
        Directory.CreateDirectory(Folder);
        var target = Path.Combine(Folder, Path.GetFileName(sourceFile));
        if (!string.Equals(Path.GetFullPath(sourceFile), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourceFile, target, overwrite: true);
        return FilePrefix + Path.GetFileName(target);
    }

    /// <summary>Entfernt eine importierte Datei (eingebaute Töne lassen sich nicht löschen).</summary>
    public static void Delete(string id)
    {
        if (!id.StartsWith(FilePrefix)) return;
        try { File.Delete(PathOf(id)); } catch { /* evtl. gerade in Wiedergabe */ }
    }

    // ---------------- Synthese ----------------

    private static float[] Render(string id) => id switch
    {
        "retro" => Retro(),
        "marimba" => Notes(4.0, 0.16, 7, 0.9, [(1, 1.0), (4, 0.25), (10, 0.05)],
            [523.25, 659.25, 783.99, 659.25, 1046.5, 783.99, 659.25, 783.99]),
        "kalimba" => Notes(3.6, 0.18, 4.5, 1.0, [(1, 1.0), (5.4, 0.12), (2.0, 0.08)],
            [783.99, 659.25, 587.33, 783.99, 880.0, 659.25]),
        "chime" => Notes(4.5, 0.55, 1.8, 0.7, [(1, 1.0), (2.0, 0.5), (2.76, 0.3), (5.4, 0.12)],
            [659.25, 523.25, 587.33, 392.0]),
        "sonar" => Notes(3.0, 0.9, 2.2, 1.6, [(1, 1.0), (2, 0.08)], [1318.5, 1318.5]),
        "synth" => Notes(3.2, 0.11, 9, 0.35, [(1, 1.0), (3, 0.33), (5, 0.2), (7, 0.14)],
            [440, 554.37, 659.25, 880, 659.25, 554.37, 440, 554.37, 659.25, 880, 1108.73, 880]),
        "digital" => Digital(),
        "soft" => Notes(3.8, 0.24, 2.6, 0.55, [(1, 1.0), (2, 0.15)],
            [523.25, 659.25, 783.99, 1046.5]),
        _ => Classic(),
    };
    /// <summary>Triller 1000/1250 Hz, 1,2 s an, 2,8 s aus.</summary>
    private static float[] Classic()
    {
        var buffer = new float[Rate * 4];
        double phase = 0;
        for (var i = 0; i < Rate * 1.2; i++)
        {
            var t = (double)i / Rate;
            var f = (int)(t / 0.05) % 2 == 0 ? 1000 : 1250;
            phase += 2 * Math.PI * f / Rate;
            buffer[i] = (float)(Math.Sin(phase) * Fade(t, 1.2));
        }
        return Normalize(buffer);
    }

    /// <summary>Mechanische Glocke mit Klöppel (≈ 20 Hz), Doppelklingeln wie beim alten Postapparat.</summary>
    private static float[] Retro()
    {
        var buffer = new float[Rate * 3];
        (double Start, double Length)[] bursts = [(0, 0.4), (0.6, 0.4)];
        foreach (var (start, length) in bursts)
        {
            for (var i = 0; i < Rate * length; i++)
            {
                var t = (double)i / Rate;
                var strike = Math.Sin(2 * Math.PI * 20 * t) > 0 ? 1.0 : 0.35;
                var tone = Math.Sin(2 * Math.PI * 1150 * t) + 0.6 * Math.Sin(2 * Math.PI * 1720 * t) + 0.3 * Math.Sin(2 * Math.PI * 2900 * t);
                buffer[(int)(start * Rate) + i] += (float)(tone * strike * Fade(t, length));
            }
        }
        return Normalize(buffer);
    }

    /// <summary>Zwei Dreiergruppen kurzer Pieptöne.</summary>
    private static float[] Digital()
    {
        var buffer = new float[Rate * 3];
        for (var group = 0; group < 2; group++)
        for (var beep = 0; beep < 3; beep++)
        {
            var start = group * 0.75 + beep * 0.15;
            for (var i = 0; i < Rate * 0.09; i++)
            {
                var t = (double)i / Rate;
                var tone = Math.Sin(2 * Math.PI * 1400 * t) + 0.3 * Math.Sin(2 * Math.PI * 4200 * t);
                buffer[(int)(start * Rate) + i] += (float)(tone * Fade(t, 0.09, 0.005));
            }
        }
        return Normalize(buffer);
    }

    /// <summary>Melodie aus abklingenden Tönen mit Obertönen (Marimba, Glocke, Sanft).</summary>
    private static float[] Notes(double period, double spacing, double decay, double ring,
        (double Multiple, double Amplitude)[] partials, double[] frequencies)
    {
        var buffer = new float[(int)(Rate * period)];
        for (var n = 0; n < frequencies.Length; n++)
        {
            var start = (int)(n * spacing * Rate);
            var length = (int)(ring * Rate);
            for (var i = 0; i < length && start + i < buffer.Length; i++)
            {
                var t = (double)i / Rate;
                double sample = 0;
                foreach (var (multiple, amplitude) in partials)
                    sample += amplitude * Math.Sin(2 * Math.PI * frequencies[n] * multiple * t) * Math.Exp(-t * decay * multiple);
                var attack = Math.Min(1, t / 0.004);
                buffer[start + i] += (float)(sample * attack);
            }
        }
        return Normalize(buffer);
    }

    private static double Fade(double t, double length, double fade = 0.01) =>
        Math.Min(1.0, Math.Min(t, length - t) / fade);

    private static float[] Normalize(float[] buffer)
    {
        var peak = buffer.Max(Math.Abs);
        if (peak <= 0) return buffer;
        var gain = 0.6f / peak;
        for (var i = 0; i < buffer.Length; i++) buffer[i] *= gain;
        return buffer;
    }

    // ---------------- Wiedergabe ----------------

    private sealed class LoopingBufferProvider(float[] samples) : ISampleProvider
    {
        private int _pos;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

        public int Read(Span<float> buffer)
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = samples[_pos];
                _pos = (_pos + 1) % samples.Length;
            }
            return buffer.Length;
        }
    }

    /// <summary>Eigene Datei (MP3, WAV, WMA, M4A …) als Mono in Endlosschleife.</summary>
    private sealed class LoopingFileProvider : ISampleProvider
    {
        private readonly AudioFileReader _reader;
        private readonly int _channels;
        private float[] _multi = [];

        public LoopingFileProvider(string file)
        {
            _reader = new AudioFileReader(file);
            _channels = _reader.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_reader.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<float> buffer)
        {
            var needed = buffer.Length * _channels;
            if (_multi.Length < needed) _multi = new float[needed];

            var filled = 0;
            var restarted = false;
            while (filled < needed)
            {
                var read = ((ISampleProvider)_reader).Read(_multi.AsSpan(filled, needed - filled));
                if (read == 0)
                {
                    if (restarted) break; // leere Datei
                    _reader.Position = 0;
                    restarted = true;
                    continue;
                }
                restarted = false;
                filled += read;
            }

            var frames = filled / _channels;
            for (var i = 0; i < frames; i++)
            {
                float sum = 0;
                for (var c = 0; c < _channels; c++) sum += _multi[i * _channels + c];
                buffer[i] = sum / _channels;
            }
            buffer[frames..].Clear();
            return buffer.Length;
        }
    }
}
