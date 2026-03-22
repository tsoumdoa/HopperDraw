using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Attributes;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Rhino;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GH_IO.Serialization;

namespace hopperborder
{
    internal enum DrawMode { Line = 0, Polyline = 1, Frame = 2, Curve = 3 }

    internal abstract class DrawShape
    {
        public int Id { get; set; }
        public float ThicknessMultiplier { get; set; } = 1.0f;
        public Color? OverrideColor { get; set; }

        public abstract void Render(Graphics g, Pen pen, float thickness);
        public abstract (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness);
        public abstract PointF[] GetPoints();
        public abstract void Move(double dx, double dy);
        public abstract void MovePoint(int pointIndex, Point3d newPos);
        public abstract void Write(GH_IWriter writer, int index);
        public abstract void Read(GH_IReader reader, int index);
    }

    internal class LineShape : DrawShape
    {
        public Point3d Start { get; set; }
        public Point3d End { get; set; }

        public LineShape() { Start = Point3d.Unset; End = Point3d.Unset; }
        public LineShape(Point3d start, Point3d end) { Start = start; End = end; }

        public override void Render(Graphics g, Pen pen, float thickness)
        {
            if (!Start.IsValid || !End.IsValid) return;
            g.DrawLine(pen, (float)Start.X, (float)Start.Y, (float)End.X, (float)End.Y);
        }

        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness)
        {
            if (Distance(pt, new PointF((float)Start.X, (float)Start.Y)) <= tolerance)
                return (true, 0);
            if (Distance(pt, new PointF((float)End.X, (float)End.Y)) <= tolerance)
                return (true, 1);
            if (DistanceToSegment(pt, new PointF((float)Start.X, (float)Start.Y), new PointF((float)End.X, (float)End.Y)) <= thickness / 2 + tolerance)
                return (true, -1);
            return (false, -1);
        }

        public override PointF[] GetPoints() => new PointF[] { new PointF((float)Start.X, (float)Start.Y), new PointF((float)End.X, (float)End.Y) };

        public override void Move(double dx, double dy)
        {
            Start = new Point3d(Start.X + dx, Start.Y + dy, 0);
            End = new Point3d(End.X + dx, End.Y + dy, 0);
        }

        public override void MovePoint(int pointIndex, Point3d newPos)
        {
            if (pointIndex == 0) Start = newPos;
            else if (pointIndex == 1) End = newPos;
        }

        public override void Write(GH_IWriter writer, int index)
        {
            writer.SetDouble($"StartX{index}", Start.X);
            writer.SetDouble($"StartY{index}", Start.Y);
            writer.SetDouble($"StartZ{index}", Start.Z);
            writer.SetDouble($"EndX{index}", End.X);
            writer.SetDouble($"EndY{index}", End.Y);
            writer.SetDouble($"EndZ{index}", End.Z);
            writer.SetInt32($"Type{index}", 0);
            writer.SetDouble($"ThickMult{index}", ThicknessMultiplier);
            if (OverrideColor.HasValue)
            {
                writer.SetInt32($"Color{index}", OverrideColor.Value.ToArgb());
            }
        }

        public override void Read(GH_IReader reader, int index)
        {
            double sx = reader.GetDouble($"StartX{index}");
            double sy = reader.GetDouble($"StartY{index}");
            double sz = reader.GetDouble($"StartZ{index}");
            double ex = reader.GetDouble($"EndX{index}");
            double ey = reader.GetDouble($"EndY{index}");
            double ez = reader.GetDouble($"EndZ{index}");
            Start = new Point3d(sx, sy, sz);
            End = new Point3d(ex, ey, ez);
            ThicknessMultiplier = (float)reader.GetDouble($"ThickMult{index}");
            int colorArgb = reader.GetInt32($"Color{index}");
            if (colorArgb != 0)
            {
                OverrideColor = Color.FromArgb(colorArgb);
            }
        }

