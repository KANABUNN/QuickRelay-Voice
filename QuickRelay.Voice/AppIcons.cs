using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace QuickRelay.Voice;

/// <summary>Draws the window and tray icons so the app ships without a binary icon file.</summary>
internal static class AppIcons
{
    internal static readonly Color Navy = Color.FromArgb(19, 33, 49);
    internal static readonly Color Aqua = Color.FromArgb(148, 234, 223);

    /// <param name="size">Edge length in pixels.</param>
    /// <param name="status">Colour of the status dot in the lower-right corner, or null for none.</param>
    internal static Icon Create(int size, Color? status = null)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            var unit = size / 16f;

            using (var plate = RoundedSquare(new RectangleF(0, 0, size - 0.5f, size - 0.5f), 3.5f * unit))
            using (var navy = new SolidBrush(Navy))
                graphics.FillPath(navy, plate);

            PointF[] speaker =
            [
                new(2.6f * unit, 6.2f * unit), new(5.2f * unit, 6.2f * unit), new(8.2f * unit, 3.4f * unit),
                new(8.2f * unit, 12.6f * unit), new(5.2f * unit, 9.8f * unit), new(2.6f * unit, 9.8f * unit),
            ];
            using (var white = new SolidBrush(Color.White))
                graphics.FillPolygon(white, speaker);

            using (var wave = new Pen(Aqua, Math.Max(1f, 1.35f * unit)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                graphics.DrawArc(wave, 7.2f * unit, 5.6f * unit, 4.4f * unit, 4.8f * unit, -55, 110);
                graphics.DrawArc(wave, 6.4f * unit, 3.2f * unit, 7.6f * unit, 9.6f * unit, -55, 110);
            }

            if (status is { } color)
            {
                var diameter = 7f * unit;
                var dot = new RectangleF(size - diameter - 0.5f, size - diameter - 0.5f, diameter, diameter);
                var ring = RectangleF.Inflate(dot, unit, unit);
                using var navy = new SolidBrush(Navy);
                using var fill = new SolidBrush(color);
                graphics.FillEllipse(navy, ring);
                graphics.FillEllipse(fill, dot);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedSquare(RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
