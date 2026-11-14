using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using Grasshopper.GUI.Canvas;

namespace hopperdraw
{
    // The fading one-line hint above the style bar. Tool and pen state live in the bar itself.
    internal static class AnnotationFeedback
    {
        internal static readonly string EditingHint = "Esc or click away finishes · Ctrl+B bold · Ctrl+I italic · Ctrl+" + StyleKeys.Digits + " color · Ctrl+, . size";
        // The keyboard rows, top to bottom; the bar's tooltips and flyouts show every key.
        private static readonly string TextRows = " · " + StyleKeys.Digits + " color · " + StyleKeys.Span(StyleKeys.Upper, StyleTarget.Sizes.Length) + " size · " +
            StyleKeys.Span(StyleKeys.Home, StyleTarget.ItalicKey + 1) + " style";
        private static readonly string ShapeRows = " · " + StyleKeys.Digits + " color · " + StyleKeys.Span(StyleKeys.Upper, StyleTarget.Widths.Length) + " thickness · " +
            StyleKeys.Span(StyleKeys.Home, AnnotationMenuBuilder.DashNames.Length) + " line type";
        private static readonly string ToolRow = " · " + DrawingTool.All[0].Key + "–" + DrawingTool.All[DrawingTool.All.Length - 1].Key + " tool";
        internal static string ToolHint(int mode) => DrawingTool.Get(mode).Hint + (mode == 5 ? TextRows : ShapeRows) + ToolRow;
        // The hint repaints on every pointer move in drawing mode: fonts, brush and format are cached. UI-thread only.
        private static readonly Dictionary<int,Font> Fonts=new Dictionary<int,Font>();
        private static readonly SolidBrush HintInk=new SolidBrush(Color.Gray);
        private static readonly StringFormat HintFormat=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap};
        private static Font For(float dpi)
        {
            int key=(int)Math.Round(dpi);
            if(Fonts.TryGetValue(key,out var font)) return font;
            string family; using(var system=SystemFonts.MessageBoxFont) family=system.FontFamily.Name;
            font=new Font(family,11*key/96f,FontStyle.Regular,GraphicsUnit.Pixel);
            Fonts[key]=font; return font;
        }
        // Physical pixels. clearance: height reserved at the bottom of the canvas (the style bar and its margin).
        // The box stays inside even a very narrow or short canvas.
        internal static RectangleF HintBox(Size canvas,float dpi,float clearance)
        {
            float s=Math.Max(1,dpi/96),margin=Math.Min(8*s,Math.Max(0,canvas.Width/4f)),height=Math.Min(20*s,Math.Max(0,canvas.Height));
            return new RectangleF(margin,Math.Max(0,canvas.Height-clearance-4*s-height),Math.Max(0,canvas.Width-2*margin),height);
        }
        internal static void Hint(GH_Canvas canvas,HopperDraw owner,AnnotationHintClock clock,bool editing,float clearance)
        {
            if(owner==null || canvas?.Graphics==null) return;
            float alpha=clock.Alpha;
            if(alpha<=0) return;
            var g=canvas.Graphics; var state=g.Save();
            try
            {
                g.ResetTransform(); g.SmoothingMode=SmoothingMode.AntiAlias;
                // Pixel fonts are explicitly sized for the actual target DPI, without double scaling GDI+ points.
                var box=HintBox(canvas.ClientSize,g.DpiX,clearance);
                if(box.Width<1 || box.Height<1) return;
                HintInk.Color=Color.FromArgb((int)(alpha*190),65,65,65);
                g.DrawString(clock.Text(editing ? EditingHint : ToolHint(owner.DrawMode)),For(g.DpiX),HintInk,box,HintFormat);
            }
            catch(Exception ex) { System.Diagnostics.Debug.WriteLine("HopperDraw hint skipped: "+ex.Message); }
            finally { g.Restore(state); }
        }
    }
}
