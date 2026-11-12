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
            using (var boundary = AnnotationGeometry.Boundary(this))
                if (boundary.PointCount > 0) g.DrawPath(pen, boundary);
        }

        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness)
        {
            var points = GetPoints();
            for (int i=0;i<points.Length;i++) if (GeometryUtilities.Distance(pt,points[i]) <= tolerance) return (true,i);
            return (AnnotationGeometry.HitLeaf(this,pt,tolerance,thickness),-1);
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

        public override DrawShape Clone()
        {
            var clone = new LineShape(Start, End)
            {
                Id = HopperDraw.GetNextId()
            };
            CopyStyleTo(clone);
            CopyArchiveStateTo(clone);
            return clone;
        }
    }
}
