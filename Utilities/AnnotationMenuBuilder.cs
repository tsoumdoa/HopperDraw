using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace hopperdraw
{
    internal static class AnnotationMenuBuilder
    {
        private static readonly List<Color> Recent=new List<Color>();
        private static readonly string[] DashNames={"Solid","Dash","Dot","Dash-dot","Dash-dot-dot"};
        private static readonly Color[] Colors={Color.Black,Color.FromArgb(70,70,70),Color.White,Color.Red,Color.Orange,Color.Yellow,Color.Green,Color.Teal,Color.Blue,Color.Purple,Color.HotPink};
        private static readonly string[] ColorNames={"Black","Dark grey","White","Red","Orange","Yellow","Green","Teal","Blue","Purple","Pink"};
        private static ToolStripMenuItem Item(ToolStripItemCollection items,string text,Action action,bool enabled=true,bool check=false,Image image=null)
        {
            var item=new ToolStripMenuItem(text,image,(s,e)=>action?.Invoke()){Name=text,Enabled=enabled,Checked=check}; items.Add(item); return item;
        }
        private static ToolStripMenuItem Sub(ToolStripItemCollection items,string text) { var item=new ToolStripMenuItem(text){Name=text}; items.Add(item); return item; }
        // Dispose a context menu and the preview bitmaps HopperDraw created for it.
        internal static void Release(ToolStrip menu)
        {
            if(menu==null || menu.IsDisposed) return;
            var images=new List<Image>();
            void Collect(ToolStripItemCollection items) { foreach(ToolStripItem item in items) { if(item.Image!=null) images.Add(item.Image); if(item is ToolStripDropDownItem drop && drop.HasDropDownItems) Collect(drop.DropDownItems); } }
            Collect(menu.Items);
            menu.Dispose();
            foreach(var image in images) if(!Shared.Contains(image)) image.Dispose();
        }
        // Preview bitmaps are stateless and shared by every menu instance (GH's component menu is never released
        // through Release), so they are cached for the session and never disposed with a menu.
        private static readonly Dictionary<string,Bitmap> PreviewCache=new Dictionary<string,Bitmap>();
        private static readonly HashSet<Image> Shared=new HashSet<Image>();
        private static Bitmap Cached(string key,Func<Bitmap> create)
        {
            if(PreviewCache.TryGetValue(key,out var bitmap)) return bitmap;
            bitmap=create(); PreviewCache[key]=bitmap; Shared.Add(bitmap); return bitmap;
        }
        // Only the finite palette (named swatches, light grey, black stroke previews) is cached for the session.
        // Arbitrary custom/recent colours get a per-menu bitmap that Release disposes, so the cache stays bounded.
        private static bool Palette(Color color) => color.ToArgb()==Color.LightGray.ToArgb() || Colors.Any(c=>c.ToArgb()==color.ToArgb());
        private static Bitmap Preview(Color color,float width,DashStyle dash,bool swatch=false) => !Palette(color) ? CreatePreview(color,width,dash,swatch) :
            Cached("p:"+color.ToArgb()+":"+width.ToString(System.Globalization.CultureInfo.InvariantCulture)+":"+(int)dash+":"+swatch,()=>CreatePreview(color,width,dash,swatch));
        internal static int CachedPreviewCount => PreviewCache.Count;
        private static Bitmap CreatePreview(Color color,float width,DashStyle dash,bool swatch)
        {
            var bitmap=new Bitmap(48,18);
            using(var g=Graphics.FromImage(bitmap))
            using(var pen=new Pen(color,Math.Min(8,Math.Max(.5f,width))))
            using(var brush=new SolidBrush(color))
            {
                g.Clear(Color.FromArgb(245,245,245));
                if(swatch) { g.FillRectangle(brush,4,2,40,14); g.DrawRectangle(Pens.Gray,4,2,40,14); }
                else { pen.DashStyle=dash; g.DrawLine(pen,2,9,46,9); }
            }
            return bitmap;
        }
        internal static void Styles(ToolStripItemCollection items,Color? color,float? width,DashStyle? dash,
            Action<Color?> setColor,Action<float> setWidth,Action<DashStyle> setDash)
        {
            Action<Color?> choose=c=>
            {
                if(c.HasValue) { Recent.RemoveAll(r=>r.ToArgb()==c.Value.ToArgb()); Recent.Insert(0,c.Value); if(Recent.Count>5) Recent.RemoveAt(5); }
                setColor(c);
            };
            var colors=Sub(items,"Color");
            for(int i=0;i<Colors.Length;i++) { var c=Colors[i]; Item(colors.DropDownItems,ColorNames[i],()=>choose(c),true,color?.ToArgb()==c.ToArgb(),Preview(c,1,DashStyle.Solid,true)); }
            var recent=Sub(colors.DropDownItems,"Recent colors"); recent.Enabled=Recent.Count>0;
            int recentIndex=0;
            foreach(var c in Recent.ToArray()) Item(recent.DropDownItems,(++recentIndex).ToString()+" #"+c.ToArgb().ToString("X8"),()=>choose(c),true,color?.ToArgb()==c.ToArgb(),Preview(c,1,DashStyle.Solid,true));
            Item(colors.DropDownItems,"Custom…",()=>
            {
                using(var dialog=new ColorDialog{Color=color??Color.Black,FullOpen=true}) if(dialog.ShowDialog()==DialogResult.OK)
                { choose(dialog.Color); }
            });
            Item(colors.DropDownItems,"Use component color",()=>choose(null),true,color==null);
            var thickness=Sub(items,"Thickness");
            foreach(float value in new[]{.5f,1f,2f,3f,5f}) Item(thickness.DropDownItems,value.ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+"x",()=>setWidth(value),true,width==value,Preview(Color.Black,value,DashStyle.Solid));
            var line=Sub(items,"Line type");
            for(int i=0;i<5;i++) { var value=(DashStyle)i; Item(line.DropDownItems,DashNames[i],()=>setDash(value),true,dash==value,Preview(Color.Black,2,value)); }
        }
        private static void Palette(ToolStripItemCollection items,Color? color,bool uniform,Action<Color?> choose,bool none)
        {
            if(none) Item(items,"None",()=>choose(null),true,uniform && !color.HasValue);
            for(int i=0;i<Colors.Length;i++)
            {
                var c=Colors[i]; Item(items,ColorNames[i],()=>choose(c),true,uniform && color?.ToArgb()==c.ToArgb(),Preview(c,1,DashStyle.Solid,true));
            }
            var recent=Sub(items,"Recent colors"); recent.Enabled=Recent.Count>0;
            int recentIndex=0;
            foreach(var c in Recent.ToArray()) Item(recent.DropDownItems,(++recentIndex).ToString()+" #"+c.ToArgb().ToString("X8"),()=>choose(c),true,uniform && color?.ToArgb()==c.ToArgb(),Preview(c,1,DashStyle.Solid,true));
            Item(items,"Custom…",()=>{ using(var dialog=new ColorDialog{Color=color??Color.Black,FullOpen=true}) if(dialog.ShowDialog()==DialogResult.OK) choose(dialog.Color); });
        }
        private static void Fill(ToolStripItemCollection items,DrawShape[] eligible,Action<string,Action<DrawShape>> apply,bool enabled,string hint)
        {
            var fill=Sub(items,"Fill"); fill.Enabled=enabled; fill.ToolTipText=hint;
            bool Same<T>(Func<DrawShape,T> get) => eligible.Length>0 && eligible.Select(get).Distinct().Count()==1;
            Color? color=eligible.FirstOrDefault()?.FillColor;
            Palette(fill.DropDownItems,color,Same(l=>l.FillColor?.ToArgb()),c=>
            {
                if(c.HasValue) { Recent.RemoveAll(r=>r.ToArgb()==c.Value.ToArgb()); Recent.Insert(0,c.Value); if(Recent.Count>5) Recent.RemoveAt(5); }
                apply("Change fill color",l=>l.FillColor=c);
            },true);
            var opacity=Sub(fill.DropDownItems,"Opacity");
            foreach(double value in new[]{.1,.25,.5,.75,1}) Item(opacity.DropDownItems,(value*100).ToString("0")+"%",()=>apply("Change fill opacity",l=>l.FillOpacity=value),true,Same(l=>l.FillOpacity) && eligible[0].FillOpacity==value);
            var hatch=Sub(fill.DropDownItems,"Hatch");
            foreach(HatchPattern value in Enum.GetValues(typeof(HatchPattern)))
            {
                var bitmap=Cached("h:"+(int)value,()=>
                {
                    var b=new Bitmap(48,24);
                    using(var g=Graphics.FromImage(b))
                    {
                        g.Clear(Color.White); var shape=new FrameShape(new Rhino.Geometry.Point3d(0,0,0),new Rhino.Geometry.Point3d(48,0,0),new Rhino.Geometry.Point3d(48,24,0),new Rhino.Geometry.Point3d(0,24,0)) { FillColor=Color.Black,Hatch=value,StrokeVisible=false,HatchScale=.75 };
                        using(var path=AnnotationGeometry.Boundary(shape)) HatchRenderer.Paint(g,shape,path);
                    }
                    return b;
                });
                Item(hatch.DropDownItems,value.ToString(),()=>apply("Change hatch",l=>l.Hatch=value),true,Same(l=>l.Hatch) && eligible[0].Hatch==value,bitmap);
            }
            var scale=Sub(fill.DropDownItems,"Hatch scale");
            foreach(double value in new[]{.5,1,2,4}) Item(scale.DropDownItems,value.ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+"x",()=>apply("Change hatch scale",l=>l.HatchScale=value),true,Same(l=>l.HatchScale) && eligible[0].HatchScale==value);
            var background=Sub(fill.DropDownItems,"Hatch background");
            Item(background.DropDownItems,"Light grey",()=>apply("Change hatch background",l=>l.HatchBackground=Color.LightGray),true,Same(l=>l.HatchBackground?.ToArgb()) && eligible[0].HatchBackground?.ToArgb()==Color.LightGray.ToArgb(),Preview(Color.LightGray,1,DashStyle.Solid,true));
            Palette(background.DropDownItems,eligible.FirstOrDefault()?.HatchBackground,Same(l=>l.HatchBackground?.ToArgb()),c=>apply("Change hatch background",l=>l.HatchBackground=c),true);
        }
        private static void TextMenu(ToolStripItemCollection items, TextShape[] texts, Action<string, Action<TextShape>> apply, Action edit = null)
        {
            var menu = Sub(items,"Text"); menu.Enabled = texts.Length > 0;
            bool Same<T>(Func<TextShape,T> get) => texts.Length > 0 && texts.Select(get).Distinct().Count() == 1;
            var style = Sub(menu.DropDownItems,"Style");
            foreach (TextStylePreset value in Enum.GetValues(typeof(TextStylePreset)))
                Item(style.DropDownItems,value.ToString(),()=>apply("Change text style",t=>t.SetPreset(value)),true,Same(t=>t.StylePreset) && texts[0].StylePreset==value);
            var size = Sub(menu.DropDownItems,"Size");
            foreach (double value in new[]{.5,.75,1,1.5,2,3,4})
                Item(size.DropDownItems,value.ToString("0.##",System.Globalization.CultureInfo.InvariantCulture)+"x",()=>apply("Change text size",t=>t.SizeMultiplier=value),true,Same(t=>t.SizeMultiplier) && texts[0].SizeMultiplier==value);
            bool bold = Same(t=>t.Bold) && texts[0].Bold, italic = Same(t=>t.Italic) && texts[0].Italic;
            Item(menu.DropDownItems,"Bold",()=>apply("Change text bold",t=>t.Bold=!bold),true,bold);
            Item(menu.DropDownItems,"Italic",()=>apply("Change text italic",t=>t.Italic=!italic),true,italic);
            var color = Sub(menu.DropDownItems,"Color");
            Action<Color?> choose = c=>
            {
                if (c.HasValue) { Recent.RemoveAll(r=>r.ToArgb()==c.Value.ToArgb()); Recent.Insert(0,c.Value); if(Recent.Count>5) Recent.RemoveAt(5); }
                apply("Change text color",t=>t.TextColorOverride=c);
            };
            Palette(color.DropDownItems,texts.FirstOrDefault()?.TextColorOverride,Same(t=>t.TextColorOverride?.ToArgb()),choose,false);
            Item(color.DropDownItems,"Use component color",()=>choose(null),true,Same(t=>t.TextColorOverride?.ToArgb()) && !texts[0].TextColorOverride.HasValue);
            if (edit != null) Item(menu.DropDownItems,"Edit text…",edit,texts.Length==1);
        }
        internal static void Selection(ToolStripItemCollection items,HopperDraw[] owners)
        {
            var selected=owners.Where(o=>o.HasSelection).ToArray();
            var leaves=selected.SelectMany(o=>o.SelectionLeaves).ToArray();
            var colors=leaves.Select(l=>l.OverrideColor?.ToArgb()).Distinct().ToArray();
            var widths=leaves.Select(l=>l.ThicknessMultiplier).Distinct().ToArray();
            var dashes=leaves.Select(l=>l.LineType).Distinct().ToArray();
            var texts=leaves.OfType<TextShape>().ToArray();
            if (texts.Length>0) TextMenu(items,texts,(name,change)=>AnnotationCommands.Apply(selected,name,
                l=> { if (!(l is TextShape t)) return false; var copy=(TextShape)t.Clone(); change(copy); return !t.SameText(copy); },
                l=>change((TextShape)l)),()=>selected.FirstOrDefault(o=>o.SelectionLeaves.Contains(texts[0]))?.EditText(texts[0]));
            int first=items.Count;
            var stroke=Sub(items,"Stroke");
            Styles(stroke.DropDownItems,colors.Length==1 && colors[0].HasValue ? Color.FromArgb(colors[0].Value) : (Color?)null,
                widths.Length==1 ? widths[0] : (float?)null,dashes.Length==1 ? dashes[0] : (DashStyle?)null,
                c=>AnnotationCommands.Apply(selected,"Change color",l=>l.OverrideColor?.ToArgb()!=c?.ToArgb(),l=>l.OverrideColor=c),
                w=>AnnotationCommands.Apply(selected,"Change thickness",l=>l.ThicknessMultiplier!=w,l=>l.ThicknessMultiplier=w),
                d=>AnnotationCommands.Apply(selected,"Change line type",l=>l.LineType!=d,l=>l.LineType=d));
            // Null means component default only for a uniform selection, never for a mixed selection.
            if(colors.Length!=1) foreach(ToolStripMenuItem item in ((ToolStripMenuItem)stroke.DropDownItems[0]).DropDownItems.OfType<ToolStripMenuItem>()) item.Checked=false;
            for(int i=first;i<items.Count;i++) items[i].Enabled=leaves.Length>0;
            stroke.DropDownItems.Add(new ToolStripSeparator());
            var visibility=leaves.Select(l=>l.StrokeVisible).Distinct().ToArray();
            Item(stroke.DropDownItems,"None",()=>AnnotationCommands.Apply(selected,"Change stroke visibility",l=>l.StrokeVisible!=(visibility.Length==1 && !visibility[0]),l=>l.StrokeVisible=visibility.Length==1 && !visibility[0]),leaves.Length>0,visibility.Length==1 && !visibility[0]);
            Fill(items,leaves.Where(l=>l.SupportsFill).ToArray(),(name,change)=>AnnotationCommands.Apply(selected,name,
                l=> { if(!l.SupportsFill) return false; var candidate=new LineShape(); l.CopyStyleTo(candidate); change(candidate); return !l.SameStyle(candidate); },change),
                leaves.Any(l=>l.SupportsFill),"Close the shape to fill it.");
            items.Add(new ToolStripSeparator());
            Item(items,"Use selection style for drawing",()=>
            {
                var target=owners.FirstOrDefault(o=>o.DrawingModeEnabled)??selected.FirstOrDefault();
                target?.PenFrom(leaves[0]);
            },leaves.Length>0 && leaves.All(l=>l.SameStyle(leaves[0])) &&
                (texts.Length==0 || texts.Length==leaves.Length && texts.All(t=>t.SameTextStyle(texts[0]))));
            items.Add(new ToolStripSeparator());
            Item(items,"Bring to front",()=>AnnotationCommands.Arrange(selected,true),selected.Length>0);
            Item(items,"Send to back",()=>AnnotationCommands.Arrange(selected,false),selected.Length>0);
            items.Add(new ToolStripSeparator());
            var group=Item(items,"Group",()=>selected[0].GroupSelectedShapes(),selected.Length==1 && selected[0].Selection.Count()>1);
            if(selected.Length>1) group.ToolTipText="Grouping requires annotations belonging to one HopperDraw component.";
            Item(items,"Ungroup",()=>
            {
                // A shared record is used for all owners; the component methods honour the transaction guard.
                var changed=selected.Where(o=>o.Selection.Any(s=>s is DrawShapeGroup)).ToArray();
                if(changed.Length==0 || !AnnotationCommands.Record(changed,"Ungroup annotations")) return;
                foreach(var o in changed) { o.MenuTransaction=true; try { o.UngroupSelectedGroups(); } finally { o.MenuTransaction=false; } }
            },selected.Any(o=>o.Selection.Any(s=>s is DrawShapeGroup)));
            items.Add(new ToolStripSeparator());
            Item(items,"Duplicate",()=>AnnotationCommands.Duplicate(selected),selected.Length>0 && selected.All(o=>o.Selection.All(s=>s.CanTransformGeometry)));
            Item(items,"Delete",()=>AnnotationCommands.Delete(selected,selected.FirstOrDefault()?.OnPingDocument(),false),selected.Length>0);
            AnnotationMenuKeys.Apply(items);
        }
        internal static void Drawing(ToolStripItemCollection items,HopperDraw owner)
        {
            var tools=Sub(items,"Tool"); bool wired=owner.Params.Input[2].SourceCount>0;
            foreach(var tool in DrawingTool.All) { int mode=tool.Index; var item=Item(tools.DropDownItems,tool.Name,()=>owner.SetTool(mode),!wired,owner.DrawMode==mode); item.Image=Cached("tool:"+tool.Index,()=>ToolGlyph.Bitmap(tool.Index,16)); item.ShortcutKeyDisplayString=tool.Key.ToString(); if(wired) item.ToolTipText="Disconnect the M input to choose a tool here."; }
            var pen=Sub(items,"Pen");
            TextMenu(pen.DropDownItems,new[]{owner.TextPen},(name,change)=>{change(owner.TextPen);owner.Redraw();});
            var stroke=Sub(pen.DropDownItems,"Stroke");
            var stylePen=owner.DrawMode==5 ? (DrawShape)owner.TextPen : owner.PenStyle;
            Styles(stroke.DropDownItems,owner.DrawMode==5 ? stylePen.OverrideColor : owner.PendingColorOverride,owner.DrawMode==5 ? stylePen.ThicknessMultiplier : owner.CurrentThicknessMultiplier,owner.DrawMode==5 ? stylePen.LineType : owner.CurrentLineType,
                c=>{ if(owner.DrawMode==5) {stylePen.OverrideColor=c;owner.Redraw();} else owner.Pen(c,owner.CurrentThicknessMultiplier,owner.CurrentLineType); },
                w=>{ if(owner.DrawMode==5) {stylePen.ThicknessMultiplier=w;owner.Redraw();} else owner.Pen(owner.PendingColorOverride,w,owner.CurrentLineType); },
                d=>{ if(owner.DrawMode==5) {stylePen.LineType=d;owner.Redraw();} else owner.Pen(owner.PendingColorOverride,owner.CurrentThicknessMultiplier,d); });
            stroke.DropDownItems.Add(new ToolStripSeparator());
            Item(stroke.DropDownItems,"None",()=>{stylePen.StrokeVisible=!stylePen.StrokeVisible; owner.Redraw();},true,!stylePen.StrokeVisible);
            Fill(pen.DropDownItems,new[]{stylePen},(name,change)=>{change(stylePen); owner.Redraw();},owner.DrawMode!=0,"Applies when the shape is closed, or to the text box.");
            var smoothing=Sub(pen.DropDownItems,"Smoothing");
            foreach(SmoothingLevel value in Enum.GetValues(typeof(SmoothingLevel)))
                Item(smoothing.DropDownItems,value.ToString(),()=>{owner.Smoothing=value; owner.Redraw();},true,owner.Smoothing==value);
            Item(items,"Reset pen to component defaults",owner.ResetPen);
            Item(items,"Exit drawing mode",owner.ExitDrawing,owner.DrawingModeEnabled);
            AnnotationMenuKeys.Apply(items);
        }
        internal static void Component(ToolStripDropDown menu,HopperDraw owner)
        {
            menu.Items.Add(new ToolStripSeparator());
            var used=new HashSet<char>(menu.Items.Cast<ToolStripItem>().Select(i=>AnnotationMenuKeys.Mnemonic(i.Text)));
            var root=Sub(menu.Items,"HopperDraw"); root.Text=AnnotationMenuKeys.Label(root.Name,used);
            // GH's renderer hides mnemonic underlines without Alt; show the entry letter in the shortcut column
            // instead (display text only, not a ShortcutKeys binding) and leave the host renderer untouched.
            root.ShortcutKeyDisplayString=AnnotationMenuKeys.Mnemonic(root.Text).ToString();
            AnnotationMenuKeys.Track(menu);
            var items=root.DropDownItems;
            var drawing=Sub(items,"Drawing");
            Item(drawing.DropDownItems,"Enter drawing mode",owner.EnterDrawingMode,!owner.DrawingModeEnabled && owner.Eligible);
            Drawing(drawing.DropDownItems,owner);
            var selection=Sub(items,"Selection");
            var owners=owner.OnPingDocument()?.Objects.OfType<HopperDraw>().Where(o=>o.Eligible).ToArray()??new[]{owner};
            Selection(selection.DropDownItems,owners);
            Item(items,"Select all annotations",()=>{ owner.ClearSelection(); for(int i=0;i<owner.Shapes.Count;i++) if(!(owner.Shapes[i] is OpaqueShape)) owner.SelectedShapeIndices.Add(i); owner.Redraw(); });
            Item(items,"Select unreadable annotation records",()=>{ owner.ClearSelection(); for(int i=0;i<owner.Shapes.Count;i++) if(owner.Shapes[i] is OpaqueShape) owner.SelectedShapeIndices.Add(i); owner.Redraw(); },owner.Shapes.Any(s=>s is OpaqueShape));
            Item(items,"Clear all annotations",owner.ClearAll,owner.Shapes.Count>0 || owner.ComponentLoadDiagnostics.Any());
            AnnotationMenuKeys.Configure(root.DropDown); AnnotationMenuKeys.Apply(items);
        }
    }
}
