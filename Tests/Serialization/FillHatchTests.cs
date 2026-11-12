using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using hopperdraw;
using Rhino.Geometry;

internal static class FillHatchTests
{
    private static FrameShape Frame() => new FrameShape(new Point3d(20,20,0),new Point3d(100,20,0),new Point3d(100,100,0),new Point3d(20,100,0));
    private static HopperDraw Owner(DrawShape shape) { var owner=new HopperDraw(); owner.Shapes.Add(shape); owner.SelectedShapeIndices.Add(0); return owner; }
    private static GH_IWriter Record(GH_LooseChunk root) => (GH_IWriter)root.FindChunk("HopperDraw").FindChunk("Shapes").FindChunk("Shape",0);
    private static ToolStripMenuItem Find(ToolStripItemCollection items,string name)=>items.OfType<ToolStripMenuItem>().Single(i=>i.Text==name);
    private static Bitmap Paint(DrawShape shape)
    {
        var b=new Bitmap(130,130); using(var g=Graphics.FromImage(b)) { g.Clear(Color.Transparent); ShapeRenderer.Render(g,shape,Color.Black,8); } return b;
    }
    internal static void Run()
    {
        Program.Test("all additive style keys default silently and preserve indexed extensions",()=>
        {
            var root=Program.Save(Owner(Frame())); var r=Record(root);
            foreach(var key in new[]{"StrokeVisible","HasFillColor","FillColor","FillOpacity","Hatch","HatchScale","HasHatchBackground","HatchBackground"}) r.RemoveItem(key);
            r.SetString("FillColor",9,"indexed extension"); r.CreateChunk("Motif",2).SetString("Data","future");
            var loaded=Program.Load(root); var shape=loaded.Shapes[0];
            Program.Check(loaded.LoadWarning==null && shape.StrokeVisible && shape.FillColor==null && shape.FillOpacity==1 && shape.Hatch==HatchPattern.None && shape.HatchScale==1 && shape.HatchBackground==null,"missing fields warned or defaulted incorrectly");
            foreach(bool binary in new[]{false,true})
            {
                var saved=(GH_IReader)Record(Program.Save(Program.RoundTrip(loaded,binary)));
                Program.Check(saved.GetString("FillColor",9)=="indexed extension" && saved.FindChunk("Motif",2).GetString("Data")=="future","extension lost");
            }
        });
        Program.Test("present invalid fill fields remain opaque without rewriting payload",()=>
        {
            Action<GH_IWriter>[] corrupt={
                r=>r.SetString("FillOpacity","bad"),r=>r.SetString("HatchScale","bad"),r=>r.SetString("StrokeVisible","bad"),r=>r.SetString("HasFillColor","bad"),r=>r.SetString("FillColor","bad"),
                r=>r.SetString("HasHatchBackground","bad"),r=>r.SetString("HatchBackground","bad"),r=>r.SetString("Hatch","bad"),
                r=>r.SetDouble("FillOpacity",double.NaN),r=>r.SetDouble("FillOpacity",double.PositiveInfinity),r=>r.SetDouble("FillOpacity",.01),r=>r.SetDouble("FillOpacity",1.1),
                r=>r.SetDouble("HatchScale",0),r=>r.SetDouble("HatchScale",-1),r=>r.SetDouble("HatchScale",double.PositiveInfinity),r=>r.SetDouble("HatchScale",double.NaN),r=>r.SetInt32("Hatch",11),
                r=>{r.SetBoolean("HasFillColor",true);r.RemoveItem("FillColor");},r=>{r.SetBoolean("HasHatchBackground",true);r.RemoveItem("HatchBackground");}
            };
            foreach(var damage in corrupt)
            {
                var root=Program.Save(Owner(Frame())); var r=Record(root);
                foreach(var key in new[]{"StrokeVisible","HasFillColor","FillColor","HasHatchBackground","HatchBackground","FillOpacity","HatchScale","Hatch"}) r.RemoveItem(key);
                damage(r); var raw=ShapeSerializer.Capture((GH_IReader)r).Serialize_Xml(); var loaded=Program.Load(root);
                Program.Check(loaded.Shapes[0] is OpaqueShape && loaded.LoadWarning!=null,"invalid fill accepted");
                Program.Check(ShapeSerializer.Capture((GH_IReader)Record(Program.Save(loaded))).Serialize_Xml()==raw,"opaque style changed");
            }
        });
        Program.Test("nested clone duplicate move cancel and undo keep complete leaf styles",()=>
        {
            var frame=Frame(); frame.OverrideColor=Color.Purple; frame.ThicknessMultiplier=3; frame.LineType=DashStyle.Dot; frame.StrokeVisible=false;
            frame.FillColor=Color.FromArgb(140,20,40,60); frame.FillOpacity=.25; frame.Hatch=HatchPattern.Brick; frame.HatchScale=4; frame.HatchBackground=Color.Gold;
            var inner=new DrawShapeGroup(); inner.Children.Add(frame); var group=new DrawShapeGroup(); group.Children.Add(inner); var owner=Owner(group);
            var clone=(DrawShapeGroup)group.Clone(); Program.Check(frame.SameStyle(((DrawShapeGroup)clone.Children[0]).Children[0]),"clone lost styles");
            AnnotationCommands.Duplicate(new[]{owner});
            DrawShape Leaf()=>((DrawShapeGroup)((DrawShapeGroup)owner.Shapes[1]).Children[0]).Children[0];
            Program.Check(frame.SameStyle(Leaf()) && Leaf().PersistentId!=frame.PersistentId,"duplicate lost style or ID");
            var move=new AnnotationMove(new[]{owner},new IGH_DocumentObject[0],null); move.Translate(new PointF(30,50),false);
            Program.Check(frame.SameStyle(Leaf()),"move lost styles"); move.Revert(false); Program.Check(frame.SameStyle(Leaf()),"cancel lost styles");
            var undo=new ShapeEditUndoAction(owner,new IGH_DocumentObject[0]); Leaf().FillColor=Color.Red; undo.Swap(null,owner);
            Program.Check(frame.SameStyle(Leaf()),"undo lost styles"); undo.Swap(null,owner); Program.Check(Leaf().FillColor==Color.Red,"redo lost styles");
        });
        Program.Test("bitmap solid fill alpha and Stroke None have exact interior and no outline",()=>
        {
            var frame=Frame(); frame.StrokeVisible=false; frame.FillColor=Color.Red;
            using(var b=Paint(frame)) { Program.Check(b.GetPixel(60,60).ToArgb()==Color.Red.ToArgb(),"interior not red"); Program.Check(b.GetPixel(17,60).A==0,"hidden outline painted"); }
            frame.FillColor=Color.FromArgb(200,255,0,0); frame.FillOpacity=.25;
            using(var b=Paint(frame)) Program.Check(Math.Abs(b.GetPixel(60,60).A-50)<=1,"alpha did not multiply");
            frame.FillColor=null; using(var b=Paint(frame)) Program.Check(b.GetPixel(60,60).A==0 && b.GetPixel(20,60).A==0,"None fill/stroke painted");
        });
        Program.Test("every hatch is clipped with canvas phase scale and optional background",()=>
        {
            var frame=Frame(); frame.StrokeVisible=false; frame.FillColor=Color.Blue;
            foreach(HatchPattern hatch in Enum.GetValues(typeof(HatchPattern)))
            {
                if(hatch==HatchPattern.None) continue; frame.Hatch=hatch;
                using(var b=Paint(frame))
                {
                    int painted=0; for(int y=0;y<130;y++) for(int x=0;x<130;x++) if(b.GetPixel(x,y).A>0)
                    { Program.Check(x>=20 && x<=100 && y>=20 && y<=100,"hatch outside clip: "+hatch); painted++; }
                    Program.Check(painted>0 && painted<6400,"empty or solid motif: "+hatch);
                }
            }
            frame.Hatch=HatchPattern.Vertical; frame.HatchScale=1;
            using(var one=Paint(frame))
            {
                frame.HatchScale=2; using(var two=Paint(frame))
                {
                    Program.Check(one.GetPixel(36,55).A>0 && two.GetPixel(36,55).A==0,"scale did not change spacing");
                    Program.Check(one.GetPixel(48,55).A>0 && two.GetPixel(48,55).A>0,"phase shifted");
                }
            }
            frame.HatchBackground=Color.White; frame.FillOpacity=.5;
            using(var b=Paint(frame)) Program.Check(b.GetPixel(55,55).R==255 && Math.Abs(b.GetPixel(55,55).A-128)<=1,"background missing/opacity wrong");
            frame.Hatch=HatchPattern.None; frame.FillOpacity=1;
            using(var b=Paint(frame)) Program.Check(b.GetPixel(55,55).ToArgb()==Color.Blue.ToArgb(),"background used for solid fill");
            frame.FillColor=null; using(var b=Paint(frame)) Program.Check(b.GetPixel(55,55).A==0,"dormant hatch painted");
        });
        Program.Test("hatch paint restores graphics clip and canvas phase survives viewport cropping",()=>
        {
            var frame=Frame(); frame.FillColor=Color.Black; frame.Hatch=HatchPattern.Grid; frame.StrokeVisible=false;
            using(var b=new Bitmap(130,130)) using(var g=Graphics.FromImage(b))
            {
                g.SetClip(new Rectangle(40,30,80,80)); ShapeRenderer.Render(g,frame,Color.Black,8);
                g.FillRectangle(Brushes.Red,110,40,5,5); Program.Check(b.GetPixel(112,42).R==255,"shape clip leaked");
                using(var reference=Paint(frame)) Program.Check(b.GetPixel(48,55)==reference.GetPixel(48,55),"cropping changed hatch phase");
            }
        });
        Program.Test("fill picking includes hatch gaps while invisible interior stays unpickable",()=>
        {
            var frame=Frame(); var center=new PointF(55,55);
            Program.Check(!AnnotationGeometry.Hit(frame,center,2,8),"unfilled interior hit"); frame.FillColor=Color.Blue; frame.Hatch=HatchPattern.Grid;
            Program.Check(AnnotationGeometry.Hit(frame,center,2,8) && frame.HitTest(center,2,8).hit && Owner(frame).HitTest(center).shapeIndex==0,"filled gap missed by a picking path");
            Program.Check(AnnotationGeometry.InRectangle(frame,new RectangleF(45,45,10,10),true,8),"crossing wholly inside fill missed");
            frame.StrokeVisible=false; frame.FillColor=null;
            Program.Check(!AnnotationGeometry.Hit(frame,center,2,100) && AnnotationGeometry.Hit(frame,new PointF(20,55),2,100),"invisible picking not boundary only");
            Program.Check(!AnnotationGeometry.Hit(frame,new PointF(15,55),2,100),"hidden stroke enlarged picking");
            Program.Check(AnnotationGeometry.InRectangle(frame,new RectangleF(20,20,80,80),false,100),"hidden stroke enlarged window envelope");
            var owner=Owner(frame); owner.ClearSelection(); owner.SelectShapesInRectangle(new RectangleF(20,20,80,80));
            Program.Check(owner.HasSelection,"fallback window diverges from shared geometry");
            frame.StrokeVisible=true; Program.Check(!AnnotationGeometry.InRectangle(frame,new RectangleF(20,20,80,80),false,8),"visible stroke omitted from envelope");
        });
        Program.Test("backdrop layer filled interiors stay empty canvas; outline and overlay interiors pick",()=>
        {
            var frame=Frame(); frame.FillColor=Color.Blue; var center=new PointF(55,55); var edge=new PointF(20,55);
            var owner=Owner(frame); owner.DrawOrder=0;
            Program.Check(AnnotationPicking.Hit(new[]{owner},center,false).owner==null,"backdrop interior captured the press");
            Program.Check(AnnotationPicking.Hit(new[]{owner},edge,false).owner==owner,"backdrop outline not pickable");
            Program.Check(owner.HitTest(center).shapeIndex<0 && owner.HitTest(edge).shapeIndex==0,"fallback picking ignores backdrop rule");
            Program.Check(!AnnotationGeometry.InRectangle(frame,new RectangleF(45,45,10,10),true,8,false),"crossing inside backdrop selected it");
            owner.ClearSelection(); owner.SelectShapesInRectangle(new RectangleF(45,45,10,10),false,true);
            Program.Check(!owner.HasSelection,"fallback crossing inside backdrop selected it");
            var group=new DrawShapeGroup(); group.Children.Add(frame.Clone()); owner.Shapes[0]=group;
            Program.Check(AnnotationPicking.Hit(new[]{owner},center,false).owner==null,"grouped backdrop interior captured the press");
            owner.Shapes[0]=frame; owner.DrawOrder=1;
            Program.Check(AnnotationPicking.Hit(new[]{owner},center,false).owner==owner,"overlay interior not pickable");
        });
        Program.Test("self intersecting closed spline paint and pick share winding boundary",()=>
        {
            var curve=new CurveShape(new[]{new Point3d(25,25,0),new Point3d(100,100,0),new Point3d(25,100,0),new Point3d(100,25,0),new Point3d(60,115,0)}.ToList(),true) { StrokeVisible=false,FillColor=Color.Red };
            using(var b=Paint(curve)) using(var path=AnnotationGeometry.Boundary(curve))
            {
                Program.Check(path.FillMode==FillMode.Winding,"wrong winding");
                for(int y=5;y<125;y+=7) for(int x=5;x<125;x+=7)
                {
                    var p=new PointF(x+.5f,y+.5f);
                    if(AnnotationGeometry.Interior(curve,p)!=path.IsVisible(p)) using(var pen=new Pen(Color.Black,1f)) Program.Check(path.IsOutlineVisible(p,pen),"pick/fill path disagree away from the flattened boundary at "+p);
                    if(b.GetPixel(x,y).A>0) Program.Check(AnnotationGeometry.Hit(curve,new PointF(x,y),1,8),"painted pixel unpickable");
                }
            }
        });
        Program.Test("fill menu eligibility checks mixed leaves and full-style pen copying",()=>
        {
            var frame=Frame(); frame.FillColor=Color.Red; frame.StrokeVisible=false; frame.Hatch=HatchPattern.Brick; frame.HatchBackground=Color.White;
            var line=new LineShape(new Point3d(0,0,0),new Point3d(50,0,0)); var owner=Owner(frame); owner.Shapes.Add(line); owner.SelectedShapeIndices.Add(1);
            using(var menu=new ContextMenuStrip())
            {
                AnnotationMenuBuilder.Selection(menu.Items,new[]{owner}); var fill=Find(menu.Items,"Fill");
                Program.Check(fill.Enabled && Find(fill.DropDownItems,"Red").Checked,"eligible subset checks wrong");
                Program.Check(!Find(menu.Items,"Use selection style for drawing").Enabled,"mixed full style enabled");
                Find(Find(fill.DropDownItems,"Opacity").DropDownItems,"25%").PerformClick();
                Program.Check(frame.FillOpacity==.25 && line.FillOpacity==1,"ineligible leaf changed");
                Find(Find(fill.DropDownItems,"Hatch").DropDownItems,"Dots").PerformClick(); Program.Check(frame.Hatch==HatchPattern.Dots && line.Hatch==HatchPattern.None,"hatch command eligibility");
            }
            owner.SelectedShapeIndices.Clear(); owner.SelectedShapeIndices.Add(1);
            using(var menu=new ContextMenuStrip()) { AnnotationMenuBuilder.Selection(menu.Items,new[]{owner}); var fill=Find(menu.Items,"Fill"); Program.Check(!fill.Enabled && fill.ToolTipText=="Close the shape to fill it.","open fill enabled"); }
            owner.SelectedShapeIndices.Clear(); owner.SelectedShapeIndices.Add(0);
            using(var menu=new ContextMenuStrip()) { AnnotationMenuBuilder.Selection(menu.Items,new[]{owner}); Find(menu.Items,"Use selection style for drawing").PerformClick(); Program.Check(frame.SameStyle(owner.PenStyle),"pen tuple incomplete"); }
            using(var menu=new ContextMenuStrip()) { AnnotationMenuBuilder.Drawing(menu.Items,owner); Program.Check(!Find(Find(menu.Items,"Pen").DropDownItems,"Fill").Enabled,"line pen fill enabled"); Program.Check(Find(Find(Find(menu.Items,"Pen").DropDownItems,"Smoothing").DropDownItems,"Medium").Checked,"smoothing default"); }
            owner.SetTool(4); using(var menu=new ContextMenuStrip()) { AnnotationMenuBuilder.Drawing(menu.Items,owner); Program.Check(Find(Find(menu.Items,"Pen").DropDownItems,"Fill").Enabled && Find(Find(menu.Items,"Tool").DropDownItems,"Freehand").Checked,"freehand tool/pen missing"); }
        });
        Program.Test("open and closed fill menus compute checks only over eligible leaves",()=>
        {
            var vertices=new[]{new Point3d(20,20,0),new Point3d(100,20,0),new Point3d(70,100,0)}.ToList();
            var open=new PolylineShape(vertices); var closed=new CurveShape(vertices,true) { FillColor=Color.Blue,FillOpacity=.5,Hatch=HatchPattern.Grid };
            var owner=Owner(open); owner.Shapes.Add(closed); owner.SelectedShapeIndices.Add(1);
            using(var menu=new ContextMenuStrip())
            {
                AnnotationMenuBuilder.Selection(menu.Items,new[]{owner}); var fill=Find(menu.Items,"Fill");
                Program.Check(Find(fill.DropDownItems,"Blue").Checked && Find(Find(fill.DropDownItems,"Opacity").DropDownItems,"50%").Checked,"open leaf affected eligible checks");
                Find(fill.DropDownItems,"None").PerformClick(); Program.Check(closed.FillColor==null && open.FillColor==null && closed.Hatch==HatchPattern.Grid,"None changed dormant hatch");
            }
            var other=Frame(); other.FillColor=Color.Red; owner.Shapes.Add(other); owner.SelectedShapeIndices.Add(2);
            using(var menu=new ContextMenuStrip())
            {
                AnnotationMenuBuilder.Selection(menu.Items,new[]{owner}); var fill=Find(menu.Items,"Fill");
                Program.Check(!fill.DropDownItems.OfType<ToolStripMenuItem>().Any(i=>i.Checked),"mixed eligible fill has check");
                Program.Check(!Find(Find(fill.DropDownItems,"Hatch").DropDownItems,"None").Checked,"mixed hatch has check");
            }
            foreach(int mode in new[]{1,2,3,4})
            {
                owner.SetTool(mode); using(var menu=new ContextMenuStrip())
                {
                    AnnotationMenuBuilder.Drawing(menu.Items,owner); var fill=Find(Find(menu.Items,"Pen").DropDownItems,"Fill");
                    Program.Check(fill.Enabled && fill.ToolTipText.Contains("closed"),"eligible pen tool disabled or unexplained");
                }
            }
        });
        Program.Test("multi-owner fill transaction records once and repeat no-op records nothing",()=>
        {
            var a=Owner(Frame()); var b=Owner(Frame());
            b.Shapes.Add(new LineShape(new Point3d(0,0,0),new Point3d(50,0,0))); b.SelectedShapeIndices.Add(1);
            int records=0; ShapeEditUndoAction[] actions=null;
            Func<HopperDraw[],string,bool> record=(owners,name)=>{records++; actions=owners.Select(o=>new ShapeEditUndoAction(o,new IGH_DocumentObject[0])).ToArray(); return true;};
            Func<DrawShape,bool> differs=l=>l.SupportsFill && l.FillColor?.ToArgb()!=Color.Red.ToArgb();
            Program.Check(AnnotationCommands.Apply(new[]{a,b},"Fill",differs,l=>l.FillColor=Color.Red,record) && records==1 && actions.Length==2,"transaction did not capture both owners once");
            Program.Check(!AnnotationCommands.Apply(new[]{a,b},"Fill",differs,l=>l.FillColor=Color.Red,record) && records==1,"no-op recorded");
            Program.Check(b.Shapes[1].FillColor==null,"open leaf restyled");
            actions[0].Swap(null,a); actions[1].Swap(null,b); Program.Check(a.Shapes[0].FillColor==null && b.Shapes[0].FillColor==null,"transaction undo lost fill");
        });
        Program.Test("invisible completion has no shape or undo and pen fill applies only when closed",()=>
        {
            var owner=new HopperDraw(); owner.PenStyle.StrokeVisible=false; owner.PenStyle.FillColor=Color.Red;
            owner.CompleteDraft(new CurveShape(new[]{new Point3d(0,0,0),new Point3d(50,50,0)}.ToList()));
            Program.Check(owner.Shapes.Count==0 && owner.PenNoticeUntil>DateTime.UtcNow,"invisible completion accepted or no HUD");
            owner.CompleteDraft(Frame()); Program.Check(owner.Shapes.Count==1 && owner.Shapes[0].FillColor==Color.Red && !owner.Shapes[0].StrokeVisible,"closed pen fill lost");
            owner.PenStyle.FillColor=Color.FromArgb(1,255,0,0); owner.PenStyle.FillOpacity=.1;
            owner.CompleteDraft(Frame()); Program.Check(owner.Shapes.Count==1,"zero effective alpha created invisible shape");
            var loaded=Program.RoundTrip(owner,true); Program.Check(loaded.PenStyle.FillColor==null && loaded.Smoothing==SmoothingLevel.Medium,"session pen persisted");
        });
        Program.Test("zero-area closed fill-only shapes are not paint, are rejected and get a ghost",()=>
        {
            var flat=new PolylineShape(new[]{new Point3d(20,50,0),new Point3d(60,50,0),new Point3d(100,50,0)}.ToList(),true) { StrokeVisible=false,FillColor=Color.Red };
            Program.Check(flat.SupportsFill && !flat.HasArea && !flat.VisibleFill && !flat.HasPaint(Color.Black),"degenerate fill counted as paint");
            Program.Check(!AnnotationGeometry.Interior(flat,new PointF(60,50)),"degenerate interior pickable");
            var thin=new FrameShape(new Point3d(20,50,0),new Point3d(100,50,0),new Point3d(100,50,0),new Point3d(20,50,0)) { StrokeVisible=false,FillColor=Color.Red };
            Program.Check(!thin.VisibleFill,"zero-height frame counted as filled");
            var owner=new HopperDraw(); owner.PenStyle.StrokeVisible=false; owner.PenStyle.FillColor=Color.Red;
            owner.CompleteDraft(new PolylineShape(flat.Points.ToList(),true)); Program.Check(owner.Shapes.Count==0,"invisible degenerate shape accepted");
            using(var b=new Bitmap(130,130)) using(var g=Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent); ShapeRenderer.Ghost(g,flat,Color.Black,1);
                int ghost=0; for(int x=20;x<=100;x++) if(b.GetPixel(x,50).A>0) ghost++;
                Program.Check(ghost>0,"degenerate fill-only shape has no ghost");
                var bad=new PolylineShape(new[]{new Point3d(double.NaN,0,0),new Point3d(1,1,0),new Point3d(2,0,0)}.ToList(),true) { StrokeVisible=false };
                ShapeRenderer.Ghost(g,bad,Color.Black,1); // must not throw into GH paint
            }
        });
        Program.Test("zoomed-out and dense hatches stay bounded and canvas-anchored",()=>
        {
            var frame=new FrameShape(new Point3d(0,0,0),new Point3d(4000,0,0),new Point3d(4000,4000,0),new Point3d(0,4000,0)) { StrokeVisible=false,FillColor=Color.Blue };
            foreach(HatchPattern hatch in Enum.GetValues(typeof(HatchPattern)))
            {
                if(hatch==HatchPattern.None) continue; frame.Hatch=hatch;
                foreach(var setup in new[]{ (zoom:.2f,scale:.5), (zoom:.2f,scale:2.0), (zoom:1f,scale:.5) })
                {
                    frame.HatchScale=setup.scale;
                    using(var b=new Bitmap(800,800)) using(var g=Graphics.FromImage(b))
                    {
                        g.Clear(Color.Transparent); g.ScaleTransform(setup.zoom,setup.zoom);
                        var watch=System.Diagnostics.Stopwatch.StartNew(); ShapeRenderer.Render(g,frame,Color.Black,8); watch.Stop();
                        Program.Check(watch.ElapsedMilliseconds<400,"hatch paint too slow: "+hatch+" "+setup+" "+watch.ElapsedMilliseconds+"ms");
                        int painted=0; for(int y=0;y<800;y+=7) for(int x=0;x<800;x+=7) if(b.GetPixel(x,y).A>0) painted++;
                        Program.Check(painted>0,"dense hatch painted nothing: "+hatch+" "+setup);
                    }
                }
            }
            // Below MinScreenCell the pattern is a uniform tint.
            frame.Hatch=HatchPattern.Insulation; frame.HatchScale=.5;
            using(var b=new Bitmap(200,200)) using(var g=Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent); g.ScaleTransform(.1f,.1f); ShapeRenderer.Render(g,frame,Color.Black,8);
                var tinted=b.GetPixel(100,100); Program.Check(Math.Abs(tinted.A-(int)Math.Round(255*HatchRenderer.TintFactor))<=1 && tinted.B>=250 && tinted.R<=5,"sub-pixel hatch not tinted: "+tinted);
            }
            // Tile path keeps canvas phase: panning the viewport by whole cells reproduces the same pixels.
            frame.Hatch=HatchPattern.Brick; frame.HatchScale=2;
            Bitmap Shot(float dx) { var b=new Bitmap(500,500); using(var g=Graphics.FromImage(b)) { g.Clear(Color.Transparent); g.ScaleTransform(.25f,.25f); g.TranslateTransform(dx,0); ShapeRenderer.Render(g,frame,Color.Black,8); } return b; }
            using(var a=Shot(-240)) using(var c=Shot(-480))
            {
                Program.Check(HatchRenderer.CachedTileCount>0,"dense brick did not use the tile path");
                int same=0,total=0; for(int y=20;y<480;y+=3) for(int x=20;x<400;x+=3) { total++; if(Math.Abs(a.GetPixel(x+60,y).A-c.GetPixel(x,y).A)<=40) same++; }
                Program.Check(same>total*9/10,"tile phase not anchored to canvas: "+same+"/"+total);
            }
        });
    }
}
