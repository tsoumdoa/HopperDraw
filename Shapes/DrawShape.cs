using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using GH_IO.Serialization;
using Rhino.Geometry;

namespace hopperdraw
{
    public abstract class DrawShape
    {
        public int Id { get; set; }
        internal GH_LooseChunk Extensions { get; set; }
        internal System.Collections.Generic.List<string> LoadDiagnostics { get; } = new System.Collections.Generic.List<string>();
        internal void CopyArchiveStateTo(DrawShape target)
        {
            target.Extensions = Extensions == null ? null : ShapeSerializer.Capture(Extensions);
            target.LoadDiagnostics.AddRange(LoadDiagnostics);
        }
        public Guid PersistentId { get; internal set; } = Guid.NewGuid();
        private float _thicknessMultiplier = 1f;
        public float ThicknessMultiplier { get => _thicknessMultiplier; set => _thicknessMultiplier = StyleValidation.Multiplier(value); }
        public Color? OverrideColor { get; set; }
        public DashStyle LineType { get; set; } = DashStyle.Solid;

        public bool StrokeVisible { get; set; } = true;
        public Color? FillColor { get; set; }
        public double FillOpacity { get; set; } = 1;
        public HatchPattern Hatch { get; set; }
        public double HatchScale { get; set; } = 1;
        public Color? HatchBackground { get; set; }
        public bool SupportsFill => this is TextShape || this is FrameShape || this is PolylineShape p && p.Closed || this is CurveShape c && c.Closed;
        internal bool VisibleFill => SupportsFill && FillColor.HasValue && FillOpacity > 0 &&
            (Math.Round(FillColor.Value.A * Math.Min(1,FillOpacity)) > 0 || Hatch != HatchPattern.None && Math.Round(HatchBackground.GetValueOrDefault().A * Math.Min(1,FillOpacity)) > 0) &&
            HasArea;
        // A closed but collinear/zero-area boundary paints no fill, so it must not count as paint or as a pick region.
        internal bool HasArea
        {
            get
            {
                if (this is TextShape t) return t.LayoutBounds.Width > 0 && t.LayoutBounds.Height > 0;
                if (this is FrameShape f) return Spans(new[] { f.TopLeft, f.TopRight, f.BottomRight, f.BottomLeft });
                if (this is PolylineShape p) return Spans(p.Points);
                if (this is CurveShape c) return Spans(c.Points);
                return false;
            }
        }
        private static bool Spans(System.Collections.Generic.IList<Rhino.Geometry.Point3d> points)
        {
            if (points == null || points.Count < 3) return false;
            var a = points[0]; int j = 1;
            while (j < points.Count && points[j] == a) j++;
            if (j >= points.Count) return false;
            var b = points[j];
            double scale = Math.Max(1e-12, a.DistanceTo(b));
            for (int i = j + 1; i < points.Count; i++)
                if (Math.Abs((b.X-a.X)*(points[i].Y-a.Y)-(b.Y-a.Y)*(points[i].X-a.X)) > 1e-9 * scale * Math.Max(1, a.DistanceTo(points[i]))) return true;
            return false;
        }
        internal bool HasPaint(Color basis) => this is TextShape t && !string.IsNullOrWhiteSpace(t.Text) && (t.TextColorOverride ?? basis).A > 0 || StrokeVisible && (OverrideColor ?? basis).A > 0 || VisibleFill;
        internal void CopyStyleTo(DrawShape target)
        {
            target.OverrideColor = OverrideColor; target.ThicknessMultiplier = ThicknessMultiplier; target.LineType = LineType;
            target.StrokeVisible = StrokeVisible; target.FillColor = FillColor; target.FillOpacity = FillOpacity;
            target.Hatch = Hatch; target.HatchScale = HatchScale; target.HatchBackground = HatchBackground;
        }
        internal bool SameStyle(DrawShape other) => OverrideColor?.ToArgb() == other.OverrideColor?.ToArgb() &&
            ThicknessMultiplier == other.ThicknessMultiplier && LineType == other.LineType && StrokeVisible == other.StrokeVisible &&
            FillColor?.ToArgb() == other.FillColor?.ToArgb() && FillOpacity == other.FillOpacity && Hatch == other.Hatch &&
            HatchScale == other.HatchScale && HatchBackground?.ToArgb() == other.HatchBackground?.ToArgb();

        internal virtual bool CanTransformGeometry => true;

        public abstract void Render(Graphics g, Pen pen, float thickness);
        public abstract (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness);
        public abstract PointF[] GetPoints();
        internal virtual PointF[] EditHandles => GetPoints();
        internal virtual RectangleF LayoutBounds
        {
            get { var p = GetPoints(); if (p.Length == 0) return RectangleF.Empty;
                return RectangleF.FromLTRB(System.Linq.Enumerable.Min(p, x => x.X), System.Linq.Enumerable.Min(p, x => x.Y),
                    System.Linq.Enumerable.Max(p, x => x.X), System.Linq.Enumerable.Max(p, x => x.Y)); }
        }
        public abstract void Move(double dx, double dy);
        public abstract void MovePoint(int pointIndex, Point3d newPos);
        public abstract DrawShape Clone();

        protected abstract int ShapeType { get; }

    }
}
