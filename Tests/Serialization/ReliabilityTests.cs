using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using GH_IO.Serialization;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using hopperdraw;
using Rhino.Geometry;

internal static class ReliabilityTests
{
    internal static void Run()
    {
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
