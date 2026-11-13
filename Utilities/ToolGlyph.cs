using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace hopperdraw
{
    // One normalized monoline path source for menu, cursor and HUD.
    internal static class ToolGlyph
    {
        internal static GraphicsPath Path(int tool)
        {
            var p=new GraphicsPath();
            switch(tool)
            {
                case 0: p.AddLine(3,13,13,3); break;
                case 1: p.AddLines(new[]{new PointF(2,12),new PointF(6,4),new PointF(10,11),new PointF(14,5)}); break;
                case 2: p.AddRectangle(new RectangleF(2,3,12,10)); break;
                case 3: p.AddBezier(2,12,4,-2,11,19,14,3); break;
                case 4: p.AddBeziers(new[]{new PointF(2,12),new PointF(4,1),new PointF(7,2),new PointF(7,8),new PointF(7,16),new PointF(11,14),new PointF(14,4)}); break;
                case 5: p.AddLine(3,3,13,3); p.StartFigure(); p.AddLine(8,3,8,14); break;
                default: throw new ArgumentOutOfRangeException(nameof(tool));
            }
            return p;
        }
        internal static void Draw(Graphics g,int tool,RectangleF box,Color color,float width)
        {
            using(var path=Path(tool))
            using(var matrix=new Matrix(box.Width/16,0,0,box.Height/16,box.X,box.Y))
            using(var pen=new Pen(color,width){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round})
            { path.Transform(matrix); g.DrawPath(pen,path); }
        }
        internal static Bitmap Bitmap(int tool,int size)
        {
            var b=new Bitmap(size,size);
            using(var g=Graphics.FromImage(b)) { g.SmoothingMode=SmoothingMode.AntiAlias; Draw(g,tool,new RectangleF(0,0,size,size),Color.FromArgb(48,48,48),Math.Max(1,size/16f)); }
            return b; // Owned by the caller, including menu items.
        }
    }
}
