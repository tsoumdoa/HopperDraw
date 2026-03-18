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
                        Point3d end = owner.DrawEnd;
                        
                        if (owner.ShiftPressed)
                        {
                            double dx = Math.Abs(owner.DrawEnd.X - owner.DrawStart.X);
                            double dy = Math.Abs(owner.DrawEnd.Y - owner.DrawStart.Y);
                            end = dx >= dy
                                ? new Point3d(owner.DrawEnd.X, owner.DrawStart.Y, 0)
                                : new Point3d(owner.DrawStart.X, owner.DrawEnd.Y, 0);
                        }

                        graphics.DrawLine(pen,
                            (float)owner.DrawStart.X, (float)owner.DrawStart.Y,
                            (float)end.X, (float)end.Y);
                    }
                }
            }
        }
    }
}
