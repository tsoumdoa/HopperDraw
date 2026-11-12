using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace hopperdraw
{
    // Cached unit-cell motifs. Coordinates and phase belong to the canvas, never the viewport.
    internal static class HatchRenderer
    {
        private sealed class Motif
        {
            internal readonly List<PointF[]> Lines = new List<PointF[]>();
            internal readonly List<PointF> Dots = new List<PointF>();
            internal void Line(float x,float y,float u,float v) => Lines.Add(new[]{new PointF(x,y),new PointF(u,v)});
        }
        private static readonly Motif[] Motifs = Build();
        private static Motif[] Build()
        {
            var all = new Motif[11]; for(int i=0;i<all.Length;i++) all[i]=new Motif();
            all[1].Line(0,12,12,0);
            all[2].Line(0,12,12,0); all[2].Line(0,0,12,12);
            all[3].Dots.AddRange(new[]{new PointF(2,2),new PointF(9,7),new PointF(4,10)});
            all[3].Lines.Add(new[]{new PointF(5,3),new PointF(8,4),new PointF(6,6),new PointF(5,3)});
            for(int i=0;i<3;i++) all[4].Line(1+i*2,8,5+i*2,4);
            all[5].Lines.Add(new[]{new PointF(0,6),new PointF(3,0),new PointF(6,6),new PointF(9,12),new PointF(12,6)});
            all[6].Line(0,0,12,0); all[6].Line(0,6,12,6); all[6].Line(0,0,0,6); all[6].Line(6,6,6,12);
            all[7].Line(0,0,12,0); all[7].Line(0,0,0,12);
            all[8].Dots.Add(new PointF(6,6));
            all[9].Line(0,0,12,0); all[10].Line(0,0,0,12);
            return all;
        }
        internal static Color Ink(Color color,double opacity) => Color.FromArgb((int)Math.Round(color.A*Math.Max(0,Math.Min(1,opacity))),color);
        internal static void Paint(Graphics g, DrawShape shape, GraphicsPath boundary)
        {
            if (!shape.VisibleFill || boundary.PointCount == 0) return;
            var state=g.Save();
            try
            {
                g.SetClip(boundary,CombineMode.Intersect);
                using(var ink=new SolidBrush(Ink(shape.FillColor.Value,shape.FillOpacity)))
                {
                    if(shape.Hatch==HatchPattern.None) { g.FillPath(ink,boundary); return; }
                    if(shape.HatchBackground.HasValue)
                        using(var bg=new SolidBrush(Ink(shape.HatchBackground.Value,shape.FillOpacity))) g.FillPath(bg,boundary);
                    var bounds=RectangleF.Intersect(boundary.GetBounds(),g.VisibleClipBounds);
                    if(bounds.Width<=0 || bounds.Height<=0) return;
                    double scale=shape.HatchScale;
                    if(double.IsNaN(scale) || double.IsInfinity(scale) || scale<=0) return;
                    double cell=12*scale;
                    double columns=Math.Ceiling(bounds.Width/cell)+2, rows=Math.Ceiling(bounds.Height/cell)+2;
                    int index=(int)shape.Hatch; if(index<1 || index>=Motifs.Length) return;
                    var motif=Motifs[index];
                    // Full visible-span lines avoid cell-by-cell work for the common ANSI/grid patterns.
                    if(index==1 || index==2 || index==7 || index==9 || index==10)
                    {
                        double count=(bounds.Width+bounds.Height)/cell*4+8;
                        if(count>2000000) { g.FillPath(ink,boundary); return; }
                        using(var pen=new Pen(ink,.75f))
                        {
                            if(index==7 || index==9)
                                for(double y=Math.Floor(bounds.Top/cell)*cell;y<=bounds.Bottom;y+=cell) g.DrawLine(pen,bounds.Left,(float)y,bounds.Right,(float)y);
                            if(index==7 || index==10)
                                for(double x=Math.Floor(bounds.Left/cell)*cell;x<=bounds.Right;x+=cell) g.DrawLine(pen,(float)x,bounds.Top,(float)x,bounds.Bottom);
                            if(index==1 || index==2)
                                for(double k=Math.Floor((bounds.Left+bounds.Top)/cell)*cell;k<=bounds.Right+bounds.Bottom;k+=cell)
                                    g.DrawLine(pen,bounds.Left,(float)(k-bounds.Left),bounds.Right,(float)(k-bounds.Right));
                            if(index==2)
                                for(double k=Math.Floor((bounds.Left-bounds.Bottom)/cell)*cell;k<=bounds.Right-bounds.Top;k+=cell)
                                    g.DrawLine(pen,bounds.Left,(float)(bounds.Left-k),bounds.Right,(float)(bounds.Right-k));
                        }
                        return;
                    }
                    // Only pathological extents/densities reach this primitive budget after visible culling.
                    int perCell=motif.Dots.Count; foreach(var line in motif.Lines) perCell+=line.Length-1;
                    if(columns*rows*perCell>2000000) { g.FillPath(ink,boundary); return; }
                    using(var pen=new Pen(ink,.75f))
                    {
                        double x0=Math.Floor(bounds.Left/cell)*cell, y0=Math.Floor(bounds.Top/cell)*cell;
                        for(int y=0;y<(int)rows;y++) for(int x=0;x<(int)columns;x++)
                        {
                            double ox=x0+x*cell, oy=y0+y*cell;
                            foreach(var line in motif.Lines)
                            {
                                var pts=new PointF[line.Length];
                                for(int i=0;i<pts.Length;i++) pts[i]=new PointF((float)(ox+line[i].X*scale),(float)(oy+line[i].Y*scale));
                                g.DrawLines(pen,pts);
                            }
                            foreach(var dot in motif.Dots) g.FillEllipse(ink,(float)(ox+dot.X*scale-.65),(float)(oy+dot.Y*scale-.65),1.3f,1.3f);
                        }
                    }
                }
            }
            finally { g.Restore(state); }
        }
    }
}
