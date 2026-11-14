using System;
using System.Drawing;
using System.Windows.Forms;
using hopperdraw;
using Rhino.Geometry;
using Point=System.Drawing.Point;

internal static class TextInputTests
{
    internal static void Run(Action<string,Action> test, Action<bool,string> check)
    {
        test("text click and normalized drag request placement without completing an empty shape",()=>
        {
            var draft=new AnnotationDraft();var p=new Point3d(100,100,0);
            check(draft.Down(5,p,new Point(100,100),1,true,1)==null,"text down completed shape");
            check(draft.Up(5,new Point3d(101,100,0),new Point(101,100),true,new Size(4,4))==null,"text up completed empty shape");
            var request=draft.TakePlacement();check(request.Anchor==p && request.Width==null && !draft.Active,"click not point text");
            draft.Down(5,p,new Point(100,100),1,false,1);
            check(draft.Preview(5,new Point3d(20,40,0),true) is FrameShape,"text drag preview absent");
            draft.Up(5,new Point3d(20,40,0),new Point(20,40),true,new Size(4,4));request=draft.TakePlacement();
            check(request.Anchor==new Point3d(20,40,0) && request.Width==80 && draft.TakePlacement()==null,"drag not normalized/consumed once");
            draft.Down(5,p,Point.Empty,1,false,1);draft.Cancel();draft.Up(5,p,Point.Empty,false,new Size(4,4));check(draft.TakePlacement()==null,"cancelled text placed");
        });
        test("DD then tool keys use immediate commands without suffix state",()=>
        {
            var dd=new DrawingKeyGesture();bool drawing=false;int mode=0,changes=0,notices=0;
            drawing=dd.KeyDown(Keys.D,true,100,500);dd.KeyUp(Keys.D);drawing=dd.KeyDown(Keys.D,true,150,500);dd.KeyUp(Keys.D);
            check(drawing && ToolShortcut.Route(Keys.N,mode,false,m=>{mode=m;changes++;},()=>notices++),"DDN not recognized");
            check(mode==5 && changes==1,"DDN not Text");
            // The bottom row, left to right, in the order of the tool buttons.
            foreach(var pair in new[]{Tuple.Create(Keys.Z,0),Tuple.Create(Keys.X,1),Tuple.Create(Keys.C,2),Tuple.Create(Keys.V,3),Tuple.Create(Keys.B,4)})
            {check(ToolShortcut.Route(pair.Item1,mode,false,m=>{mode=m;changes++;},()=>notices++),"key not recognized");check(mode==pair.Item2,"wrong tool");}
            int before=changes;ToolShortcut.Route(Keys.B,mode,false,m=>changes++,()=>notices++);check(changes==before,"reselection changed draft");
            foreach(var old in new[]{Keys.T,Keys.L,Keys.P,Keys.R,Keys.F}) check(!ToolShortcut.Route(old,mode,false,m=>changes++,()=>notices++) && changes==before,"a style-row letter still switches tools");
            check(!ToolShortcut.Route(Keys.Control|Keys.Z,mode,false,m=>changes++,()=>notices++) && !ToolShortcut.Route(Keys.Control|Keys.C,mode,false,m=>changes++,()=>notices++) &&
                !ToolShortcut.Route(Keys.Control|Keys.V,mode,false,m=>changes++,()=>notices++) && !ToolShortcut.Route(Keys.Shift|Keys.N,mode,false,m=>changes++,()=>notices++),"undo, copy, paste or a modified key taken as a tool");
            var draft=new AnnotationDraft();draft.Down(1,new Point3d(10,10,0),Point.Empty,1,false,1);
            ToolShortcut.Route(Keys.N,mode,true,m=>{changes++;draft.Cancel();},()=>notices++);
            check(changes==before && notices==1 && draft.Active,"wired M changed/cancelled draft or missed notice");
        });
        test("text pen stays separate and external tool changes cancel only the gesture",()=>
        {
            var owner=new HopperDraw();owner.TextPen.Bold=true;owner.TextPen.FillColor=Color.Yellow;
            var text=new TextShape();owner.ApplyPen(text);check(text.Bold && !text.StrokeVisible && text.FillColor==Color.Yellow,"text pen defaults wrong");
            var line=new LineShape();owner.ApplyPen(line);check(line.StrokeVisible && line.FillColor==null,"geometry inherited text pen");
            var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
            typeof(HopperDraw).GetField("_drawingModeEnabled",flags).SetValue(owner,true);
            owner.Draft.Down(1,new Point3d(0,0,0),Point.Empty,1,false,1);owner.CancelGesture();
            check(owner.DrawingModeEnabled && !owner.Draft.Active,"tool change exited drawing mode");
            owner.ResetPen();check(!owner.TextPen.Bold && !owner.TextPen.StrokeVisible && owner.PenStyle.StrokeVisible,"reset pen wrong");
        });
        test("finishing text leaves drawing mode only from the Text tool",()=>
        {
            var owner=new HopperDraw();var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
            check(!CanvasAnnotationController.ExitsAfterText(null) && !CanvasAnnotationController.ExitsAfterText(owner),"exit outside drawing mode");
            typeof(HopperDraw).GetField("_drawingModeEnabled",flags).SetValue(owner,true);
            owner.SetTool(0);check(!CanvasAnnotationController.ExitsAfterText(owner),"editing text from Line tool exits drawing");
            owner.SetTool(5);check(CanvasAnnotationController.ExitsAfterText(owner),"Text tool stays after finishing");
            owner.ExitDrawing();check(!owner.DrawingModeEnabled,"exit failed");
        });
    }
}
