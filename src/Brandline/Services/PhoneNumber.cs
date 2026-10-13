using System.Text;
using System.Text.RegularExpressions;

namespace Brandline.Services;

public static partial class PhoneNumber
{
    /// <summary>
    /// Bereitet eine eingegebene Nummer zum Wählen auf: entfernt Leer- und Trennzeichen,
    /// wandelt "+49..." in "0049..." um und entfernt die in Signaturen übliche "(0)" nach der Ländervorwahl.
    /// Interne Kurzwahlen (z. B. "12") bleiben unverändert.
    /// </summary>
    public static string ForDialing(string input)
    {
        var text = input.Trim();
        if (text.StartsWith('+')) text = text.Replace("(0)", "");

        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsDigit(c) || c is '*' or '#') sb.Append(c);
            else if (c == '+' && sb.Length == 0) sb.Append("00");
        }
        return sb.ToString();
    }

    /// <summary>Vergleichsform für Adressbuch-Suche: deutsche Nummern einheitlich mit führender 0.</summary>
    public static string Canonical(string input)
    {
        var n = ForDialing(input);
        if (n.StartsWith("0049")) n = "0" + n[4..];
        return n;
    }

    public static bool SameNumber(string a, string b)
    {
        var ca = Canonical(a);
        return ca.Length > 0 && ca == Canonical(b);
    }

    /// <summary>
    /// Liest die Nummer aus einem tel:/callto:/sip:-Link, z. B. "tel:+49%2089%20123456;ext=12".
    /// Liefert null, wenn das Argument kein solcher Link ist.
    /// </summary>
    public static string? FromUri(string uri)
    {
        var match = UriPattern().Match(uri.Trim());
        if (!match.Success) return null;

        // UnescapeDataString statt UrlDecode: ein "+" muss "+" bleiben (UrlDecode macht ein Leerzeichen daraus).
        var rest = Uri.UnescapeDataString(match.Groups[2].Value).TrimStart('/');
        var cut = rest.IndexOfAny([';', '?', '@']);
        if (cut >= 0) rest = rest[..cut];
        return rest.Trim();
    }

    /// <summary>Sucht die erste Telefonnummer in einem markierten Text (z. B. aus einer E-Mail-Signatur).</summary>
    public static string? Extract(string text)
    {
        foreach (Match m in NumberPattern().Matches(text))
        {
            var candidate = m.Value.Trim();
            if (candidate.Count(char.IsDigit) >= 3) return candidate;
        }
        return null;
    }

    [GeneratedRegex(@"^(tel|callto|sip|sips):(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex UriPattern();

    [GeneratedRegex(@"\+?\(?\d[\d\s\-/().]{1,}\d")]
    private static partial Regex NumberPattern();
}
