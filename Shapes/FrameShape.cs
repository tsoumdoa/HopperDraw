using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using GH_IO.Serialization;
using Rhino.Geometry;

namespace hopperdraw
{
    public class FrameShape : DrawShape
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

        protected override int ShapeType => 2;

        public override void Render(Graphics g, Pen pen, float thickness)
        {
            var points = new PointF[]
            {
                TopLeft.ToPointF(),
                TopRight.ToPointF(),
                BottomRight.ToPointF(),
                BottomLeft.ToPointF(),
                TopLeft.ToPointF()
            };
            g.DrawPolygon(pen, points);
        }

        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness)
        {
            var corners = new PointF[] { TopLeft.ToPointF(), TopRight.ToPointF(), BottomRight.ToPointF(), BottomLeft.ToPointF() };
            for (int i = 0; i < 4; i++)
                if (GeometryUtilities.Distance(pt, corners[i]) <= tolerance) return (true, i);
            if (GeometryUtilities.DistanceToSegment(pt, corners[0], corners[1]) <= thickness / 2 + tolerance) return (true, -1);
            if (GeometryUtilities.DistanceToSegment(pt, corners[1], corners[2]) <= thickness / 2 + tolerance) return (true, -1);
            if (GeometryUtilities.DistanceToSegment(pt, corners[2], corners[3]) <= thickness / 2 + tolerance) return (true, -1);
            if (GeometryUtilities.DistanceToSegment(pt, corners[3], corners[0]) <= thickness / 2 + tolerance) return (true, -1);
            return (false, -1);
        }

        public override PointF[] GetPoints() => new PointF[] { TopLeft.ToPointF(), TopRight.ToPointF(), BottomRight.ToPointF(), BottomLeft.ToPointF() };

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
            WriteCommonProperties(writer, index);
        }

        public override void Read(GH_IReader reader, int index)
        {
            TopLeft = new Point3d(reader.GetDouble($"TLX{index}"), reader.GetDouble($"TLY{index}"), reader.GetDouble($"TLZ{index}"));
            TopRight = new Point3d(reader.GetDouble($"TRX{index}"), reader.GetDouble($"TRY{index}"), reader.GetDouble($"TRZ{index}"));
            BottomRight = new Point3d(reader.GetDouble($"BRX{index}"), reader.GetDouble($"BRY{index}"), reader.GetDouble($"BRZ{index}"));
            BottomLeft = new Point3d(reader.GetDouble($"BLX{index}"), reader.GetDouble($"BLY{index}"), reader.GetDouble($"BLZ{index}"));
            ReadCommonProperties(reader, index);
        }

        public override DrawShape Clone()
        {
            return new FrameShape(TopLeft, TopRight, BottomRight, BottomLeft)
            {
                ThicknessMultiplier = ThicknessMultiplier,
                OverrideColor = OverrideColor,
                LineType = LineType,
                Id = HopperDraw.GetNextId()
            };
        }
    }
}
