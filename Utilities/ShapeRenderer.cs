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
                using (var boundary = AnnotationGeometry.Boundary(shape))
                {
                    HatchRenderer.Paint(graphics,shape,boundary);
                    if (shape.StrokeVisible)
                        using (var pen = new Pen(shape.OverrideColor ?? baseColor, width))
                        {
                            pen.DashStyle = StyleValidation.SafeDash(shape.LineType);
                            if (boundary.PointCount > 0) graphics.DrawPath(pen,boundary);
                        }
                }
            }
            catch (Exception ex)
            {
                // GDI+ can reject finite degenerate geometry. One annotation must not break GH paint.
                System.Diagnostics.Debug.WriteLine("HopperDraw paint skipped: " + ex.Message);
            }
        }

        internal static void Ghost(Graphics g, DrawShape shape, Color basis, float scale)
        {
            if (shape is DrawShapeGroup group) { foreach(var child in group.Children) Ghost(g,child,basis,scale); return; }
            if (shape is OpaqueShape || shape.HasPaint(basis)) return;
            using(var path=AnnotationGeometry.Boundary(shape))
            using(var pen=new Pen(Color.FromArgb(150,100,100,100),scale) { DashStyle=System.Drawing.Drawing2D.DashStyle.Dash })
                if(path.PointCount>0) g.DrawPath(pen,path);
        }

        private static void Outline(Graphics g, DrawShape shape, bool selected, float scale)
        {
            if (shape is DrawShapeGroup group) { foreach(var child in group.Children) Outline(g,child,selected,scale); return; }
            if (shape is OpaqueShape) return;
            using(var boundary=AnnotationGeometry.Boundary(shape))
            {
                if(boundary.PointCount==0) return;
                if(selected) using(var halo=new Pen(Color.White,4*scale)) g.DrawPath(halo,boundary);
                bool ghost=!shape.StrokeVisible && !shape.VisibleFill;
                using(var pen=new Pen(selected ? Color.FromArgb(20,150,70) : Color.FromArgb(110,30,150,80),(selected?2:ghost?1:3)*scale))
                {
                    if(selected || ghost) pen.DashStyle=System.Drawing.Drawing2D.DashStyle.Dash;
                    g.DrawPath(pen,boundary);
                }
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
                    if (drawOutline) Outline(graphics,shape,selected,scale);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("HopperDraw handles skipped: " + ex.Message); }
        }
    }
}
