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
                string text=DrawingTool.Get(owner.DrawMode).Name+" ["+DrawingTool.Get(owner.DrawMode).Key+"]   "+
                    (owner.PenNoticeUntil > System.DateTime.UtcNow ? owner.PenNotice : DrawingTool.Get(owner.DrawMode).Hint);
                var font=HudFont;
                using(var background=new SolidBrush(Color.FromArgb(235,245,245,245)))
                using(var ink=new SolidBrush(Color.FromArgb(45,45,45)))
                {
                    var size=g.MeasureString(text,font); float width=System.Math.Min(canvas.ClientSize.Width-16*dpi,size.Width+64*dpi);
                    var box=new RectangleF((canvas.ClientSize.Width-width)/2,canvas.ClientSize.Height-40*dpi,width,28*dpi);
                    using(var pill=new GraphicsPath())
                    {
                        float d=box.Height;
                        pill.AddArc(box.X,box.Y,d,d,90,180); pill.AddArc(box.Right-d,box.Y,d,d,270,180); pill.CloseFigure();
                        g.FillPath(background,pill); g.DrawPath(Pens.Gray,pill);
                    }
                    var a=new Rhino.Geometry.Point3d(box.X+10*dpi,box.Y+7*dpi,0);
                    var b=new Rhino.Geometry.Point3d(box.X+38*dpi,box.Y+21*dpi,0);
                    DrawShape sample=owner.DrawMode==0 ? (DrawShape)new LineShape(a,b) : new FrameShape(a,new Rhino.Geometry.Point3d(b.X,a.Y,0),b,new Rhino.Geometry.Point3d(a.X,b.Y,0));
                    owner.ApplyPen(sample); ShapeRenderer.Render(g,sample,owner.BorderColor,2*dpi);
                    g.DrawString(text,font,ink,new RectangleF(box.X+46*dpi,box.Y+6*dpi,box.Width-48*dpi,box.Height-6*dpi));
                }
            }
            catch(System.Exception ex) { System.Diagnostics.Debug.WriteLine("HopperDraw HUD skipped: "+ex.Message); }
            finally { g.Restore(state); }
        }
    }
}
