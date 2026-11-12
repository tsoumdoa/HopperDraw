using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using hopperdraw;
using Rhino.Geometry;

internal static class CanvasMenuTests
{
    private static HopperDraw Owner()
    {
        var o=new HopperDraw(); o.Shapes.Add(new LineShape(new Point3d(0,0,0),new Point3d(100,0,0))); o.SelectedShapeIndices.Add(0); return o;
    }
    private static ToolStripMenuItem Find(ToolStripItemCollection items,string name)=>items.OfType<ToolStripMenuItem>().Single(i=>i.Name==name);
    internal static void Run()
    {
        Program.Test("selection menu checks uniform styles and exposes explicit line types",()=>
        {
            var o=Owner(); o.Shapes[0].OverrideColor=Color.Red; o.Shapes[0].ThicknessMultiplier=2; o.Shapes[0].LineType=DashStyle.Dot;
            using(var menu=new ContextMenuStrip())
            {
                AnnotationMenuBuilder.Selection(menu.Items,new[]{o});
                Program.Check(Find(Find(Find(menu.Items,"Stroke").DropDownItems,"Color").DropDownItems,"Red").Checked,"Uniform red unchecked");
                Program.Check(Find(Find(Find(menu.Items,"Stroke").DropDownItems,"Thickness").DropDownItems,"2x").Checked,"Uniform width unchecked");
                Program.Check(Find(Find(Find(menu.Items,"Stroke").DropDownItems,"Line type").DropDownItems,"Dot").Checked,"Uniform dash unchecked");
                Program.Check(Find(menu.Items,"Use selection style for drawing").Enabled,"Uniform style unavailable");
                Find(Find(Find(menu.Items,"Stroke").DropDownItems,"Line type").DropDownItems,"Dash-dot-dot").PerformClick();
                Program.Check(o.Shapes[0].LineType==DashStyle.DashDotDot && o.CurrentLineType==DashStyle.Solid,"Selection style changed pen");
            }
        });
        Program.Test("mixed cross-owner menu has no checks, disables grouping with explanation",()=>
        {
            HopperDraw a=Owner(), b=Owner(); a.Shapes[0].OverrideColor=Color.Red; b.Shapes[0].OverrideColor=Color.Blue;
            b.Shapes[0].ThicknessMultiplier=3; b.Shapes[0].LineType=DashStyle.Dash;
            using(var menu=new ContextMenuStrip())
            {
                AnnotationMenuBuilder.Selection(menu.Items,new[]{a,b});
                foreach(string name in new[]{"Color","Thickness","Line type"}) Program.Check(!Find(Find(menu.Items,"Stroke").DropDownItems,name).DropDownItems.OfType<ToolStripMenuItem>().Any(i=>i.Checked),"Mixed style checked: "+name);
                Program.Check(!Find(menu.Items,"Use selection style for drawing").Enabled,"Mixed style copied to pen");
                Program.Check(!Find(menu.Items,"Group").Enabled && Find(menu.Items,"Group").ToolTipText.Length>0,"Cross-owner grouping available or unexplained");
                Find(Find(Find(menu.Items,"Stroke").DropDownItems,"Thickness").DropDownItems,"5x").PerformClick();
                Program.Check(a.Shapes[0].ThicknessMultiplier==5 && b.Shapes[0].ThicknessMultiplier==5,"Menu did not style both owners");
            }
        });
        Program.Test("drawing menu tool and pen checks reflect state; wired M cannot switch",()=>
        {
            var o=Owner(); o.Pen(null,3,DashStyle.Dash);
            using(var menu=new ContextMenuStrip())
            {
                AnnotationMenuBuilder.Drawing(menu.Items,o);
                Program.Check(Find(Find(menu.Items,"Tool").DropDownItems,"Line").Checked,"Current tool not checked");
                Program.Check(Find(Find(Find(Find(menu.Items,"Pen").DropDownItems,"Stroke").DropDownItems,"Thickness").DropDownItems,"3x").Checked,"Pen width not checked");
                Find(Find(menu.Items,"Tool").DropDownItems,"Curve").PerformClick();
                Program.Check(o.DrawMode==3,"Tool persistent API not applied");
            }
            o.Params.Input[2].AddSource(new Grasshopper.Kernel.Parameters.Param_Integer());
            using(var menu=new ContextMenuStrip())
            {
                AnnotationMenuBuilder.Drawing(menu.Items,o);
                Program.Check(Find(menu.Items,"Tool").DropDownItems.OfType<ToolStripMenuItem>().All(i=>!i.Enabled && i.ToolTipText.Contains("M input")),"Wired tool is enabled or unexplained");
            }
        });
        Program.Test("shared selected styling no-op returns before undo and dirty/redraw boundary",()=>
        {
            using(var canvas=new GH_Canvas())
            {
                HopperDraw a=Owner(), b=Owner(); var handle=canvas.Handle;
                typeof(HopperDraw).GetField("_canvas",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(a,canvas);
                typeof(HopperDraw).GetField("_canvas",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(b,canvas);
                int redraws=0; canvas.Invalidated+=(s,e)=>redraws++;
                bool changed=AnnotationCommands.Apply(new[]{a,b},"Same width",s=>s.ThicknessMultiplier!=1,s=>s.ThicknessMultiplier=1);
                Program.Check(!changed && redraws==0,"No-op crossed transaction/dirty boundary");
            }
        });
        Program.Test("multi-owner annotation-only undo snapshots swap independently with stable IDs",()=>
        {
            HopperDraw a=Owner(), b=Owner(); var aid=a.Shapes[0].PersistentId; var bid=b.Shapes[0].PersistentId;
            var move=new AnnotationMove(new[]{a,b},new IGH_DocumentObject[0],null);
            move.Translate(new PointF(40,20),false);
            foreach(var action in move.Actions) action.Value.Swap(null,action.Key);
            Program.Check(a.Shapes[0].GetPoints()[0]==PointF.Empty && b.Shapes[0].GetPoints()[0]==PointF.Empty,"Undo did not restore both owners");
            foreach(var action in move.Actions) action.Value.Swap(null,action.Key);
            Program.Check(a.Shapes[0].GetPoints()[0]==new PointF(40,20) && b.Shapes[0].GetPoints()[0]==new PointF(40,20),"Redo did not restore delta");
            Program.Check(a.Shapes[0].PersistentId==aid && b.Shapes[0].PersistentId==bid,"Undo changed shape IDs");
        });
        Program.Test("arrange preserves relative order and selection; already-front is a no-op",()=>
        {
            var o=Owner(); var a=o.Shapes[0]; var b=a.Clone(); var c=a.Clone(); o.Shapes.Add(b); o.Shapes.Add(c); o.SelectedShapeIndices.Add(2);
            AnnotationCommands.Arrange(new[]{o},true);
            Program.Check(o.Shapes.SequenceEqual(new[]{b,a,c}) && o.SelectedShapeIndices.SequenceEqual(new[]{1,2}),"Front arrangement lost order/selection");
            var before=o.Shapes; AnnotationCommands.Arrange(new[]{o},true); Program.Check(ReferenceEquals(before,o.Shapes),"No-op arrangement rewrote list");
        });
    }
}
