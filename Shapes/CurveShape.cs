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

        public CurveShape() { }
        public CurveShape(List<Point3d> points, bool closed = false) { Points = points; Closed = closed; }

        protected override int ShapeType => 3;

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

        public override DrawShape Clone()
        {
            var clone = new CurveShape(new List<Point3d>(Points), Closed)
            {
                Id = HopperDraw.GetNextId()
            };
            CopyStyleTo(clone);
            CopyArchiveStateTo(clone);
            return clone;
        }
    }
}
