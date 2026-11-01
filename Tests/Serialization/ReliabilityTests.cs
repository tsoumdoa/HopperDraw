using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Linq;
using GH_IO.Serialization;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using hopperdraw;
using Rhino.Geometry;

internal static class ReliabilityTests
{
    internal static void Run()
    {
        Program.Test("snapshot undo swaps each mutation without losing IDs or selection state", () =>
        {
            Action<HopperDraw>[] edits = {
                c => c.Shapes.Add(new LineShape(new Point3d(3, 4, 0), new Point3d(9, 8, 0))),
                c => Invoke(c, "DeleteSelectedShapes"),
                c => c.ClearAll(),
                c => Invoke(c, "SetThicknessMultiplier", 3f),
                c => Invoke(c, "CycleLineType"),
                c => c.SetSelectedColor(Color.Blue),
                c => c.GroupSelectedShapes(),
                c => { c.GroupSelectedShapes(); c.UngroupSelectedGroups(); },
                c => Invoke(c, "DuplicateSelectedShapes"),
                c => c.Shapes[0].Move(30, 40),
                c => c.Shapes[0].MovePoint(0, new Point3d(123, 456, 0))
            };
            foreach (var edit in edits)
            {
                var c = new HopperDraw();
                c.Shapes.Add(new LineShape(new Point3d(10, 10, 0), new Point3d(100, 10, 0)));
                c.Shapes.Add(new LineShape(new Point3d(10, 30, 0), new Point3d(100, 30, 0)));
                c.SelectedShapeIndices.AddRange(new[] { 0, 1 });
                string before = string.Join("|", c.Shapes.Select(Program.Fingerprint));
                var action = new ShapeEditUndoAction(c, Enumerable.Empty<IGH_DocumentObject>());
                Program.Check(!action.ExpiresSolution && !action.ExpiresDisplay, "undo expires solution/display");
                edit(c);
                string after = string.Join("|", c.Shapes.Select(Program.Fingerprint));
                action.Swap(null, c);
                Program.Check(string.Join("|", c.Shapes.Select(Program.Fingerprint)) == before && !c.HasSelection, "undo changed original snapshot");
                action.Swap(null, c);
                Program.Check(string.Join("|", c.Shapes.Select(Program.Fingerprint)) == after && !c.HasSelection, "redo changed edited snapshot");
            }
        });
        Program.Test("opaque records survive undo snapshots and warnings resolve after clear", () =>
        {
            var c = new HopperDraw(); c.Shapes.Add(new LineShape(new Point3d(10, 10, 0), new Point3d(100, 10, 0)));
            var root = Program.Save(c); ((GH_IWriter)root.FindChunk("HopperDraw").FindChunk("Shapes").FindChunk("Shape", 0)).RemoveItem("X");
            c = Program.Load(root);
            string before = Program.Fingerprint(c.Shapes[0]);
            var action = new ShapeEditUndoAction(c, Enumerable.Empty<IGH_DocumentObject>());
            c.ClearAll(); Program.Check(c.LoadWarning == null && c.RuntimeMessages(GH_RuntimeMessageLevel.Warning).Count == 0, "resolved warning remains");
            action.Swap(null, c); Program.Check(Program.Fingerprint(c.Shapes[0]) == before && c.LoadWarning != null, "opaque snapshot not exact");
        });
        Program.Test("drag snapshots require real geometry changes and reset on interruption", () =>
        {
            var c = new HopperDraw(); c.Shapes.Add(new LineShape(new Point3d(10, 10, 0), new Point3d(100, 10, 0)));
            Set(c, "_dragShapeIndex", 0); Set(c, "_dragPointIndex", -1); Set(c, "_dragOffset", new Point3d(50, 10, 0)); Set(c, "_isDragging", true);
            Invoke(c, "DragShapeTo", new Point3d(50, 10, 0)); Program.Check(!(bool)Get(c, "_dragUndoRecorded"), "same-position event recorded undo");
            Invoke(c, "DragShapeTo", new Point3d(60, 10, 0)); Program.Check((bool)Get(c, "_dragUndoRecorded"), "movement not recorded");
            foreach (string method in new[] { "CancelDrawing", "Canvas_LostFocus", "Canvas_MouseCaptureChanged", "EndShapeGesture" })
            {
                Set(c, "_dragUndoRecorded", true); Set(c, "_isDragging", true);
                if (method.StartsWith("Canvas_")) Invoke(c, method, null, EventArgs.Empty); else Invoke(c, method);
                Program.Check(!(bool)Get(c, "_dragUndoRecorded") && !(bool)Get(c, "_isDragging"), "gesture survived " + method);
            }
            Set(c, "_dragShapeIndex", 0); Set(c, "_dragPointIndex", 0); Set(c, "_isDragging", true);
            var line = (LineShape)c.Shapes[0]; Invoke(c, "DragShapeTo", line.Start); Program.Check(!(bool)Get(c, "_dragUndoRecorded"), "unchanged handle recorded undo");
            Invoke(c, "DragShapeTo", new Point3d(30, 30, 0)); Program.Check((bool)Get(c, "_dragUndoRecorded"), "second gesture lost undo");
            c.RestoreShapes(ShapeEditUndoAction.Snapshot(c.Shapes)); Program.Check(!(bool)Get(c, "_dragUndoRecorded"), "restore retained drag marker");
        });
        Program.Test("group order, child styles, clone styles and reference-based ungroup selection", () =>
        {
            var c = new HopperDraw();
            foreach (var color in new[] { Color.Red, Color.Green, Color.Blue, Color.Black })
                c.Shapes.Add(new LineShape(new Point3d(10, 10, 0), new Point3d(100, 10, 0)) { OverrideColor = color, ThicknessMultiplier = 2, LineType = DashStyle.Dot });
            var red = c.Shapes[0]; var blue = c.Shapes[2]; var black = c.Shapes[3];
            c.SelectedShapeIndices.AddRange(new[] { 2, 0 }); var group = c.GroupSelectedShapes();
            Program.Check(c.Shapes.IndexOf(group) == 1 && group.Children.SequenceEqual(new[] { red, blue }) && c.Shapes[2] == black, "group order changed");
            var clone = (DrawShapeGroup)group.Clone();
            Program.Check(clone.Children[1].OverrideColor == Color.Blue && clone.Children[1].ThicknessMultiplier == 2 && clone.Children[1].LineType == DashStyle.Dot, "clone styles lost");
            c.Shapes.Insert(0, clone); c.SelectedShapeIndices.Clear(); c.SelectedShapeIndices.AddRange(new[] { 0, 2, 3 });
            c.UngroupSelectedGroups();
            Program.Check(c.SelectedShapeIndices.SequenceEqual(new[] { 0, 1, 3, 4, 5 }) && c.Shapes[5] == black, "ungroup selection shifted");
        });
        Program.Test("horizontal, vertical, coincident and nested group bounds retain zero-area geometry", () =>
        {
            foreach (var delta in new[] { new Point3d(100, 0, 0), new Point3d(0, 100, 0), new Point3d(0, 0, 0) })
            {
                var inner = new DrawShapeGroup(); inner.Children.Add(new LineShape(new Point3d(10, 20, 0), new Point3d(10 + delta.X, 20 + delta.Y, 0)));
                var outer = new DrawShapeGroup(); outer.Children.Add(inner);
                Program.Check(inner.GetPoints().Length == 4 && outer.GetPoints().Length == 4 && outer.GetBoundingBox().X == 10 && outer.GetBoundingBox().Y == 20, "zero-area geometry lost");
            }
            Program.Check(new DrawShapeGroup().GetPoints().Length == 0, "empty group invented geometry");
        });
        Program.Test("topmost picking respects child multipliers and screen-space zoom tolerance", () =>
        {
            using (var canvas = new GH_Canvas())
            {
                var c = new HopperDraw { LineThickness = 8 };
                var line = new LineShape(new Point3d(0, 100, 0), new Point3d(500, 100, 0)) { ThicknessMultiplier = 3 };
                c.Shapes.Add(line); var group = new DrawShapeGroup(); group.Children.Add(line.Clone()); c.Shapes.Add(group);
                typeof(HopperDraw).GetField("_canvas", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(c, canvas);
                Program.Check(c.HitTest(new PointF(250, 100)).shapeIndex == 1, "picked bottom shape");
                foreach (float zoom in new[] { 0.25f, 1, 4 })
                {
                    canvas.Viewport.Zoom = zoom;
                    Program.Check(c.HitTest(new PointF(250, 100 + 12 + 14 / zoom)).shapeIndex == 1, "zoom hit missed");
                    Program.Check(c.HitTest(new PointF(250, 100 + 12 + 16 / zoom)).shapeIndex == -1, "zoom tolerance too large");
                }
            }
        });
        Program.Test("input values clamp with runtime warnings, including double.MaxValue", () =>
        {
            var solve = typeof(HopperDraw).GetMethod("SolveInstance", BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (double width in new[] { double.MaxValue, double.NaN, double.PositiveInfinity, -1, 0, 1e-100 })
            {
                var c = new HopperDraw();
                solve.Invoke(c, new object[] { new FakeAccess(false, true, 99, Color.Black, width, -1, 99) });
                Program.Check(c.DrawMode == 3 && c.DrawOrder == 1 && StyleValidation.ValidWidth(c.LineThickness), "unsafe input");
                Program.Check(c.RuntimeMessages(GH_RuntimeMessageLevel.Warning).Count >= 4, "missing input warnings");
            }
        });
        Program.Test("invalid serialized styles remain opaque with persistent warnings", () =>
        {
            foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.MaxValue, -1, 0, 1e-100 })
            {
                var c = new HopperDraw(); c.Shapes.Add(new LineShape(new Point3d(1, 1, 0), new Point3d(20, 20, 0)));
                var root = Program.Save(c); var r = (GH_IWriter)root.FindChunk("HopperDraw").FindChunk("Shapes").FindChunk("Shape", 0);
                r.RemoveItem("ThickMult"); r.SetDouble("ThickMult", value);
                var loaded = Program.Load(root);
                Program.Check(loaded.Shapes[0] is OpaqueShape && loaded.LoadWarning != null, "unsafe style accepted");
                Program.Check(Program.RoundTrip(loaded, true).Shapes[0] is OpaqueShape, "unsafe payload erased");
            }
        });
        Program.Test("renderer guards invalid dash, widths and geometry before first solve", () =>
        {
            using (var bitmap = new Bitmap(200, 200))
            using (var g = Graphics.FromImage(bitmap))
            using (var canvas = new GH_Canvas())
            {
                var c = new HopperDraw();
                c.Shapes.Add(new LineShape(new Point3d(5, 5, 0), new Point3d(100, 100, 0)) { LineType = (DashStyle)int.MaxValue, ThicknessMultiplier = float.MaxValue });
                c.Shapes.Add(new LineShape(new Point3d(double.MaxValue, 1, 0), Point3d.Unset));
                foreach (float width in new[] { float.NaN, float.PositiveInfinity, float.MaxValue, 0, -1 })
                    foreach (var shape in c.Shapes) ShapeRenderer.Render(g, shape, Color.Black, width);
                Program.Check(StyleValidation.EffectiveWidth(float.MaxValue, float.MaxValue) <= 1000, "effective ceiling ignored");
                try { GH_Skin.LoadSkin(); } catch { }
                foreach (var field in typeof(GH_Skin).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    if (field.FieldType == typeof(GH_PaletteStyle) && field.GetValue(null) == null)
                        field.SetValue(null, new GH_PaletteStyle(Color.Gray, Color.Black, Color.White));
                typeof(GH_Canvas).GetField("_graphics", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(canvas, g);
                c.Attributes.PerformLayout();
                var render = typeof(HopperDrawUIAttributes).GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Instance);
                render.Invoke(c.Attributes, new object[] { canvas, g, GH_CanvasChannel.Objects });
                var preview = HopperDrawUIAttributes.BuildPreview(c, new Point3d(double.MaxValue, 1, 0));
                ShapeRenderer.Render(g, preview, Color.Black, float.PositiveInfinity);
            }
        });
    }
    private static object Get(HopperDraw c, string field) => typeof(HopperDraw).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(c);
    private static void Set(HopperDraw c, string field, object value) => typeof(HopperDraw).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(c, value);
    private static void Invoke(HopperDraw c, string method, params object[] args) => typeof(HopperDraw).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(c, args);
}
