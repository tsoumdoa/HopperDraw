using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using GH_IO.Serialization;
using Rhino.Geometry;

namespace hopperdraw
{
    public class DrawShapeGroup : DrawShape
    {
        public List<DrawShape> Children { get; set; } = new List<DrawShape>();

        protected override int ShapeType => 4;

        public override void Render(Graphics g, Pen pen, float thickness)
        {
            foreach (var child in Children)
            {
                child.Render(g, pen, thickness);
            }
        }

        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness)
        {
            foreach (var child in Children)
            {
                var (hit, ptIdx) = child.HitTest(pt, tolerance, thickness);
                if (hit) return (true, -1);
            }
            return (false, -1);
        }

        public override PointF[] GetPoints()
        {
            if (Children.Count == 0) return new PointF[0];

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var child in Children)
            {
                foreach (var p in child.GetPoints())
                {
                    minX = Math.Min(minX, p.X);
                    minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X);
                    maxY = Math.Max(maxY, p.Y);
                }
            }
            return new PointF[]
            {
                new PointF((float)minX, (float)minY),
                new PointF((float)maxX, (float)minY),
                new PointF((float)maxX, (float)maxY),
                new PointF((float)minX, (float)maxY)
            };
        }

        internal RectangleF GetBoundingBox()
        {
            if (Children.Count == 0) return RectangleF.Empty;

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var child in Children)
            {
                foreach (var p in child.GetPoints())
                {
                    minX = Math.Min(minX, p.X);
                    minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X);
                    maxY = Math.Max(maxY, p.Y);
                }
            }
            return new RectangleF((float)minX, (float)minY, (float)(maxX - minX), (float)(maxY - minY));
        }

        public override void Move(double dx, double dy)
        {
            foreach (var child in Children)
            {
                child.Move(dx, dy);
            }
        }

        public override void MovePoint(int pointIndex, Point3d newPos)
        {
        }

        public override void Write(GH_IWriter writer, int index)
        {
            writer.SetInt32($"GroupChildCount{index}", Children.Count);
            for (int i = 0; i < Children.Count; i++)
            {
                Children[i].Write(writer, 1000 * index + i);
            }
            WriteCommonProperties(writer, index);
        }

        public override void Read(GH_IReader reader, int index)
        {
            int childCount = reader.GetInt32($"GroupChildCount{index}");
            Children.Clear();
            for (int i = 0; i < childCount; i++)
            {
                int childIndex = 1000 * index + i;
                int type = reader.GetInt32($"Type{childIndex}");
                DrawShape child;
                switch (type)
                {
                    case 0: child = new LineShape(); break;
                    case 1: child = new PolylineShape(); break;
                    case 2: child = new FrameShape(); break;
                    case 3: child = new CurveShape(); break;
                    default: child = new LineShape(); break;
                }
                child.Read(reader, childIndex);
                Children.Add(child);
            }
            ReadCommonProperties(reader, index);
        }

        public override DrawShape Clone()
        {
            var group = new DrawShapeGroup();
            foreach (var child in Children)
            {
                var clonedChild = child.Clone();
                clonedChild.ThicknessMultiplier = ThicknessMultiplier;
                clonedChild.OverrideColor = OverrideColor;
                clonedChild.LineType = LineType;
                group.Children.Add(clonedChild);
            }
            group.Id = HopperDraw.GetNextId();
            return group;
        }
    }
}
