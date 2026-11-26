using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using hopperdraw;
using Rhino.Geometry;

internal static class RevisionCloudTests
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static FrameShape Frame(double x=40,double y=40,double w=96,double h=96) => new FrameShape(new Point3d(x,y,0),new Point3d(x+w,y,0),new Point3d(x+w,y+h,0),new Point3d(x,y+h,0));
    private static HopperDraw Owner(params DrawShape[] shapes)
    {
        var owner=new HopperDraw(); owner.Shapes.AddRange(shapes);
        for(int i=0;i<shapes.Length;i++) owner.SelectedShapeIndices.Add(i);
        return owner;
    }
    private static HopperDraw Drawing(int mode) { var owner=new HopperDraw(); typeof(HopperDraw).GetField("_drawMode",Private).SetValue(owner,mode); return owner; }
    private static GH_IWriter Record(GH_LooseChunk root) => (GH_IWriter)root.FindChunk("HopperDraw").FindChunk("Shapes").FindChunk("Shape",0);
    private static ToolStripMenuItem Find(ToolStripItemCollection items,string name) => items.OfType<ToolStripMenuItem>().Single(i=>i.Name==name);
    private static StyleKey Key(Keys key) { Program.Check(StyleKeys.Match(key,false,out var result),"key missing"); return result; }
    private static int Arcs(GraphicsPath path) => (path.PointCount-1)/6;
    internal static void Run()
    {
        Program.Test("cloud frame has two cubics per 150 degree lobe, exact corner cusps and outward fill in either orientation",()=>
        {
            foreach(bool reverse in new[]{false,true})
            {
                var points=Frame().GetPoints(); if(reverse) Array.Reverse(points);
                var shape=new PolylineShape(points.Select(p=>new Point3d(p.X,p.Y,0)).ToList(),true) { CloudSize=48, FillColor=Color.Red };
                using(var path=AnnotationGeometry.Boundary(shape))
                {
                    Program.Check(Arcs(path)==8 && path.FillMode==FillMode.Winding && (path.PathTypes.Last()&128)!=0,"arc count or closure");
                    foreach(var corner in points) Program.Check(Enumerable.Range(0,8).Any(i=>path.PathPoints[i*6]==corner),"corner not a cusp");
                    Program.Check(path.IsVisible(64,30) && path.IsVisible(64,50) && !path.IsVisible(64,20),"bulge not outward or wrong apex");
                }
                Program.Check(shape.EditHandles.SequenceEqual(points),"handles moved off base vertices");
            }
            var frame=Frame(w:119,h:48); frame.CloudSize=48;
            using(var a=AnnotationGeometry.Boundary(frame)) Program.Check(Arcs(a)==6,"below rounding threshold");
            frame.TopRight=new Point3d(161,40,0); frame.BottomRight=new Point3d(161,88,0);
            using(var a=AnnotationGeometry.Boundary(frame)) Program.Check(Arcs(a)==8,"above rounding threshold");
        });
        Program.Test("curve cloud samples whole spline perimeter and subdivides chords across tight bends",()=>
        {
            var curve=new CurveShape(new[]{new Point3d(50,50,0),new Point3d(180,40,0),new Point3d(210,150,0),new Point3d(80,170,0)}.ToList(),true);
            using(var plain=AnnotationGeometry.Boundary(curve))
            {
                plain.Flatten(null,.2f); var p=plain.PathPoints; double perimeter=0;
                for(int i=0;i<p.Length;i++) perimeter+=GeometryUtilities.Distance(p[i],p[(i+1)%p.Length]);
                curve.CloudSize=24;
                using(var path=AnnotationGeometry.Boundary(curve))
                    Program.Check(Arcs(path)==Math.Max(3,(int)Math.Round(perimeter/24)),"curve not sampled over full perimeter");
                curve.CloudSize=500;
                using(var path=AnnotationGeometry.Boundary(curve)) Program.Check(Arcs(path)>=3 && Arcs(path)<=RevisionCloud.MaxArcs,"curve minimum/budget");
            }
            var bend=new CurveShape(new[]{new Point3d(40,40,0),new Point3d(240,40,0),new Point3d(60,50,0),new Point3d(240,60,0),new Point3d(240,180,0),new Point3d(40,180,0)}.ToList(),true) { CloudSize=90 };
            using(var plain=new GraphicsPath())
            {
                plain.AddClosedCurve(bend.GetPoints(),.5f); plain.Flatten(null,.2f); var p=plain.PathPoints;
                using(var cloud=AnnotationGeometry.Boundary(bend))
                {
                    cloud.Flatten(null,.2f);
                    foreach(var v in p) Program.Check(cloud.IsVisible(v) || Enumerable.Range(1,cloud.PointCount-1).Min(i=>GeometryUtilities.DistanceToSegment(v,cloud.PathPoints[i-1],cloud.PathPoints[i]))<91,"tight bend cut too far");
                }
            }
        });
        Program.Test("cloud degeneracy, duplicates, concavity, self intersections, tiny sizes and large coordinates are bounded",()=>
        {
            var flat=new PolylineShape(new[]{new Point3d(0,0,0),new Point3d(50,0,0),new Point3d(100,0,0)}.ToList(),true);
            using(var plain=AnnotationGeometry.Boundary(flat)) { flat.CloudSize=24; using(var cloud=AnnotationGeometry.Boundary(flat)) Program.Check(plain.PathPoints.SequenceEqual(cloud.PathPoints),"collinear changed"); }
            var frame=Frame(); var vertices=frame.GetPoints().Select(p=>new Point3d(p.X,p.Y,0)).ToList(); vertices.Insert(1,vertices[0]); vertices.Add(vertices[0]);
            var duplicate=new PolylineShape(vertices,true) { CloudSize=48 };
            using(var path=AnnotationGeometry.Boundary(duplicate)) Program.Check(Arcs(path)==8,"duplicate endpoint added arcs");
            foreach(var shape in new DrawShape[]{Frame(w:.0001,h:.0001),Frame(x:9000000,y:9000000,w:100,h:100),Frame(w:1000000,h:1000000),
                new PolylineShape(new[]{new Point3d(0,0,0),new Point3d(100,0,0),new Point3d(30,30,0),new Point3d(100,100,0),new Point3d(0,100,0)}.ToList(),true),
                new PolylineShape(new[]{new Point3d(0,0,0),new Point3d(120,100,0),new Point3d(0,100,0),new Point3d(80,0,0)}.ToList(),true),
                new CurveShape(vertices,true)})
                foreach(double size in new[]{double.Epsilon,.00001,24,1000000,double.PositiveInfinity})
                {
                    shape.CloudSize=size; using(var path=AnnotationGeometry.Boundary(shape))
                        Program.Check(path.PointCount<=RevisionCloud.MaxArcs*6+1 && ShapeRenderer.ValidPoints(path.PathPoints),"unbounded/invalid cloud");
                }
            var many=new PolylineShape(Enumerable.Range(0,4001).Select(i=>new Point3d(500+100*Math.Cos(i*2*Math.PI/4001),500+100*Math.Sin(i*2*Math.PI/4001),0)).ToList(),true);
            using(var plain=AnnotationGeometry.Boundary(many)) { many.CloudSize=.001; using(var cloud=AnnotationGeometry.Boundary(many)) Program.Check(plain.PathPoints.SequenceEqual(cloud.PathPoints),"mandatory edge budget not plain"); }
        });
        Program.Test("cloud cache toggles and filled interior, text window/crossing and backdrop follow scallops",()=>
        {
            var frame=Frame(); var apex=new PointF(64,21.58f);
            Program.Check(!AnnotationGeometry.Hit(frame,apex,.5f,1),"plain frame hit apex");
            frame.CloudSize=48; Program.Check(AnnotationGeometry.Hit(frame,apex,.5f,1),"cloud cache stale");
            frame.FillColor=Color.Red;
            Program.Check(AnnotationGeometry.Interior(frame,new PointF(64,30)) && !AnnotationGeometry.Interior(frame,new PointF(64,20)),"interior ignores scallops");
            frame.CloudSize=0; Program.Check(!AnnotationGeometry.Hit(frame,apex,.5f,1),"cache stale on undo/off");
            var text=new TextShape { Text="revision",Anchor=new Point3d(60,60,0),BoxWidth=96,CloudSize=48 };
            var bounds=text.LayoutBounds; var mid=new PointF(bounds.Left+24,bounds.Top-10);
            Program.Check(AnnotationGeometry.Interior(text,mid),"text scallop interior missing");
            Program.Check(!AnnotationGeometry.InRectangle(text,bounds,false),"text window ignored bulge");
            var crossing=new RectangleF(mid.X-1,mid.Y-1,2,2);
            Program.Check(AnnotationGeometry.InRectangle(text,crossing,true) && !AnnotationGeometry.InRectangle(text,crossing,true,0,false),"text interior/backdrop crossing");
            var outer=bounds; outer.Inflate(25,25); Program.Check(AnnotationGeometry.InRectangle(text,outer,false),"cloud window too large");
        });
        Program.Test("cloud archive default, round trip, group style and indexed extensions; corrupt fields recover losslessly",()=>
        {
            var shape=Frame(); shape.CloudSize=48; var group=new DrawShapeGroup { CloudSize=96 }; group.Children.Add(shape);
            foreach(bool binary in new[]{false,true}) Program.Check(Program.Fingerprint(Owner(group).Shapes[0])==Program.Fingerprint(Program.RoundTrip(Owner(group),binary).Shapes[0]),"cloud round trip");
            var root=Program.Save(Owner(shape)); var record=Record(root); record.RemoveItem("CloudSize");
            record.SetString("CloudSize",7,"extension"); record.CreateChunk("Future",3).SetString("Data","kept");
            var loaded=Program.Load(root); Program.Check(loaded.LoadWarning==null && loaded.Shapes[0].CloudSize==0,"missing key warning/default");
            foreach(bool binary in new[]{false,true})
            {
                var saved=(GH_IReader)Record(Program.Save(Program.RoundTrip(loaded,binary)));
                Program.Check(saved.GetString("CloudSize",7)=="extension" && saved.FindChunk("Future",3).GetString("Data")=="kept","extension lost");
            }
            foreach(var corrupt in new Action<GH_IWriter>[] { r=>r.SetString("CloudSize","bad"),r=>r.SetInt32("CloudSize",24),r=>r.SetDouble("CloudSize",double.NaN),r=>r.SetDouble("CloudSize",double.PositiveInfinity),r=>r.SetDouble("CloudSize",-1),r=>r.SetDouble("CloudSize",1000001) })
            {
                root=Program.Save(Owner(shape)); record=Record(root); record.RemoveItem("CloudSize"); corrupt(record);
                string raw=ShapeSerializer.Capture((GH_IReader)record).Serialize_Xml(); loaded=Program.Load(root);
                Program.Check(loaded.Shapes[0] is OpaqueShape && loaded.LoadWarning.Contains("Invalid fill/stroke style"),"bad cloud accepted");
                Program.Check(ShapeSerializer.Capture((GH_IReader)Record(Program.Save(loaded))).Serialize_Xml()==raw,"opaque cloud rewritten");
            }
        });
        Program.Test("cloud shared command filters actual nested leaves, reveals only wholly hidden eligible strokes, copies and undoes",()=>
        {
            var frame=Frame(); frame.StrokeVisible=false; var text=new TextShape { Text="box" }; var line=new LineShape();
            var group=new DrawShapeGroup(); group.Children.Add(frame); var nested=new DrawShapeGroup(); group.Children.Add(nested); nested.Children.Add(text); nested.Children.Add(line);
            var opaque=new OpaqueShape(new GH_LooseChunk("Unknown"),"unknown test shape"); group.Children.Add(opaque);
            var owner=Owner(group); var target=new SelectionTarget(new[]{owner}); var undo=new ShapeEditUndoAction(owner,new IGH_DocumentObject[0]);
            target.Cloud(48);
            Program.Check(frame.CloudSize==48 && text.CloudSize==48 && frame.StrokeVisible && text.StrokeVisible && line.CloudSize==0 && opaque.CloudSize==0,"leaf eligibility/reveal");
            var copy=new LineShape(); frame.CopyStyleTo(copy); Program.Check(frame.SameStyle(copy),"copy cloud lost"); copy.CloudSize=0; Program.Check(!frame.SameStyle(copy),"SameStyle ignores cloud");
            target.Cloud(0); Program.Check(frame.StrokeVisible && text.StrokeVisible,"Off hid stroke");
            frame.StrokeVisible=false; target.Cloud(24); Program.Check(!frame.StrokeVisible && text.StrokeVisible,"mixed visibility revealed");
            undo.Swap(null,owner); owner.SelectedShapeIndices.Add(0); Program.Check(owner.SelectionLeaves.All(s=>s.CloudSize==0),"undo cloud");
            undo.Swap(null,owner); owner.SelectedShapeIndices.Add(0); Program.Check(owner.SelectionLeaves.Count(s=>s.CloudSize==24)==2,"redo cloud");
        });
        Program.Test("cloud pen is tool aware, does not override line input, H-L and Shift H-L retain distinct meanings",()=>
        {
            foreach(int mode in Enumerable.Range(0,6))
            {
                var owner=Drawing(mode); var target=new PenTarget(owner); target.Cloud(48);
                var frame=Frame(); owner.ApplyPen(frame); var text=new TextShape(); owner.ApplyPen(text); var line=new LineShape(); owner.ApplyPen(line);
                Program.Check(target.CloudShapes.Length==(mode==0?0:1) && (mode==5?text.CloudSize:frame.CloudSize)==(mode==0?0:48) && line.CloudSize==0,"tool eligibility "+mode);
                Program.Check(!(bool)typeof(HopperDraw).GetField("_hasPendingLineTypeOverride",Private).GetValue(owner),"cloud overrode line input");
            }
            var pen=new PenTarget(Drawing(2));
            foreach(int i in Enumerable.Range(0,4))
            {
                Program.Check(pen.Run(Key(StyleKeys.Home[i+5]))==(i==0?"Cloud off":"Cloud: "+StyleTarget.CloudNames[i]) && pen.Shapes[0].CloudSize==StyleTarget.Clouds[i],"H-L mapping");
                Program.Check(pen.Run(Key(Keys.Shift|StyleKeys.Home[i+5]))=="Hatch: "+new[]{"Insulation","Brick","Grid","Dots"}[i],"Shift H-L mapping");
                Program.Check(!StyleKeys.Match(Keys.Control|StyleKeys.Home[i+5],true,out _) && !StyleKeys.Match(StyleKeys.Home[i+5],true,out _),"typing cloud chord");
            }
            Program.Check(new SelectionTarget(new[]{Owner(new LineShape())}).Run(Key(Keys.J))==null && new PenTarget(Drawing(0)).Run(Key(Keys.L))==null && new PenTarget(Drawing(5)).Run(Key(Keys.K))==null,"ineligible/text keys consumed");
        });
        Program.Test("cloud selection makes one shared undo record, repeated choice is a no-op and redo restores it",()=>
        {
            // GH_Document requires Rhino's native runtime; exercise its transaction boundary with the
            // existing recorder seam, retaining the real snapshot actions for undo/redo.
            var a=Owner(Frame()); var b=Owner(new TextShape { Text="hidden border" },new LineShape());
            int records=0; ShapeEditUndoAction[] actions=null;
            Func<HopperDraw[],string,bool> record=(owners,name)=>{records++; actions=owners.Select(o=>new ShapeEditUndoAction(o,new IGH_DocumentObject[0])).ToArray(); return true;};
            Func<DrawShape,bool> differs=s=>s.SupportsCloud && s.CloudSize!=48;
            Action<DrawShape> edit=s=>s.CloudSize=48;
            Program.Check(AnnotationCommands.Apply(new[]{a,b},"Change revision cloud",differs,edit,record) && records==1 && actions.Length==2,"shared cloud transaction");
            Program.Check(!AnnotationCommands.Apply(new[]{a,b},"Change revision cloud",differs,edit,record) && records==1 && b.Shapes[1].CloudSize==0,"cloud no-op/ineligible");
            actions[0].Swap(null,a); actions[1].Swap(null,b); Program.Check(a.Shapes[0].CloudSize==0 && b.Shapes[0].CloudSize==0,"cloud undo");
            actions[0].Swap(null,a); actions[1].Swap(null,b); Program.Check(a.Shapes[0].CloudSize==48 && b.Shapes[0].CloudSize==48,"cloud redo");
        });
        Program.Test("cloud menu states and bar cells share command, tool eligibility and key caps",()=>
        {
            foreach(int mode in Enumerable.Range(0,6))
            {
                var owner=Drawing(mode);
                using(var menu=new ContextMenuStrip())
                {
                    AnnotationMenuBuilder.Drawing(menu.Items,owner);
                    var cloud=Find(Find(Find(menu.Items,"Pen").DropDownItems,"Stroke").DropDownItems,"Revision cloud");
                    Program.Check(cloud.Enabled==(mode!=0) && cloud.Text=="&Revision cloud" && Find(cloud.DropDownItems,"Off").Checked==(mode!=0),"pen menu state "+mode);
                    if(mode!=0) Find(cloud.DropDownItems,"Small").PerformClick();
                    Program.Check(mode==0 || (mode==5?owner.TextPen:owner.PenStyle).CloudSize==24,"menu cloud command");
                }
            }
            var frame=Frame(); frame.CloudSize=48;
            using(var menu=new ContextMenuStrip())
            {
                AnnotationMenuBuilder.Selection(menu.Items,new[]{Owner(frame,new LineShape())});
                var cloud=Find(Find(menu.Items,"Stroke").DropDownItems,"Revision cloud");
                Program.Check(Find(cloud.DropDownItems,"Medium").Checked,"eligible selection check");
            }
            using(var host=new Form { ClientSize=new Size(1000,700),Location=new System.Drawing.Point(-32000,-32000),ShowInTaskbar=false })
            using(var bar=new StyleBar())
            {
                bar.Attach(host); host.Show(); var owner=Drawing(2);
                bar.Source=()=>new StyleBarState { Target=new PenTarget(owner),Tool=owner.DrawMode }; bar.Sync();
                bar.Activate(bar.Items.Single(i=>i.Part==StyleBar.Part.Stroke));
                Program.Check(string.Concat(bar.Flyout.Cells.Where(c=>c.Tip!=null && c.Tip.StartsWith("Cloud:")).Select(c=>c.Legend))=="HJKL","cloud key caps");
                bar.Choose(bar.Flyout.Cells.Single(c=>c.Tip=="Cloud: Small"));
                Program.Check(bar.FlyoutOpen && owner.PenStyle.CloudSize==24 && bar.Flyout.Cells.Single(c=>c.Tip=="Cloud: Small").Checked,"bar keep/check");
                owner.SetTool(0); bar.Sync();
                Program.Check(bar.Flyout.Cells.Where(c=>c.Tip!=null && c.Tip.StartsWith("Cloud applies")).Count(c=>c.Click==null)==4,"line bar cloud enabled");
            }
        });
        Program.Test("cloud text style-only commit and editor frame scale at multiple zooms",()=>
        {
            var text=new TextShape { Text="hello",Anchor=new Point3d(60,60,0),BoxWidth=120 }; var owner=Owner(text); var doc=new object(); int records=0;
            var session=new TextEditSession(owner,doc,text,false); var target=new TextEditTarget(session,null,()=>{}); target.Cloud(48);
            Program.Check(text.CloudSize==0 && session.Commit(doc,true,_=>{records++;return true;}) && records==1 && text.CloudSize==48 && text.StrokeVisible,"style-only cloud commit");
            session=new TextEditSession(owner,doc,text,false); target=new TextEditTarget(session,null,()=>{}); target.Cloud(48);
            Program.Check(!session.Commit(doc,true,_=>{records++;return true;}) && records==1,"cloud no-op commit");
            using(var canvas=new GH_Canvas { Size=new Size(900,600) })
            {
                var handle=canvas.Handle; typeof(HopperDraw).GetField("_isActivated",Private).SetValue(owner,true);
                var controller=CanvasAnnotationController.Register(canvas,owner);
                try
                {
                    foreach(float zoom in new[]{.5f,1f,2f})
                    {
                        canvas.Viewport.Zoom=zoom; canvas.Viewport.ComputeProjection(); controller.BeginText(owner,text,false);
                        using(var bitmap=new Bitmap(900,600)) using(var g=Graphics.FromImage(bitmap)) controller.PaintEditorFrame(g);
                        var sample=(FrameShape)typeof(CanvasAnnotationController).GetField("_frameSample",Private).GetValue(controller);
                        Program.Check(sample.CloudSize==48*zoom && sample.HatchScale==text.HatchScale*zoom,"editor sample scale");
                        controller.CancelOwner(owner,false);
                    }
                }
                finally { controller.Remove(owner); }
            }
        });
        Program.Test("real renderer clips hatch to cloud and supports dashed scallops",()=>
        {
            var frame=Frame(); frame.CloudSize=48; frame.FillColor=Color.Blue; frame.Hatch=HatchPattern.Diagonal; frame.HatchBackground=Color.White; frame.StrokeVisible=false;
            using(var b=new Bitmap(180,180)) using(var g=Graphics.FromImage(b)) using(var path=AnnotationGeometry.Boundary(frame))
            {
                ShapeRenderer.Render(g,frame,Color.Black,8); int expanded=0;
                for(int y=0;y<180;y++) for(int x=0;x<180;x++) if(b.GetPixel(x,y).A>0)
                { Program.Check(path.IsVisible(x,y) || path.IsVisible(x+.5f,y+.5f),"hatch outside cloud"); if(y<40) expanded++; }
                Program.Check(expanded>0 && b.GetPixel(64,20).A==0,"hatch not expanded/clipped");
                g.Clear(Color.Transparent); frame.FillColor=null; frame.HatchBackground=null; frame.StrokeVisible=true; frame.LineType=DashStyle.Dash;
                ShapeRenderer.Render(g,frame,Color.Red,2);
                Program.Check(Enumerable.Range(20,25).Any(y=>b.GetPixel(64,y).A>0),"dashed cloud missing");
            }
        });
        string output=Environment.GetEnvironmentVariable("HOPPERDRAW_DEMO_OUT");
        if(!string.IsNullOrWhiteSpace(output)) Program.Test("render revision cloud demo",()=>Demo(output));
    }

    // Optional deterministic artifact from the same renderer used on the Grasshopper canvas.
    private static void Demo(string output)
    {
        using(var bitmap=new Bitmap(1400,960)) using(var g=Graphics.FromImage(bitmap))
        using(var title=new Font("Segoe UI",28,FontStyle.Bold)) using(var label=new Font("Segoe UI",14)) using(var note=new Font("Segoe UI",11))
        using(var ink=new SolidBrush(Color.FromArgb(45,49,52))) using(var grid=new Pen(Color.FromArgb(18,75,75,75)))
        {
            g.Clear(Color.FromArgb(212,208,200)); g.SmoothingMode=SmoothingMode.AntiAlias;
            for(int x=0;x<1400;x+=20) g.DrawLine(grid,x,0,x,960); for(int y=0;y<960;y+=20) g.DrawLine(grid,0,y,1400,y);
            g.DrawString("Revision clouds",title,ink,54,30);
            g.DrawString("Closed annotations · editable base vertices · canvas-sized scallops",label,ink,58,82);
            void Label(string s,float x,float y) => g.DrawString(s,label,ink,x,y);
            void Render(DrawShape shape) => ShapeRenderer.Render(g,shape,Color.FromArgb(50,55,60),8);
            var red=Frame(95,190,300,150); red.CloudSize=48; red.OverrideColor=Color.FromArgb(210,48,48); Render(red);
            Render(new TextShape { Text="Rev A",Anchor=new Point3d(164,230,0),SizeMultiplier=2,Bold=true,TextColorOverride=red.OverrideColor });
            Label("Frame · Medium",90,140);
            var hatch=new PolylineShape(new[]{new Point3d(540,200,0),new Point3d(810,190,0),new Point3d(850,310,0),new Point3d(690,355,0),new Point3d(535,305,0)}.ToList(),true)
                { CloudSize=48,OverrideColor=Color.FromArgb(33,106,164),FillColor=Color.FromArgb(120,33,106,164),Hatch=HatchPattern.Diagonal,HatchScale=1.3,HatchBackground=Color.FromArgb(155,212,232,245) }; Render(hatch);
            Label("Closed polyline · clipped hatch",510,140);
            var curve=new CurveShape(new[]{new Point3d(1010,225,0),new Point3d(1120,190,0),new Point3d(1265,228,0),new Point3d(1255,310,0),new Point3d(1135,345,0),new Point3d(1015,300,0)}.ToList(),true)
                { CloudSize=48,OverrideColor=Color.FromArgb(65,125,70) }; Render(curve); Label("Closed curve / freehand",980,140);
            var text=new TextShape { Text="Text box cloud\nBorder follows the text",Anchor=new Point3d(100,455,0),BoxWidth=360,SizeMultiplier=1.25,CloudSize=48,StrokeVisible=true,OverrideColor=Color.FromArgb(155,80,37),FillColor=Color.FromArgb(95,255,242,210) }; Render(text);
            Label("Text · Medium",90,410);
            var selected=Frame(640,465,260,100); selected.CloudSize=48; selected.OverrideColor=Color.FromArgb(109,77,156); Render(selected);
            ShapeRenderer.Handles(g,selected,-1,true,1); ShapeRenderer.Handles(g,selected,-1,true,1,false);
            Label("Selected · handles at real corners",600,410);
            g.DrawString("Cloud size changes the outline.\nThe frame and its handles\nstay fully editable.",label,ink,1020,460);
            Label("Same frame, same base stroke width (8 canvas units)",58,625);
            for(int i=0;i<4;i++)
            {
                float x=90+i*330; var sample=Frame(x,750,240,96); sample.CloudSize=StyleTarget.Clouds[i]; Render(sample);
                Label(i==0?"Off":new[]{"","S · 24","M · 48","L · 96"}[i],x,670);
            }
            g.DrawString("150° outward lobes · corner cusps · Off / Small / Medium / Large",note,ink,58,925);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))); bitmap.Save(output,ImageFormat.Png);
        }
    }
}
