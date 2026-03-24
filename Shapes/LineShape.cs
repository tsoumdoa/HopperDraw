using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using GH_IO.Serialization;
using Rhino.Geometry;

namespace hopperdraw
{
    public class LineShape : DrawShape
    {
        public Point3d Start { get; set; }
        public Point3d End { get; set; }

        public LineShape() { Start = Point3d.Unset; End = Point3d.Unset; }
        public LineShape(Point3d start, Point3d end) { Start = start; End = end; }

        protected override int ShapeType => 0;

        public override void Render(Graphics g, Pen pen, float thickness)
        {
            if (!Start.IsValid || !End.IsValid) return;
            g.DrawLine(pen, (float)Start.X, (float)Start.Y, (float)End.X, (float)End.Y);
        }

        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness)
        {
            if (GeometryUtilities.Distance(pt, Start.ToPointF()) <= tolerance)
                return (true, 0);
            if (GeometryUtilities.Distance(pt, End.ToPointF()) <= tolerance)
                return (true, 1);
            if (GeometryUtilities.DistanceToSegment(pt, Start.ToPointF(), End.ToPointF()) <= thickness / 2 + tolerance)
                return (true, -1);
            return (false, -1);
        }

        public override PointF[] GetPoints() => new PointF[] { Start.ToPointF(), End.ToPointF() };

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
            WriteCommonProperties(writer, index);
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
            ReadCommonProperties(reader, index);
        }

        public override DrawShape Clone()
        {
            return new LineShape(Start, End)
            {
                ThicknessMultiplier = ThicknessMultiplier,
                OverrideColor = OverrideColor,
                LineType = LineType,
                Id = HopperDraw.GetNextId()
            };
        }
    }
}
