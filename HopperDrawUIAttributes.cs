using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Rhino;
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
        private Point3d _previewPoint = Point3d.Unset;

        public HopperDrawUIAttributes(HopperDraw owner) : base(owner)
        {
        }

        protected override void Layout()
        {
            base.Layout();
            Bounds = new RectangleF(Bounds.X, Bounds.Y, Bounds.Width + 10, Bounds.Height + 24);
        }

        protected override void Render(
            GH_Canvas canvas,
            Graphics graphics,
            GH_CanvasChannel channel
        )
        {
            base.Render(canvas, graphics, channel);

            if (channel != GH_CanvasChannel.Objects)
                return;

            var owner = (HopperDraw)Owner;
            var modeNames = new[] { "Line", "Polyline", "Frame", "Curve" };
            var modeText = owner.DrawMode >= 0 && owner.DrawMode < modeNames.Length ? modeNames[owner.DrawMode] : "Unknown";

            var footer = new RectangleF(Bounds.X + 4, Bounds.Bottom - 22, Bounds.Width - 8, 18);

            using (var capsule = GH_Capsule.CreateTextCapsule(
                footer,
                footer,
                GH_Palette.Black,
                modeText,
                2,
                0
            ))
            {
                capsule.Render(graphics, Selected, Owner.Locked, false);
            }

            _previewPoint = Point3d.Unset;
            if (canvas != null)
            {
                var mousePos = System.Windows.Forms.Control.MousePosition;
                var screenPt = canvas.PointToClient(mousePos);
                if (canvas.Viewport != null)
                {
                    var unprojected = canvas.Viewport.UnprojectPoint(screenPt);
                    _previewPoint = new Point3d(unprojected.X, unprojected.Y, 0);
                }
            }

            if (!owner.Locked && owner.IsActivated && owner.Visible)
            {
                float effectiveThickness = owner.LineThickness * owner.CurrentThicknessMultiplier;
                Color effectiveColor = owner.PendingColorOverride ?? owner.BorderColor;

                using (var pen = new Pen(effectiveColor, effectiveThickness))
                {
                    pen.DashStyle = owner.CurrentLineType;
                    if (owner.DrawMode == 0 && owner.IsDrawing && owner.DrawStart.IsValid && owner.DrawEnd.IsValid)
                    {
                        Point3d end = owner.DrawEnd;

                        bool shiftPressed = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
                        if (shiftPressed)
                        {
                            double dx = owner.DrawEnd.X - owner.DrawStart.X;
                            double dy = owner.DrawEnd.Y - owner.DrawStart.Y;
                            double length = Math.Sqrt(dx * dx + dy * dy);
                            if (length > 0)
                            {
                                double angle = Math.Atan2(dy, dx);
                                double snapAngle = Math.Round(angle / (Math.PI / 4.0)) * (Math.PI / 4.0);
                                end = new Point3d(
                                    owner.DrawStart.X + length * Math.Cos(snapAngle),
                                    owner.DrawStart.Y + length * Math.Sin(snapAngle),
                                    0);
                            }
                        }

                        graphics.DrawLine(pen,
                            (float)owner.DrawStart.X, (float)owner.DrawStart.Y,
                            (float)end.X, (float)end.Y);
                    }
                    else if (owner.DrawMode == 1 && owner.IsDrawing && owner.CurrentPoints.Count > 0)
                    {
                        var pts = owner.CurrentPoints;

                        if (pts.Count >= 2)
                        {
                            var points = pts.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();
                            graphics.DrawLines(pen, points);
                        }

                        if (_previewPoint.IsValid && pts.Count > 0)
                        {
                            var lastPt = pts[pts.Count - 1];
                            Point3d endPt = _previewPoint;

                            bool shiftPressed = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
                            if (shiftPressed)
                            {
                                double dx = endPt.X - lastPt.X;
                                double dy = endPt.Y - lastPt.Y;
                                double length = Math.Sqrt(dx * dx + dy * dy);

                                if (length > 0)
                                {
                                    double angle = Math.Atan2(dy, dx);
                                    double snapAngle = Math.Round(angle / (Math.PI / 4.0)) * (Math.PI / 4.0);
                                    endPt = new Point3d(
                                        lastPt.X + length * Math.Cos(snapAngle),
                                        lastPt.Y + length * Math.Sin(snapAngle),
                                        0);
                                }
                            }

                            graphics.DrawLine(pen,
                                (float)lastPt.X, (float)lastPt.Y,
                                (float)endPt.X, (float)endPt.Y);
                        }

                        foreach (var p in pts)
                        {
                            graphics.FillRectangle(new SolidBrush(Color.White), (float)p.X - 4, (float)p.Y - 4, 8, 8);
                            graphics.DrawRectangle(new Pen(Color.FromArgb(200, 0, 120, 215), 2), (int)p.X - 4, (int)p.Y - 4, 8, 8);
                        }
                    }
                    else if (owner.DrawMode == 3 && owner.IsDrawing && owner.CurrentPoints.Count > 0)
                    {
                        var pts = owner.CurrentPoints;

                        if (_previewPoint.IsValid && pts.Count > 0)
                        {
                            var lastPt = pts[pts.Count - 1];
                            Point3d endPt = _previewPoint;

                            bool shiftPressed = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
                            if (shiftPressed)
                            {
                                double dx = endPt.X - lastPt.X;
                                double dy = endPt.Y - lastPt.Y;
                                double length = Math.Sqrt(dx * dx + dy * dy);

                                if (length > 0)
                                {
                                    double angle = Math.Atan2(dy, dx);
                                    double snapAngle = Math.Round(angle / (Math.PI / 4.0)) * (Math.PI / 4.0);
                                    endPt = new Point3d(
                                        lastPt.X + length * Math.Cos(snapAngle),
                                        lastPt.Y + length * Math.Sin(snapAngle),
                                        0);
                                }
                            }

                            var previewPoints = new List<PointF>();
                            previewPoints.AddRange(pts.Select(p => new PointF((float)p.X, (float)p.Y)));
                            previewPoints.Add(new PointF((float)endPt.X, (float)endPt.Y));

                            if (previewPoints.Count >= 2)
                            {
                                graphics.DrawCurve(pen, previewPoints.ToArray(), 0.5f);
                            }
                        }
                        else if (pts.Count >= 2)
                        {
                            var points = pts.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();
                            graphics.DrawCurve(pen, points, 0.5f);
                        }

                        foreach (var p in pts)
                        {
                            graphics.FillRectangle(new SolidBrush(Color.White), (float)p.X - 4, (float)p.Y - 4, 8, 8);
                            graphics.DrawRectangle(new Pen(Color.FromArgb(200, 0, 120, 215), 2), (int)p.X - 4, (int)p.Y - 4, 8, 8);
                        }

                        if (_previewPoint.IsValid)
                        {
                            graphics.FillRectangle(new SolidBrush(Color.Yellow), (float)_previewPoint.X - 4, (float)_previewPoint.Y - 4, 8, 8);
                            graphics.DrawRectangle(new Pen(Color.FromArgb(200, 0, 120, 215), 2), (int)_previewPoint.X - 4, (int)_previewPoint.Y - 4, 8, 8);
                        }
                    }
                    else if (owner.DrawMode == 2 && owner.IsDrawing && owner.FrameFirstCorner.IsValid && _previewPoint.IsValid)
                    {
                        var corner1 = owner.FrameFirstCorner;
                        var corner2 = _previewPoint;

                        bool shiftPressed = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
                        if (shiftPressed)
                        {
                            double dx = corner2.X - corner1.X;
                            double dy = corner2.Y - corner1.Y;
                            double size = Math.Max(Math.Abs(dx), Math.Abs(dy));
                            corner2 = new Point3d(
                                corner1.X + size * Math.Sign(dx),
                                corner1.Y + size * Math.Sign(dy),
                                0);
                        }

                        var topLeft = new PointF((float)Math.Min(corner1.X, corner2.X), (float)Math.Max(corner1.Y, corner2.Y));
                        var topRight = new PointF((float)Math.Max(corner1.X, corner2.X), (float)Math.Max(corner1.Y, corner2.Y));
                        var bottomRight = new PointF((float)Math.Max(corner1.X, corner2.X), (float)Math.Min(corner1.Y, corner2.Y));
                        var bottomLeft = new PointF((float)Math.Min(corner1.X, corner2.X), (float)Math.Min(corner1.Y, corner2.Y));

                        var points = new PointF[] { topLeft, topRight, bottomRight, bottomLeft, topLeft };
                        graphics.DrawPolygon(pen, points);
                    }
                }
            }

            if (!owner.Locked && owner.Visible)
            {
                foreach (var shape in owner.Shapes)
                {
                    float effectiveThickness = owner.LineThickness * shape.ThicknessMultiplier;
                    Color effectiveColor = shape.OverrideColor ?? owner.BorderColor;
                    using (var pen = new Pen(effectiveColor, effectiveThickness))
                    {
                        pen.DashStyle = shape.LineType;
                        shape.Render(graphics, pen, effectiveThickness);
                    }
                }

                if (owner.HasSelection)
                {
                    foreach (var shapeIdx in owner.SelectedShapeIndices)
                    {
                        if (shapeIdx >= 0 && shapeIdx < owner.Shapes.Count)
                        {
                            var selectedShape = owner.Shapes[shapeIdx];
                            float selectedThickness = owner.LineThickness * selectedShape.ThicknessMultiplier;
                            RenderSelectionHandles(graphics, selectedShape, -1, selectedThickness);
                        }
                    }
                }

                if (owner.HoveredShapeIndex >= 0 && owner.HoveredShapeIndex < owner.Shapes.Count)
                {
                    var hoveredShape = owner.Shapes[owner.HoveredShapeIndex];
                    float hoveredThickness = owner.LineThickness * hoveredShape.ThicknessMultiplier;
                    RenderHoverHandles(graphics, hoveredShape, owner.HoveredPointIndex, hoveredThickness);
                }
            }
        }

        private void RenderSelectionHandles(Graphics g, DrawShape shape, int selectedPointIndex, float thickness)
        {
            if (shape is DrawShapeGroup group)
            {
                var bbox = group.GetBoundingBox();
                using (var selPen = new Pen(Color.FromArgb(200, 0, 120, 215), 1))
                {
                    selPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                    selPen.DashPattern = new float[] { 5, 5 };
                    g.DrawRectangle(selPen, bbox.X, bbox.Y, bbox.Width, bbox.Height);
                }
                return;
            }

            var points = shape.GetPoints();
            using (var handleBrush = new SolidBrush(Color.White))
            using (var handlePen = new Pen(Color.FromArgb(200, 0, 120, 215), 2))
            {
                for (int i = 0; i < points.Length; i++)
                {
                    var pt = points[i];
                    g.FillRectangle(handleBrush, pt.X - 4, pt.Y - 4, 8, 8);
                    g.DrawRectangle(handlePen, (int)pt.X - 4, (int)pt.Y - 4, 8, 8);
                }
            }
            using (var selPen = new Pen(Color.FromArgb(200, 0, 120, 215), 1))
            {
                selPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                if (points.Length > 1)
                {
                    if (shape is FrameShape || shape is PolylineShape poly && poly.Closed)
                        g.DrawPolygon(selPen, points);
                    else
                        g.DrawLines(selPen, points);
                }
            }
        }

        private void RenderHoverHandles(Graphics g, DrawShape shape, int hoveredPointIndex, float thickness)
        {
            var points = shape.GetPoints();
            using (var hoverBrush = new SolidBrush(Color.FromArgb(100, 0, 120, 215)))
            {
                for (int i = 0; i < points.Length; i++)
                {
                    if (i == hoveredPointIndex)
                    {
                        var pt = points[i];
                        g.FillRectangle(hoverBrush, pt.X - 4, pt.Y - 4, 8, 8);
                    }
                }
            }
        }
    }
}
