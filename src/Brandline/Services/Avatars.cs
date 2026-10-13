using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Brandline.Services;

/// <summary>Kontaktbilder: werden quadratisch zugeschnitten, auf 160 px verkleinert und als PNG abgelegt.</summary>
public static class Avatars
{
    private const int Size = 160;
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new();

    public static string Folder => Path.Combine(Storage.Folder, "avatars");

    /// <summary>Liest ein Bild (JPG, PNG, BMP, GIF …), schneidet die Mitte quadratisch aus und speichert es.</summary>
    /// <returns>Dateiname für <see cref="Models.Contact.Avatar"/>.</returns>
    public static string Import(string sourceFile, Guid contactId)
    {
        var source = new BitmapImage();
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        source.UriSource = new Uri(sourceFile);
        source.EndInit();

        var side = Math.Min(source.PixelWidth, source.PixelHeight);
        var crop = new CroppedBitmap(source, new Int32Rect((source.PixelWidth - side) / 2, (source.PixelHeight - side) / 2, side, side));
        var scaled = new TransformedBitmap(crop, new ScaleTransform((double)Size / side, (double)Size / side));

        Directory.CreateDirectory(Folder);
        // Neuer Dateiname bei jeder Änderung, damit kein veraltetes Bild aus dem Cache erscheint.
        var file = $"{contactId:N}-{DateTime.Now:yyyyMMddHHmmss}.png";
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(scaled));
        using (var stream = File.Create(Path.Combine(Folder, file))) encoder.Save(stream);
        return file;
    }

    public static ImageSource? Load(string file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        return Cache.GetOrAdd(file, f =>
        {
            var path = Path.Combine(Folder, f);
            if (!File.Exists(path)) return null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad; // Datei nicht sperren
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        });
    }

    public static void Delete(string file)
    {
        if (string.IsNullOrEmpty(file)) return;
        Cache.TryRemove(file, out _);
        try { File.Delete(Path.Combine(Folder, file)); } catch { /* ignorieren */ }
    }

    /// <summary>Initialen für die Ersatzdarstellung: „Max Mustermann“ → „MM“, „0170 …“ → Telefonsymbol.</summary>
    public static string Initials(string name)
    {
        var parts = name.Split([' ', '-', '.', ','], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => char.IsLetter(p[0])).ToArray();
        return parts.Length switch
        {
            0 => "",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant()
        };
    }

    // Markenfarben-nahe Palette für Initialen-Kreise; die Farbe hängt stabil vom Namen ab.
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xA9, 0x12, 0x12), Color.FromRgb(0x15, 0x77, 0x9B), Color.FromRgb(0x4D, 0x4D, 0x6B),
        Color.FromRgb(0x8A, 0x4B, 0x12), Color.FromRgb(0x2E, 0x7D, 0x5B), Color.FromRgb(0x6B, 0x2E, 0x7D),
    ];

    public static Brush BrushFor(string name)
    {
        var hash = name.Aggregate(17, (h, c) => unchecked(h * 31 + char.ToLowerInvariant(c)));
        var brush = new SolidColorBrush(Palette[Math.Abs(hash % Palette.Length)]);
        brush.Freeze();
        return brush;
    }
}
