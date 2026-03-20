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
        public BorderModeUIAttributes(BorderController owner) : base(owner)
        {
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);

            if (channel != GH_CanvasChannel.Objects)
                return;

            var owner = (BorderController)Owner;

            if (!owner.Locked && owner.IsActivated && owner.Annotation != null && owner.Annotation.Visible)
            {
                var annotation = owner.Annotation;
                if (owner.IsDrawing && owner.DrawStart.IsValid && owner.DrawEnd.IsValid)
                {
                    using (var pen = new Pen(annotation.BorderColor, annotation.LineThickness))
                    {
                        Point3d end = owner.DrawEnd;
                        
                        if (owner.ShiftPressed)
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

                        if (owner.DrawFrame)
                        {
                            Point3d topRight = new Point3d(end.X, owner.DrawStart.Y, 0);
                            Point3d bottomLeft = new Point3d(owner.DrawStart.X, end.Y, 0);

                            graphics.DrawLine(pen,
                                (float)owner.DrawStart.X, (float)owner.DrawStart.Y,
                                (float)topRight.X, (float)topRight.Y);
                            graphics.DrawLine(pen,
                                (float)topRight.X, (float)topRight.Y,
                                (float)end.X, (float)end.Y);
                            graphics.DrawLine(pen,
                                (float)end.X, (float)end.Y,
                                (float)bottomLeft.X, (float)bottomLeft.Y);
                            graphics.DrawLine(pen,
                                (float)bottomLeft.X, (float)bottomLeft.Y,
                                (float)owner.DrawStart.X, (float)owner.DrawStart.Y);
                        }
                        else
                        {
                            graphics.DrawLine(pen,
                                (float)owner.DrawStart.X, (float)owner.DrawStart.Y,
                                (float)end.X, (float)end.Y);
                        }
                    }
                }
            }
        }
    }
}
