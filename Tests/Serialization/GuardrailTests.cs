using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using hopperdraw;
using Rhino.Geometry;

internal static class GuardrailTests
{
    internal static void Run()
    {
        Program.Test("additive shape/component/list extensions survive XML/binary reads, edits, duplicate and undo", () =>
        {
            var c = new HopperDraw(); var group = new DrawShapeGroup();
            group.Children.Add(new LineShape(new Point3d(10, 10, 0), new Point3d(100, 10, 0))); c.Shapes.Add(group);
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
        Program.Test("snapshot undo swaps each mutation without losing IDs or selection state", () =>
        {
            var c = new HopperDraw();
            var inner = new DrawShapeGroup();
            inner.Children.Add(new LineShape(new Point3d(10, 10, 0), new Point3d(100, 10, 0)) { OverrideColor = Color.Red });
            var outer = new DrawShapeGroup(); outer.Children.Add(inner);
            c.Shapes.Add(outer);
            c.SelectedShapeIndices.Add(0);
            string before = Program.Fingerprint(outer);
            var action = new ShapeEditUndoAction(c, Enumerable.Empty<IGH_DocumentObject>());
            Program.Check(!action.ExpiresSolution && !action.ExpiresDisplay, "undo expires solution/display");
            c.SetSelectedColor(Color.Blue);
            Program.Check(c.SelectedShapeIndices.SequenceEqual(new[] { 0 }), "style edit changed selection");
            string after = Program.Fingerprint(c.Shapes[0]);
            Program.Check(before != after, "nested style mutation did not change snapshot");
            Program.Check(((DrawShapeGroup)((DrawShapeGroup)c.Shapes[0]).Children[0]).Children[0].OverrideColor == Color.Blue, "nested leaf style did not change");
            action.Swap(null, c);
            // Snapshot restore intentionally clears selection; recursive fingerprints include IDs and styles.
            Program.Check(Program.Fingerprint(c.Shapes[0]) == before && !c.HasSelection, "undo changed IDs/styles or retained stale selection");
            c.SelectedShapeIndices.Add(0);
            action.Swap(null, c);
            Program.Check(Program.Fingerprint(c.Shapes[0]) == after && !c.HasSelection, "redo changed IDs/styles or retained stale selection");
        });
        Program.Test("DrawOrder channels and grouped child styles render to real bitmaps", () =>
        {
            var c = new HopperDraw { LineThickness = 4 };
            var group = new DrawShapeGroup { OverrideColor = Color.Lime, ThicknessMultiplier = 9 };
            group.Children.Add(new LineShape(new Point3d(20, 50, 0), new Point3d(200, 50, 0)) { OverrideColor = Color.Red });
            group.Children.Add(new LineShape(new Point3d(20, 100, 0), new Point3d(200, 100, 0)) { OverrideColor = Color.Blue, ThicknessMultiplier = 3 });
            c.Shapes.Add(group);
            var render = typeof(HopperDrawUIAttributes).GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Instance);
            using (var canvas = new GH_Canvas())
            using (var bitmap = new Bitmap(250, 200))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                foreach (int order in new[] { 0, 1 })
                {
                    c.DrawOrder = order;
                    foreach (var channel in new[] { GH_CanvasChannel.Groups, GH_CanvasChannel.Overlay })
                    {
                        graphics.Clear(Color.White);
                        render.Invoke(c.Attributes, new object[] { canvas, graphics, channel });
                        bool expected = order == 0 ? channel == GH_CanvasChannel.Groups : channel == GH_CanvasChannel.Overlay;
                        Program.Check((bitmap.GetPixel(100, 50).ToArgb() == Color.Red.ToArgb()) == expected, "wrong render channel");
                        if (expected)
                        {
                            Program.Check(bitmap.GetPixel(100, 100).ToArgb() == Color.Blue.ToArgb(), "group flattened color");
                            Program.Check(bitmap.GetPixel(100, 104).ToArgb() == Color.Blue.ToArgb() && bitmap.GetPixel(100, 54).ToArgb() == Color.White.ToArgb(), "group flattened width");
                        }
                    }
                }
            }
        });
        Program.Test("real controller interaction teardown releases the canvas after mouse-up and Escape",()=>
        {
            foreach (bool cancel in new[]{false,true})
            using (var canvas=new GH_Canvas())
            {
                var owner=new HopperDraw(); owner.SetTool(1);
                var controller=CanvasAnnotationController.Register(canvas,owner);
                try
                {
                    var press=new GH_CanvasMouseEvent(canvas.Viewport,new MouseEventArgs(MouseButtons.Left,1,10,10,0));
                    var interaction=new HopperDrawInteraction(controller,canvas,press,null,-1,-1,owner);
                    canvas.ActiveInteraction=interaction;
                    interaction.RespondToMouseDown(canvas,press);
                    if (cancel) interaction.Cancel(true);
                    else interaction.RespondToMouseUp(canvas,press);
                    // GH calls Destroy before clearing ActiveInteraction. An exception here traps all later input.
                    canvas.ActiveInteraction=null;
                    Program.Check(canvas.ActiveInteraction==null,"Completed interaction still captures the canvas");
                    Program.Check(owner.Draft.Active==!cancel,"Teardown changed draft cancellation semantics");
                    owner.ExitDrawing();
                }
                finally { controller.Remove(owner); }
            }
        });
    }
}
