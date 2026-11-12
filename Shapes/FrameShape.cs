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
            using (var boundary = AnnotationGeometry.Boundary(this))
                if (boundary.PointCount > 0) g.DrawPath(pen, boundary);
        }

        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness)
        {
            var points = GetPoints();
            for (int i=0;i<points.Length;i++) if (GeometryUtilities.Distance(pt,points[i]) <= tolerance) return (true,i);
            return (AnnotationGeometry.HitLeaf(this,pt,tolerance,thickness),-1);
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

        public override DrawShape Clone()
        {
            var clone = new FrameShape(TopLeft, TopRight, BottomRight, BottomLeft)
            {
                Id = HopperDraw.GetNextId()
            };
            CopyStyleTo(clone);
            CopyArchiveStateTo(clone);
            return clone;
        }
    }
}
