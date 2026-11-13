using System;
using System.Drawing;
using Rhino.Geometry;

namespace hopperdraw
{
    public enum TextStylePreset { Title, Subtitle, Body }

    public sealed class TextShape : DrawShape
    {
        private string _text = "";
        internal static string NormalizeNewlines(string text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        public string Text { get => _text; set => _text = NormalizeNewlines(value); }
        public Point3d Anchor { get; set; }
        public double? BoxWidth { get; set; }
        public TextStylePreset StylePreset { get; set; } = TextStylePreset.Body;
        public double SizeMultiplier { get; set; } = 1;
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public Color? TextColorOverride { get; set; }
        internal TextLayout Layout => TextLayoutService.Get(this);
        public TextShape() { StrokeVisible = false; }
        protected override int ShapeType => 5;
        internal static bool ValidSize(double value) => !double.IsNaN(value) && value > .1 && value <= 20;
        internal void SetPreset(TextStylePreset preset)
        { StylePreset = preset; SizeMultiplier = 1; Bold = preset == TextStylePreset.Title; Italic = false; }
        internal void CopyTextStyleTo(TextShape other)
        {
            other.StylePreset = StylePreset; other.SizeMultiplier = SizeMultiplier;
            other.Bold = Bold; other.Italic = Italic; other.TextColorOverride = TextColorOverride;
        }
        internal bool SameTextStyle(TextShape other) => StylePreset == other.StylePreset && SizeMultiplier == other.SizeMultiplier &&
            Bold == other.Bold && Italic == other.Italic && TextColorOverride?.ToArgb() == other.TextColorOverride?.ToArgb();
        internal bool SameText(TextShape other) => Text == other.Text && Anchor == other.Anchor && BoxWidth == other.BoxWidth && SameTextStyle(other);
        public override PointF[] GetPoints() => new[] { Anchor.ToPointF() };
        internal override RectangleF LayoutBounds => Layout.Bounds;
        internal override PointF[] EditHandles => new[] { new PointF(Layout.Bounds.Right, Layout.Bounds.Top + Layout.Bounds.Height / 2) };
        public override void Move(double dx, double dy) { Anchor = new Point3d(Anchor.X + dx, Anchor.Y + dy, 0); }
        // A box must fit its padding plus at least one em of content, or glyphs are clipped away entirely.
        internal double MinBoxWidth => 1.8 * TextLayoutService.Em(this);
        internal const double MaxBoxWidth = 1000000;
        internal static bool ValidBox(double anchorX, double width) =>
            !double.IsNaN(width) && !double.IsInfinity(width) && width > 0 && width <= MaxBoxWidth && ShapeSerializer.ValidCoordinate(anchorX + width);
        internal double ClampBoxWidth(double width)
        {
            double max = Math.Min(MaxBoxWidth, 10000000 - Anchor.X);
            return Math.Max(Math.Min(MinBoxWidth, max), Math.Min(max, width));
        }
        public override void MovePoint(int pointIndex, Point3d newPos)
        { if (pointIndex == 0 && ShapeSerializer.ValidCoordinate(newPos.X)) BoxWidth = ClampBoxWidth(newPos.X - Anchor.X); }
        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness) =>
            (AnnotationGeometry.HitLeaf(this, pt, tolerance, thickness), -1);
        public override void Render(Graphics g, Pen pen, float thickness) => ShapeRenderer.Render(g, this, pen.Color, thickness);
        public override DrawShape Clone()
        {
            var copy = new TextShape { Id = HopperDraw.GetNextId(), Text = Text, Anchor = Anchor, BoxWidth = BoxWidth };
            CopyTextStyleTo(copy); CopyStyleTo(copy); CopyArchiveStateTo(copy); return copy;
        }
    }
}
