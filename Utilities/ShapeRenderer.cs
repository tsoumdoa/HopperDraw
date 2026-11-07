using System;
using System.Drawing;

namespace hopperdraw
{
    internal static class ShapeRenderer
    {
        internal static bool ValidPoints(PointF[] points)
        {
            foreach (var point in points)
                if (!ShapeSerializer.ValidCoordinate(point.X) || !ShapeSerializer.ValidCoordinate(point.Y)) return false;
            return true;
        }

        // Final shared boundary for persisted and preview paint. Mutable model styles are untrusted.
        internal static void Render(Graphics graphics, DrawShape shape, Color baseColor, float baseWidth)
        {
            try
            {
                if (shape is OpaqueShape) return;
                if (shape is DrawShapeGroup group)
                {
                    foreach (var child in group.Children) Render(graphics, child, baseColor, baseWidth);
                    return;
                }
                if (!ValidPoints(shape.GetPoints())) return;
                float width = StyleValidation.EffectiveWidth(baseWidth, shape.ThicknessMultiplier);
                using (var pen = new Pen(shape.OverrideColor ?? baseColor, width))
                {
                    pen.DashStyle = StyleValidation.SafeDash(shape.LineType);
                    shape.Render(graphics, pen, width);
                }
            }
            catch (Exception ex)
            {
                // GDI+ can reject finite degenerate geometry. One annotation must not break GH paint.
                System.Diagnostics.Debug.WriteLine("HopperDraw paint skipped: " + ex.Message);
            }
        }

        internal static void Handles(Graphics graphics, DrawShape shape, int hoveredPoint, bool selected, float scale, bool drawOutline = true)
        {
            try
            {
                var points = shape.GetPoints();
                if (!ValidPoints(points)) return;
                float size = 8 * scale;
                using (var brush = new SolidBrush(selected ? Color.White : Color.FromArgb(100, 0, 120, 215)))
                using (var pen = new Pen(Color.FromArgb(200, 0, 120, 215), 2 * scale))
                {
                    for (int i = 0; i < points.Length; i++)
                    {
                        if (shape is DrawShapeGroup || !selected && i != hoveredPoint) continue;
                        var pt = points[i];
                        graphics.FillRectangle(brush, pt.X - size / 2, pt.Y - size / 2, size, size);
                        graphics.DrawRectangle(pen, pt.X - size / 2, pt.Y - size / 2, size, size);
                    }
                    if (!selected && drawOutline)
                        foreach (var stroke in AnnotationGeometry.Strokes(shape))
                            using (var hover = new Pen(Color.FromArgb(110, 30, 150, 80), 3 * scale)) graphics.DrawLines(hover,stroke);
                    if (selected && drawOutline && points.Length > 1)
                    {
                        foreach (var stroke in AnnotationGeometry.Strokes(shape))
                        {
                            using (var halo = new Pen(Color.White, 4 * scale)) graphics.DrawLines(halo, stroke);
                            pen.Color = Color.FromArgb(20, 150, 70); pen.Width = 2 * scale;
                            pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                            graphics.DrawLines(pen, stroke);
                        }
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("HopperDraw handles skipped: " + ex.Message); }
        }
    }
}
