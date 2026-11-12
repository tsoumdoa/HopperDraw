using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Grasshopper.GUI.Canvas;

namespace hopperdraw
{
    internal readonly struct HudLayout
    {
        internal readonly RectangleF Chip,Glyph,Swatch,Name,Key,Hint;
        internal readonly bool Fill,HiddenStroke,Hatched;
        internal HudLayout(RectangleF chip,RectangleF glyph,RectangleF swatch,RectangleF name,RectangleF key,RectangleF hint,bool fill,bool hidden,bool hatched)
        { Chip=chip; Glyph=glyph; Swatch=swatch; Name=name; Key=key; Hint=hint; Fill=fill; HiddenStroke=hidden; Hatched=fill && hatched; }
        // Measurements are supplied in physical pixels. Every rectangle stays inside even a very narrow viewport.
        internal static HudLayout Measure(Size canvas,float dpi,float nameWidth,float keyWidth,bool fill,bool hidden,bool hatched)
        {
            float s=Math.Max(1,dpi/96),margin=Math.Min(8*s,Math.Max(0,canvas.Width/4f));
            float width=Math.Max(0,Math.Min(canvas.Width-2*margin,nameWidth+keyWidth+100*s));
            var chip=new RectangleF((canvas.Width-width)/2,Math.Max(0,canvas.Height-42*s),width,Math.Min(30*s,Math.Max(0,canvas.Height)));
            RectangleF Part(float left,float w) => new RectangleF(chip.X+Math.Min(width,left*s),chip.Y+6*s,Math.Max(0,Math.Min(w*s,width-left*s)),Math.Max(0,chip.Height-12*s));
            var glyph=Part(8,18); var swatch=Part(34,26);
            float key=Math.Min(keyWidth,Math.Max(0,width-76*s)),name=Math.Max(0,width-84*s-key);
            var nameBox=new RectangleF(chip.X+Math.Min(width,70*s),chip.Y,name,chip.Height);
            var keyBox=new RectangleF(Math.Max(chip.X,chip.Right-8*s-key),chip.Y,key,chip.Height);
            return new HudLayout(chip,glyph,swatch,nameBox,keyBox,new RectangleF(margin,Math.Max(0,chip.Y-24*s),Math.Max(0,canvas.Width-2*margin),20*s),fill,hidden,hatched);
        }
    }
    internal static class AnnotationFeedback
    {
        // The chip repaints on every pointer move in drawing mode: all typography, brushes, formats, measurements and
        // chip paths are cached (per DPI where size-dependent) instead of being allocated per paint. UI-thread only.
        private sealed class DpiResources { internal Font Font, Small; internal readonly System.Collections.Generic.Dictionary<int,(float name,float key)> Widths=new System.Collections.Generic.Dictionary<int,(float,float)>(); }
        private static readonly System.Collections.Generic.Dictionary<int,DpiResources> Resources=new System.Collections.Generic.Dictionary<int,DpiResources>();
        private static readonly SolidBrush Ink=new SolidBrush(Color.FromArgb(48,48,48)), Muted=new SolidBrush(Color.FromArgb(130,130,130)),
            Background=new SolidBrush(Color.FromArgb(245,247,247,247)), Shadow=new SolidBrush(Color.FromArgb(24,0,0,0)), HintInk=new SolidBrush(Color.Gray);
        private static readonly StringFormat LabelFormat=new StringFormat{LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap};
        private static readonly StringFormat HintFormat=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap};
        private static RectangleF _pathChip; private static float _pathRadius; private static GraphicsPath _chipPath, _shadowPath;
        private static readonly FrameShape Sample=new FrameShape();
        internal static int CachedDpiCount => Resources.Count;
        private static DpiResources For(float dpi)
        {
            int key=(int)Math.Round(dpi);
            if(Resources.TryGetValue(key,out var r)) return r;
            float s=key/96f; string family; using(var system=SystemFonts.MessageBoxFont) family=system.FontFamily.Name;
            r=new DpiResources{Font=new Font(family,12*s,FontStyle.Regular,GraphicsUnit.Pixel),Small=new Font(family,11*s,FontStyle.Regular,GraphicsUnit.Pixel)};
            Resources[key]=r; return r;
        }
        private static void ChipPaths(RectangleF chip,float radius,float s)
        {
            if(_chipPath!=null && chip==_pathChip && radius==_pathRadius) return;
            _chipPath?.Dispose(); _shadowPath?.Dispose();
            var shade=chip; shade.Offset(0,2*s);
            _chipPath=Rounded(chip,radius); _shadowPath=Rounded(shade,radius); _pathChip=chip; _pathRadius=radius;
        }
        private static GraphicsPath Rounded(RectangleF b,float radius)
        {
            var p=new GraphicsPath(); float d=Math.Min(radius*2,Math.Min(b.Width,b.Height));
            if(d<=0) return p;
            p.AddArc(b.Left,b.Top,d,d,180,90); p.AddArc(b.Right-d,b.Top,d,d,270,90);
            p.AddArc(b.Right-d,b.Bottom-d,d,d,0,90); p.AddArc(b.Left,b.Bottom-d,d,d,90,90); p.CloseFigure(); return p;
        }
        internal static void Hud(GH_Canvas canvas,HopperDraw owner,AnnotationHintClock clock,bool editing=false)
        {
            if(owner==null || canvas?.Graphics==null) return;
            var g=canvas.Graphics; var state=g.Save();
            try
            {
                g.ResetTransform(); g.SmoothingMode=SmoothingMode.AntiAlias; float s=g.DpiX/96f;
                // Pixel fonts are explicitly measured at the actual target DPI, without double scaling GDI+ points.
                var res=For(g.DpiX); var font=res.Font; var small=res.Small;
                var tool=DrawingTool.Get(owner.DrawMode); var pen=owner.DrawMode==5 ? owner.TextPen : owner.PenStyle;
                if(!res.Widths.TryGetValue(tool.Index,out var widths))
                { widths=(g.MeasureString(tool.Name,font).Width,g.MeasureString(tool.Key.ToString(),small).Width); res.Widths[tool.Index]=widths; }
                var layout=HudLayout.Measure(canvas.ClientSize,g.DpiX,widths.name,widths.key,pen.FillColor.HasValue,!pen.StrokeVisible,pen.Hatch!=HatchPattern.None);
                if(layout.Chip.Width<1 || layout.Chip.Height<1) return;
                ChipPaths(layout.Chip,8*s,s);
                g.FillPath(Shadow,_shadowPath); g.FillPath(Background,_chipPath);
                var clip=g.Save(); g.SetClip(layout.Chip);
                ToolGlyph.Draw(g,tool.Index,layout.Glyph,Color.FromArgb(55,55,55),s);
                DrawSwatch(g,owner,pen,layout,s);
                if(layout.Name.Width>0) g.DrawString(tool.Name,font,Ink,layout.Name,LabelFormat);
                if(layout.Key.Width>0) g.DrawString(tool.Key.ToString(),small,Muted,layout.Key,LabelFormat);
                g.Restore(clip);
                float alpha=clock.Alpha;
                if(alpha>0)
                {
                    HintInk.Color=Color.FromArgb((int)(alpha*190),65,65,65);
                    g.DrawString(clock.Text(editing ? "Type text - Esc / Ctrl+Enter commits" : tool.Hint),small,HintInk,layout.Hint,HintFormat);
                }
            }
            catch(Exception ex) { System.Diagnostics.Debug.WriteLine("HopperDraw HUD skipped: "+ex.Message); }
            finally { g.Restore(state); }
        }
        private static void DrawSwatch(Graphics g,HopperDraw owner,DrawShape pen,HudLayout layout,float s)
        {
            var b=layout.Swatch; if(b.Width<=0 || b.Height<=0) return;
            var clip=g.Save(); g.SetClip(b);
            if(layout.Fill)
            {
                var square=new RectangleF(b.Right-12*s,b.Top+2*s,12*s,12*s);
                var sample=Sample;
                sample.TopLeft=new Rhino.Geometry.Point3d(square.Left,square.Top,0); sample.TopRight=new Rhino.Geometry.Point3d(square.Right,square.Top,0);
                sample.BottomRight=new Rhino.Geometry.Point3d(square.Right,square.Bottom,0); sample.BottomLeft=new Rhino.Geometry.Point3d(square.Left,square.Bottom,0);
                pen.CopyStyleTo(sample); sample.StrokeVisible=false; sample.HatchScale=.4*s;
                using(var path=AnnotationGeometry.Boundary(sample)) HatchRenderer.Paint(g,sample,path);
                g.DrawRectangle(Pens.DarkGray,square.X,square.Y,square.Width,square.Height);
            }
            float y=b.Top+b.Height/2;
            if(layout.HiddenStroke)
            {
                using(var line=new Pen(Color.FromArgb(130,130,130),s)) { g.DrawEllipse(line,b.Left,y-4*s,8*s,8*s); g.DrawLine(line,b.Left,y+4*s,b.Left+8*s,y-4*s); }
            }
            else using(var line=new Pen(owner.DrawMode==5 ? pen.OverrideColor??owner.BorderColor : owner.PendingColorOverride??owner.BorderColor,Math.Min(4,Math.Max(1,owner.DrawMode==5 ? pen.ThicknessMultiplier : owner.CurrentThicknessMultiplier))*s){DashStyle=owner.DrawMode==5 ? pen.LineType : owner.CurrentLineType})
                g.DrawLine(line,b.Left,y,b.Left+(layout.Fill ? 10 : 24)*s,y);
            g.Restore(clip);
        }
    }
}
