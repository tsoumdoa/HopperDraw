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
        public bool SupportsFill => this is FrameShape || this is PolylineShape p && p.Closed || this is CurveShape c && c.Closed;
        internal bool VisibleFill => SupportsFill && FillColor.HasValue && FillOpacity > 0 &&
            (Math.Round(FillColor.Value.A * Math.Min(1,FillOpacity)) > 0 || Hatch != HatchPattern.None && Math.Round(HatchBackground.GetValueOrDefault().A * Math.Min(1,FillOpacity)) > 0);
        internal bool HasPaint(Color basis) => StrokeVisible && (OverrideColor ?? basis).A > 0 || VisibleFill;
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
        public abstract void Move(double dx, double dy);
        public abstract void MovePoint(int pointIndex, Point3d newPos);
        public abstract DrawShape Clone();

        protected abstract int ShapeType { get; }

    }
}
