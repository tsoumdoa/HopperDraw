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
        private static readonly string[] ToolNames={"Line","Polyline","Frame","Curve"};
        private static readonly string[] DashNames={"Solid","Dash","Dot","Dash-dot","Dash-dot-dot"};
        private static readonly Color[] Colors={Color.Black,Color.FromArgb(70,70,70),Color.White,Color.Red,Color.Orange,Color.Yellow,Color.Green,Color.Teal,Color.Blue,Color.Purple,Color.HotPink};
        private static readonly string[] ColorNames={"Black","Dark grey","White","Red","Orange","Yellow","Green","Teal","Blue","Purple","Pink"};
        private static ToolStripMenuItem Item(ToolStripItemCollection items,string text,Action action,bool enabled=true,bool check=false,Image image=null)
        {
            var item=new ToolStripMenuItem(text,image,(s,e)=>action?.Invoke()){Enabled=enabled,Checked=check}; items.Add(item); return item;
        }
        private static ToolStripMenuItem Sub(ToolStripItemCollection items,string text) { var item=new ToolStripMenuItem(text); items.Add(item); return item; }
        private static Bitmap Preview(Color color,float width,DashStyle dash,bool swatch=false)
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
            foreach(var c in Recent.ToArray()) Item(recent.DropDownItems,"#"+c.ToArgb().ToString("X8"),()=>choose(c),true,color?.ToArgb()==c.ToArgb(),Preview(c,1,DashStyle.Solid,true));
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
        internal static void Selection(ToolStripItemCollection items,HopperDraw[] owners)
        {
            var selected=owners.Where(o=>o.HasSelection).ToArray();
            var leaves=selected.SelectMany(o=>o.SelectionLeaves).ToArray();
            var colors=leaves.Select(l=>l.OverrideColor?.ToArgb()).Distinct().ToArray();
            var widths=leaves.Select(l=>l.ThicknessMultiplier).Distinct().ToArray();
            var dashes=leaves.Select(l=>l.LineType).Distinct().ToArray();
            int first=items.Count;
            Styles(items,colors.Length==1 && colors[0].HasValue ? Color.FromArgb(colors[0].Value) : (Color?)null,
                widths.Length==1 ? widths[0] : (float?)null,dashes.Length==1 ? dashes[0] : (DashStyle?)null,
                c=>AnnotationCommands.Apply(selected,"Change color",l=>l.OverrideColor?.ToArgb()!=c?.ToArgb(),l=>l.OverrideColor=c),
                w=>AnnotationCommands.Apply(selected,"Change thickness",l=>l.ThicknessMultiplier!=w,l=>l.ThicknessMultiplier=w),
                d=>AnnotationCommands.Apply(selected,"Change line type",l=>l.LineType!=d,l=>l.LineType=d));
            // Null means component default only for a uniform selection, never for a mixed selection.
            if(colors.Length!=1) foreach(ToolStripMenuItem item in ((ToolStripMenuItem)items[first]).DropDownItems.OfType<ToolStripMenuItem>()) item.Checked=false;
            for(int i=first;i<items.Count;i++) items[i].Enabled=leaves.Length>0;
            items.Add(new ToolStripSeparator());
            Item(items,"Use selection style for drawing",()=>
            {
                var target=owners.FirstOrDefault(o=>o.DrawingModeEnabled)??selected.FirstOrDefault();
                target?.Pen(leaves[0].OverrideColor,widths[0],dashes[0]);
            },leaves.Length>0 && colors.Length==1 && widths.Length==1 && dashes.Length==1);
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
        }
        internal static void Drawing(ToolStripItemCollection items,HopperDraw owner)
        {
            var tools=Sub(items,"Tool"); bool wired=owner.Params.Input[2].SourceCount>0;
            for(int i=0;i<4;i++) { int mode=i; var item=Item(tools.DropDownItems,ToolNames[i],()=>owner.SetTool(mode),!wired,owner.DrawMode==i); if(wired) item.ToolTipText="Disconnect the M input to choose a tool here."; }
            var pen=Sub(items,"Pen");
            Styles(pen.DropDownItems,owner.PendingColorOverride,owner.CurrentThicknessMultiplier,owner.CurrentLineType,
                c=>owner.Pen(c,owner.CurrentThicknessMultiplier,owner.CurrentLineType),
                w=>owner.Pen(owner.PendingColorOverride,w,owner.CurrentLineType),
                d=>owner.Pen(owner.PendingColorOverride,owner.CurrentThicknessMultiplier,d));
            Item(items,"Reset pen to component defaults",owner.ResetPen);
            Item(items,"Exit drawing mode",owner.ExitDrawing,owner.DrawingModeEnabled);
        }
        internal static void Component(ToolStripDropDown menu,HopperDraw owner)
        {
            menu.Items.Add(new ToolStripSeparator());
            var drawing=Sub(menu.Items,"Drawing");
            Item(drawing.DropDownItems,"Enter drawing mode",owner.EnterDrawingMode,!owner.DrawingModeEnabled && owner.Eligible);
            Drawing(drawing.DropDownItems,owner);
            var selection=Sub(menu.Items,"Selection");
            var owners=owner.OnPingDocument()?.Objects.OfType<HopperDraw>().Where(o=>o.Eligible).ToArray()??new[]{owner};
            Selection(selection.DropDownItems,owners);
            Item(menu.Items,"Select all annotations",()=>{ owner.ClearSelection(); for(int i=0;i<owner.Shapes.Count;i++) if(!(owner.Shapes[i] is OpaqueShape)) owner.SelectedShapeIndices.Add(i); owner.Redraw(); });
            Item(menu.Items,"Select unreadable annotation records",()=>{ owner.ClearSelection(); for(int i=0;i<owner.Shapes.Count;i++) if(owner.Shapes[i] is OpaqueShape) owner.SelectedShapeIndices.Add(i); owner.Redraw(); },owner.Shapes.Any(s=>s is OpaqueShape));
            Item(menu.Items,"Clear all annotations",owner.ClearAll,owner.Shapes.Count>0 || owner.ComponentLoadDiagnostics.Any());
        }
    }
}
