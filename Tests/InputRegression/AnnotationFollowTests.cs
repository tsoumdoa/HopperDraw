using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;
using hopperdraw;
using Rhino.Geometry;

internal static class AnnotationFollowTests
{
    // Identity tokens only: GH_Document initializes native Rhino meshing/display services outside Rhino.
    // The production wrappers gather selection/bounds; tests exercise the same managed move/detection core.
    private static GH_Document Document()
    {
        var doc = (GH_Document)FormatterServices.GetUninitializedObject(typeof(GH_Document));
        GC.SuppressFinalize(doc); return doc;
    }
    private static HopperDraw Owner(int order = 1)
    {
        var owner = new HopperDraw { DrawOrder = order };
        typeof(HopperDraw).GetField("_isActivated", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(owner,true);
        owner.CreateAttributes();
        return owner;
    }
    private static LineShape Line(float x, float y, float end) => new LineShape(new Point3d(x,y,0),new Point3d(end,y,0));
    private static HopperDraw Native(float x, float y)
    {
        var native = Owner(); native.Attributes.Pivot = new PointF(x,y); native.Attributes.PerformLayout();
        native.Attributes.Selected = true; return native;
    }
    private static GH_Group Group(params IGH_DocumentObject[] children)
    {
        var group = new GH_Group(); group.CreateAttributes();
        group.ObjectIDs.AddRange(children.Select(c => c.InstanceGuid));
        typeof(GH_Group).GetField("_objects",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(group,children.ToList());
        group.Attributes.ExpireLayout(); group.Attributes.PerformLayout(); return group;
    }
    internal static void Run(Action<string,Action> test, Action<bool,string> check)
    {
        test("group containment includes nested bounds and both channels without changing selection", () =>
        {
            var first = Native(100,100); var last = Native(500,400);
            var inner = Group(first); var outer = Group(inner,last); outer.Attributes.Selected = true;
            var bounds = outer.Attributes.Bounds;
            check(bounds.Width > 300 && bounds.Height > 200,"Real group bounds did not cover descendants");
            foreach (int order in new[] { 0,1 })
            {
                var owner = Owner(order); float x = bounds.Left + 30, y = bounds.Top + 30;
                owner.Shapes.Add(Line(x,y,x+20)); // inside
                owner.Shapes.Add(Line(x,y,bounds.Right+20)); // partial
                var unit = new DrawShapeGroup(); unit.Children.Add(Line(x,y+10,x+20)); unit.Children.Add(Line(x,y+20,x+20));
                owner.Shapes.Add(unit);
                var partial = new DrawShapeGroup(); partial.Children.Add(Line(x,y+10,x+20)); partial.Children.Add(Line(x,y,bounds.Right+20));
                owner.Shapes.Add(partial);
                var ib = inner.Attributes.Bounds;
                owner.Shapes.Add(Line(ib.Left+10,ib.Top+10,ib.Left+20)); // nested group, outer alone selected
                owner.Shapes.Add(Line(bounds.Right+40,y,bounds.Right+60)); owner.SelectedShapeIndices.Add(5);
                owner.SelectedShapeIndices.Add(0); // selected AND contained: only one entry
                owner.Shapes.Add(new OpaqueShape(new GH_IO.Serialization.GH_LooseChunk("Shape"),"future"));
                var unreadable = new DrawShapeGroup(); unreadable.Children.Add(Line(x,y,x+20));
                unreadable.Children.Add(new OpaqueShape(new GH_IO.Serialization.GH_LooseChunk("Shape"),"future")); owner.Shapes.Add(unreadable);
                var moving = AnnotationMove.MovingSet(new[] { owner },new[] { outer });
                check(moving[owner].SequenceEqual(new[] { 0,2,4,5 }),"Wrong containment/selection union in channel " + order);
                check(owner.SelectedShapeIndices.SequenceEqual(new[] { 5,0 }),"Containment changed selection");
                inner.Attributes.Selected = true;
                check(AnnotationMove.MovingSet(new[] { owner },new[] { outer,inner })[owner].SequenceEqual(moving[owner]),"Overlapping selected groups duplicated moving shapes");
                inner.Attributes.Selected = false;
                owner.ClearSelection();
                check(AnnotationMove.MovingSet(new[] { owner },new[] { outer })[owner].SequenceEqual(new[] { 0,2,4 }),"No-selection group follow missing");
                owner.SelectedShapeIndices.Add(7);
                var move = new AnnotationMove(AnnotationMove.MovingSet(new[] { owner },new[] { outer }),new IGH_DocumentObject[0],null);
                check(!move.Allowed,"Selected opaque group did not block union");
                owner.SelectedShapeIndices.Clear(); owner.Locked = true;
                check(AnnotationMove.MovingSet(new[] { owner },new[] { outer }).Count == 0,"Ineligible owner included");
            }
        });
        test("explicit move indices deduplicate translate and revert only the moving set", () =>
        {
            var owner = Owner(); owner.Shapes.Add(Line(10,10,30)); owner.Shapes.Add(Line(60,10,80));
            var untouched = owner.Shapes[1];
            var move = new AnnotationMove(new Dictionary<HopperDraw,int[]> { [owner] = new[] { 0,0,-1,99 } },new IGH_DocumentObject[0],null);
            move.Translate(new PointF(20,30),false); move.Translate(new PointF(25,35),false);
            check(move.Allowed && move.Changed && owner.Shapes[0].GetPoints()[0] == new PointF(35,45),"Explicit move accumulated/duplicated delta");
            check(ReferenceEquals(untouched,owner.Shapes[1]) && !owner.HasSelection,"Move altered untouched shapes or selection");
            move.Revert(false);
            check(!move.Changed && owner.Shapes[0].GetPoints()[0] == new PointF(10,10) && ReferenceEquals(untouched,owner.Shapes[1]),"Revert did not restore only moving set");
        });
        test("nudge uniform pivot detection rejects align zero foreign and non-pivot records", () =>
        {
            var doc = Document(); var foreign = Document();
            var a = Native(100,100); var b = Native(300,100);
            var before = new Dictionary<IGH_DocumentObject,PointF> { [a]=a.Attributes.Pivot, [b]=b.Attributes.Pivot };
            var record = new GH_UndoRecord("Nudge Right",new[] { new GH_PivotAction(a),new GH_PivotAction(b) });
            check(!AnnotationNudge.UniformDelta(doc,doc,record,2,before,out _),"Zero accepted");
            a.Attributes.Pivot += new Size(1,0); b.Attributes.Pivot += new Size(1,0);
            check(AnnotationNudge.UniformDelta(doc,doc,record,2,before,out var delta) && delta == new PointF(1,0),"Uniform pivot record rejected");
            check(!AnnotationNudge.UniformDelta(doc,foreign,record,2,before,out _),"Other document accepted");
            check(!AnnotationNudge.UniformDelta(doc,doc,record,1,before,out _),"Partial record accepted");
            var align = new GH_UndoRecord("Align",new[] { new GH_PivotAction(a),new GH_PivotAction(b) });
            check(!AnnotationNudge.UniformDelta(doc,doc,align,2,before,out _),"Uniform Align accepted");
            b.Attributes.Pivot += new Size(0,10);
            check(!AnnotationNudge.UniformDelta(doc,doc,record,2,before,out _),"Non-uniform align delta accepted");
            b.Attributes.Pivot -= new Size(0,10);
            record.AddAction(new ShapeEditUndoAction(a,new IGH_DocumentObject[0]));
            check(!AnnotationNudge.UniformDelta(doc,doc,record,3,before,out _),"Non-pivot record accepted");
        });
        test("pre-move nudge capture coalesces repeats and annotation undo redo restores the union", () =>
        {
            var doc = Document();
            var native = Native(100,100); var group = Group(native); group.Attributes.Selected = true;
            var owner = Owner(0);
            var bounds = group.Attributes.Bounds;
            var start = new PointF(bounds.Left+10,bounds.Top+10);
            owner.Shapes.Add(Line(start.X,start.Y,start.X+10)); // unselected, inside selected GH group
            owner.Shapes.Add(Line(1000,1000,1020)); owner.SelectedShapeIndices.Add(1);
            var selected = new IGH_DocumentObject[] { native,group };
            var record = new GH_UndoRecord("Nudge Right",selected.Select(o => new GH_PivotAction(o)));
            var nudge = new AnnotationNudge(AnnotationMove.MovingSet(new[] { owner },new[] { group }),selected,doc,record);

            check(!nudge.Sync(doc,selected,record.Guid) && record.ActionCount == selected.Length,"Attached before native moved");
            native.Attributes.Pivot += new Size(1,0);
            check(nudge.Sync(doc,selected,record.Guid) && record.ActionCount == selected.Length+1,"First nudge did not attach once");
            native.Attributes.Pivot += new Size(1,0); check(nudge.Sync(doc,selected,record.Guid),"Coalesced repeat missed");
            check(!nudge.Sync(doc,selected,record.Guid) && record.ActionCount == selected.Length+1,"Repeated sync duplicated actions/records");
            check(owner.Shapes[0].GetPoints()[0] == new PointF(start.X+2,start.Y) && owner.Shapes[1].GetPoints()[0] == new PointF(1002,1000),"Contained and selected did not move together");
            check(owner.SelectedShapeIndices.SequenceEqual(new[] { 1 }),"Nudge selected contained shape");
            foreach (var action in record.Actions.OfType<ShapeEditUndoAction>()) action.Swap(null,owner);
            check(owner.Shapes[0].GetPoints()[0] == start && owner.Shapes[1].GetPoints()[0] == new PointF(1000,1000),"One undo did not restore both");
            foreach (var action in record.Actions.OfType<ShapeEditUndoAction>()) action.Swap(null,owner);
            check(owner.Shapes[0].GetPoints()[0] == new PointF(start.X+2,start.Y),"Redo did not restore both");
            check(!nudge.Sync(Document()),"Session accepted another document");
        });
        test("coalesced nudges resnapshot annotation selection and reject stale record targets", () =>
        {
            var doc = Document(); var native = Native(100,100); var owner = Owner();
            owner.Shapes.Add(Line(10,10,30)); owner.Shapes.Add(Line(50,10,70)); owner.SelectedShapeIndices.Add(0);
            var selected = new IGH_DocumentObject[] { native };
            var record = new GH_UndoRecord("Nudge Up",new[] { new GH_PivotAction(native) });
            Func<Dictionary<HopperDraw,int[]>> moving = () => AnnotationMove.MovingSet(new[] { owner },new GH_Group[0]);
            var nudge = new AnnotationNudge(moving(),selected,doc,record);
            native.Attributes.Pivot += new Size(0,-1);
            check(nudge.Sync(doc,selected,record.Guid),"Initial selected annotation did not follow");
            owner.SelectedShapeIndices.Clear(); owner.SelectedShapeIndices.Add(1);
            nudge.Prepare(moving(),selected); native.Attributes.Pivot += new Size(0,-1);
            check(!nudge.Sync(doc,selected,Guid.NewGuid()),"Unrelated top record accepted");
            check(!nudge.Sync(Document(),selected,record.Guid),"Foreign document accepted");
            check(nudge.Sync(doc,selected,record.Guid) && record.ActionCount == 2,"Repeat did not reuse owner's undo action");
            check(owner.Shapes[0].GetPoints()[0] == new PointF(10,9) && owner.Shapes[1].GetPoints()[0] == new PointF(50,9),"Repeat ignored new annotation selection");
            foreach (var action in record.Actions.OfType<ShapeEditUndoAction>()) action.Swap(null,owner);
            check(owner.Shapes[0].GetPoints()[0] == new PointF(10,10) && owner.Shapes[1].GetPoints()[0] == new PointF(50,10),"Coalesced undo did not restore both annotation sets");
            nudge.Prepare(moving(),new[] { Native(500,500) }); native.Attributes.Pivot += new Size(0,-1);
            check(!nudge.Sync(doc,selected,record.Guid),"Changed native targets accepted");
        });
    }
}
