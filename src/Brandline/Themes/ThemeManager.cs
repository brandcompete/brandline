using System.Windows;
using System.Windows.Media;
using Brandline.Models;
using Microsoft.Win32;

namespace Brandline.Themes;

/// <summary>
/// Schaltet zwischen hellem und dunklem Design um. Grundlage ist das Fluent-Design von .NET
/// (Application.ThemeMode) für die Standardsteuerelemente; darüber liegen die brandline-Paletten
/// (Palette.Light/Dark.xaml) und eigenen Styles (Controls.xaml).
/// </summary>
public static class ThemeManager
{
    private static AppTheme _theme = AppTheme.System;
    private static bool _watching;

    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    public static void Apply(AppTheme theme)
    {
        _theme = theme;
        var dark = theme == AppTheme.Dark || (theme == AppTheme.System && WindowsUsesDarkApps());
        IsDark = dark;

        var app = Application.Current;
#pragma warning disable WPF0001 // ThemeMode ist als experimentell markiert.
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        // Eigene Wörterbücher immer zuletzt einhängen, damit sie Vorrang vor dem Fluent-Design haben.
        var dicts = app.Resources.MergedDictionaries;
        // Nur unsere eigenen Wörterbücher ersetzen – das Fluent-Wörterbuch von WPF liegt ebenfalls unter „Themes/“.
        foreach (var d in dicts.Where(d => IsOwnDictionary(d) || d.Contains(AccentMarker)).ToList())
            dicts.Remove(d);
        dicts.Add(BuildAccentOverrides(dicts));
        dicts.Add(Load(dark ? "Palette.Dark.xaml" : "Palette.Light.xaml"));
        dicts.Add(Load("Controls.xaml"));

        if (!_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _theme == AppTheme.System)
                    app.Dispatcher.BeginInvoke(() => { if (WindowsUsesDarkApps() != IsDark) Apply(AppTheme.System); });
            };
        }
        Changed?.Invoke();
    }

    private const string AccentMarker = "Brandline.AccentOverrides";

    /// <summary>
    /// Das Fluent-Design leitet seine Akzentfarben (Häkchen, Optionsfelder, Fokuslinie, Auswahl) beim Laden
    /// aus der Windows-Akzentfarbe ab und legt sie in vielen einzelnen Ressourcen ab. Statt jede davon von
    /// Hand zu kennen, werden alle Pinsel/Farben mit einem Windows-Akzentton gesucht und durch den
    /// passenden brandcompete-Rotton ersetzt.
    /// </summary>
    private static ResourceDictionary BuildAccentOverrides(IEnumerable<ResourceDictionary> loaded)
    {
        var map = new Dictionary<Color, Color>
        {
            [Rgb(SystemColors.AccentColor)] = Color.FromRgb(0xA9, 0x12, 0x12),
            [Rgb(SystemColors.AccentColorLight1)] = Color.FromRgb(0xC4, 0x15, 0x15),
            [Rgb(SystemColors.AccentColorLight2)] = Color.FromRgb(0xD8, 0x48, 0x48),
            [Rgb(SystemColors.AccentColorLight3)] = Color.FromRgb(0xE9, 0x85, 0x85),
            [Rgb(SystemColors.AccentColorDark1)] = Color.FromRgb(0x8A, 0x0E, 0x0E),
            [Rgb(SystemColors.AccentColorDark2)] = Color.FromRgb(0x6E, 0x0B, 0x0B),
            [Rgb(SystemColors.AccentColorDark3)] = Color.FromRgb(0x52, 0x08, 0x08),
        };

        var result = new ResourceDictionary { [AccentMarker] = true };
        var visited = new HashSet<ResourceDictionary>();

        // Ab .NET 10 verweist das Fluent-Design direkt auf die Windows-Systemressourcen (SystemColors.Accent…Key)
        // statt eigene Kopien anzulegen – diese Schlüssel daher ebenfalls überschreiben.
        (ResourceKey ColorKey, ResourceKey BrushKey, Color Windows)[] systemKeys =
        [
            (SystemColors.AccentColorKey, SystemColors.AccentColorBrushKey, SystemColors.AccentColor),
            (SystemColors.AccentColorLight1Key, SystemColors.AccentColorLight1BrushKey, SystemColors.AccentColorLight1),
            (SystemColors.AccentColorLight2Key, SystemColors.AccentColorLight2BrushKey, SystemColors.AccentColorLight2),
            (SystemColors.AccentColorLight3Key, SystemColors.AccentColorLight3BrushKey, SystemColors.AccentColorLight3),
            (SystemColors.AccentColorDark1Key, SystemColors.AccentColorDark1BrushKey, SystemColors.AccentColorDark1),
            (SystemColors.AccentColorDark2Key, SystemColors.AccentColorDark2BrushKey, SystemColors.AccentColorDark2),
            (SystemColors.AccentColorDark3Key, SystemColors.AccentColorDark3BrushKey, SystemColors.AccentColorDark3),
        ];
        foreach (var (colorKey, brushKey, windows) in systemKeys)
        {
            var brand = map[Rgb(windows)];
            result[colorKey] = brand;
            var brush = new SolidColorBrush(brand);
            brush.Freeze();
            result[brushKey] = brush;
        }

        void Scan(ResourceDictionary dict)
        {
            if (!visited.Add(dict) || IsOwnDictionary(dict)) return;
            foreach (var key in dict.Keys.Cast<object>().ToList())
            {
                object? value;
                try { value = dict[key]; } catch { continue; }
                switch (value)
                {
                    case SolidColorBrush brush when map.TryGetValue(Rgb(brush.Color), out var c):
                        var replacement = new SolidColorBrush(Color.FromArgb(brush.Color.A, c.R, c.G, c.B)) { Opacity = brush.Opacity };
                        replacement.Freeze();
                        result[key] = replacement;
                        break;
                    case Color color when map.TryGetValue(Rgb(color), out var c):
                        result[key] = Color.FromArgb(color.A, c.R, c.G, c.B);
                        break;
                    // .NET 10: Fokuslinien usw. sind Verläufe (Akzentfarbe → transparent)
                    case GradientBrush gradient when gradient.GradientStops.Any(s => map.ContainsKey(Rgb(s.Color))):
                        var copy = gradient.Clone();
                        foreach (var stop in copy.GradientStops)
                            if (map.TryGetValue(Rgb(stop.Color), out var g))
                                stop.Color = Color.FromArgb(stop.Color.A, g.R, g.G, g.B);
                        copy.Freeze();
                        result[key] = copy;
                        break;
                }
            }
            foreach (var merged in dict.MergedDictionaries) Scan(merged);
        }

        foreach (var dict in loaded) Scan(dict);
        return result;
    }

    private static Color Rgb(Color c) => Color.FromRgb(c.R, c.G, c.B);

    private static bool IsOwnDictionary(ResourceDictionary d)
    {
        var source = d.Source?.OriginalString ?? "";
        return source.Contains("Themes/Palette.") || source.EndsWith("Themes/Controls.xaml");
    }

    private static ResourceDictionary Load(string file) =>
        new() { Source = new Uri($"pack://application:,,,/brandline;component/Themes/{file}") };

    /// <summary>Windows-Einstellung „App-Modus: Dunkel“.</summary>
    public static bool WindowsUsesDarkApps()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }
}
