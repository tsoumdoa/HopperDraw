using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using Rhino.Geometry;

namespace hopperdraw
{
    // One source of stroke geometry for picking, window selection and feedback.
    internal static class AnnotationGeometry
    {
        internal static IEnumerable<PointF[]> Strokes(DrawShape shape)
        {
            if (shape is OpaqueShape) yield break;
            if (shape is DrawShapeGroup group)
            {
                foreach (var child in group.Children)
                    foreach (var stroke in Strokes(child)) yield return stroke;
                yield break;
            }
            var points = shape.GetPoints();
            if (points.Length < 2 || !ShapeRenderer.ValidPoints(points)) yield break;
            using (var path = new GraphicsPath())
            {
                if (shape is CurveShape curve)
                {
                    if (curve.Closed && points.Length > 2) path.AddClosedCurve(points, .5f);
                    else path.AddCurve(points, .5f);
                }
                else if (shape is FrameShape || shape is PolylineShape poly && poly.Closed) path.AddPolygon(points);
                else path.AddLines(points);
                path.Flatten(null, .2f);
                var stroke = path.PathPoints;
                if ((path.PathTypes.Last() & 128) != 0) stroke = stroke.Concat(new[] { stroke[0] }).ToArray();
                yield return stroke;
            }
        }
        internal static bool Hit(DrawShape shape, PointF point, float tolerance, float width)
        {
            if (shape is DrawShapeGroup group) return group.Children.Any(c => Hit(c, point, tolerance, width));
            return Strokes(shape).Any(s => Enumerable.Range(1, s.Length - 1).Any(i =>
                GeometryUtilities.DistanceToSegment(point, s[i - 1], s[i]) <= tolerance + StyleValidation.EffectiveWidth(width, shape.ThicknessMultiplier) / 2));
        }
        internal static bool InRectangle(DrawShape shape, RectangleF rect, bool crossing, float baseWidth = 0)
        {
            if (shape is DrawShapeGroup group)
            {
                var children=group.Children.Where(c=>Strokes(c).Any()).ToArray();
                return children.Length>0 && (crossing ? children.Any(c=>InRectangle(c,rect,true,baseWidth)) : children.All(c=>InRectangle(c,rect,false,baseWidth)));
            }
            float half=baseWidth<=0 ? 0 : StyleValidation.EffectiveWidth(baseWidth,shape.ThicknessMultiplier)/2;
            if (!crossing && (rect.Width<2*half || rect.Height<2*half)) return false;
            rect.Inflate(crossing?half:-half,crossing?half:-half);
            var strokes = Strokes(shape).ToArray();
            if (strokes.Length == 0) return false;
            if (!crossing) return strokes.All(s => s.All(p => Inside(rect, p)));
            return strokes.Any(s => s.Any(p => Inside(rect, p)) || Enumerable.Range(1, s.Length - 1).Any(i => Intersects(rect, s[i - 1], s[i])));
        }
        private static bool Inside(RectangleF r, PointF p) => p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;
        private static bool Intersects(RectangleF r, PointF a, PointF b)
        {
            float lo = 0, hi = 1, dx = b.X - a.X, dy = b.Y - a.Y;
            return Clip(-dx, a.X-r.Left, ref lo, ref hi) && Clip(dx, r.Right-a.X, ref lo, ref hi) &&
                Clip(-dy, a.Y-r.Top, ref lo, ref hi) && Clip(dy, r.Bottom-a.Y, ref lo, ref hi);
        }
        private static bool Clip(float p, float q, ref float lo, ref float hi)
        {
            if (p == 0) return q >= 0;
            float t = q / p;
            if (p < 0) lo = Math.Max(lo, t); else hi = Math.Min(hi, t);
            return lo <= hi;
        }
    }
    internal enum EscapeLayer { None, Gesture, Drawing, Selection }
    internal static class AnnotationEscape
    {
        internal static EscapeLayer Layer(bool gesture, bool drawing, bool selection) => gesture ? EscapeLayer.Gesture : drawing ? EscapeLayer.Drawing : selection ? EscapeLayer.Selection : EscapeLayer.None;
    }
    internal static class AnnotationPicking
    {
        internal static (HopperDraw owner, int shape, int handle) Hit(IEnumerable<HopperDraw> owners, PointF p, bool handles)
        {
            // Stable sort preserves reverse document order for owners in the same channel.
            foreach (var o in owners.Reverse().OrderByDescending(o => o.DrawOrder))
                for (int i = o.Shapes.Count-1; i >= 0; i--)
                {
                    if (handles)
                    {
                        if (!o.SelectedShapeIndices.Contains(i) || o.Shapes[i] is DrawShapeGroup) continue;
                        var points = o.Shapes[i].GetPoints();
                        for (int j=0;j<points.Length;j++) if (GeometryUtilities.Distance(p,points[j]) <= 6 * o.CanvasScale) return (o,i,j);
                    }
                    else if (AnnotationGeometry.Hit(o.Shapes[i],p,5*o.CanvasScale,o.LineThickness)) return (o,i,-1);
                }
            return (null,-1,-1);
        }
    }
    internal static class AnnotationSelection
    {
        // Return whether the pressed body remains selected and may start a drag.
        internal static bool Press(bool selected, bool shift, bool control, Action replace, Action add, Action remove)
        {
            if (control) { if (!shift || selected) { remove(); return false; } add(); return true; }
            if (shift) add(); else if (!selected) replace();
            return true;
        }
    }
}
