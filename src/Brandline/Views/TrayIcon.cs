using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Brandline.Services;
using Forms = System.Windows.Forms;

namespace Brandline.Views;

public enum TrayStatus
{
    Offline,
    Registering,
    Online,
    DoNotDisturb,
    Error,
    Ringing
}

/// <summary>Symbol im Infobereich: brandline-Marke mit Statuspunkt, dazu Kontextmenü.</summary>
public sealed class TrayIcon : IDisposable
{
    private static readonly Color BrandRed = Color.FromArgb(0xA9, 0x12, 0x12);
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _dndItem;
    private IntPtr _iconHandle;
    private bool _disposed;

    public TrayIcon(Action open, Action toggleDnd, Action settings, Action about, Action exit)
    {
        _dndItem = new Forms.ToolStripMenuItem("Nicht stören", null, (_, _) => toggleDnd());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Öffnen", null, (_, _) => open()) { Font = new Font(Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(_dndItem);
        menu.Items.Add("Einstellungen …", null, (_, _) => settings());
        menu.Items.Add($"Über {SystemIntegration.AppName} …", null, (_, _) => about());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Text = SystemIntegration.AppName, Visible = true };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => BalloonClicked?.Invoke();
        SetStatus(TrayStatus.Offline, "Nicht angemeldet");
    }

    public event Action? BalloonClicked;

    public void SetStatus(TrayStatus status, string text)
    {
        // Beim Beenden können noch Statusmeldungen eintreffen, nachdem das Symbol entfernt wurde.
        if (_disposed) return;
        var old = _iconHandle;
        using var bitmap = Draw(32, status);
        _iconHandle = bitmap.GetHicon();
        _icon.Icon = Icon.FromHandle(_iconHandle);
        if (old != IntPtr.Zero) DestroyIcon(old);

        // Tooltip ist auf 127 Zeichen begrenzt.
        var tip = $"{SystemIntegration.AppName} – {text}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    public void SetDoNotDisturb(bool on) => _dndItem.Checked = on;

    public void ShowBalloon(string title, string text, bool warning = false)
    {
        if (!_disposed) _icon.ShowBalloonTip(5000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    /// <summary>App-Symbol für Fenster und Taskleiste (Marke ohne Statuspunkt).</summary>
    public static BitmapSource CreateWindowIcon()
    {
        // Über PNG, damit die Transparenz der abgerundeten Ecken erhalten bleibt.
        using var bitmap = Draw(64, null);
        using var stream = new System.IO.MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Rotes, abgerundetes Quadrat (wie das brandcompete-Logo) mit weißem Hörer, optional Statuspunkt.</summary>
    private static Bitmap Draw(int size, TrayStatus? status)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var radius = size * 0.22f;
        using (var path = RoundedRect(new RectangleF(0, 0, size - 1, size - 1), radius))
        using (var brush = new SolidBrush(BrandRed))
            g.FillPath(brush, path);

        using (var font = new Font("Segoe MDL2 Assets", size * 0.5f, GraphicsUnit.Pixel))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString("", font, Brushes.White, new RectangleF(0, size * 0.04f, size, size), format);

        if (status is { } s)
        {
            var dot = size * 0.44f;
            var rect = new RectangleF(size - dot, size - dot, dot - 1, dot - 1);
            using (var ring = new SolidBrush(Color.White)) g.FillEllipse(ring, rect);
            rect.Inflate(-size * 0.06f, -size * 0.06f);
            using (var fill = new SolidBrush(StatusColor(s))) g.FillEllipse(fill, rect);
            if (s == TrayStatus.DoNotDisturb)
            {
                using var pen = new Pen(Color.White, size * 0.07f);
                var y = rect.Top + rect.Height / 2;
                g.DrawLine(pen, rect.Left + rect.Width * 0.25f, y, rect.Right - rect.Width * 0.25f, y);
            }
        }
        return bmp;
    }

    private static Color StatusColor(TrayStatus status) => status switch
    {
        TrayStatus.Online => Color.FromArgb(0x22, 0xA4, 0x47),
        TrayStatus.Registering => Color.FromArgb(0xE8, 0xA3, 0x17),
        TrayStatus.DoNotDisturb => Color.FromArgb(0x1A, 0x1A, 0x2E),
        TrayStatus.Error => Color.FromArgb(0xF2, 0xC9, 0x4C),
        TrayStatus.Ringing => Color.FromArgb(0x15, 0x77, 0x9B),
        _ => Color.FromArgb(0x8A, 0x8A, 0x99),
    };

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
    }
}
