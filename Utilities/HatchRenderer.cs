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
        internal const float LineWidth = .75f, DotRadius = .65f;
        // Tint alpha = mean alpha of exactly what is displayed just above the threshold, measured with the real ink
        // (opacity and overlap compositing included) and smoothing mode: the threshold tile for cell motifs, and the
        // span-line pattern averaged over sub-pixel phases for line motifs. The tint keeps this tone further out.
        private static readonly Dictionary<(int pattern,long scale,SmoothingMode mode,int argb),int> TintCache = new Dictionary<(int,long,SmoothingMode,int),int>();
        internal static int TintAlpha(HatchPattern pattern, double scale, SmoothingMode mode, Color ink)
        {
            int index=(int)pattern; if(index<1 || index>=Motifs.Length || !(scale>0) || double.IsInfinity(scale)) return 0;
            if(mode==SmoothingMode.Invalid) mode=SmoothingMode.Default;
            var key=(index,BitConverter.DoubleToInt64Bits(scale),mode,ink.ToArgb());
            if(TintCache.TryGetValue(key,out int cached)) return cached;
            if(TintCache.Count>=256) TintCache.Clear();
            double cell=12*scale; long sum=0, count=0;
            if(SpanPattern(index))
            {
                const int cells=32; float zoom=(float)(MinScreenCell/cell); int size=(int)Math.Round(cells*MinScreenCell);
                foreach(float phase in new[]{0f,.25f,.5f,.75f})
                    using(var bitmap=new Bitmap(size,size,System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        using(var g=Graphics.FromImage(bitmap))
                        using(var pen=new Pen(ink,LineWidth))
                        {
                            g.Clear(Color.Transparent); g.SmoothingMode=mode; g.TranslateTransform(phase,phase); g.ScaleTransform(zoom,zoom);
                            SpanLines(g,index,pen,new RectangleF(0,0,(float)(cells*cell),(float)(cells*cell)),cell);
                        }
                        sum+=AlphaSum(bitmap); count+=(long)size*size;
                    }
            }
            else
            {
                var image=TileFor(index,Motifs[index],(int)Math.Round(MinScreenCell),ink,scale,mode).Image;
                sum=AlphaSum(image); count=(long)image.Width*image.Height;
            }
            int alpha=count==0 ? 0 : (int)Math.Round(sum/(double)count);
            TintCache[key]=alpha; return alpha;
        }
        private static long AlphaSum(Bitmap bitmap)
        {
            var data=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),System.Drawing.Imaging.ImageLockMode.ReadOnly,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                long sum=0; var row=new byte[bitmap.Width*4];
                for(int y=0;y<bitmap.Height;y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),row,0,row.Length);
                    for(int x=0;x<bitmap.Width;x++) sum+=row[x*4+3];
                }
                return sum;
            }
            finally { bitmap.UnlockBits(data); }
        }
        private static bool SpanPattern(int index) => index==1 || index==2 || index==7 || index==9 || index==10;
        private static void SpanLines(Graphics g,int index,Pen pen,RectangleF bounds,double cell)
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

        // Rendering strategy depends on on-screen density only, so a view never changes tone with region size:
        // cells below MinScreenCell px read as a tone (measured tint); span patterns draw full-span lines; cell motifs
        // fill once with a cached screen-resolution tile brush, and switch to exact vectors only when a cell exceeds
        // MaxTile px (where the tile's integer rounding is negligible and few cells are visible).
        internal const float MinScreenCell = 3f;
        internal const int LineBudget = 20000, MaxTile = 256, MaxCachedTiles = 48;
        
        private sealed class Tile { internal Bitmap Image; internal TextureBrush Brush; }
        private static readonly Dictionary<(int pattern,int size,int argb,long scale,SmoothingMode mode),Tile> Tiles = new Dictionary<(int,int,int,long,SmoothingMode),Tile>();
        internal static int CachedTileCount => Tiles.Count;
        internal static Color Ink(Color color,double opacity) => Color.FromArgb((int)Math.Round(color.A*Math.Max(0,Math.Min(1,opacity))),color);
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
                    if(!(screenCell>=MinScreenCell)) { Tinted(g,TintAlpha(shape.Hatch,scale,g.SmoothingMode,inkColor),inkColor,boundary); return; }
                    double columns=Math.Ceiling(bounds.Width/cell)+2, rows=Math.Ceiling(bounds.Height/cell)+2;
                    var motif=Motifs[index];
                    // Full visible-span lines avoid cell-by-cell work for the common ANSI/grid patterns.
                    if(SpanPattern(index))
                    {
                        double count=(bounds.Width+bounds.Height)/cell*2+8;
                        if(count>LineBudget) { Tinted(g,TintAlpha(shape.Hatch,scale,g.SmoothingMode,inkColor),inkColor,boundary); return; }
                        using(var pen=new Pen(ink,LineWidth)) SpanLines(g,index,pen,bounds,cell);
                        return;
                    }
                    if(screenCell>MaxTile) { Vectors(g,motif,inkColor,bounds,cell,scale,columns,rows); return; }
                    var tile=TileFor(index,motif,(int)Math.Round(screenCell),inkColor,scale,g.SmoothingMode);
                    tile.Brush.ResetTransform();
                    float unit=(float)(cell/tile.Image.Width); tile.Brush.ScaleTransform(unit,unit);
                    g.FillPath(tile.Brush,boundary);
                }
            }
            finally { g.Restore(state); }
        }
        private static void Tinted(Graphics g,int alpha,Color inkColor,GraphicsPath boundary)
        {
            using(var tint=new SolidBrush(Color.FromArgb(Math.Max(0,Math.Min(255,alpha)),inkColor))) g.FillPath(tint,boundary);
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
                    foreach(var dot in motif.Dots) dots.AddEllipse((float)(ox+dot.X*scale-DotRadius),(float)(oy+dot.Y*scale-DotRadius),2*DotRadius,2*DotRadius);
                }
                if(lines.PointCount>0) using(var pen=new Pen(inkColor,LineWidth)) g.DrawPath(pen,lines);
                if(dots.PointCount>0) using(var brush=new SolidBrush(inkColor)) g.FillPath(brush,dots);
            }
        }
        // A seamless tile rendered at screen resolution. The brush lives in canvas space with its origin at the
        // canvas origin, so the phase stays canvas-anchored exactly as in the vector path.
        // The bitmap is stretched from `size` px to the true screen cell, so primitives are sized in tile pixels
        // (size / canvas cell), not screen pixels: after the brush transform they match the vector path exactly.
        // The key holds every rasterization input: pattern, tile size, ink and exact hatch scale.
        private static Tile TileFor(int index,Motif motif,int size,Color inkColor,double scale,SmoothingMode mode)
        {
            size=Math.Max(1,Math.Min(MaxTile,size));
            if(mode==SmoothingMode.Invalid) mode=SmoothingMode.Default;
            var key=(index,size,inkColor.ToArgb(),BitConverter.DoubleToInt64Bits(scale),mode);
            if(Tiles.TryGetValue(key,out var tile)) return tile;
            if(Tiles.Count>=MaxCachedTiles) ClearTiles(); // Paint is single-threaded: no brush is in use here.
            var image=new Bitmap(size,size,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            float k=size/12f; float perUnit=(float)(size/(12*scale));
            using(var tg=Graphics.FromImage(image))
            using(var pen=new Pen(inkColor,LineWidth*perUnit))
            using(var brush=new SolidBrush(inkColor))
            {
                tg.Clear(Color.Transparent); tg.SmoothingMode=mode; // same rasterization as the vector path and TintAlpha()
                float r=DotRadius*perUnit;
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
