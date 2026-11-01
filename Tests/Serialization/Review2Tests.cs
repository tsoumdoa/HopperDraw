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
