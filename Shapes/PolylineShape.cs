using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using GH_IO.Serialization;
using Rhino.Geometry;

namespace hopperdraw
{
    public class PolylineShape : DrawShape
    {
        public List<Point3d> Points { get; set; } = new List<Point3d>();
        public bool Closed { get; set; } = false;

        public PolylineShape() { }
        public PolylineShape(List<Point3d> points, bool closed = false) { Points = points; Closed = closed; }

        protected override int ShapeType => 1;

        public override void Render(Graphics g, Pen pen, float thickness)
        {
            if (Points.Count < 2) return;
            var pts = Points.ToPointFArray();
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
                if (GeometryUtilities.Distance(pt, Points[i].ToPointF()) <= tolerance)
                    return (true, i);
            }
            for (int i = 0; i < Points.Count - 1; i++)
            {
                if (GeometryUtilities.DistanceToSegment(pt, Points[i].ToPointF(), Points[i + 1].ToPointF()) <= thickness / 2 + tolerance)
                    return (true, -1);
            }
            if (Closed && Points.Count > 2)
            {
                if (GeometryUtilities.DistanceToSegment(pt, Points[Points.Count - 1].ToPointF(), Points[0].ToPointF()) <= thickness / 2 + tolerance)
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

        public override DrawShape Clone()
        {
            return new PolylineShape(new List<Point3d>(Points), Closed)
            {
                ThicknessMultiplier = ThicknessMultiplier,
                OverrideColor = OverrideColor,
                LineType = LineType,
                Id = HopperDraw.GetNextId()
            };
        }
    }
}
