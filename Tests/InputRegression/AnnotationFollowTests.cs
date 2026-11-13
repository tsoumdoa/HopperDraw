using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Windows.Forms;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;
using hopperdraw;
using Rhino.Geometry;

internal static class AnnotationFollowTests
{
    // GH_Document's constructor needs native Rhino display services. Initialize only its managed
    // object table/events; use GH's real selection, undo server and record notifications below.
    private static GH_Document Document(params IGH_DocumentObject[] objects)
    {
        var doc = (GH_Document)FormatterServices.GetUninitializedObject(typeof(GH_Document));
        GC.SuppressFinalize(doc);
        Set(doc,"m_objects",objects.ToList()); Set(doc,"m_raiseEvents",true);
        return doc;
    }
    private sealed class CloneProbe : LineShape
    {
        internal int Clones;
        internal CloneProbe() : base(new Point3d(2000,2000,0), new Point3d(2020,2000,0)) { }
        public override DrawShape Clone() { Clones++; return base.Clone(); }
    }
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object target, string field, object value)
    {
        // FieldInfo.SetValue runs GH_Document's native meshing static initializer. Direct instance
        // field stores avoid that initializer, just as GH's managed instance methods do.
        var info = target.GetType().GetField(field,Private);
        var method = new DynamicMethod("SetManagedField",null,new[] { typeof(object),typeof(object) },typeof(AnnotationFollowTests),true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass,target.GetType()); il.Emit(OpCodes.Ldarg_1);
        il.Emit(info.FieldType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass,info.FieldType);
        il.Emit(OpCodes.Stfld,info); il.Emit(OpCodes.Ret);
        ((Action<object,object>)method.CreateDelegate(typeof(Action<object,object>)))(target,value);
    }
    private static object Get(object target, string field) => target.GetType().GetField(field, Private).GetValue(target);
    private static void Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private).Invoke(target,args);
    private static CanvasAnnotationController Controller(GH_Document document)
    {
        // Bypass native canvas initialization as well; these paths only read its managed document/
        // interaction fields. No HWND means QueueNudge deliberately leaves dispatch to the test.
        var canvas = (GH_Canvas)FormatterServices.GetUninitializedObject(typeof(GH_Canvas));
        GC.SuppressFinalize(canvas);
        // The WinForms base needs its normal window/state holder even though no HWND is created.
        var initialize = new DynamicMethod("InitializeControl",null,new[] { typeof(GH_Canvas) },typeof(AnnotationFollowTests),true);
        var il = initialize.GetILGenerator(); il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call,typeof(Control).GetConstructor(Type.EmptyTypes)); il.Emit(OpCodes.Ret);
        ((Action<GH_Canvas>)initialize.CreateDelegate(typeof(Action<GH_Canvas>)))(canvas);
        Set(canvas,"_document",document);
        var controller = (CanvasAnnotationController)FormatterServices.GetUninitializedObject(typeof(CanvasAnnotationController));
        Set(controller,"_canvas",canvas); return controller;
    }
    private static GH_DocUndoEventArgs UndoEvent(GH_Document doc, GH_UndoOperation operation, GH_UndoRecord record) =>
        (GH_DocUndoEventArgs)Activator.CreateInstance(typeof(GH_DocUndoEventArgs),Private,null,new object[] { doc,operation,record },null);
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
        test("nudge group bounds require all real descendants selected while drag still expands groups", () =>
        {
            var a = Native(100,100); var b = Native(500,400); var outside = Native(1000,1000);
            var inner = Group(a); var group = Group(inner,b); group.Attributes.Selected = true;
            var bounds = group.Attributes.Bounds;
            var owner = Owner(); owner.Shapes.Add(Line(bounds.Left+30,bounds.Top+30,bounds.Left+50));
            var start = owner.Shapes[0].GetPoints()[0];
            foreach (var selection in new[] { new[] { outside }, new[] { a,b }, new[] { a } })
            {
                a.Attributes.Selected = selection.Contains(a); b.Attributes.Selected = selection.Contains(b);
                outside.Attributes.Selected = selection.Contains(outside);
                bool all = selection.Contains(a) && selection.Contains(b);
                var moving = AnnotationMove.MovingSet(new[] { owner },new[] { group },true);
                check(moving.ContainsKey(owner) == all,"Wrong nudge containment for native selection");
                check(AnnotationMove.MovingSet(new[] { owner },new[] { group }).ContainsKey(owner),"Drag lost group expansion");
                var selected = new IGH_DocumentObject[] { group }.Concat(selection).ToArray();
                var doc = Document(selected);
                var record = new GH_UndoRecord("Nudge Right",selected.Select(o => new GH_PivotAction(o)));
                doc.UndoServer.PushUndoRecord(record);
                var nudge = new AnnotationNudge(moving,selected,doc,record);
                foreach (var obj in selection) obj.Attributes.Pivot += new Size(1,0);
                check(nudge.Sync(doc) == all,"Wrong annotation movement for native selection");
                check(owner.Shapes[0].GetPoints()[0] == new PointF(start.X+(all ? 1 : 0),start.Y),"Contained annotation nudged with incomplete group");
                owner.Shapes[0] = Line(start.X,start.Y,start.X+20);
            }
            a.Attributes.Selected = b.Attributes.Selected = false;
            check(AnnotationMove.MovingSet(new[] { owner },new[] { group },true).Count == 0,"Group alone contributed nudge bounds");
        });
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
            var nudge = new AnnotationNudge(AnnotationMove.MovingSet(new[] { owner },new[] { group },true),selected,doc,record);

            check(!nudge.Sync(doc,selected,record.Guid) && record.ActionCount == selected.Length,"Attached before native moved");
            native.Attributes.Pivot += new Size(1,0);
            check(nudge.Sync(doc,selected,record.Guid) && record.ActionCount == selected.Length+1,"First nudge did not attach once");
            nudge.Prepare(AnnotationMove.MovingSet(new[] { owner },new[] { group },true),selected);
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
        test("nudge float deltas tolerate pivot rounding and use the first representative", () =>
        {
            var a = Native(1023.9998f,100); var b = Native(8191.9995f,100); var doc = Document(a,b);
            a.Attributes.Pivot = new PointF(1023.9998f,100); b.Attributes.Pivot = new PointF(8191.9995f,100);
            var before = new Dictionary<IGH_DocumentObject,PointF> { [a] = a.Attributes.Pivot, [b] = b.Attributes.Pivot };
            var record = new GH_UndoRecord("Nudge Right",new[] { new GH_PivotAction(a), new GH_PivotAction(b) });
            var owner = Owner(); owner.Shapes.Add(Line(10,10,30)); owner.SelectedShapeIndices.Add(0);
            var moving = AnnotationMove.MovingSet(new[] { owner },doc,true);
            doc.UndoServer.PushUndoRecord(record);
            var nudge = new AnnotationNudge(moving,doc.SelectedObjects(),doc,record);
            a.Attributes.Pivot += new Size(1,0); b.Attributes.Pivot += new Size(1,0);
            var da = a.Attributes.Pivot.X - before[a].X; var db = b.Attributes.Pivot.X - before[b].X;
            check(da != db && Math.Abs(da-db) < .001f,"Test did not produce unequal rounded native steps");
            check(AnnotationNudge.UniformDelta(doc,doc,record,2,before,out var delta) && delta.X == da,"Rounding rejected or representative changed");
            check(nudge.Sync(doc) && owner.Shapes[0].GetPoints()[0] == new PointF(10+da,10),"Rounded native nudge left annotation behind");
            check(!nudge.Sync(doc),"Same rounded step applied twice");
            b.Attributes.Pivot += new SizeF(.01f,0);
            check(!nudge.Sync(doc),"Genuinely unequal step accepted");
        });
        test("repeat prepare clones only moving shapes after each owner undo action is attached", () =>
        {
            var native = Native(100,100); var doc = Document(native); var owner = Owner();
            owner.Shapes.Add(Line(10,10,30)); owner.SelectedShapeIndices.Add(0);
            var probe = new CloneProbe(); owner.Shapes.Add(probe);
            var record = new GH_UndoRecord("Nudge Right",new[] { new GH_PivotAction(native) });
            doc.UndoServer.PushUndoRecord(record);
            var moving = AnnotationMove.MovingSet(new[] { owner },doc,true);
            var nudge = new AnnotationNudge(moving,doc.SelectedObjects(),doc,record);
            check(probe.Clones == 1,"Initial full undo snapshot missing");
            native.Attributes.Pivot += new Size(1,0); check(nudge.Sync(doc),"First step failed");
            var action = record.Actions.OfType<ShapeEditUndoAction>().Single();
            nudge.Prepare(moving,doc.SelectedObjects());
            check(probe.Clones == 1 && !nudge.Sync(doc),"Prepare cloned untouched shapes or moved before native step");
            native.Attributes.Pivot += new Size(1,0);
            check(nudge.Sync(doc) && ReferenceEquals(action,record.Actions.OfType<ShapeEditUndoAction>().Single()),"Repeat replaced undo action");
            check(owner.Shapes[0].GetPoints()[0] == new PointF(12,10),"Repeat missed prepare-before-move delta");
            var second = Owner(); second.Shapes.Add(Line(50,10,70)); second.SelectedShapeIndices.Add(0);
            nudge.Prepare(AnnotationMove.MovingSet(new[] { owner,second },doc,true),doc.SelectedObjects());
            native.Attributes.Pivot += new Size(1,0);
            check(nudge.Sync(doc) && record.Actions.OfType<ShapeEditUndoAction>().Count() == 2,"New owner was not attached once");
            foreach (var undo in record.Actions.OfType<ShapeEditUndoAction>())
                undo.Swap(null,ReferenceEquals(undo,action) ? owner : second);
            check(owner.Shapes[0].GetPoints()[0] == new PointF(10,10) && second.Shapes[0].GetPoints()[0] == new PointF(50,10),"New owner/reused owner snapshots lost original geometry");
        });
        test("key observation deduplicates filter dispatch but preserves identical held repeats", () =>
        {
            var gate = new KeyMessageObservation();
            var message = Message.Create(new IntPtr(100),0x104,new IntPtr((int)Keys.Right),new IntPtr(1L << 30));
            check(gate.ShouldObserve(message,true) && !gate.ShouldObserve(message,false),"Filter and dispatch both observed one message");
            check(gate.ShouldObserve(message,true) && !gate.ShouldObserve(message,false),"Identical next repeat was lost");
            check(gate.ShouldObserve(message,false),"Direct native dispatch was lost");
            check(gate.ShouldObserve(message,true) && gate.ShouldObserve(message,true) && !gate.ShouldObserve(message,false),"Preprocessed key prevented next repeat");
        });
        test("controller captures RecordAdded before native move then prepares each repeat before translation", () =>
        {
            var native = Native(100,100); var owner = Owner(); owner.Shapes.Add(Line(10,10,30)); owner.SelectedShapeIndices.Add(0);
            var doc = Document(native,owner); var controller = Controller(doc);
            doc.UndoStateChanged += (sender,args) => Invoke(controller,"NudgeUndoChanged",sender,args);
            var record = new GH_UndoRecord("Nudge Right",new[] { new GH_PivotAction(native) });
            doc.UndoServer.PushUndoRecord(record);
            check(Get(controller,"_nudge") != null && record.ActionCount == 1,"RecordAdded failed to capture session before move");
            check(owner.Shapes[0].GetPoints()[0] == new PointF(10,10),"RecordAdded moved annotations too early");
            native.Attributes.Pivot += new Size(1,0);
            // A queued first sample may still be pending when the next key arrives.
            Invoke(controller,"BeforeNativeKey",Keys.Alt | Keys.Right);
            check(owner.Shapes[0].GetPoints()[0] == new PointF(11,10) && record.ActionCount == 2,"BeforeKey failed to flush preceding native step");
            native.Attributes.Pivot += new Size(1,0); Invoke(controller,"QueuedNudge");
            check(owner.Shapes[0].GetPoints()[0] == new PointF(12,10) && record.ActionCount == 2,"Queued repeat missed prepared delta or duplicated action");
            Invoke(controller,"QueuedNudge");
            check(owner.Shapes[0].GetPoints()[0] == new PointF(12,10),"Queued sample applied twice");
            doc.Objects.Remove(native);
            check(!((AnnotationNudge)Get(controller,"_nudge")).Sync(doc),"Removed native target was accepted by real document overload");
        });
        test("controller nudge record guards clear sessions for unrelated operations records and documents", () =>
        {
            var native = Native(100,100); var owner = Owner(); owner.Shapes.Add(Line(10,10,30)); owner.SelectedShapeIndices.Add(0);
            var doc = Document(native,owner); var controller = Controller(doc);
            var record = new GH_UndoRecord("Nudge Right",new[] { new GH_PivotAction(native) });
            var nonPivot = new GH_UndoRecord("Nudge Right",new[] { new ShapeEditUndoAction(owner,new IGH_DocumentObject[0]) });
            var align = new GH_UndoRecord("Align",new[] { new GH_PivotAction(native) });
            foreach (var rejected in new[] {
                UndoEvent(doc,GH_UndoOperation.RecordAdded,null),
                UndoEvent(doc,GH_UndoOperation.RecordAdded,new GH_UndoRecord("Nudge Right")),
                UndoEvent(doc,GH_UndoOperation.RecordAdded,nonPivot), UndoEvent(doc,GH_UndoOperation.RecordAdded,align),
                UndoEvent(Document(),GH_UndoOperation.RecordAdded,record),
                UndoEvent(doc,GH_UndoOperation.Undo,record) })
            {
                Invoke(controller,"NudgeUndoChanged",doc,UndoEvent(doc,GH_UndoOperation.RecordAdded,record));
                check(Get(controller,"_nudge") != null,"Valid guard setup did not capture a session");
                Invoke(controller,"NudgeUndoChanged",doc,rejected);
                check(Get(controller,"_nudge") == null,"Rejected event retained a session");
            }
        });
        test("nudge entry points contain exceptions and clear pending sessions", () =>
        {
            var native = Native(100,100); var owner = Owner(); owner.Shapes.Add(Line(10,10,30)); owner.SelectedShapeIndices.Add(0);
            var doc = Document(native,owner); var controller = Controller(doc);
            var record = new GH_UndoRecord("Nudge Right",new[] { new GH_PivotAction(native) });
            doc.UndoServer.PushUndoRecord(record);
            var moving = AnnotationMove.MovingSet(new[] { owner },doc,true);
            var eventArgs = UndoEvent(doc,GH_UndoOperation.RecordAdded,record);
            foreach (var entry in new[] { "BeforeNativeKey", "NudgeUndoChanged", "QueuedNudge" })
            {
                Set(doc,"m_objects",new List<IGH_DocumentObject> { native,owner });
                Set(controller,"_nudge",new AnnotationNudge(moving,new[] { native },doc,record));
                Set(controller,"_nudgePending",true);
                Set(doc,"m_objects",null); // Real GH selection/bounds enumeration now throws.
                if (entry == "BeforeNativeKey") Invoke(controller,entry,Keys.Alt | Keys.Right);
                else if (entry == "NudgeUndoChanged") Invoke(controller,entry,doc,eventArgs);
                else Invoke(controller,entry);
                check(Get(controller,"_nudge") == null && !(bool)Get(controller,"_nudgePending"),entry+" leaked exception or left a live session");
                check(owner.Shapes[0].GetPoints()[0] == new PointF(10,10) && record.ActionCount == 1,"Failure mutated annotations/undo");
            }
        });
        test("shortcut observes sibling editor controls and contains a failing key observer", () =>
        {
            using (var form = new Form()) using (var canvas = new Control()) using (var sibling = new Button()) using (var foreign = new Control())
            {
                form.Controls.Add(canvas); form.Controls.Add(sibling);
                // Force HWNDs for the actual Control.FromChildHandle scope check without showing UI.
                var canvasWindow = canvas.Handle; var siblingWindow = sibling.Handle; var foreignWindow = foreign.Handle;
                using (var registration = CanvasDrawingShortcut.Register(canvas,() => false,() => {}))
                {
                    var hook = (CanvasDrawingShortcut)Get(registration,"_hook");
                    check(hook.ObservesKeyWindow(canvasWindow) && hook.ObservesKeyWindow(siblingWindow) && !hook.ObservesKeyWindow(foreignWindow),"KeyPreview scope missed sibling or included another window");
                    int resets = 0, calls = 0;
                    registration.BeforeKey = _ => { calls++; throw new InvalidOperationException("Injected observer failure"); };
                    registration.KeyFailed = () => resets++;
                    var key = Message.Create(siblingWindow,0x104,new IntPtr((int)Keys.Right),IntPtr.Zero);
                    check(!hook.PreFilterMessage(ref key) && calls == 1 && resets == 1,"Sibling key failure escaped or did not reset owner");
                }
            }
        });
    }
}
