using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

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

        // Rendering strategy depends on on-screen density, never on canvas extent alone:
        // cells below MinScreenCell px read as a tone, so paint a tinted fill; few visible cells draw exact
        // vectors in one batched path; dense views fill once with a cached screen-resolution tile brush.
        internal const float MinScreenCell = 3f;
        internal const int VectorCellBudget = 4000, LineBudget = 20000, MaxTile = 256, MaxCachedTiles = 48;
        internal const float TintFactor = .35f;
        private sealed class Tile { internal Bitmap Image; internal TextureBrush Brush; }
        private static readonly Dictionary<(int pattern,int size,int argb,int width),Tile> Tiles = new Dictionary<(int,int,int,int),Tile>();
        internal static int CachedTileCount => Tiles.Count;
        internal static Color Ink(Color color,double opacity) => Color.FromArgb((int)Math.Round(color.A*Math.Max(0,Math.Min(1,opacity))),color);
        private static Color Tint(Color ink) => Color.FromArgb((int)Math.Round(ink.A*TintFactor),ink);
        private static float ScreenScale(Graphics g)
        {
            using(var m=g.Transform)
            {
                var e=m.Elements; double det=Math.Abs((double)e[0]*e[3]-(double)e[1]*e[2]);
                return det>0 && !double.IsInfinity(det) ? (float)Math.Sqrt(det) : 1f;
            }
        }
        internal static void Paint(Graphics g, DrawShape shape, GraphicsPath boundary)
        {
            if (!shape.VisibleFill || boundary.PointCount == 0) return;
            var state=g.Save();
            try
            {
                g.SetClip(boundary,CombineMode.Intersect);
                var inkColor=Ink(shape.FillColor.Value,shape.FillOpacity);
                using(var ink=new SolidBrush(inkColor))
                {
                    if(shape.Hatch==HatchPattern.None) { g.FillPath(ink,boundary); return; }
                    if(shape.HatchBackground.HasValue)
                        using(var bg=new SolidBrush(Ink(shape.HatchBackground.Value,shape.FillOpacity))) g.FillPath(bg,boundary);
                    var bounds=RectangleF.Intersect(boundary.GetBounds(),g.VisibleClipBounds);
                    if(bounds.Width<=0 || bounds.Height<=0) return;
                    double scale=shape.HatchScale;
                    if(double.IsNaN(scale) || double.IsInfinity(scale) || scale<=0) return;
                    int index=(int)shape.Hatch; if(index<1 || index>=Motifs.Length) return;
                    double cell=12*scale; float zoom=ScreenScale(g); double screenCell=cell*zoom;
                    if(!(screenCell>=MinScreenCell)) { Tinted(g,inkColor,boundary); return; }
                    double columns=Math.Ceiling(bounds.Width/cell)+2, rows=Math.Ceiling(bounds.Height/cell)+2;
                    var motif=Motifs[index];
                    // Full visible-span lines avoid cell-by-cell work for the common ANSI/grid patterns.
                    if(index==1 || index==2 || index==7 || index==9 || index==10)
                    {
                        double count=(bounds.Width+bounds.Height)/cell*2+8;
                        if(count>LineBudget) { Tinted(g,inkColor,boundary); return; }
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
                    if(columns*rows<=VectorCellBudget || screenCell>MaxTile) { Vectors(g,motif,inkColor,bounds,cell,scale,columns,rows); return; }
                    var tile=TileFor(index,motif,(int)Math.Round(screenCell),inkColor,zoom);
                    tile.Brush.ResetTransform();
                    float unit=(float)(cell/tile.Image.Width); tile.Brush.ScaleTransform(unit,unit);
                    g.FillPath(tile.Brush,boundary);
                }
            }
            finally { g.Restore(state); }
        }
        private static void Tinted(Graphics g,Color inkColor,GraphicsPath boundary)
        {
            using(var tint=new SolidBrush(Tint(inkColor))) g.FillPath(tint,boundary);
        }
        // One batched path per primitive kind; motif buffers are reused (GraphicsPath copies them).
        private static void Vectors(Graphics g,Motif motif,Color inkColor,RectangleF bounds,double cell,double scale,double columns,double rows)
        {
            var buffers=motif.Lines.Select(l=>new PointF[l.Length]).ToArray();
            using(var lines=new GraphicsPath())
            using(var dots=new GraphicsPath(FillMode.Winding))
            {
                double x0=Math.Floor(bounds.Left/cell)*cell, y0=Math.Floor(bounds.Top/cell)*cell;
                for(int y=0;y<(int)rows;y++) for(int x=0;x<(int)columns;x++)
                {
                    double ox=x0+x*cell, oy=y0+y*cell;
                    for(int l=0;l<motif.Lines.Count;l++)
                    {
                        var line=motif.Lines[l]; var pts=buffers[l];
                        for(int i=0;i<pts.Length;i++) pts[i]=new PointF((float)(ox+line[i].X*scale),(float)(oy+line[i].Y*scale));
                        lines.StartFigure(); lines.AddLines(pts);
                    }
                    foreach(var dot in motif.Dots) dots.AddEllipse((float)(ox+dot.X*scale-.65),(float)(oy+dot.Y*scale-.65),1.3f,1.3f);
                }
                if(lines.PointCount>0) using(var pen=new Pen(inkColor,.75f)) g.DrawPath(pen,lines);
                if(dots.PointCount>0) using(var brush=new SolidBrush(inkColor)) g.FillPath(brush,dots);
            }
        }
        // A seamless tile rendered at screen resolution. The brush lives in canvas space with its origin at the
        // canvas origin, so the phase stays canvas-anchored exactly as in the vector path.
        private static Tile TileFor(int index,Motif motif,int size,Color inkColor,float zoom)
        {
            size=Math.Max(1,Math.Min(MaxTile,size));
            int width=(int)Math.Round(.75*zoom*20);
            var key=(index,size,inkColor.ToArgb(),width);
            if(Tiles.TryGetValue(key,out var tile)) return tile;
            if(Tiles.Count>=MaxCachedTiles) ClearTiles(); // Paint is single-threaded: no brush is in use here.
            var image=new Bitmap(size,size,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            float k=size/12f;
            using(var tg=Graphics.FromImage(image))
            using(var pen=new Pen(inkColor,Math.Max(.05f,width/20f)))
            using(var brush=new SolidBrush(inkColor))
            {
                tg.Clear(Color.Transparent); tg.SmoothingMode=SmoothingMode.AntiAlias;
                float r=.65f*zoom;
                for(int dy=-1;dy<=1;dy++) for(int dx=-1;dx<=1;dx++)
                {
                    foreach(var line in motif.Lines) tg.DrawLines(pen,line.Select(p=>new PointF((p.X+dx*12)*k,(p.Y+dy*12)*k)).ToArray());
                    foreach(var dot in motif.Dots) tg.FillEllipse(brush,(dot.X+dx*12)*k-r,(dot.Y+dy*12)*k-r,2*r,2*r);
                }
            }
            tile=new Tile{Image=image,Brush=new TextureBrush(image,WrapMode.Tile)};
            Tiles[key]=tile; return tile;
        }
        internal static void ClearTiles()
        {
            foreach(var tile in Tiles.Values) { tile.Brush.Dispose(); tile.Image.Dispose(); }
            Tiles.Clear();
        }
    }
}
