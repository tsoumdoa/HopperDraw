using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using Grasshopper.Kernel;
using hopperdraw;
using Rhino.Geometry;
using Point=System.Drawing.Point;

internal static class CanvasUxTests
{
    private static Point3d P(double x,double y=0)=>new Point3d(x,y,0);
    private static HopperDraw Owner(double x=0)
    {
        var o=new HopperDraw(); o.Shapes.Add(new LineShape(P(x),P(x+100))); o.SelectedShapeIndices.Add(0); return o;
    }
    internal static void Run(Action<string,Action> test,Action<bool,string> check)
    {
        test("all annotation modifier transitions preserve mixed selection when required",()=>
        {
            foreach(bool selected in new[]{false,true}) foreach(bool shift in new[]{false,true}) foreach(bool control in new[]{false,true})
            {
                bool current=selected; int replacements=0, native=2, otherOwners=2;
                bool drag=AnnotationSelection.Press(selected,shift,control,()=>{current=true; replacements++; native=otherOwners=0;},()=>current=true,()=>current=false);
                bool expected=control ? shift && !selected : true;
                check(current==expected && drag==expected,"Wrong click/drag state");
                check(replacements==(!selected && !shift && !control ? 1:0),"Wrong replace decision");
                if(selected && !shift && !control) check(native==2 && otherOwners==2,"Mixed selection destroyed");
            }
        });
        test("absolute multi-owner move snapshots return to start and Escape records nothing",()=>
        {
            HopperDraw a=Owner(), b=Owner(200); var move=new AnnotationMove(new[]{a,b},new IGH_DocumentObject[0],null);
            move.Translate(new PointF(25,15),false); move.Translate(new PointF(30,20),false);
            check(((LineShape)a.Shapes[0]).Start==P(30,20) && ((LineShape)b.Shapes[0]).Start==P(230,20),"Accumulated delta");
            move.Revert(false); check(!move.Changed && ((LineShape)a.Shapes[0]).Start==P(0),"Escape did not restore");
            move.Translate(PointF.Empty,false); check(!move.Changed,"No-op creates commit");
            move.Translate(new PointF(50,10),false); move.Translate(PointF.Empty,false); check(!move.Changed,"Return to press still changed");
        });
        test("endpoint and polyline vertex edits snap relative to their anchors",()=>
        {
            var o=Owner(); o.Shapes[0]=new PolylineShape(new List<Point3d>{P(0),P(50),P(100,40)});
            var move=new AnnotationMove(new[]{o},new IGH_DocumentObject[0],null);
            move.Edit(o,0,2,P(100,44),true); var p=((PolylineShape)o.Shapes[0]).Points[2];
            check(Math.Abs(p.X-50-p.Y)<.0001,"Vertex did not snap to previous point");
            move.Revert(false); check(((PolylineShape)o.Shapes[0]).Points[2]==P(100,40),"Edit rollback lost snapshot");
        });
        test("opaque record in any owner blocks every selected geometry",()=>
        {
            HopperDraw a=Owner(), b=Owner(); b.Shapes.Add(new OpaqueShape(new GH_IO.Serialization.GH_LooseChunk("Shape"),"unreadable")); b.SelectedShapeIndices.Add(1);
            var move=new AnnotationMove(new[]{a,b},new IGH_DocumentObject[0],null); move.Translate(new PointF(40,0),false);
            check(!move.Allowed && !move.Changed && ((LineShape)a.Shapes[0]).Start==P(0),"Partial geometry change");
        });
        test("Escape resolves exactly one layer",()=>
        {
            check(AnnotationEscape.Layer(true,true,true)==EscapeLayer.Gesture,"Gesture did not win");
            check(AnnotationEscape.Layer(false,true,true)==EscapeLayer.Drawing,"Drawing layer missing");
            check(AnnotationEscape.Layer(false,false,true)==EscapeLayer.Selection,"Selection layer missing");
            check(AnnotationEscape.Layer(false,false,false)==EscapeLayer.None,"Idle Escape claimed");
        });
        test("line and frame accept drag and click-click with screen thresholds",()=>
        {
            foreach(int mode in new[]{0,2})
            {
                var d=new AnnotationDraft(); check(d.Down(mode,P(0),new Point(0,0),1,false,1)==null,"First press finished");
                check(d.Up(mode,P(1),new Point(1,0),false,new Size(6,6))==null && d.Active,"Click threshold failed");
                check(d.Down(mode,P(100,50),new Point(100,50),1,false,1)!=null && !d.Active,"Second click failed");
                d.Down(mode,P(0),new Point(0,0),1,false,1); var s=d.Up(mode,P(100,50),new Point(100,50),true,new Size(6,6));
                check(s!=null && !d.Active,"Drag failed");
                if(s is FrameShape f) check(f.BottomRight.X==f.BottomRight.Y,"Frame is not square");
            }
        });
        test("native double-click finishes open once; Enter also finishes open",()=>
        {
            foreach(int mode in new[]{1,3})
            {
                var d=new AnnotationDraft(); d.Down(mode,P(0),Point.Empty,1,false,1); d.Down(mode,P(50,40),Point.Empty,1,false,1);
                var shape=d.Down(mode,P(50,40),Point.Empty,2,false,1);
                check(shape.GetPoints().Length==2 && (shape is PolylineShape p ? !p.Closed : !((CurveShape)shape).Closed),"Double-click closed/duplicated endpoint");
                check(d.Up(mode,P(50,40),Point.Empty,false,new Size(4,4))==null && !d.Active,"Trailing up finished twice");
                d.Down(mode,P(0),Point.Empty,1,false,1); d.Down(mode,P(50),Point.Empty,1,false,1); check(d.Finish(mode,false)!=null,"Enter failed");
            }
        });
        test("close-on-start requires three distinct points; Backspace cancels last point",()=>
        {
            var d=new AnnotationDraft(); d.Down(1,P(0),Point.Empty,1,false,1); d.Down(1,P(50),Point.Empty,1,false,1);
            check(!d.NearStart(P(0),1),"Two-point closure allowed"); d.Down(1,P(50,50),Point.Empty,1,false,1);
            check(d.NearStart(P(2),.5f),"Screen-space closure tolerance wrong");
            var closed=(PolylineShape)d.Down(1,P(0),Point.Empty,1,false,1); check(closed.Closed && closed.Points.Count==3,"Closure duplicated start");
            d.Down(1,P(0),Point.Empty,1,false,1); d.Backspace(); check(!d.Active,"Backspace did not cancel");
        });
        test("45 degree snapping covers eight octants and zero",()=>
        {
            for(int i=0;i<8;i++)
            {
                double angle=i*Math.PI/4+.1; var p=GeometryUtilities.SnapAngle(P(10,20),P(10+100*Math.Cos(angle),20+100*Math.Sin(angle)));
                check(Math.Abs(p.X-10-100*Math.Cos(i*Math.PI/4))<.00001 && Math.Abs(p.Y-20-100*Math.Sin(i*Math.PI/4))<.00001,"Wrong snap octant");
            }
            check(GeometryUtilities.SnapAngle(P(0),P(0))==P(0),"Zero snap invalid");
        });
        test("window contains entire stroke; crossing finds segments and closing edges",()=>
        {
            var r=new RectangleF(40,-5,20,10); var line=new LineShape(P(0),P(100));
            check(!AnnotationGeometry.InRectangle(line,r,false) && AnnotationGeometry.InRectangle(line,r,true),"Crossing reduced to vertices");
            var poly=new PolylineShape(new List<Point3d>{P(0),P(100),P(100,100)},true);
            check(AnnotationGeometry.InRectangle(poly,new RectangleF(45,45,10,10),true),"Closing segment ignored");
            var frame=new FrameShape(P(0),P(100),P(100,100),P(0,100));
            check(!AnnotationGeometry.InRectangle(frame,new RectangleF(45,45,10,10),true),"Frame interior counts as stroke");
            var curve=new CurveShape(new List<Point3d>{P(0),P(20,100),P(80,100),P(100)});
            var stroke=AnnotationGeometry.Strokes(curve).Single(); var peak=stroke.OrderByDescending(p=>p.Y).First();
            check(peak.Y>100 && AnnotationGeometry.Hit(curve,peak,1,1),"Curve extrema missing");
            check(!AnnotationGeometry.InRectangle(curve,new RectangleF(-1,-1,102,102),false),"Window ignored curve extrema");
        });
        test("hit precedence follows channel then owner then shape, handles stay narrow",()=>
        {
            var overlay=Owner(); var groups=Owner(); groups.DrawOrder=0;
            var hit=AnnotationPicking.Hit(new[]{overlay,groups},new PointF(50,0),false); check(hit.owner==overlay,"Groups beat Overlay");
            groups.DrawOrder=1; hit=AnnotationPicking.Hit(new[]{overlay,groups},new PointF(50,0),false); check(hit.owner==groups,"Document order reversed");
            groups.Shapes.Add(new LineShape(P(0),P(100))); hit=AnnotationPicking.Hit(new[]{overlay,groups},new PointF(50,0),false); check(hit.shape==1,"Shape order reversed");
            check(AnnotationPicking.Hit(new[]{overlay},new PointF(50,0),true).owner==null,"Whole selected body treated as handle");
            check(AnnotationPicking.Hit(new[]{overlay},new PointF(0,0),true).handle==0,"Selected handle missing");
        });
        test("pen survives completion cancellation and exit; selection style is independent",()=>
        {
            var o=Owner(); o.Pen(Color.Red,3,DashStyle.Dash);
            o.CompleteDraft(new LineShape(P(0),P(50))); o.Draft.Down(1,P(0),Point.Empty,1,false,1); o.Draft.Cancel(); o.ExitDrawing();
            o.SetSelectedColor(Color.Blue);
            check(o.PendingColorOverride==Color.Red && o.CurrentThicknessMultiplier==3 && o.CurrentLineType==DashStyle.Dash,"Pen was reset by unrelated action");
            o.ResetPen(); check(o.PendingColorOverride==null && o.CurrentThicknessMultiplier==1,"Explicit pen reset failed");
        });
    }
}
