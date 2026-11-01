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
        public Guid PersistentId { get; internal set; } = Guid.NewGuid();
        public float ThicknessMultiplier { get; set; } = 1.0f;
        public Color? OverrideColor { get; set; }
        public DashStyle LineType { get; set; } = DashStyle.Solid;

        public abstract void Render(Graphics g, Pen pen, float thickness);
        public abstract (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness);
        public abstract PointF[] GetPoints();
        public abstract void Move(double dx, double dy);
        public abstract void MovePoint(int pointIndex, Point3d newPos);
        public abstract DrawShape Clone();

        protected abstract int ShapeType { get; }

    }
}
