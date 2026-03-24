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
        public float ThicknessMultiplier { get; set; } = 1.0f;
        public Color? OverrideColor { get; set; }
        public DashStyle LineType { get; set; } = DashStyle.Solid;

        public abstract void Render(Graphics g, Pen pen, float thickness);
        public abstract (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness);
        public abstract PointF[] GetPoints();
        public abstract void Move(double dx, double dy);
        public abstract void MovePoint(int pointIndex, Point3d newPos);
        public abstract void Write(GH_IWriter writer, int index);
        public abstract void Read(GH_IReader reader, int index);
        public abstract DrawShape Clone();

        protected abstract int ShapeType { get; }

        protected void WriteCommonProperties(GH_IWriter writer, int index)
        {
            writer.SetInt32($"Type{index}", ShapeType);
            writer.SetDouble($"ThickMult{index}", ThicknessMultiplier);
            writer.SetInt32($"Color{index}", OverrideColor.HasValue ? OverrideColor.Value.ToArgb() : 0);
            writer.SetInt32($"LineType{index}", (int)LineType);
        }

        protected void ReadCommonProperties(GH_IReader reader, int index)
        {
            ThicknessMultiplier = (float)reader.GetDouble($"ThickMult{index}");
            int colorArgb = reader.GetInt32($"Color{index}");
            if (colorArgb != 0)
            {
                OverrideColor = Color.FromArgb(colorArgb);
            }
            int lineType = reader.GetInt32($"LineType{index}");
            if (lineType >= 0 && lineType <= 4)
            {
                LineType = (DashStyle)lineType;
            }
        }
    }
}
