using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using hopperdraw;
using Rhino.Geometry;

internal static class ReviewRegressionTests
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static FieldInfo Field(string name) => typeof(CanvasAnnotationController).GetField(name, Private);
    private static ToolStripMenuItem Find(ToolStripItemCollection items, string name) =>
        items.OfType<ToolStripMenuItem>().Single(i => i.Name == name);
    private static int Ink(TextShape text, bool tail = false)
    {
        using (var bitmap = new Bitmap(500, 500))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            if (tail) graphics.TranslateTransform(500 - text.Layout.Bounds.Right, 0);
            TextLayoutService.Paint(graphics, text, Color.Black);
            int count = 0;
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                    if (bitmap.GetPixel(x, y).R < 200) count++;
            return count;
        }
    }
    internal static void Run()
    {
        Program.Test("long CJK Arabic and mixed paragraphs layout pick and paint without a width cap", () =>
        {
            foreach (string value in new[] { new string('\u4e2d', 32001), new string('\u0628', 40001),
                string.Concat(Enumerable.Repeat("Latin\u4e2d\u0628", 6000)) })
            {
                var text = new TextShape { Text = value };
                var layout = text.Layout;
                var half = new TextShape { Text = value.Substring(0, value.Length / 2) }.Layout;
                Program.Check(layout.Bounds.Width > half.Bounds.Width * 1.8f, "width capped with length");
                Program.Check(layout.Bounds.Width > layout.Em * 8192, "fallback-font width capped");
                Program.Check(layout.Runs[0].All(run => run.Text.Length <= 1024), "oversized paint run");
                float measured = layout.Runs[0].Sum(run => run.Width);
                Program.Check(Math.Abs(layout.Bounds.Width - measured - 2 * layout.Padding) < 1, "bounds below painted width");
                Program.Check(AnnotationGeometry.Hit(text, new PointF(layout.Bounds.Right - 10, 10), 5, 8), "long tail unpickable");
                Program.Check(Ink(text, true) > 10, "paint missing at measured tail");
            }
        });
        Program.Test("narrow text paints after menu size and preset changes with compound undo", () =>
        {
            foreach (string command in new[] { "4x", "Title" })
            {
                var owner = new HopperDraw();
                var text = new TextShape { Text = "MMMM", BoxWidth = 40 };
                owner.Shapes.Add(text);
                owner.SelectedShapeIndices.Add(0);
                using (var menu = new ContextMenuStrip())
                {
                    AnnotationMenuBuilder.Selection(menu.Items, new[] { owner });
                    var typography = Find(menu.Items, "Text");
                    Find(Find(typography.DropDownItems, command == "4x" ? "Size" : "Style").DropDownItems, command).PerformClick();
                }
                Program.Check(text.BoxWidth >= text.MinBoxWidth && Ink(text) > 20, "menu lost glyphs or did not clamp box");
                text.BoxWidth = 40;
                int records = 0;
                AnnotationCommands.Apply(new[] { owner }, "Typography", leaf => leaf is TextShape,
                    leaf => { var t = (TextShape)leaf; t.SizeMultiplier = 4; t.BoxWidth = t.ClampBoxWidth(t.BoxWidth.Value); },
                    (owners, name) => { records++; return true; });
                Program.Check(records == 1, "compound size/box change recorded twice");
                text.BoxWidth = 1;
                var loaded = (TextShape)Program.RoundTrip(owner, false).Shapes[0];
                Program.Check(loaded.BoxWidth == 1 && loaded.Layout.Bounds.Width >= loaded.MinBoxWidth - .001 && Ink(loaded) > 20,
                    "reader changed width or layout lost glyphs");
            }
        });
        Program.Test("boxed text moves and duplicates validate the right edge atomically", () =>
        {
            var owner = new HopperDraw();
            var text = new TextShape { Text = "edge", Anchor = new Point3d(9999940, 0, 0), BoxWidth = 50 };
            var group = new DrawShapeGroup();
            group.Children.Add(text);
            owner.Shapes.Add(group);
            owner.SelectedShapeIndices.Add(0);
            var move = new AnnotationMove(new[] { owner }.Where(item => item.HasSelection).ToDictionary(item => item, item => item.SelectedShapeIndices.ToArray()), new IGH_DocumentObject[0], null);
            move.Translate(new PointF(30, 0), false);
            Program.Check(!move.Changed && ReferenceEquals(owner.Shapes[0], group), "move accepted invalid box edge");
            AnnotationCommands.Duplicate(new[] { owner });
            Program.Check(owner.Shapes.Count == 1 && owner.SelectedShapeIndices.SequenceEqual(new[] { 0 }), "duplicate changed selection");
            Program.Check(((DrawShapeGroup)Program.RoundTrip(owner, false).Shapes[0]).Children[0] is TextShape, "box became opaque");
        });
        Program.Test("double-click press retains interaction and controller owner abort is scoped", () =>
        {
            using (var canvas = new GH_Canvas())
            {
                var owner = new HopperDraw();
                var peer = new HopperDraw();
                var text = new TextShape { Text = "original", Anchor = new Point3d(20, 20, 0) };
                owner.Shapes.Add(text);
                var controller = CanvasAnnotationController.Register(canvas, owner);
                try
                {
                    typeof(HopperDraw).GetField("_isActivated", Private).SetValue(owner, true);
                    // Force font creation to fail before a control can be published.
                    bool failed = false;
                    try { controller.BeginText(owner, new TextShape { Text = "font failure", SizeMultiplier = double.NaN }, true); }
                    catch (ArgumentException) { failed = true; }
                    Program.Check(failed && Field("_text").GetValue(controller) == null && Field("_editor").GetValue(controller) == null,
                        "failed editor creation left a partial session");
                    var point = canvas.Viewport.ProjectPoint(new PointF(25, 25));
                    var mouse = new GH_CanvasMouseEvent(canvas.Viewport,
                        new MouseEventArgs(MouseButtons.Left, 2, (int)point.X, (int)point.Y, 0));
                    var interaction = new HopperDrawInteraction(controller, canvas, mouse, owner, 0, -1, null);
                    Program.Check(interaction.RespondToMouseDown(canvas, mouse) == GH_ObjectResponse.Handled,
                        "double-click press released to GH relay handler");
                    Program.Check(Field("_text").GetValue(controller) == null, "editor focused before double-click dispatch");
                    Program.Check(ReferenceEquals(typeof(HopperDrawInteraction).GetField("_pendingText", Private).GetValue(interaction), text),
                        "double-click probe missed text");
                    var session = new TextEditSession(owner, new object(), text, false) { Buffer = "typed" };
                    using (var editor = new TextEditorControl { Text = "typed", Font = new Font("Segoe UI", 12) })
                    {
                        Field("_text").SetValue(controller, session);
                        Field("_editor").SetValue(controller, editor);
                        controller.AbortText(peer);
                        Program.Check(!session.Ended, "peer abort ended editor");
                        controller.CancelOwner(owner);
                        Program.Check(session.Ended && text.Text == "original", "controller abort committed buffer");
                        var epoch = typeof(CanvasAnnotationController).GetMethod("OwnerTextEpoch", Private);
                        Program.Check((int)epoch.Invoke(controller, new object[] { owner }) == 1 &&
                            (int)epoch.Invoke(controller, new object[] { peer }) == 1 && (int)Field("_textEpoch").GetValue(controller) == 0,
                            "owner abort invalidated global placements");
                        controller.Remove(owner);
                        Program.Check((int)epoch.Invoke(controller, new object[] { owner }) == 2,
                            "removal reset epoch and could revive stale placement");
                    }
                }
                finally { if (!((AnnotationHintClock)Field("_hints").GetValue(controller)).IsDisposed) controller.Remove(owner); }
            }
        });
    }
}
