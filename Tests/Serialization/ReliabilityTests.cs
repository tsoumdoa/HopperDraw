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
}
