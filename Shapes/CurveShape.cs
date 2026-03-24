using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using GH_IO.Serialization;
using Rhino.Geometry;

namespace hopperdraw
{
    public class CurveShape : DrawShape
    {
        public List<Point3d> Points { get; set; } = new List<Point3d>();
        public bool Closed { get; set; } = false;
        private const float Tension = 0.5f;

        public CurveShape() { }
        public CurveShape(List<Point3d> points, bool closed = false) { Points = points; Closed = closed; }

        protected override int ShapeType => 3;

        public override void Render(Graphics g, Pen pen, float thickness)
        {
            if (Points.Count < 2) return;
            var pts = Points.ToPointFArray();
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
                if (GeometryUtilities.Distance(pt, Points[i].ToPointF()) <= tolerance)
                    return (true, i);
            }

            var curvePoints = SampleCurve(20);
            for (int i = 0; i < curvePoints.Count - 1; i++)
            {
                if (GeometryUtilities.DistanceToSegment(pt, curvePoints[i], curvePoints[i + 1]) <= thickness / 2 + tolerance)
                    return (true, -1);
            }
            return (false, -1);
        }

        public override PointF[] GetPoints() => Points.ToPointFArray();

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
            WriteCommonProperties(writer, index);
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
            ReadCommonProperties(reader, index);
        }

        private List<PointF> SampleCurve(int segmentsPerSpan)
        {
            var result = new List<PointF>();
            if (Points.Count < 2) return result;

            var pts = Points.ToPointFArray();

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

        public override DrawShape Clone()
        {
            return new CurveShape(new List<Point3d>(Points), Closed)
            {
                ThicknessMultiplier = ThicknessMultiplier,
                OverrideColor = OverrideColor,
                LineType = LineType,
                Id = HopperDraw.GetNextId()
            };
        }
    }
}
