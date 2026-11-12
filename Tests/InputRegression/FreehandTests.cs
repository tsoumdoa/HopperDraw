using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using hopperdraw;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI;
using System.Windows.Forms;
using Rhino.Geometry;
using Point=System.Drawing.Point;

internal static class FreehandTests
{
    private static Point3d P(double x,double y=0)=>new Point3d(x,y,0);
    internal static void Run(Action<string,Action> test,Action<bool,string> check)
    {
        test("freehand smoothing deterministic bounded reduced and open endpoints preserved",()=>
        {
            var raw=Enumerable.Range(0,2000).Select(i=>P(i*.15,30*Math.Sin(i*.003)+Math.Sin(i)*.1)).ToList();
            foreach(SmoothingLevel level in Enum.GetValues(typeof(SmoothingLevel)))
            {
                var a=FreehandStroke.Process(raw,1,level,1); var b=FreehandStroke.Process(raw,1,level,1);
                check(a!=null && !a.Closed && a.Points.SequenceEqual(b.Points),"nondeterministic or unexpectedly closed");
                check(a.Points.Count<raw.Count/4 && a.Points.Count<=FreehandStroke.MaxOutput,"not reduced or bounded");
                check(a.Points.First()==raw.First() && a.Points.Last()==raw.Last(),"open endpoints changed");
            }
        });
        test("freehand close ring appears only when release would close and preview does not copy samples",()=>
        {
            var draft=new AnnotationDraft(); draft.Down(4,P(0),Point.Empty,1,false,1);
            PointF last=new PointF(0,0);
            void To(double x,double y) { var p=new PointF((float)x,(float)y); draft.Sample(P(x,y),last,p); last=p; }
            To(3,0); To(3,3); To(0,3);
            check(!draft.NearStart(P(1,1),1),"ring flashed for a tiny wiggle near the start");
            check(FreehandStroke.Process(draft.Points.Concat(new[]{P(1,1)}).ToList(),1,SmoothingLevel.Medium,1)?.Closed!=true,"wiggle closed");
            for(int i=0;i<=360;i+=5) To(30-30*Math.Cos(i*Math.PI/180),30*Math.Sin(i*Math.PI/180));
            check(draft.NearStart(P(1,1),1),"ring missing for a closing loop");
            var preview=(CurveShape)draft.Preview(4,P(1,1),false);
            check(ReferenceEquals(preview.Points,draft.Points),"freehand preview copied raw samples");
            var shape=(CurveShape)draft.Up(4,P(1,1),new Point(1,1),false,new Size(4,4));
            check(shape!=null && shape.Closed,"ring promised a close that Process refused");
            var line=new AnnotationDraft(); line.Down(4,P(0),Point.Empty,1,false,1); last=new PointF(0,0);
            foreach(var x in new[]{10,20,30,40,30,20,10,2}) { var q=new PointF(x,0); line.Sample(P(x),last,q); last=q; }
            check(!line.NearStart(P(0),1),"ring shown for a collinear stroke that release keeps open");
            check(((CurveShape)line.Up(4,P(0),Point.Empty,false,new Size(4,4)))?.Closed!=true,"collinear stroke closed");
        });
        test("freehand fast two-sample drag remains a valid open curve",()=>
        {
            var draft=new AnnotationDraft(); draft.Down(4,P(0),Point.Empty,1,false,1);
            var curve=(CurveShape)draft.Up(4,P(100,20),new Point(100,20),false,new Size(4,4));
            check(curve!=null && !curve.Closed && curve.Points.First()==P(0) && curve.Points.Last()==P(100,20),"fast drag rejected");
        });
        test("freehand close detection requires travel noncollinear loop and no repeated vertex",()=>
        {
            var circle=Enumerable.Range(0,361).Select(i=>P(60+50*Math.Cos(i*Math.PI/180),60+50*Math.Sin(i*Math.PI/180))).ToList();
            foreach(SmoothingLevel level in Enum.GetValues(typeof(SmoothingLevel)))
            {
                var shape=FreehandStroke.Process(circle,1,level,1);
                check(shape!=null && shape.Closed && shape.Points.Count>=3 && FreehandStroke.NonCollinear(shape.Points),"loop did not close");
                check(shape.Points.First()!=shape.Points.Last(),"duplicate closing vertex");
                var owner=new HopperDraw(); owner.CompleteDraft(shape); check(owner.Shapes[0] is CurveShape,"freehand did not use existing Kind 3 curve");
            }
            check(FreehandStroke.Process(new[]{P(0),P(1),P(2),P(0)},1,SmoothingLevel.Medium,1)==null,"near-start no-travel stroke accepted");
            var line=FreehandStroke.Process(new[]{P(0),P(50),P(100),P(0)},1,SmoothingLevel.Medium,1);
            check(line==null || !line.Closed,"collinear stroke closed");
        });
        test("freehand held gesture ignores click counts Shift Enter and Backspace",()=>
        {
            var draft=new AnnotationDraft(); draft.Down(3,P(500),Point.Empty,1,false,1);
            check(draft.Down(4,P(0),Point.Empty,2,true,1)==null && draft.Held && draft.Points.Count==1,"down retained stale draft or double-click finished");
            draft.Sample(P(20,7),Point.Empty,new PointF(20,7));
            draft.Backspace(); check(draft.Points.Count==2,"Backspace edited held stroke");
            check(draft.Finish(4,false)==null && draft.Held,"Enter finished held stroke");
            var preview=draft.Preview(4,P(500,500),true);
            check(preview.GetPoints().SequenceEqual(draft.Points.Select(p=>p.ToPointF())),"preview appended or snapped pointer");
            var shape=(CurveShape)draft.Up(4,P(40,13),new Point(40,13),true,new Size(4,4));
            check(shape!=null && shape.Points.Last()==P(40,13) && !draft.Active && !draft.Held,"release lost endpoint/state or snapped");
            check(draft.Up(4,P(60,13),new Point(60,13),false,new Size(4,4))==null,"trailing up completed twice");
        });
        test("freehand samples current viewport projection during stationary pointer auto-pan",()=>
        {
            var draft=new AnnotationDraft(); draft.Down(4,P(100),new Point(100,0),1,false,1);
            draft.Sample(P(101),new PointF(99,0),new PointF(100,0)); check(draft.Points.Count==1,"sub-spacing sample added");
            // Pointer has not moved; auto-pan projects the old canvas sample three pixels away.
            draft.Sample(P(103),new PointF(97,0),new PointF(100,0)); check(draft.Points.Count==2 && draft.Points.Last()==P(103),"stationary pointer auto-pan sample lost");
        });
        test("freehand click degenerate cancellation and capture-loss trailing up produce nothing",()=>
        {
            var draft=new AnnotationDraft();
            draft.Down(4,P(0),Point.Empty,1,false,1); check(draft.Up(4,P(0),Point.Empty,false,new Size(4,4))==null && !draft.Active,"click produced curve");
            draft.Down(4,P(0),Point.Empty,1,false,1); draft.Sample(P(1),Point.Empty,new Point(10,0));
            check(draft.Up(4,P(1),new Point(1,0),false,new Size(4,4))==null && !draft.Active,"degenerate stroke accepted");
            foreach(var reason in new[]{"Escape","capture loss","focus loss","document switch","disable","tool change"})
            {
                draft.Down(4,P(0),Point.Empty,1,false,1); draft.Sample(P(50,10),Point.Empty,new Point(50,10));
                draft.Cancel(); check(!draft.Active && !draft.Held && draft.Up(4,P(100),new Point(100,0),false,new Size(4,4))==null,reason+" retained gesture");
            }
        });
        test("claimed freehand interaction cancellation consumes trailing release",()=>
        {
            using(var canvas=new GH_Canvas())
            {
                var owner=new HopperDraw(); owner.SetTool(4);
                var down=new GH_CanvasMouseEvent(canvas.Viewport,new MouseEventArgs(MouseButtons.Left,1,10,10,0));
                var interaction=new HopperDrawInteraction(null,canvas,down,null,-1,-1,owner);
                interaction.RespondToMouseDown(canvas,down);
                check(owner.Draft.Held,"interaction did not claim freehand");
                owner.Draft.Sample(P(70,30),PointF.Empty,new PointF(60,20));
                interaction.Cancel();
                check(!owner.Draft.Held && !owner.Draft.Active,"capture/focus cancellation retained held stroke");
                interaction.RespondToMouseUp(canvas,new GH_CanvasMouseEvent(canvas.Viewport,new MouseEventArgs(MouseButtons.Left,1,100,50,0)));
                check(owner.Shapes.Count==0,"cancelled interaction completed on trailing up");
                interaction.Destroy();
            }
        });
        test("freehand raw samples and output stay bounded under long drags",()=>
        {
            var draft=new AnnotationDraft(); draft.Down(4,P(0),Point.Empty,1,false,1);
            for(int i=1;i<20000;i++) draft.Sample(P(i*2,Math.Sin(i*.01)*20),Point.Empty,new Point(3,0));
            check(draft.Points.Count<=FreehandStroke.MaxRaw,"raw samples unbounded");
            var curve=(CurveShape)draft.Up(4,P(40000),new Point(40000,0),false,new Size(4,4));
            check(curve!=null && curve.Points.Count<=FreehandStroke.MaxOutput && curve.Points.First()==P(0) && curve.Points.Last()==P(40000),"output bound/endpoints wrong");
        });
        test("freehand overshoot guard restores samples at long sharp turns",()=>
        {
            var raw=new List<Point3d>();
            for(int i=0;i<=1000;i++) raw.Add(P(i));
            for(int i=1;i<=20;i++) raw.Add(P(1000,i));
            for(int i=1;i<=1000;i++) raw.Add(P(1000+i,20));
            var curve=FreehandStroke.Process(raw,1,SmoothingLevel.Low,1);
            foreach(var stroke in AnnotationGeometry.Strokes(curve)) foreach(var point in stroke)
            {
                double distance=Math.Min(GeometryUtilities.DistanceToSegment(point,new PointF(0,0),new PointF(1000,0)),
                    Math.Min(GeometryUtilities.DistanceToSegment(point,new PointF(1000,0),new PointF(1000,20)),GeometryUtilities.DistanceToSegment(point,new PointF(1000,20),new PointF(2000,20))));
                check(distance<=4,"spline severely overshot sampled stroke");
            }
        });
        test("freehand gesture zoom and smoothing are captured at down",()=>
        {
            var draft=new AnnotationDraft(); draft.Down(4,P(0),Point.Empty,1,false,.5f,SmoothingLevel.High);
            for(int i=1;i<70;i++) draft.Sample(P(i*2,Math.Sin(i*.15)*10),Point.Empty,new Point(4,0));
            var raw=draft.Points.ToList(); raw.Add(P(145,15)); var expected=FreehandStroke.Process(raw,2,SmoothingLevel.High,.5f);
            var got=(CurveShape)draft.Up(4,P(145,15),new Point(290,30),false,new Size(4,4));
            check(got.Points.SequenceEqual(expected.Points),"gesture settings changed during held stroke");
        });
    }
}
