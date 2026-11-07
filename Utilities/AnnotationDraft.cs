using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino.Geometry;
using Point = System.Drawing.Point;

namespace hopperdraw
{
    // No host input or persistence: a draft consumes native click counts and screen-space thresholds.
    internal sealed class AnnotationDraft
    {
        internal readonly List<Point3d> Points = new List<Point3d>();
        private Point _press;
        private bool _started;
        internal bool Active => Points.Count > 0;
        internal void Cancel() { Points.Clear(); _started = false; }
        internal void Backspace() { if (Active) Points.RemoveAt(Points.Count-1); }
        internal bool NearStart(Point3d p, float scale) => Points.Distinct().Count() >= 3 && p.DistanceTo(Points[0]) <= 9 * scale;
        internal DrawShape Down(int mode, Point3d p, Point screen, int clicks, bool shift, float scale)
        {
            _press = screen; _started = !Active;
            if (clicks > 1) return mode == 1 || mode == 3 ? Finish(mode, false) : null;
            if (Active && (mode == 1 || mode == 3) && NearStart(p, scale)) return Finish(mode, true);
            if (Active && shift) p = mode == 2 ? Square(Points[0], p) : GeometryUtilities.SnapAngle(Points.Last(), p);
            if (Points.Count == 0 || Points.Last() != p) Points.Add(p);
            if ((mode == 0 || mode == 2) && Points.Count == 2) return Finish(mode, false);
            return null;
        }
        internal DrawShape Up(int mode, Point3d p, Point screen, bool shift, Size threshold)
        {
            if (!_started || !Active || (mode != 0 && mode != 2) || !Moved(_press, screen, threshold)) return null;
            if (shift) p = mode == 2 ? Square(Points[0], p) : GeometryUtilities.SnapAngle(Points[0], p);
            if (p == Points[0]) return null;
            Points.Add(p); return Finish(mode, false);
        }
        internal DrawShape Finish(int mode, bool closed)
        {
            if (Points.Count < 2) return null;
            DrawShape shape = mode == 0 ? (DrawShape)new LineShape(Points[0], Points.Last()) :
                mode == 2 ? Frame(Points[0], Points.Last()) : mode == 1 ? (DrawShape)new PolylineShape(Points.ToList(), closed) : new CurveShape(Points.ToList(), closed);
            Cancel(); return shape;
        }
        internal DrawShape Preview(int mode, Point3d p, bool shift)
        {
            if (!Active) return null;
            if (shift) p = mode == 2 ? Square(Points[0], p) : GeometryUtilities.SnapAngle(Points.Last(), p);
            if (mode == 0) return new LineShape(Points[0], p);
            if (mode == 2) return Frame(Points[0], p);
            var points = Points.Concat(new[] { p }).ToList();
            return mode == 1 ? (DrawShape)new PolylineShape(points) : new CurveShape(points);
        }
        internal static bool Moved(Point a, Point b, Size threshold) => Math.Abs(a.X-b.X) > threshold.Width/2 || Math.Abs(a.Y-b.Y) > threshold.Height/2;
        private static Point3d Square(Point3d a, Point3d b)
        {
            double size = Math.Max(Math.Abs(b.X-a.X), Math.Abs(b.Y-a.Y));
            return new Point3d(a.X + size * (b.X < a.X ? -1 : 1), a.Y + size * (b.Y < a.Y ? -1 : 1), 0);
        }
        private static FrameShape Frame(Point3d a, Point3d b) => new FrameShape(a, new Point3d(b.X,a.Y,0), b, new Point3d(a.X,b.Y,0));
    }
}
