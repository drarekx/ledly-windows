using System.Drawing;
using System.Drawing.Drawing2D;
using Ledly.Windows.Interop;
using Microsoft.Win32;

namespace Ledly.Windows.App;

/// <summary>
/// Notification-area icon drawn on demand, like the Mac menu bar one: a monitor with a ring of LEDs,
/// rainbow when the strip is on and grey when it's off; the outline follows the taskbar's theme.
/// </summary>
static class TrayIcons
{
    public static Icon Draw(bool on, int size)
    {
        var light = TaskbarIsLight();
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            // Designed on a 22×22 grid (the Mac icon is 22×18), scaled to the icon size.
            var s = size / 22f;
            var ink = light ? Color.FromArgb(on ? 235 : 150, 20, 20, 24) : Color.FromArgb(on ? 245 : 160, 245, 245, 248);
            using (var pen = new Pen(ink, Math.Max(1f, 1.6f * s)))
            using (var path = RoundedRect(new RectangleF(5 * s, 7 * s, 12 * s, 8 * s), 1.6f * s))
                g.DrawPath(pen, path);

            var dots = new List<PointF>();
            for (var x = 6f; x <= 16.01f; x += 2.5f) dots.Add(new(x, 3.5f));
            for (var y = 8.5f; y <= 13.51f; y += 2.5f) dots.Add(new(19.5f, y));
            for (var x = 16f; x >= 5.99f; x -= 2.5f) dots.Add(new(x, 18.5f));
            for (var y = 13.5f; y >= 8.49f; y -= 2.5f) dots.Add(new(2.5f, y));
            var radius = Math.Max(0.75f, 1.25f * s);
            for (var i = 0; i < dots.Count; i++)
            {
                var color = on ? FromHue(i / (double)dots.Count) : (light ? Color.FromArgb(110, 0, 0, 0) : Color.FromArgb(110, 255, 255, 255));
                using var brush = new SolidBrush(color);
                var p = dots[i];
                g.FillEllipse(brush, p.X * s - radius, p.Y * s - radius, radius * 2, radius * 2);
            }
        }
        var handle = bitmap.GetHicon();
        try
        {
            // Clone so the icon owns its handle and the GDI one can be destroyed now.
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
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

    static Color FromHue(double hue)
    {
        var c = RGB.FromHSV((float)hue, 0.85f, 1f);
        return Color.FromArgb(255, c.R8, c.G8, c.B8);
    }

    static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The app icon (multi-size .ico) at the size Windows wants for the window caption / taskbar.</summary>
    public static Icon AppIcon()
    {
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream("Ledly.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }
}
