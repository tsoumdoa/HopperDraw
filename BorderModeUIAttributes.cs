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

            if (owner.IsActivated && owner.Annotation != null)
            {
                var annotation = owner.Annotation;
                if (owner.IsDrawing && owner.DrawStart.IsValid && owner.DrawEnd.IsValid)
                {
                    using (var pen = new Pen(annotation.BorderColor, annotation.LineThickness))
                    {
                        double dx = Math.Abs(owner.DrawEnd.X - owner.DrawStart.X);
                        double dy = Math.Abs(owner.DrawEnd.Y - owner.DrawStart.Y);

                        bool isRect = (dx > owner.RectThreshold && dy > 1) || (dy > owner.RectThreshold && dx > 1);
                        Point3d end = owner.DrawEnd;

                        if (owner.ShiftPressed && isRect)
                        {
                            double size = Math.Max(dx, dy);
                            end = new Point3d(
                                owner.DrawStart.X + Math.Sign(owner.DrawEnd.X - owner.DrawStart.X) * size,
                                owner.DrawStart.Y + Math.Sign(owner.DrawEnd.Y - owner.DrawStart.Y) * size,
                                0);
                            dx = Math.Abs(end.X - owner.DrawStart.X);
                            dy = Math.Abs(end.Y - owner.DrawStart.Y);
                        }

                        if (isRect && !owner.ShiftPressed)
                        {
                            graphics.DrawRectangle(pen,
                                (float)Math.Min(owner.DrawStart.X, end.X),
                                (float)Math.Min(owner.DrawStart.Y, end.Y),
                                (float)dx, (float)dy);
                        }
                        else if (owner.ShiftPressed || (!isRect))
                        {
                            Point3d constrainedEnd = dx >= dy
                                ? new Point3d(end.X, owner.DrawStart.Y, 0)
                                : new Point3d(owner.DrawStart.X, end.Y, 0);

                            graphics.DrawLine(pen,
                                (float)owner.DrawStart.X, (float)owner.DrawStart.Y,
                                (float)constrainedEnd.X, (float)constrainedEnd.Y);
                        }
                    }
                }
            }
        }
    }
}
