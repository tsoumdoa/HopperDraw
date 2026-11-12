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
            cache.Stroke = points.Length < 2 || !ShapeRenderer.ValidPoints(points) ? null : Flatten(shape);
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
        // Every consumer uses the identical GDI+ cardinal spline and winding rule.
        internal static GraphicsPath Boundary(DrawShape shape)
        {
            var path = new GraphicsPath(FillMode.Winding);
            var points = shape.GetPoints();
            if (points.Length < 2 || !ShapeRenderer.ValidPoints(points)) return path;
            if (shape is CurveShape curve)
            {
                if (curve.Closed && points.Length > 2) path.AddClosedCurve(points, .5f);
                else path.AddCurve(points, .5f);
            }
            else if (IsClosed(shape) && points.Length > 2) path.AddPolygon(points);
            else path.AddLines(points);
            return path;
        }
        private static PointF[] Flatten(DrawShape shape)
        {
            using (var path = Boundary(shape))
            {
                path.Flatten(null, .2f);
                var stroke = path.PathPoints;
                if (stroke.Length > 0 && (path.PathTypes.Last() & 128) != 0) stroke = stroke.Concat(new[] { stroke[0] }).ToArray();
                return stroke;
            }
        }
        // Non-zero winding over the cached flattened boundary: the same winding rule and geometry as paint,
        // without rebuilding a GraphicsPath on every hover/pick.
        internal static bool Interior(DrawShape shape, PointF point) => shape.VisibleFill && Interior(Leaf(shape), point);
        private static bool Interior(StrokeCache leaf, PointF p)
        {
            var s = leaf.Stroke; var b = leaf.Bounds;
            if (s == null || s.Length < 3 || p.X < b.Left || p.X > b.Right || p.Y < b.Top || p.Y > b.Bottom) return false;
            int winding = 0;
            for (int i = 0; i < s.Length; i++)
            {
                var a = s[i]; var c = s[(i + 1) % s.Length];
                double cross = (double)(c.X - a.X) * (p.Y - a.Y) - (double)(p.X - a.X) * (c.Y - a.Y);
                if (a.Y <= p.Y) { if (c.Y > p.Y && cross > 0) winding++; }
                else if (c.Y <= p.Y && cross < 0) winding--;
            }
            return winding != 0;
        }
        // interior=false: backdrop layer (DrawOrder 0) is picked by its outline only, so GH window selection can start over it.
        internal static bool Hit(DrawShape shape, PointF point, float tolerance, float width, bool interior = true)
        {
            if (shape is DrawShapeGroup group) return group.Children.Any(c => Hit(c, point, tolerance, width, interior));
            if (shape is OpaqueShape) return false;
            return HitLeaf(shape,point,tolerance,StyleValidation.EffectiveWidth(width,shape.ThicknessMultiplier),interior);
        }
        // Public shape HitTest receives an already-effective width; controller picking receives a base width.
        internal static bool HitLeaf(DrawShape shape, PointF point, float tolerance, float effectiveWidth, bool interior = true)
        {
            var leaf = Leaf(shape);
            if (leaf.Stroke == null) return false;
            float reach = tolerance + (shape.StrokeVisible ? effectiveWidth / 2 : 0);
            var b = leaf.Bounds;
            if (point.X < b.Left - reach || point.X > b.Right + reach || point.Y < b.Top - reach || point.Y > b.Bottom + reach) return false;
            if (interior && shape.VisibleFill && Interior(leaf, point)) return true;
            var s = leaf.Stroke;
            for (int i = 1; i < s.Length; i++)
                if (GeometryUtilities.DistanceToSegment(point, s[i - 1], s[i]) <= reach) return true;
            return false;
        }
        internal static bool InRectangle(DrawShape shape, RectangleF rect, bool crossing, float baseWidth = 0, bool interior = true)
        {
            if (shape is DrawShapeGroup group)
            {
                var children=group.Children.Where(c=>Strokes(c).Any()).ToArray();
                return children.Length>0 && (crossing ? children.Any(c=>InRectangle(c,rect,true,baseWidth,interior)) : children.All(c=>InRectangle(c,rect,false,baseWidth,interior)));
            }
            float half=baseWidth<=0 || !shape.StrokeVisible ? 0 : StyleValidation.EffectiveWidth(baseWidth,shape.ThicknessMultiplier)/2;
            if (!crossing && (rect.Width<2*half || rect.Height<2*half)) return false;
            if (crossing && interior && new[] { new PointF(rect.Left,rect.Top), new PointF(rect.Right,rect.Top), new PointF(rect.Right,rect.Bottom), new PointF(rect.Left,rect.Bottom) }.Any(p => Interior(shape,p))) return true;
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
                    else if (AnnotationGeometry.Hit(o.Shapes[i],p,5*o.CanvasScale,o.LineThickness,o.DrawOrder!=0)) return (o,i,-1);
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
