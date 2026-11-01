using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino.Geometry;

namespace hopperdraw
{
    // Pure container: style commands explicitly visit leaves; grouping never changes their styles.
    public class DrawShapeGroup : DrawShape
    {
        public List<DrawShape> Children { get; } = new List<DrawShape>();
        protected override int ShapeType => 4;
        public override void Render(Graphics g, Pen pen, float thickness)
        {
            foreach (var child in Children) ShapeRenderer.Render(g, child, pen.Color, thickness);
        }
        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness)
        {
            for (int i = Children.Count - 1; i >= 0; i--)
                if (HitChild(Children[i], pt, tolerance, thickness)) return (true, -1);
            return (false, -1);
        }
        private static bool HitChild(DrawShape child, PointF pt, float tolerance, float basis)
        {
            float width = child is DrawShapeGroup ? basis : StyleValidation.EffectiveWidth(basis, child.ThicknessMultiplier);
            return child.HitTest(pt, tolerance, width).hit;
        }
        public override PointF[] GetPoints()
        {
            if (!TryGetBoundingBox(out var box)) return new PointF[0];
            return new[] { new PointF(box.Left, box.Top), new PointF(box.Right, box.Top), new PointF(box.Right, box.Bottom), new PointF(box.Left, box.Bottom) };
        }
        internal bool TryGetBoundingBox(out RectangleF bounds)
        {
            bool found = false;
            float minX = 0, minY = 0, maxX = 0, maxY = 0;
            foreach (var child in Children)
                foreach (var point in child.GetPoints())
                {
                    if (!ShapeSerializer.ValidCoordinate(point.X) || !ShapeSerializer.ValidCoordinate(point.Y)) continue;
                    if (!found) { minX = maxX = point.X; minY = maxY = point.Y; found = true; }
                    else { minX = Math.Min(minX, point.X); minY = Math.Min(minY, point.Y); maxX = Math.Max(maxX, point.X); maxY = Math.Max(maxY, point.Y); }
                }
            bounds = found ? RectangleF.FromLTRB(minX, minY, maxX, maxY) : RectangleF.Empty;
            return found; // Zero width/height is valid geometry, independently of RectangleF.IsEmpty.
        }
        internal RectangleF GetBoundingBox() { TryGetBoundingBox(out var bounds); return bounds; }
        public override void Move(double dx, double dy) { foreach (var child in Children) child.Move(dx, dy); }
        public override void MovePoint(int pointIndex, Point3d newPos) { }
        public override DrawShape Clone()
        {
            var group = new DrawShapeGroup { Id = HopperDraw.GetNextId() };
            group.Children.AddRange(Children.Select(child => child.Clone()));
            return group;
        }
    }
}
