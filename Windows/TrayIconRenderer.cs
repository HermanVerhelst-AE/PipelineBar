using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PipelineBar;

/// Het icoon in het systeemvak: het app-icoon als er niets te melden is, in kleur zodra iets aandacht vraagt,
/// met een badge voor het aantal mislukte of lopende pipelines.
public static class TrayIconRenderer
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static string? _iconFont;

    /// Windows 11 heeft Segoe Fluent Icons, Windows 10 Segoe MDL2 Assets (zelfde codepunten).
    private static string IconFont => _iconFont ??= PickFont();

    private static string PickFont()
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            using var f = new Font(name, 10);
            if (f.Name == name) return name;   // GDI+ valt anders stil terug op een andere letter
        }
        return "Segoe UI Symbol";
    }

    public static Icon Render(RunState? state, int? count)
    {
        if (state is RunState.Succeeded or RunState.None && count is null) return AppIcon();

        var dark = Theme.TaskbarDark;
        var neutral = dark ? Color.White : Color.Black;
        var (glyph, color) = state switch
        {
            RunState.Failed => ("", Parse(Theme.StateColor(RunState.Failed, dark))),
            RunState.PartiallySucceeded => ("", Parse(Theme.StateColor(RunState.PartiallySucceeded, dark))),
            RunState.Waiting => ("", Parse(Theme.StateColor(RunState.Waiting, dark))),
            { } s when s.IsRunning() => ("", neutral),     // Sync
            RunState.Succeeded => ("", neutral),           // Completed
            not null => ("", neutral),                     // StatusCircleRing
            null => ("", neutral),                         // Error
        };

        var size = SystemInformation.SmallIconSize;
        int w = size.Width, h = size.Height;
        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;   // geen ClearType op transparant
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using (var font = new Font(IconFont, h * 0.88f, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
            using (var fmt = Centered())
            {
                g.DrawString(glyph, font, brush, new RectangleF(0, 0, w, h), fmt);
            }

            if (count is int n) DrawBadge(g, n > 9 ? "9+" : n.ToString(), w, h, state, dark);
        }

        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    private static Icon AppIcon()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/PipelineBar.ico"));
        using var stream = info.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    private static void DrawBadge(Graphics g, string text, int w, int h, RunState? state, bool dark)
    {
        var fill = Parse(Theme.StateColor(state == RunState.Failed ? RunState.Failed : RunState.InProgress, dark));
        var ink = dark ? Color.Black : Color.White;

        using var font = new Font("Segoe UI", h * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fmt = Centered();
        var textSize = g.MeasureString(text, font, int.MaxValue, fmt);
        var bh = (float)Math.Round(h * 0.62f);
        var bw = Math.Max(bh, (float)Math.Ceiling(textSize.Width + h * 0.12f));
        var rect = new RectangleF(w - bw, h - bh, bw, bh);

        // Uitsparing rond de badge zodat hij loskomt van het icoon.
        var cut = RectangleF.Inflate(rect, 1.2f, 1.2f);
        var oldMode = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var clear = new SolidBrush(Color.Transparent))
        using (var path = Pill(cut)) g.FillPath(clear, path);
        g.CompositingMode = oldMode;

        using (var brush = new SolidBrush(fill))
        using (var path = Pill(rect)) g.FillPath(brush, path);
        using var inkBrush = new SolidBrush(ink);
        g.DrawString(text, font, inkBrush, rect, fmt);
    }

    private static GraphicsPath Pill(RectangleF r)
    {
        var d = Math.Min(r.Width, r.Height);
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }

    private static StringFormat Centered()
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.Alignment = StringAlignment.Center;
        f.LineAlignment = StringAlignment.Center;
        f.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        return f;
    }

    private static Color Parse(string hex) => ColorTranslator.FromHtml("#" + hex[3..]);
}
