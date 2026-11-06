using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Grasshopper.GUI.Canvas;

namespace hopperdraw
{
    internal static class AnnotationFeedback
    {
        // SystemFonts.* returns a new Font per access; the HUD repaints on every move while drawing.
        private static Font _font;
        private static Font HudFont => _font ?? (_font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9));
        internal static void Hud(GH_Canvas canvas,HopperDraw owner)
        {
            if(owner==null || canvas?.Graphics==null) return;
            var g=canvas.Graphics; var state=g.Save();
            try
            {
                g.ResetTransform(); float dpi=g.DpiX/96f;
                string tool=new[]{"Line","Polyline","Frame","Curve"}[owner.DrawMode];
                string hint=!owner.Draft.Active ? "Click or drag to start · Esc" : owner.DrawMode==0 || owner.DrawMode==2 ? "Click to finish · Esc cancels" : "Click to add · Enter/double-click finishes · click start closes · Backspace · Esc";
                string text=tool+"   "+hint;
                var font=HudFont;
                using(var background=new SolidBrush(Color.FromArgb(235,245,245,245)))
                using(var ink=new SolidBrush(Color.FromArgb(45,45,45)))
                using(var pen=new Pen(owner.PendingColorOverride??owner.BorderColor, System.Math.Min(6,owner.CurrentThicknessMultiplier*2)*dpi))
                {
                    var size=g.MeasureString(text,font); float width=System.Math.Min(canvas.ClientSize.Width-16*dpi,size.Width+64*dpi);
                    var box=new RectangleF((canvas.ClientSize.Width-width)/2,canvas.ClientSize.Height-40*dpi,width,28*dpi);
                    using(var pill=new GraphicsPath())
                    {
                        float d=box.Height;
                        pill.AddArc(box.X,box.Y,d,d,90,180); pill.AddArc(box.Right-d,box.Y,d,d,270,180); pill.CloseFigure();
                        g.FillPath(background,pill); g.DrawPath(Pens.Gray,pill);
                    }
                    pen.DashStyle=owner.CurrentLineType; g.DrawLine(pen,box.X+10*dpi,box.Y+14*dpi,box.X+38*dpi,box.Y+14*dpi);
                    g.DrawString(text,font,ink,new RectangleF(box.X+46*dpi,box.Y+6*dpi,box.Width-48*dpi,box.Height-6*dpi));
                }
            }
            catch(System.Exception ex) { System.Diagnostics.Debug.WriteLine("HopperDraw HUD skipped: "+ex.Message); }
            finally { g.Restore(state); }
        }
    }
}
