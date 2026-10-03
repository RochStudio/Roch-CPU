using System.Drawing.Drawing2D;

namespace RochPower.UI;

/// <summary>The Roch family filled crescent on an 18 by 18 logical-pixel canvas.</summary>
internal static class SharedMoonGeometry
{
    // Same 257-point closed contour as output/shared-controls/moon-geometry.json:
    // circle (11.875, 9), r=8, minus circle (16.375, 9), r=8.
    private static readonly PointF[] Points = BuildPoints();

    private static PointF[] BuildPoints()
    {
        const int steps = 128;
        double angle = Math.Acos(4.5 / 16);
        var points = new PointF[steps * 2 + 1];
        for (int i = 0; i <= steps; i++)
        {
            double t = -angle - (2 * Math.PI - 2 * angle) * i / steps;
            points[i] = new((float)(11.875 + 8 * Math.Cos(t)), (float)(9 + 8 * Math.Sin(t)));
        }
        for (int i = 1; i <= steps; i++)
        {
            double t = Math.PI - angle + 2 * angle * i / steps;
            points[steps + i] = new((float)(16.375 + 8 * Math.Cos(t)), (float)(9 + 8 * Math.Sin(t)));
        }
        return points;
    }

    internal static void Draw(Graphics graphics, Rectangle bounds, int dpi, Color color)
    {
        float scale = dpi / 96f;
        using var path = new GraphicsPath();
        path.AddPolygon(Points);
        using var transform = new Matrix(scale, 0, 0, scale,
            bounds.X + (bounds.Width - 18 * scale) / 2,
            bounds.Y + (bounds.Height - 18 * scale) / 2);
        path.Transform(transform);
        var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(color);
            graphics.FillPath(fill, path);
        }
        finally { graphics.Restore(state); }
    }
}
