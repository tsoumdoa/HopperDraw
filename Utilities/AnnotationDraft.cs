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
        internal sealed class TextPlacement
        {
            internal Point3d Anchor; internal double? Width;
        }
        internal TextPlacement Placement { get; private set; }
        internal TextPlacement TakePlacement() { var result = Placement; Placement = null; return result; }
        internal readonly List<Point3d> Points = new List<Point3d>();
        private Point _press;
        private bool _started;
        internal bool Held { get; private set; }
        private double _gestureZoom;
        private float _closeScale;
        private SmoothingLevel _smoothing;
        internal void Sample(Point3d point, PointF previousOnScreen, PointF currentOnScreen)
        {
            if (!Held || GeometryUtilities.Distance(previousOnScreen,currentOnScreen)<2) return;
            if(Points.Count>=FreehandStroke.MaxRaw)
            { for(int i=Points.Count-2;i>0;i-=2) Points.RemoveAt(i); _version++; }
            var last=Points.Last();
            if(last!=point) { _travel+=last.DistanceTo(point); _extent=Math.Max(_extent,point.DistanceTo(Points[0])); Points.Add(point); _version++; }
        }
        // Tracked incrementally so paint-time close feedback never scans the raw samples.
        private double _travel, _extent;
        private int _version, _ringVersion=-1; private Point3d _ringPoint; private float _ringScale; private bool _ringResult;
        internal bool Active => Points.Count > 0;
        internal void Cancel() { Placement = null; Points.Clear(); _started = false; Held=false; _travel=0; _extent=0; _version++; }
        internal void Backspace() { if (Active && !Held) Points.RemoveAt(Points.Count-1); }
        internal bool NearStart(Point3d p, float scale) => Held ? FreehandWouldClose(p, scale)
            : Points.Distinct().Count() >= 3 && p.DistanceTo(Points[0]) <= 9 * scale;
        // For a held freehand stroke the ring promises exactly what release will produce: cheap incremental gates
        // first, then the real FreehandStroke.Process (incl. degeneracy fallback), cached until samples/pointer change.
        private bool FreehandWouldClose(Point3d p, float scale)
        {
            if (Points.Count<3 || p.DistanceTo(Points[0]) > 9 * scale || _extent*_gestureZoom < FreehandStroke.CloseExtent ||
                (_travel+Points.Last().DistanceTo(p))*_gestureZoom < FreehandStroke.CloseTravel) return false;
            if (_ringVersion==_version && _ringPoint==p && _ringScale==scale) return _ringResult;
            var release=new List<Point3d>(Points);
            if (release.Last()!=p) { if (release.Count>=FreehandStroke.MaxRaw) release.RemoveAt(release.Count-1); release.Add(p); }
            _ringResult=FreehandStroke.Process(release,_gestureZoom,_smoothing,scale)?.Closed==true;
            _ringVersion=_version; _ringPoint=p; _ringScale=scale;
            return _ringResult;
        }
        internal DrawShape Down(int mode, Point3d p, Point screen, int clicks, bool shift, float scale, SmoothingLevel smoothing = SmoothingLevel.Medium)
        {
            if (mode == 5)
            { Cancel(); Points.Add(p); _press = screen; _started = true; return null; }
            if(mode==4)
            {
                Cancel(); Points.Add(p); _version++; Held=true; _gestureZoom=1/Math.Max(.00001,scale); _closeScale=scale; _smoothing=smoothing; return null;
            }
            _press = screen; _started = !Active;
            if (clicks > 1) return mode == 1 || mode == 3 ? Finish(mode, false) : null;
            if (Active && (mode == 1 || mode == 3) && NearStart(p, scale)) return Finish(mode, true);
            if (Active && shift) p = mode == 2 ? Square(Points[0], p) : GeometryUtilities.SnapAngle(Points.Last(), p);
            if (Points.Count == 0 || Points.Last() != p) Points.Add(p);
            if ((mode == 0 || mode == 2) && Points.Count == 2) return Finish(mode, false);
            return null;
        }
        internal DrawShape Up(int mode, Point3d p, Point screen, bool shift, Size threshold, float? currentScale = null)
        {
            if (mode == 5)
            {
                if (!_started || !Active) return null;
                var a = Points[0]; bool box = Moved(_press, screen, threshold);
                Cancel();
                Placement = new TextPlacement { Anchor = box ? new Point3d(Math.Min(a.X,p.X),Math.Min(a.Y,p.Y),0) : a,
                    Width = box ? (double?)Math.Max(1,Math.Abs(p.X-a.X)) : null };
                return null;
            }
            if(mode==4)
            {
                if(!Held) return null;
                if(Points.Last()!=p) { if(Points.Count>=FreehandStroke.MaxRaw) Points.RemoveAt(Points.Count-1); Points.Add(p); }
                var result=FreehandStroke.Process(Points,_gestureZoom,_smoothing,currentScale ?? _closeScale); Cancel(); return result;
            }
            if (!_started || !Active || (mode != 0 && mode != 2) || !Moved(_press, screen, threshold)) return null;
            if (shift) p = mode == 2 ? Square(Points[0], p) : GeometryUtilities.SnapAngle(Points[0], p);
            if (p == Points[0]) return null;
            Points.Add(p); return Finish(mode, false);
        }
        internal DrawShape Finish(int mode, bool closed)
        {
            if (Held || mode==4 || mode==5 || Points.Count < 2) return null;
            DrawShape shape = mode == 0 ? (DrawShape)new LineShape(Points[0], Points.Last()) :
                mode == 2 ? Frame(Points[0], Points.Last()) : mode == 1 ? (DrawShape)new PolylineShape(Points.ToList(), closed) : new CurveShape(Points.ToList(), closed);
            Cancel(); return shape;
        }
        internal DrawShape Preview(int mode, Point3d p, bool shift)
        {
            if (!Active) return null;
            // Shares the sample list (no per-paint copy); the preview shape is transient and never mutated.
            if (mode==5) return Frame(Points[0],p);
            if (mode==4) return Points.Count<2 ? null : new CurveShape(Points);
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