        private float DistanceToSegment(PointF pt, PointF a, PointF b)
        {
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            float lengthSq = dx * dx + dy * dy;
            if (lengthSq < 0.0001f) return Distance(pt, a);
            float t = Math.Max(0, Math.Min(1, ((pt.X - a.X) * dx + (pt.Y - a.Y) * dy) / lengthSq));
            return Distance(pt, new PointF(a.X + t * dx, a.Y + t * dy));
        }

        private float Distance(PointF a, PointF b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    internal class PolylineShape : DrawShape
    {
        public List<Point3d> Points { get; set; } = new List<Point3d>();
        public bool Closed { get; set; } = false;

        public PolylineShape() { }
        public PolylineShape(List<Point3d> points, bool closed = false) { Points = points; Closed = closed; }

        public override void Render(Graphics g, Pen pen, float thickness)
        {
            if (Points.Count < 2) return;
            var pts = Points.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();
            if (Closed && pts.Length > 2)
            {
                g.DrawPolygon(pen, pts);
            }
            else
            {
                g.DrawLines(pen, pts);
            }
        }

        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness)
        {
            for (int i = 0; i < Points.Count; i++)
            {
                if (Distance(pt, new PointF((float)Points[i].X, (float)Points[i].Y)) <= tolerance)
                    return (true, i);
            }
            for (int i = 0; i < Points.Count - 1; i++)
            {
                if (DistanceToSegment(pt, new PointF((float)Points[i].X, (float)Points[i].Y), new PointF((float)Points[i + 1].X, (float)Points[i + 1].Y)) <= thickness / 2 + tolerance)
                    return (true, -1);
            }
            if (Closed && Points.Count > 2)
            {
                if (DistanceToSegment(pt, new PointF((float)Points[Points.Count - 1].X, (float)Points[Points.Count - 1].Y), new PointF((float)Points[0].X, (float)Points[0].Y)) <= thickness / 2 + tolerance)
                    return (true, -1);
            }
            return (false, -1);
        }

        public override PointF[] GetPoints() => Points.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();

        public override void Move(double dx, double dy)
        {
            for (int i = 0; i < Points.Count; i++)
                Points[i] = new Point3d(Points[i].X + dx, Points[i].Y + dy, 0);
        }

        public override void MovePoint(int pointIndex, Point3d newPos)
        {
            if (pointIndex >= 0 && pointIndex < Points.Count)
                Points[pointIndex] = newPos;
        }

        public override void Write(GH_IWriter writer, int index)
        {
            writer.SetInt32($"PointCount{index}", Points.Count);
            writer.SetBoolean($"Closed{index}", Closed);
            for (int i = 0; i < Points.Count; i++)
            {
                writer.SetDouble($"Pt{i}X{index}", Points[i].X);
                writer.SetDouble($"Pt{i}Y{index}", Points[i].Y);
                writer.SetDouble($"Pt{i}Z{index}", Points[i].Z);
            }
            writer.SetInt32($"Type{index}", 1);
            writer.SetDouble($"ThickMult{index}", ThicknessMultiplier);
            if (OverrideColor.HasValue)
            {
                writer.SetInt32($"Color{index}", OverrideColor.Value.ToArgb());
            }
        }

        public override void Read(GH_IReader reader, int index)
        {
            int count = reader.GetInt32($"PointCount{index}");
            Closed = reader.GetBoolean($"Closed{index}");
            Points.Clear();
            for (int i = 0; i < count; i++)
            {
                double px = reader.GetDouble($"Pt{i}X{index}");
                double py = reader.GetDouble($"Pt{i}Y{index}");
                double pz = reader.GetDouble($"Pt{i}Z{index}");
                Points.Add(new Point3d(px, py, pz));
            }
            ThicknessMultiplier = (float)reader.GetDouble($"ThickMult{index}");
            int colorArgb = reader.GetInt32($"Color{index}");
            if (colorArgb != 0)
            {
                OverrideColor = Color.FromArgb(colorArgb);
            }
        }

        private float DistanceToSegment(PointF pt, PointF a, PointF b)
        {
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            float lengthSq = dx * dx + dy * dy;
            if (lengthSq < 0.0001f) return Distance(pt, a);
            float t = Math.Max(0, Math.Min(1, ((pt.X - a.X) * dx + (pt.Y - a.Y) * dy) / lengthSq));
            return Distance(pt, new PointF(a.X + t * dx, a.Y + t * dy));
        }

        private float Distance(PointF a, PointF b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    internal class CurveShape : DrawShape
    {
        public List<Point3d> Points { get; set; } = new List<Point3d>();
        public bool Closed { get; set; } = false;
        private const float Tension = 0.5f;

        public CurveShape() { }
        public CurveShape(List<Point3d> points, bool closed = false) { Points = points; Closed = closed; }

        public override void Render(Graphics g, Pen pen, float thickness)
        {
            if (Points.Count < 2) return;
            var pts = Points.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();
            if (Closed && pts.Length > 2)
            {
                g.DrawClosedCurve(pen, pts, Tension, System.Drawing.Drawing2D.FillMode.Winding);
            }
            else
            {
                g.DrawCurve(pen, pts, Tension);
            }
        }

        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness)
        {
            for (int i = 0; i < Points.Count; i++)
            {
                if (Distance(pt, new PointF((float)Points[i].X, (float)Points[i].Y)) <= tolerance)
                    return (true, i);
            }

            var curvePoints = SampleCurve(20);
            for (int i = 0; i < curvePoints.Count - 1; i++)
            {
                if (DistanceToSegment(pt, curvePoints[i], curvePoints[i + 1]) <= thickness / 2 + tolerance)
                    return (true, -1);
            }
            return (false, -1);
        }

        public override PointF[] GetPoints() => Points.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();

        public override void Move(double dx, double dy)
        {
            for (int i = 0; i < Points.Count; i++)
                Points[i] = new Point3d(Points[i].X + dx, Points[i].Y + dy, 0);
        }

        public override void MovePoint(int pointIndex, Point3d newPos)
        {
            if (pointIndex >= 0 && pointIndex < Points.Count)
                Points[pointIndex] = newPos;
        }

        public override void Write(GH_IWriter writer, int index)
        {
            writer.SetInt32($"PointCount{index}", Points.Count);
            writer.SetBoolean($"Closed{index}", Closed);
            for (int i = 0; i < Points.Count; i++)
            {
                writer.SetDouble($"Pt{i}X{index}", Points[i].X);
                writer.SetDouble($"Pt{i}Y{index}", Points[i].Y);
                writer.SetDouble($"Pt{i}Z{index}", Points[i].Z);
            }
            writer.SetInt32($"Type{index}", 3);
            writer.SetDouble($"ThickMult{index}", ThicknessMultiplier);
            if (OverrideColor.HasValue)
            {
                writer.SetInt32($"Color{index}", OverrideColor.Value.ToArgb());
            }
        }

        public override void Read(GH_IReader reader, int index)
        {
            int count = reader.GetInt32($"PointCount{index}");
            Closed = reader.GetBoolean($"Closed{index}");
            Points.Clear();
            for (int i = 0; i < count; i++)
            {
                double px = reader.GetDouble($"Pt{i}X{index}");
                double py = reader.GetDouble($"Pt{i}Y{index}");
                double pz = reader.GetDouble($"Pt{i}Z{index}");
                Points.Add(new Point3d(px, py, pz));
            }
            ThicknessMultiplier = (float)reader.GetDouble($"ThickMult{index}");
            int colorArgb = reader.GetInt32($"Color{index}");
            if (colorArgb != 0)
            {
                OverrideColor = Color.FromArgb(colorArgb);
            }
        }

        private List<PointF> SampleCurve(int segmentsPerSpan)
        {
            var result = new List<PointF>();
            if (Points.Count < 2) return result;

            var pts = Points.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();

            if (Closed && pts.Length > 2)
            {
                int totalSegments = pts.Length * segmentsPerSpan;
                for (int i = 0; i < totalSegments; i++)
                {
                    float t = (float)i / totalSegments;
                    int idx = (int)(t * pts.Length);
                    float localT = (t * pts.Length) - idx;
                    int i0 = idx;
                    int i1 = (idx + 1) % pts.Length;
                    int i2 = (idx + 2) % pts.Length;
                    int i3 = (idx + 3) % pts.Length;

                    if (i1 >= pts.Length) i1 -= pts.Length;
                    if (i2 >= pts.Length) i2 -= pts.Length;
                    if (i3 >= pts.Length) i3 -= pts.Length;

                    var p = CatmullRom(pts[i0], pts[i1], pts[i2], pts[i3], localT);
                    result.Add(p);
                }
            }
            else
            {
                int totalSegments = (pts.Length - 1) * segmentsPerSpan;
                for (int i = 0; i < totalSegments; i++)
                {
                    float t = (float)i / totalSegments;
                    int span = (int)(t * (pts.Length - 1));
                    if (span >= pts.Length - 1) span = pts.Length - 2;
                    float localT = (t * (pts.Length - 1)) - span;

                    int i0 = Math.Max(0, span - 1);
                    int i1 = span;
                    int i2 = Math.Min(pts.Length - 1, span + 1);
                    int i3 = Math.Min(pts.Length - 1, span + 2);

                    var p = CatmullRom(pts[i0], pts[i1], pts[i2], pts[i3], localT);
                    result.Add(p);
                }
                result.Add(pts[pts.Length - 1]);
            }

            return result;
        }

        private PointF CatmullRom(PointF p0, PointF p1, PointF p2, PointF p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return new PointF(
                0.5f * ((2 * p1.X) + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3),
                0.5f * ((2 * p1.Y) + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3)
            );
        }

        private float DistanceToSegment(PointF pt, PointF a, PointF b)
        {
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            float lengthSq = dx * dx + dy * dy;
            if (lengthSq < 0.0001f) return Distance(pt, a);
            float t = Math.Max(0, Math.Min(1, ((pt.X - a.X) * dx + (pt.Y - a.Y) * dy) / lengthSq));
            return Distance(pt, new PointF(a.X + t * dx, a.Y + t * dy));
        }

        private float Distance(PointF a, PointF b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    internal class FrameShape : DrawShape
    {
        public Point3d TopLeft { get; set; }
        public Point3d TopRight { get; set; }
        public Point3d BottomRight { get; set; }
        public Point3d BottomLeft { get; set; }

        public FrameShape() { }
        public FrameShape(Point3d topLeft, Point3d topRight, Point3d bottomRight, Point3d bottomLeft)
        {
            TopLeft = topLeft; TopRight = topRight; BottomRight = bottomRight; BottomLeft = bottomLeft;
        }

        public override void Render(Graphics g, Pen pen, float thickness)
        {
            var points = new PointF[]
            {
                new PointF((float)TopLeft.X, (float)TopLeft.Y),
                new PointF((float)TopRight.X, (float)TopRight.Y),
                new PointF((float)BottomRight.X, (float)BottomRight.Y),
                new PointF((float)BottomLeft.X, (float)BottomLeft.Y),
                new PointF((float)TopLeft.X, (float)TopLeft.Y)
            };
            g.DrawPolygon(pen, points);
        }

        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness)
        {
            var corners = new PointF[] { new PointF((float)TopLeft.X, (float)TopLeft.Y), new PointF((float)TopRight.X, (float)TopRight.Y), new PointF((float)BottomRight.X, (float)BottomRight.Y), new PointF((float)BottomLeft.X, (float)BottomLeft.Y) };
            for (int i = 0; i < 4; i++)
                if (Distance(pt, corners[i]) <= tolerance) return (true, i);
            if (HitTestLine(pt, corners[0], corners[1], thickness, tolerance)) return (true, -1);
            if (HitTestLine(pt, corners[1], corners[2], thickness, tolerance)) return (true, -1);
            if (HitTestLine(pt, corners[2], corners[3], thickness, tolerance)) return (true, -1);
            if (HitTestLine(pt, corners[3], corners[0], thickness, tolerance)) return (true, -1);
            return (false, -1);
        }

        public override PointF[] GetPoints() => new PointF[] { new PointF((float)TopLeft.X, (float)TopLeft.Y), new PointF((float)TopRight.X, (float)TopRight.Y), new PointF((float)BottomRight.X, (float)BottomRight.Y), new PointF((float)BottomLeft.X, (float)BottomLeft.Y) };

        public override void Move(double dx, double dy)
        {
            TopLeft = new Point3d(TopLeft.X + dx, TopLeft.Y + dy, 0);
            TopRight = new Point3d(TopRight.X + dx, TopRight.Y + dy, 0);
            BottomRight = new Point3d(BottomRight.X + dx, BottomRight.Y + dy, 0);
            BottomLeft = new Point3d(BottomLeft.X + dx, BottomLeft.Y + dy, 0);
        }

        public override void MovePoint(int pointIndex, Point3d newPos)
        {
            switch (pointIndex)
            {
                case 0: TopLeft = newPos; break;
                case 1: TopRight = newPos; break;
                case 2: BottomRight = newPos; break;
                case 3: BottomLeft = newPos; break;
            }
        }

        public override void Write(GH_IWriter writer, int index)
        {
            writer.SetDouble($"TLX{index}", TopLeft.X);
            writer.SetDouble($"TLY{index}", TopLeft.Y);
            writer.SetDouble($"TLZ{index}", TopLeft.Z);
            writer.SetDouble($"TRX{index}", TopRight.X);
            writer.SetDouble($"TRY{index}", TopRight.Y);
            writer.SetDouble($"TRZ{index}", TopRight.Z);
            writer.SetDouble($"BRX{index}", BottomRight.X);
            writer.SetDouble($"BRY{index}", BottomRight.Y);
            writer.SetDouble($"BRZ{index}", BottomRight.Z);
            writer.SetDouble($"BLX{index}", BottomLeft.X);
            writer.SetDouble($"BLY{index}", BottomLeft.Y);
            writer.SetDouble($"BLZ{index}", BottomLeft.Z);
            writer.SetInt32($"Type{index}", 2);
            writer.SetDouble($"ThickMult{index}", ThicknessMultiplier);
            if (OverrideColor.HasValue)
            {
                writer.SetInt32($"Color{index}", OverrideColor.Value.ToArgb());
            }
        }

        public override void Read(GH_IReader reader, int index)
        {
            TopLeft = new Point3d(reader.GetDouble($"TLX{index}"), reader.GetDouble($"TLY{index}"), reader.GetDouble($"TLZ{index}"));
            TopRight = new Point3d(reader.GetDouble($"TRX{index}"), reader.GetDouble($"TRY{index}"), reader.GetDouble($"TRZ{index}"));
            BottomRight = new Point3d(reader.GetDouble($"BRX{index}"), reader.GetDouble($"BRY{index}"), reader.GetDouble($"BRZ{index}"));
            BottomLeft = new Point3d(reader.GetDouble($"BLX{index}"), reader.GetDouble($"BLY{index}"), reader.GetDouble($"BLZ{index}"));
            ThicknessMultiplier = (float)reader.GetDouble($"ThickMult{index}");
            int colorArgb = reader.GetInt32($"Color{index}");
            if (colorArgb != 0)
            {
                OverrideColor = Color.FromArgb(colorArgb);
            }
        }

        private bool HitTestLine(PointF pt, PointF a, PointF b, float thickness, float tolerance) => DistanceToSegment(pt, a, b) <= thickness / 2 + tolerance;

        private float DistanceToSegment(PointF pt, PointF a, PointF b)
        {
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            float lengthSq = dx * dx + dy * dy;
            if (lengthSq < 0.0001f) return Distance(pt, a);
            float t = Math.Max(0, Math.Min(1, ((pt.X - a.X) * dx + (pt.Y - a.Y) * dy) / lengthSq));
            return Distance(pt, new PointF(a.X + t * dx, a.Y + t * dy));
        }

        private float Distance(PointF a, PointF b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    internal class BorderAnnotation : GH_Component
    {
        private static int _shapeIdCounter = 1;

        public List<DrawShape> Shapes { get; set; } = new List<DrawShape>();
        public Color BorderColor { get; set; } = Color.Black;
        public float LineThickness { get; set; } = 8f;
        public int DrawOrder { get; set; } = 1;
        public bool Visible { get; set; } = true;
        public BorderController Controller { get; set; }

        public int SelectedShapeIndex { get; set; } = -1;
        public int SelectedPointIndex { get; set; } = -1;
        public int HoveredShapeIndex { get; set; } = -1;
        public int HoveredPointIndex { get; set; } = -1;

        public static int GetNextId() => _shapeIdCounter++;

        private const float HandleSize = 8f;
        private const float HitTolerance = 15f;

        public BorderAnnotation() : base("Canvas Border", "Border", "Canvas border annotation", "Draw", "Annotation")
        {
            CreateAttributes();
        }

        public override Guid ComponentGuid => new Guid("B1C2D3E4-F5A6-7890-1234-567890ABCDEF");

        public override void CreateAttributes()
        {
            m_attributes = new BorderAnnotationAttributes(this);
        }

        public RectangleF Bounds
        {
            get
            {
                if (Shapes.Count == 0) return new RectangleF(0, 0, 1, 1);
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var shape in Shapes)
                {
                    foreach (var pt in shape.GetPoints())
                    {
                        minX = Math.Min(minX, pt.X);
                        minY = Math.Min(minY, pt.Y);
                        maxX = Math.Max(maxX, pt.X);
                        maxY = Math.Max(maxY, pt.Y);
                    }
                }
                return new RectangleF((float)(minX - HandleSize), (float)(minY - HandleSize), (float)(maxX - minX + HandleSize * 2), (float)(maxY - minY + HandleSize * 2));
            }
        }

        public (int shapeIndex, int pointIndex) HitTest(PointF point)
        {
            for (int i = 0; i < Shapes.Count; i++)
            {
                var (hit, ptIdx) = Shapes[i].HitTest(point, HitTolerance, LineThickness);
                if (hit) return (i, ptIdx);
            }
            return (-1, -1);
        }

        public void ClearSelection()
        {
            SelectedShapeIndex = -1;
            SelectedPointIndex = -1;
            HoveredShapeIndex = -1;
            HoveredPointIndex = -1;
        }

        public void ExpireDisplay()
        {
            var doc = OnPingDocument();
            if (doc != null)
                doc.ScheduleSolution(5, d => this.ExpirePreview(false));
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager) { }
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager) { }
        protected override void SolveInstance(IGH_DataAccess DA) { }

        public override bool Write(GH_IWriter writer)
        {
            writer.SetInt32("ShapeCount", Shapes.Count);
            writer.SetInt32("Color", BorderColor.ToArgb());
            writer.SetDouble("Thickness", LineThickness);
            writer.SetInt32("DrawOrder", DrawOrder);
            for (int i = 0; i < Shapes.Count; i++)
            {
                Shapes[i].Write(writer, i);
            }
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            Shapes.Clear();
            int count = reader.GetInt32("ShapeCount");
            BorderColor = Color.FromArgb(reader.GetInt32("Color"));
            LineThickness = (float)reader.GetDouble("Thickness");
            DrawOrder = reader.GetInt32("DrawOrder");
            for (int i = 0; i < count; i++)
            {
                int type = reader.GetInt32($"Type{i}");
                DrawShape shape;
                switch (type)
                {
                    case 0:
                        shape = new LineShape();
                        break;
                    case 1:
                        shape = new PolylineShape();
                        break;
                    case 2:
                        shape = new FrameShape();
                        break;
                    case 3:
                        shape = new CurveShape();
                        break;
                    default:
                        shape = new LineShape();
                        break;
                }
                shape.Read(reader, i);
                Shapes.Add(shape);
            }
            return base.Read(reader);
        }
    }

    internal class BorderAnnotationAttributes : GH_ComponentAttributes
    {
        public BorderAnnotationAttributes(BorderAnnotation owner) : base(owner) { }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            var annotation = (BorderAnnotation)Owner;

            bool shouldRenderInThisChannel = 
                (annotation.DrawOrder == 0 && channel == GH_CanvasChannel.Objects) ||
                (annotation.DrawOrder == 1 && channel == GH_CanvasChannel.Overlay);

            if (!shouldRenderInThisChannel)
            {
                if (channel == GH_CanvasChannel.Objects || channel == GH_CanvasChannel.Overlay)
                {
                    base.Render(canvas, graphics, channel);
                }
                return;
            }

            if (!annotation.Visible || (annotation.Controller != null && annotation.Controller.Locked))
            {
                base.Render(canvas, graphics, channel);
                return;
            }

            foreach (var shape in annotation.Shapes)
            {
                float effectiveThickness = annotation.LineThickness * shape.ThicknessMultiplier;
                Color effectiveColor = shape.OverrideColor ?? annotation.BorderColor;
                using (var pen = new Pen(effectiveColor, effectiveThickness))
                {
                    shape.Render(graphics, pen, effectiveThickness);
                }
            }

            if (annotation.SelectedShapeIndex >= 0 && annotation.SelectedShapeIndex < annotation.Shapes.Count)
            {
                var selectedShape = annotation.Shapes[annotation.SelectedShapeIndex];
                float selectedThickness = annotation.LineThickness * selectedShape.ThicknessMultiplier;
                RenderSelectionHandles(graphics, selectedShape, annotation.SelectedPointIndex, selectedThickness);
            }

            if (annotation.HoveredShapeIndex >= 0 && annotation.HoveredShapeIndex < annotation.Shapes.Count)
            {
                var hoveredShape = annotation.Shapes[annotation.HoveredShapeIndex];
                float hoveredThickness = annotation.LineThickness * hoveredShape.ThicknessMultiplier;
                RenderHoverHandles(graphics, hoveredShape, annotation.HoveredPointIndex, hoveredThickness);
            }
        }

        private void RenderSelectionHandles(Graphics g, DrawShape shape, int selectedPointIndex, float thickness)
        {
            var points = shape.GetPoints();
            using (var handleBrush = new SolidBrush(Color.White))
            using (var handlePen = new Pen(Color.FromArgb(200, 0, 120, 215), 2))
            {
                for (int i = 0; i < points.Length; i++)
                {
                    var pt = points[i];
                    g.FillRectangle(handleBrush, pt.X - 4, pt.Y - 4, 8, 8);
                    g.DrawRectangle(handlePen, (int)pt.X - 4, (int)pt.Y - 4, 8, 8);
                }
            }
            using (var selPen = new Pen(Color.FromArgb(200, 0, 120, 215), 1))
            {
                selPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                if (points.Length > 1)
                {
                    if (shape is FrameShape || shape is PolylineShape poly && poly.Closed)
                        g.DrawPolygon(selPen, points);
                    else
                        g.DrawLines(selPen, points);
                }
            }
        }

        private void RenderHoverHandles(Graphics g, DrawShape shape, int hoveredPointIndex, float thickness)
        {
            var points = shape.GetPoints();
            using (var hoverBrush = new SolidBrush(Color.FromArgb(100, 0, 120, 215)))
            {
                for (int i = 0; i < points.Length; i++)
                {
                    if (i == hoveredPointIndex)
                    {
                        var pt = points[i];
                        g.FillRectangle(hoverBrush, pt.X - 4, pt.Y - 4, 8, 8);
                    }
                }
            }
        }
    }
}
