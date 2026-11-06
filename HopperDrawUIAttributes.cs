using Grasshopper.Kernel.Attributes;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace hopperdraw
{
    public class HopperDrawUIAttributes : GH_ComponentAttributes
    {
        public HopperDrawUIAttributes(HopperDraw owner) : base(owner) { }

        protected override void Layout()
        {
            base.Layout();
            Bounds = new RectangleF(Bounds.X, Bounds.Y, Bounds.Width + 10, Bounds.Height + 24);
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);
            var owner = (HopperDraw)Owner;
            if (channel == GH_CanvasChannel.Objects)
            {
                var names = new[] { "Line", "Polyline", "Frame", "Curve" };
                string text = names[StyleValidation.Clamp(owner.DrawMode, 0, 3)] + (owner.DrawingModeEnabled ? " ON" : "");
                var footer = new RectangleF(Bounds.X + 4, Bounds.Bottom - 22, Bounds.Width - 8, 18);
                using (var capsule = GH_Capsule.CreateTextCapsule(footer, footer,
                    owner.DrawingModeEnabled ? GH_Palette.Blue : GH_Palette.Black, text, 2, 0))
                    capsule.Render(graphics, Selected, Owner.Locked, false);
            }
            if (owner.Locked || !owner.Visible) return;
            var shapeChannel = owner.DrawOrder == 0 ? GH_CanvasChannel.Groups : GH_CanvasChannel.Overlay;
            if (channel == shapeChannel)
                foreach (var shape in owner.Shapes) ShapeRenderer.Render(graphics, shape, owner.BorderColor, owner.LineThickness);
            if (channel != GH_CanvasChannel.Overlay) return;
            if (owner.IsActivated && (owner.Draft.Active || owner.IsDrawing))
            {
                var point = owner.DrawEnd;
                if (canvas?.Viewport != null)
                {
                    var mouse = canvas.Viewport.UnprojectPoint(canvas.PointToClient(Control.MousePosition));
                    point = new Point3d(mouse.X, mouse.Y, 0);
                }
                var preview = owner.Draft.Active ? owner.Draft.Preview(owner.DrawMode, point, owner.ShiftPressed) : BuildPreview(owner, point);
                if (owner.Draft.NearStart(point, owner.CanvasScale))
                    using (var ring = new Pen(Color.LimeGreen, 2 * owner.CanvasScale))
                    { var first = owner.Draft.Points[0]; float r = 9 * owner.CanvasScale; graphics.DrawEllipse(ring, (float)first.X-r,(float)first.Y-r,2*r,2*r); }
                if (preview != null)
                {
                    preview.ThicknessMultiplier = owner.CurrentThicknessMultiplier;
                    preview.OverrideColor = owner.PendingColorOverride;
                    preview.LineType = owner.CurrentLineType;
                    ShapeRenderer.Render(graphics, preview, owner.BorderColor, owner.LineThickness);
                    if (owner.DrawMode == 1 || owner.DrawMode == 3)
                        ShapeRenderer.Handles(graphics, preview, -1, true, owner.CanvasScale, drawOutline: false);
                }
            }
            foreach (int index in owner.SelectedShapeIndices)
                if (index >= 0 && index < owner.Shapes.Count) ShapeRenderer.Handles(graphics, owner.Shapes[index], -1, true, owner.CanvasScale);
            if (owner.HoveredShapeIndex >= 0 && owner.HoveredShapeIndex < owner.Shapes.Count)
                ShapeRenderer.Handles(graphics, owner.Shapes[owner.HoveredShapeIndex], owner.HoveredPointIndex, false, owner.CanvasScale);
        }

        internal static DrawShape BuildPreview(HopperDraw owner, Point3d point)
        {
            if (!ShapeSerializer.ValidCoordinate(point.X) || !ShapeSerializer.ValidCoordinate(point.Y)) return null;
            if (owner.DrawMode == 0)
                return new LineShape(owner.DrawStart, owner.ShiftPressed ? GeometryUtilities.SnapAngle(owner.DrawStart, point) : point);
            if (owner.DrawMode == 1 || owner.DrawMode == 3)
            {
                var points = new List<Point3d>(owner.CurrentPoints);
                if (points.Count > 0 && point.IsValid)
                    points.Add(owner.ShiftPressed ? GeometryUtilities.SnapAngle(points.Last(), point) : point);
                return owner.DrawMode == 1 ? (DrawShape)new PolylineShape(points) : new CurveShape(points);
            }
            if (owner.DrawMode == 2)
            {
                var first = owner.FrameFirstCorner;
                if (!ShapeSerializer.ValidCoordinate(first.X) || !ShapeSerializer.ValidCoordinate(first.Y)) return null;
                if (owner.ShiftPressed)
                {
                    double dx = point.X - first.X, dy = point.Y - first.Y;
                    double size = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    point = new Point3d(first.X + size * Math.Sign(dx), first.Y + size * Math.Sign(dy), 0);
                }
                return new FrameShape(first, new Point3d(point.X, first.Y, 0), point, new Point3d(first.X, point.Y, 0));
            }
            return null;
        }
    }
}
