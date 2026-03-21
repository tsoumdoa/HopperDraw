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

namespace hopperborder
{
    public class BorderModeUIAttributes : GH_ComponentAttributes
    {
        private Point3d _previewPoint = Point3d.Unset;

        public BorderModeUIAttributes(BorderController owner) : base(owner)
        {
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);

            if (channel != GH_CanvasChannel.Objects)
                return;

            var owner = (BorderController)Owner;

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

            if (!owner.Locked && owner.IsActivated && owner.Annotation != null && owner.Annotation.Visible)
            {
                var annotation = owner.Annotation;

                using (var pen = new Pen(annotation.BorderColor, annotation.LineThickness))
                {
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
                    else if (owner.DrawMode == 2 && owner.IsDrawing && owner.FrameFirstCorner.IsValid && _previewPoint.IsValid)
                    {
                        var corner1 = owner.FrameFirstCorner;
                        var corner2 = _previewPoint;

                        var topLeft = new PointF((float)Math.Min(corner1.X, corner2.X), (float)Math.Max(corner1.Y, corner2.Y));
                        var topRight = new PointF((float)Math.Max(corner1.X, corner2.X), (float)Math.Max(corner1.Y, corner2.Y));
                        var bottomRight = new PointF((float)Math.Max(corner1.X, corner2.X), (float)Math.Min(corner1.Y, corner2.Y));
                        var bottomLeft = new PointF((float)Math.Min(corner1.X, corner2.X), (float)Math.Min(corner1.Y, corner2.Y));

                        var points = new PointF[] { topLeft, topRight, bottomRight, bottomLeft, topLeft };
                        graphics.DrawPolygon(pen, points);
                    }
                }
            }
        }
    }
}
