using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using hopperdraw;
using Rhino.Geometry;

internal static class Review2Tests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(HopperDraw c, string name, object value) => typeof(HopperDraw).GetField(name, Private).SetValue(c, value);
    private static object Get(HopperDraw c, string name) => typeof(HopperDraw).GetField(name, Private).GetValue(c);
    private static object Call(HopperDraw c, string name, params object[] args) => typeof(HopperDraw).GetMethod(name, Private).Invoke(c, args);
    private static LineShape Line() => new LineShape(new Point3d(10, 10, 0), new Point3d(100, 10, 0));
    private static OpaqueShape Opaque()
    {
        var record = new GH_LooseChunk("Shape"); record.SetInt32("Kind", 99); record.SetGuid("Id", Guid.NewGuid());
        record.SetDoubleArray("X", new[] { 10d, 100d }); record.SetDoubleArray("Y", new[] { 20d, 20d });
        return new OpaqueShape(record, "Unreadable test record.");
    }
    private static HopperDraw ReadOnly()
    {
        var root = Program.Save(new HopperDraw()); var payload = (GH_IWriter)root.FindChunk("HopperDraw");
        payload.RemoveItem("FormatVersion"); payload.SetInt32("FormatVersion", 99);
        return Program.Load(root);
    }
    internal static void Run()
    {
        Program.Test("missing annotation chunk loads empty/editable with a one-time notice and no metadata nesting", () =>
        {
            var root = Program.Save(new HopperDraw()); root.RemoveChunk("HopperDraw");
            root.SetInt32("ShapeCount", 123); // Unsupported pre-v1 data must never be interpreted or captured.
            var c = Program.Load(root);
            Program.Check(c.CanEditShapes && c.Shapes.Count == 0 && c.LoadWarning == null, "missing payload locked component");
            Program.Check(c.RuntimeMessages(GH_RuntimeMessageLevel.Warning).Any(m => m.Contains("pre-v1")), "missing one-time notice");
            var saved = Program.Save(c).FindChunk("HopperDraw");
            Program.Check(!saved.ChunkExists("Attributes") && !saved.ChunkExists("param_input", 0) && !saved.ItemExists("ShapeCount"), "nested GH/legacy metadata");
            c.Shapes.Add(Line());
            c.ClearRuntimeMessages();
            typeof(HopperDraw).GetMethod("SolveInstance", Private).Invoke(c, new object[] { new FakeAccess(false, true, 0, Color.Black, 8d, 0, 1) });
            Program.Check(c.RuntimeMessages(GH_RuntimeMessageLevel.Warning).Count == 0, "one-time notice repeated on solve");
            Program.Check(Program.RoundTrip(c, true).CanEditShapes, "new annotation data not editable");
        });
        Program.Test("missing written style fields retain defaults with persistent diagnostics", () =>
        {
            foreach (string field in new[] { "ThickMult", "LineType", "HasColor" })
            {
                var c = new HopperDraw(); c.Shapes.Add(Line()); var root = Program.Save(c);
                ((GH_IWriter)root.FindChunk("HopperDraw").FindChunk("Shapes").FindChunk("Shape", 0)).RemoveItem(field);
                c = Program.Load(root);
                Program.Check(c.Shapes[0] is LineShape && c.LoadWarning.Contains(field) && c.Shapes[0].ThicknessMultiplier == 1, "missing shape style silently defaulted");
                c.SelectedShapeIndices.Add(0); c.SetSelectedColor(Color.Red);
                Program.Check(c.LoadWarning.Contains(field), "edit erased shape diagnostic");
            }
            foreach (string field in new[] { "Color", "Thickness", "DrawOrder", "Visible" })
            {
                var c = new HopperDraw(); c.Shapes.Add(Line()); var root = Program.Save(c);
                ((GH_IWriter)root.FindChunk("HopperDraw")).RemoveItem(field);
                c = Program.Load(root);
                Program.Check(c.CanEditShapes && c.LoadWarning.Contains(field) && c.LineThickness == 8 && c.DrawOrder == 1 && c.Visible, "missing component style silently defaulted");
                c.SelectedShapeIndices.Add(0); c.SetSelectedColor(Color.Red);
                Program.Check(c.LoadWarning.Contains(field), "edit erased component diagnostic");
                c.ClearRuntimeMessages(); Call(c, "SolveInstance", new FakeAccess(false, true, 0, Color.Black, 8d, 0, 1));
                Program.Check(c.RuntimeMessages(GH_RuntimeMessageLevel.Warning).Any(m => m.Contains(field)), "solve lost component warning");
                Program.Check(c.Read(Program.Save(new HopperDraw())) && c.LoadWarning == null, "successful read retained old diagnostic");
            }
        });
        Program.Test("component diagnostics persist through edits and are cleared/restored by clear undo", () =>
        {
            var c = new HopperDraw(); c.Shapes.Add(Line()); var root = Program.Save(c); var payload = (GH_IWriter)root.FindChunk("HopperDraw");
            payload.RemoveItem("Thickness"); payload.SetDouble("Thickness", double.MaxValue);
            c = Program.Load(root); string warning = c.LoadWarning;
            c.SelectedShapeIndices.Add(0); c.SetSelectedColor(Color.Blue);
            Program.Check(c.LoadWarning == warning, "first edit erased component diagnostic");
            var undo = new ShapeEditUndoAction(c, Enumerable.Empty<IGH_DocumentObject>());
            c.ClearAll(); Program.Check(c.LoadWarning == null, "clear retained diagnostics");
            undo.Swap(null, c); Program.Check(c.LoadWarning == warning, "clear undo lost component diagnostic");
            undo.Swap(null, c); Program.Check(c.LoadWarning == null, "clear redo retained diagnostic");
        });
        Program.Test("additive shape/component/list extensions survive XML/binary reads, edits, duplicate and undo", () =>
        {
            var c = new HopperDraw(); var group = new DrawShapeGroup(); group.Children.Add(Line()); c.Shapes.Add(group);
            var root = Program.Save(c); var payload = (GH_Chunk)root.FindChunk("HopperDraw");
            payload.SetDouble("FutureOpacity", 0.375); payload.SetString("Color", 7, "indexed extension");
            payload.CreateChunk("Extension", 9).CreateChunk("Nested").SetByteArray("Bytes", new byte[] { 0, 2, 255 });
            var list = (GH_Chunk)payload.FindChunk("Shapes"); list.SetString("FutureListMode", "preserve"); list.CreateChunk("IndexExtension").SetInt32("Value", 42);
            var record = (GH_Chunk)list.FindChunk("Shape", 0); record.SetString("GroupFeature", "future group style");
            var children = (GH_Chunk)record.FindChunk("Children"); children.SetDouble("ChildFeature", 0.75); children.CreateChunk("ChildIndexExtension").SetInt32("Value", 3);
            var line = (GH_Chunk)children.FindChunk("Shape", 0); line.SetDouble("Opacity", 0.5); line.SetString("Kind", 13, "indexed shape key");
            line.CreateChunk("Extension").SetString("Label", "future line metadata");
            c = Program.Load(root); Program.Check(c.CanEditShapes && c.LoadWarning == null, "additive extension made v1 unreadable");
            var snapshot = new ShapeEditUndoAction(c, Enumerable.Empty<IGH_DocumentObject>());
            c.Shapes[0].Move(25, 10); snapshot.Swap(null, c); snapshot.Swap(null, c);
            c.Shapes.Add(c.Shapes[0].Clone()); c.BorderColor = Color.Red;
            foreach (bool binary in new[] { false, true })
            {
                var copy = Program.RoundTrip(Program.RoundTrip(c, binary), binary);
                Program.Check(copy.CanEditShapes && copy.LoadWarning == null && copy.BorderColor.ToArgb() == Color.Red.ToArgb(), "extension affected normal edits");
                var saved = Program.Save(copy).FindChunk("HopperDraw");
                Program.Check(saved.GetDouble("FutureOpacity") == 0.375 && saved.GetString("Color", 7) == "indexed extension", "component unknown items lost");
                Program.Check(saved.FindChunk("Extension", 9).FindChunk("Nested").GetByteArray("Bytes").SequenceEqual(new byte[] { 0, 2, 255 }), "component unknown chunks lost");
                var savedList = saved.FindChunk("Shapes");
                Program.Check(savedList.GetString("FutureListMode") == "preserve" && savedList.FindChunk("IndexExtension").GetInt32("Value") == 42, "list extensions lost");
                for (int i = 0; i < 2; i++)
                {
                    var savedGroup = savedList.FindChunk("Shape", i); var savedChildren = savedGroup.FindChunk("Children"); var savedLine = savedChildren.FindChunk("Shape", 0);
                    Program.Check(savedGroup.GetString("GroupFeature") == "future group style" && savedChildren.GetDouble("ChildFeature") == 0.75 && savedChildren.FindChunk("ChildIndexExtension").GetInt32("Value") == 3, "group extensions lost");
                    Program.Check(savedLine.GetDouble("Opacity") == 0.5 && savedLine.GetString("Kind", 13) == "indexed shape key" && savedLine.FindChunk("Extension").GetString("Label") == "future line metadata", "leaf extensions lost");
                    Program.Check(savedLine.GetDoubleArray("X")[0] == 35, "extension restored old geometry");
                }
                Program.Check(copy.Shapes[0].PersistentId != copy.Shapes[1].PersistentId, "extension copied known ID on duplicate");
            }
        });
        Program.Test("read-only components cannot win fallback, selected, or editing ownership", () =>
        {
            var opaque = ReadOnly(); var editable = new HopperDraw();
            Set(opaque, "_isActivated", true); Set(editable, "_isActivated", true);
            opaque.Attributes.Selected = true; opaque.SelectedShapeIndices.Add(0);
            Program.Check(ReferenceEquals(HopperDraw.ChooseInputOwner(new[] { opaque, editable }), editable), "read-only owner stole input");
            Program.Check(HopperDraw.ChooseInputOwner(new[] { opaque }) == null, "read-only owner accepted");
            editable.Attributes.Selected = true;
            Program.Check(ReferenceEquals(HopperDraw.ChooseInputOwner(new[] { opaque, editable }), editable), "selected editable owner lost input");
        });
        Program.Test("stale read-only capturing owner is released and cannot enter drawing", () =>
        {
            var opaque = ReadOnly(); Set(opaque, "_isActivated", true); Set(opaque, "_drawingModeEnabled", true);
            var capture = typeof(HopperDraw).GetField("_capturingOwner", BindingFlags.Static | BindingFlags.NonPublic);
            capture.SetValue(null, opaque);
            try
            {
                Call(new HopperDraw(), "IsInputOwner");
                Program.Check(capture.GetValue(null) == null && !opaque.DrawingModeEnabled, "read-only capture survived");
                opaque.EnterDrawingMode();
                Program.Check(capture.GetValue(null) == null, "read-only owner acquired capture");
            }
            finally { capture.SetValue(null, null); }
        });
        Program.Test("opaque descendant blocks model and component body/handle transforms atomically", () =>
        {
            var nested = new DrawShapeGroup(); nested.Children.Add(Opaque());
            var group = new DrawShapeGroup(); group.Children.Add(Line()); group.Children.Add(nested);
            string before = Program.Fingerprint(group);
            foreach (Action edit in new Action[] { () => group.Move(500, 0), () => group.MovePoint(0, new Point3d(500, 0, 0)) })
            {
                bool blocked = false; try { edit(); } catch (InvalidOperationException) { blocked = true; }
                Program.Check(blocked && before == Program.Fingerprint(group), "model partially transformed");
            }
            var c = new HopperDraw(); c.Shapes.Add(group); Set(c, "_dragShapeIndex", 0); Set(c, "_dragOffset", new Point3d(0, 0, 0));
            foreach (int handle in new[] { -1, 0 })
            {
                Set(c, "_dragPointIndex", handle); Call(c, "DragShapeTo", new Point3d(500, 0, 0));
                Program.Check(before == Program.Fingerprint(group) && !(bool)Get(c, "_dragUndoRecorded"), "drag partially transformed or recorded undo");
            }
            Program.Check(c.RuntimeMessages(GH_RuntimeMessageLevel.Warning).Any(m => m.Contains("blocked")), "blocked edit gave no feedback");
        });
        Program.Test("multi-drag blocks entire selection when any subtree is opaque", () =>
        {
            using (var canvas = new GH_Canvas())
            {
                var group = new DrawShapeGroup(); group.Children.Add(Line()); group.Children.Add(Opaque());
                var c = new HopperDraw(); c.Shapes.Add(Line()); c.Shapes.Add(group); c.SelectedShapeIndices.AddRange(new[] { 0, 1 });
                string before = string.Join("|", c.Shapes.Select(Program.Fingerprint));
                Set(c, "_canvas", canvas); Set(c, "_isActivated", true); Set(c, "_drawingModeEnabled", true); Set(c, "_isMultiDragging", true); Set(c, "_multiDragStart", new Point3d(0, 0, 0));
                var capture = typeof(HopperDraw).GetField("_capturingOwner", BindingFlags.Static | BindingFlags.NonPublic); capture.SetValue(null, c);
                try
                {
                    Call(c, "Canvas_MouseMove", canvas, new MouseEventArgs(MouseButtons.Left, 0, 500, 0, 0));
                    Program.Check(before == string.Join("|", c.Shapes.Select(Program.Fingerprint)) && !(bool)Get(c, "_dragUndoRecorded"), "mixed selection partially moved");
                }
                finally { capture.SetValue(null, null); }
            }
        });
        Program.Test("selected opaque record deletion is undoable and preserves neighbors", () =>
        {
            var c = new HopperDraw(); c.Shapes.Add(Line()); c.Shapes.Add(Opaque()); c.Shapes.Add(Line()); c.SelectedShapeIndices.Add(1);
            string opaque = Program.Fingerprint(c.Shapes[1]);
            var undo = new ShapeEditUndoAction(c, Enumerable.Empty<IGH_DocumentObject>());
            Call(c, "DeleteSelectedShapes"); Program.Check(c.Shapes.Count == 2 && c.Shapes.All(s => s is LineShape), "selective deletion failed");
            undo.Swap(null, c); Program.Check(c.Shapes.Count == 3 && Program.Fingerprint(c.Shapes[1]) == opaque, "undo lost opaque data/slot");
        });
        Program.Test("preview handle squares do not add a dashed selection path", () =>
        {
            using (var bitmap = new Bitmap(200, 100))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.White); ShapeRenderer.Handles(g, Line(), -1, true, 1, drawOutline: false);
                Program.Check(bitmap.GetPixel(50, 10).ToArgb() == Color.White.ToArgb(), "preview drew selection outline");
                Program.Check(bitmap.GetPixel(6, 10).ToArgb() != Color.White.ToArgb(), "preview lost handle squares");
            }
        });
    }
}
