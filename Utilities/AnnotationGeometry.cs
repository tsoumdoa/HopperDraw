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
        // Flattened strokes are cached per leaf and rebuilt only when its control points/closure change, so hover
        // and picking do not rebuild a GraphicsPath for every shape on every mouse move.
        private sealed class StrokeCache { internal PointF[] Key; internal bool Closed; internal PointF[] Stroke; internal RectangleF Bounds; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DrawShape, StrokeCache> Cache =
            new System.Runtime.CompilerServices.ConditionalWeakTable<DrawShape, StrokeCache>();
        private static bool IsClosed(DrawShape shape) => shape is FrameShape || shape is PolylineShape p && p.Closed || shape is CurveShape c && c.Closed;
        private static StrokeCache Leaf(DrawShape shape)
        {
            var points = shape.GetPoints();
            bool closed = IsClosed(shape);
            var cache = Cache.GetValue(shape, _ => new StrokeCache());
            if (cache.Key != null && cache.Closed == closed && cache.Key.SequenceEqual(points)) return cache;
            cache.Key = points; cache.Closed = closed;
            cache.Stroke = points.Length < 2 || !ShapeRenderer.ValidPoints(points) ? null : Flatten(shape, points);
            cache.Bounds = Bounds(cache.Stroke);
            return cache;
        }
        private static RectangleF Bounds(PointF[] stroke)
        {
            if (stroke == null || stroke.Length == 0) return RectangleF.Empty;
            float minX = stroke.Min(p => p.X), minY = stroke.Min(p => p.Y), maxX = stroke.Max(p => p.X), maxY = stroke.Max(p => p.Y);
            return RectangleF.FromLTRB(minX, minY, maxX, maxY);
        }
        internal static IEnumerable<PointF[]> Strokes(DrawShape shape)
        {
            if (shape is OpaqueShape) yield break;
            if (shape is DrawShapeGroup group)
            {
                foreach (var child in group.Children)
                    foreach (var stroke in Strokes(child)) yield return stroke;
                yield break;
            }
            var cached = Leaf(shape).Stroke;
            if (cached != null) yield return cached;
        }
        private static PointF[] Flatten(DrawShape shape, PointF[] points)
        {
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
                return stroke;
            }
        }
        internal static bool Hit(DrawShape shape, PointF point, float tolerance, float width)
        {
            if (shape is DrawShapeGroup group) return group.Children.Any(c => Hit(c, point, tolerance, width));
            if (shape is OpaqueShape) return false;
            var leaf = Leaf(shape);
            if (leaf.Stroke == null) return false;
            float reach = tolerance + StyleValidation.EffectiveWidth(width, shape.ThicknessMultiplier) / 2;
            var b = leaf.Bounds;
            if (point.X < b.Left - reach || point.X > b.Right + reach || point.Y < b.Top - reach || point.Y > b.Bottom + reach) return false;
            var s = leaf.Stroke;
            for (int i = 1; i < s.Length; i++)
                if (GeometryUtilities.DistanceToSegment(point, s[i - 1], s[i]) <= reach) return true;
            return false;
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
