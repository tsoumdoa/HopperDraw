using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Grasshopper.GUI.Canvas;
using hopperdraw;
using Point3d = Rhino.Geometry.Point3d;

// The style bar, its three targets (pen, selection, text being typed) and the in-place editor. Bar and editor
// run as real windows in a real message loop; GH itself cannot paint outside Rhino, so the canvas is created
// with a handle but never shown.
internal static class StyleBarTests
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private).GetValue(target);
    private static HopperDraw Drawing(int mode)
    {
        var owner = new HopperDraw();
        typeof(HopperDraw).GetField("_drawMode", Private).SetValue(owner, mode);
        return owner;
    }
    private static FrameShape Frame() => new FrameShape(new Point3d(0, 0, 0), new Point3d(50, 0, 0), new Point3d(50, 40, 0), new Point3d(0, 40, 0));
    private static LineShape Line() => new LineShape(new Point3d(0, 0, 0), new Point3d(40, 30, 0));
    private static HopperDraw Selected(params DrawShape[] shapes)
    {
        var owner = new HopperDraw(); owner.Shapes.AddRange(shapes);
        for (int i = 0; i < shapes.Length; i++) owner.SelectedShapeIndices.Add(i);
        return owner;
    }
    private static FrameShape Pen(HopperDraw owner) { var frame = Frame(); owner.ApplyPen(frame); return frame; }
    private static IntPtr At(RectangleF box) => new IntPtr(((int)(box.Top + box.Height / 2) << 16) | ((int)(box.Left + box.Width / 2) & 0xffff));
    private static void Click(Control control, RectangleF box, bool right = false)
    {
        SendMessage(control.Handle, 0x200, IntPtr.Zero, At(box));
        SendMessage(control.Handle, right ? 0x204 : 0x201, new IntPtr(right ? 2 : 1), At(box));
        SendMessage(control.Handle, right ? 0x205 : 0x202, IntPtr.Zero, At(box));
        Application.DoEvents();
    }
    private static StyleBar.Item Item(StyleBar bar, StyleBar.Part part, int tool = -1) => bar.Items.Single(i => i.Part == part && i.Tool == tool);
    private static FlyoutCell Cell(StyleBar bar, string tip) => bar.Flyout.Cells.Single(c => c.Click != null && (c.Tip ?? c.Text) == tip);
    // The palette by number key.
    private static Color Ink(int digit) => AnnotationMenuBuilder.Colors[digit - 1];
    private static Color Soft(int digit) => AnnotationMenuBuilder.FillColors[digit - 1];
    private static Form Host(Size client) => new Form
    {
        FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
        Location = new Point(-32000, -32000), ClientSize = client
    };

    internal static void Run()
    {
        Program.Test("pen target edits the session pen for drawing: stroke, fill, hatch ink, hidden stroke and the line type input", () =>
        {
            var owner = Drawing(2); var pen = new PenTarget(owner);
            Program.Check(pen.Shapes.Length == 1 && pen.FillShapes.Length == 1 && pen.Texts.Length == 0, "frame pen target shape set");
            pen.FillColor(Color.Yellow); pen.FillOpacity(.5);
            Program.Check(!Field<bool>(owner, "_hasPendingLineTypeOverride"), "a fill change overrode the line type input");
            pen.StrokeColor(Color.Red); pen.StrokeWidth(3); pen.StrokeDash(DashStyle.Dash);
            var drawn = Pen(owner);
            Program.Check(drawn.OverrideColor?.ToArgb() == Color.Red.ToArgb() && drawn.ThicknessMultiplier == 3 && drawn.LineType == DashStyle.Dash && drawn.StrokeVisible,
                "stroke not applied to the next shape");
            Program.Check(drawn.FillColor?.ToArgb() == Color.Yellow.ToArgb() && drawn.FillOpacity == .5 && Field<bool>(owner, "_hasPendingLineTypeOverride"), "fill lost or line type not overridden");
            Program.Check(AnnotationMenuBuilder.Recent.Any(c => c.ToArgb() == Color.Red.ToArgb()), "picked colour not remembered");
            pen.StrokeNone(); Program.Check(!Pen(owner).StrokeVisible, "None kept the stroke");
            pen.StrokeWidth(2); Program.Check(Pen(owner).StrokeVisible && Pen(owner).ThicknessMultiplier == 2, "choosing a thickness did not bring the stroke back");
            var plain = Drawing(2); new PenTarget(plain).Hatch(HatchPattern.Grid);
            Program.Check(Pen(plain).Hatch == HatchPattern.Grid && Pen(plain).VisibleFill, "hatch without a fill colour stays invisible");
            new PenTarget(plain).Hatch(HatchPattern.None); Program.Check(Pen(plain).Hatch == HatchPattern.None, "hatch not cleared");
            var line = Drawing(0);
            Program.Check(new PenTarget(line).FillShapes.Length == 0, "Line tool offers fill");
        });
        Program.Test("pen target for the Text tool edits the text pen only", () =>
        {
            var owner = Drawing(5); var pen = new PenTarget(owner);
            Program.Check(pen.Texts.Length == 1 && ReferenceEquals(pen.Texts[0], owner.TextPen) && pen.FillShapes.Length == 1, "text pen not targeted");
            pen.ToggleBold(); pen.ToggleItalic(); pen.StepSize(1); pen.TextColor(Color.Blue);
            Program.Check(owner.TextPen.Bold && owner.TextPen.Italic && owner.TextPen.SizeMultiplier == 1.5 && owner.TextPen.TextColorOverride?.ToArgb() == Color.Blue.ToArgb(), "typography not applied");
            pen.StepSize(-1); pen.StepSize(-1); pen.StepSize(-1); pen.StepSize(-1);
            Program.Check(owner.TextPen.SizeMultiplier == .5, "size steps wrong or ran past the smallest size: " + owner.TextPen.SizeMultiplier);
            foreach (int i in Enumerable.Range(0, 9)) pen.StepSize(1);
            Program.Check(owner.TextPen.SizeMultiplier == 4, "size ran past the largest size");
            pen.Preset(TextStylePreset.Title);
            Program.Check(owner.TextPen.StylePreset == TextStylePreset.Title && owner.TextPen.SizeMultiplier == 1 && owner.TextPen.Bold && !owner.TextPen.Italic, "preset not applied");
            Program.Check(!owner.TextPen.StrokeVisible, "text box border visible by default");
            pen.StrokeColor(Color.Green); pen.FillColor(Color.White);
            Program.Check(owner.TextPen.StrokeVisible && owner.TextPen.OverrideColor?.ToArgb() == Color.Green.ToArgb() && owner.TextPen.FillColor.HasValue, "text box style not applied");
            var geometric = Pen(owner);
            Program.Check(!geometric.OverrideColor.HasValue && !geometric.FillColor.HasValue && !Field<bool>(owner, "_hasPendingLineTypeOverride"), "text styling leaked into the geometric pen");
        });
        Program.Test("selection target restyles leaves across owners, keeps mixed stroke visibility and fills only closed shapes", () =>
        {
            var text = new TextShape { Text = "note", BoxWidth = 40 }; var line = Line(); var frame = Frame();
            var nested = new TextShape { Text = "inner" }; var group = new DrawShapeGroup(); group.Children.Add(nested);
            var first = Selected(line, text); var second = Selected(frame, group);
            var target = new SelectionTarget(new[] { first, second, new HopperDraw() });
            Program.Check(target.Shapes.Length == 4 && target.Texts.Length == 2 && target.FillShapes.Length == 3, "selection leaves wrong: " + target.Shapes.Length);
            target.StrokeColor(Color.Blue); target.StrokeWidth(3);
            Program.Check(line.OverrideColor?.ToArgb() == Color.Blue.ToArgb() && frame.ThicknessMultiplier == 3 && line.StrokeVisible, "stroke not applied across owners");
            Program.Check(!text.StrokeVisible && !nested.StrokeVisible, "recolouring a mixed selection put borders on its texts");
            target.FillColor(Color.Yellow); target.FillOpacity(.25); target.Hatch(HatchPattern.Dots);
            Program.Check(frame.FillColor.HasValue && text.FillColor.HasValue && nested.Hatch == HatchPattern.Dots && frame.FillOpacity == .25, "fill not applied to closed shapes");
            Program.Check(!line.FillColor.HasValue && line.Hatch == HatchPattern.None && line.FillOpacity == 1, "fill reached an open line");
            target.ToggleBold(); target.Size(4); target.TextColor(Color.Red);
            Program.Check(text.Bold && nested.Bold && nested.SizeMultiplier == 4 && text.TextColorOverride?.ToArgb() == Color.Red.ToArgb(), "typography not applied to text leaves");
            Program.Check(text.BoxWidth >= text.MinBoxWidth, "size change left a box narrower than one character");
            target.ToggleBold(); Program.Check(!text.Bold && !nested.Bold, "bold did not toggle off");
            var only = Selected(new TextShape { Text = "solo" }); var solo = (TextShape)only.Shapes[0];
            new SelectionTarget(new[] { only }).StrokeColor(Color.Black);
            Program.Check(solo.StrokeVisible && solo.OverrideColor.HasValue, "a border colour chosen for texts only did not show the border");
        });
        Program.Test("text edit target restyles only the edit buffer; commit writes style once and never geometry", () =>
        {
            var owner = new HopperDraw(); var doc = new object(); int records = 0, changes = 0; string named = null;
            Func<string, bool> record = n => { records++; named = n; return true; };
            var text = new TextShape { Text = "Hello", Anchor = new Point3d(20, 20, 0), BoxWidth = 60 }; owner.Shapes.Add(text);
            var session = new TextEditSession(owner, doc, text, false);
            var target = new TextEditTarget(session, null, () => changes++);
            Program.Check(target.Shapes.Length == 1 && ReferenceEquals(target.Shapes[0], session.Draft) && target.Texts.Length == 1, "draft not targeted");
            target.ToggleBold(); target.Size(2); target.TextColor(Color.Red); target.FillColor(Color.Yellow); target.StrokeColor(Color.Blue);
            Program.Check(changes == 5 && session.StyleEdited && session.Draft.Bold && !text.Bold && !text.FillColor.HasValue && text.SizeMultiplier == 1, "styling was not buffer-only");
            Program.Check(!owner.TextPen.Bold && !owner.TextPen.FillColor.HasValue, "editing an existing text changed the pen");
            owner.Shapes = ShapeEditUndoAction.Snapshot(owner.Shapes); owner.Shapes[0].Move(50, 40);
            Program.Check(session.Commit(doc, true, record) && records == 1 && named == "Edit text", "style-only edit not recorded exactly once");
            var live = TextEditSession.Find(owner.Shapes, text.PersistentId);
            Program.Check(live.Bold && live.SizeMultiplier == 2 && live.TextColorOverride?.ToArgb() == Color.Red.ToArgb() && live.FillColor.HasValue && live.StrokeVisible &&
                live.OverrideColor?.ToArgb() == Color.Blue.ToArgb() && live.Text == "Hello", "style not written on commit");
            Program.Check(live.Anchor == new Point3d(70, 60, 0) && live.BoxWidth >= live.MinBoxWidth, "commit overwrote geometry or left the box too narrow");
            session = new TextEditSession(owner, doc, live, false); target = new TextEditTarget(session, null, () => { });
            target.ToggleItalic(); target.ToggleItalic();
            Program.Check(!session.Commit(doc, true, record) && records == 1, "a style changed back was recorded");
            session = new TextEditSession(owner, doc, live, false); session.Draft.Bold = false;
            Program.Check(!session.Commit(doc, true, record) && records == 1, "an unedited session wrote its snapshot style");
            session = new TextEditSession(owner, doc, live, false); new TextEditTarget(session, null, () => { }).ToggleItalic(); session.Buffer = "  ";
            Program.Check(session.Commit(doc, true, record) && named == "Delete text" && owner.Shapes.Count == 0, "blank restyled text not deleted");
            var typing = new TextEditSession(owner, doc, new TextShape { Anchor = new Point3d(5, 5, 0) }, true);
            var fresh = new TextEditTarget(typing, owner.TextPen, () => { });
            fresh.Preset(TextStylePreset.Subtitle); fresh.ToggleItalic(); fresh.FillColor(Color.White);
            Program.Check(owner.TextPen.Italic && owner.TextPen.StylePreset == TextStylePreset.Subtitle && owner.TextPen.FillColor.HasValue, "the next text would not start with the style just chosen");
            typing.Buffer = "typed";
            Program.Check(typing.Commit(doc, true, record) && named == "Create text" && ((TextShape)owner.Shapes[0]).Italic && ((TextShape)owner.Shapes[0]).StylePreset == TextStylePreset.Subtitle, "new text lost its style");
        });
        Program.Test("style bar lays out inside any canvas, drops tools then presets when narrow and hides without a target", () =>
        {
            using (var host = Host(new Size(1200, 600)))
            using (var bar = new StyleBar())
            {
                bar.Attach(host); host.Show();
                foreach (int mode in new[] { 0, 2, 5 }) foreach (int width in new[] { 1200, 520, 330, 260, 150, 40, 0 }) foreach (bool locked in new[] { false, true })
                {
                    host.ClientSize = new Size(width, 300); var owner = Drawing(mode);
                    bar.Present(new StyleBarState { Target = new PenTarget(owner), Tool = mode, ToolsLocked = locked });
                    string where = "mode " + mode + " width " + host.ClientSize.Width;
                    Program.Check(bar.Visible && bar.Left >= 0 && bar.Right <= Math.Max(bar.Width, host.ClientSize.Width) && bar.Bottom <= 300 && bar.Top >= 0, "bar outside the canvas: " + where + " " + bar.Bounds);
                    var items = bar.Items.ToArray(); bool tools = items.Any(i => i.Part == StyleBar.Part.Tool), preset = items.Any(i => i.Part == StyleBar.Part.Preset);
                    for (int i = 1; i < items.Length; i++) Program.Check(items[i].Bounds.Left >= items[i - 1].Bounds.Right - .01f, "items overlap: " + where);
                    Program.Check(items.All(i => i.Bounds.Top >= 0 && i.Bounds.Bottom <= bar.Height), "item outside the bar: " + where);
                    if (host.ClientSize.Width >= 330) Program.Check(items.Last().Bounds.Right <= bar.Width, "item clipped on a canvas with room: " + where);
                    Program.Check(items.Count(i => i.Part == StyleBar.Part.Stroke) == 1 && items.Count(i => i.Part == StyleBar.Part.Fill) == 1, "stroke/fill missing: " + where);
                    Program.Check(items.Any(i => i.Part == StyleBar.Part.Bold) == (mode == 5), "typography shown for the wrong tool: " + where);
                    Program.Check(Item(bar, StyleBar.Part.Fill).Enabled == (mode != 0), "fill availability wrong: " + where);
                    if (host.ClientSize.Width >= 1200)
                        Program.Check(tools && items.Count(i => i.Part == StyleBar.Part.Tool) == 6 && items.Where(i => i.Part == StyleBar.Part.Tool).All(i => i.Enabled == !locked) && preset == (mode == 5), "wide layout incomplete: " + where);
                    if (host.ClientSize.Width <= 150) Program.Check(!tools, "tools kept on a canvas too narrow for them: " + where);
                    if (mode == 5 && host.ClientSize.Width == 520) Program.Check(!tools && preset, "text layout should drop tools before the preset list");
                    if (mode == 5 && host.ClientSize.Width == 330) Program.Check(!tools && !preset, "preset list kept on a canvas too narrow for it");
                    if (mode == 2 && host.ClientSize.Width == 330) Program.Check(tools, "tools dropped on a canvas with room for them");
                }
                host.ClientSize = new Size(1200, 600);
                bar.Present(new StyleBarState { Target = new SelectionTarget(new[] { Selected(Line()) }), Tool = -1 });
                Program.Check(bar.Items.All(i => i.Part != StyleBar.Part.Tool) && bar.Visible, "selection bar shows tools");
                Program.Check(Math.Abs(bar.Left + bar.Width / 2 - 600) <= 1 && bar.Bottom < 600 && bar.Bottom > 560, "bar not bottom-centred: " + bar.Bounds);
                bar.Present(null); Program.Check(!bar.Visible && bar.Items.Count == 0 && !bar.FlyoutOpen, "bar shown without a target");
                Program.Check(AnnotationFeedback.HintBox(new Size(1200, 600), 96, 600 - 552).Bottom <= 552, "hint overlaps the bar");
                foreach (int dpi in new[] { 96, 144, 192 }) foreach (int width in new[] { 0, 1, 16, 48, 100, 200, 1200 })
                {
                    var box = AnnotationFeedback.HintBox(new Size(width, 200), dpi, 48 * dpi / 96f);
                    Program.Check(box.Width >= 0 && box.Left >= 0 && box.Right <= width + .01 && box.Top >= 0 && box.Bottom <= 200, "hint exceeds canvas: " + width + " " + box);
                }
            }
        });
        Program.Test("style bar clicks apply without taking focus; flyouts stay for multi-part choices and close on outside presses", () =>
        {
            var remembered = AnnotationMenuBuilder.Recent.ToArray();
            using (var host = Host(new Size(900, 500)))
            using (var typing = new TextBox { Location = new Point(10, 10), Width = 200 })
            using (var bar = new StyleBar())
            {
                host.Controls.Add(typing); bar.Attach(host); host.Show(); host.Activate(); typing.Focus(); Application.DoEvents();
                Program.Check(typing.Focused, "test host cannot focus");
                var owner = Drawing(5); int switched = -1, modal = 0;
                bar.Source = () => new StyleBarState { Target = new PenTarget(owner), Tool = owner.DrawMode, SetTool = mode => { switched = mode; typeof(HopperDraw).GetField("_drawMode", Private).SetValue(owner, mode); } };
                bar.Modal = open => modal++;
                bar.Sync(); Application.DoEvents();
                Program.Check(bar.Visible && bar.IsHandleCreated && !bar.CanSelect && !bar.Flyout.CanSelect, "bar not shown or focusable");
                Program.Check(bar.Cursor == Cursors.Default && bar.Flyout.Cursor == Cursors.Default, "bar inherits the drawing cursor");
                Click(bar, Item(bar, StyleBar.Part.Bold).Bounds);
                Program.Check(owner.TextPen.Bold && typing.Focused, "bold click not applied or stole focus");
                Click(bar, Item(bar, StyleBar.Part.Bold).Bounds, right: true);
                Program.Check(owner.TextPen.Bold, "right-click acted on the bar");
                Click(bar, Item(bar, StyleBar.Part.Larger).Bounds); Click(bar, Item(bar, StyleBar.Part.Larger).Bounds);
                Program.Check(owner.TextPen.SizeMultiplier == 2 && typing.Focused, "size clicks not applied: " + owner.TextPen.SizeMultiplier);
                Click(bar, Item(bar, StyleBar.Part.Stroke).Bounds);
                Program.Check(bar.FlyoutOpen && bar.Flyout.Visible && bar.Flyout.Bottom <= bar.Top && bar.Flyout.Top >= 0 && bar.Flyout.Right <= 900 && typing.Focused, "stroke flyout not opened above the bar: " + bar.Flyout.Bounds);
                Click(bar.Flyout, Cell(bar, "Red").Bounds);
                Program.Check(owner.TextPen.StrokeVisible && owner.TextPen.OverrideColor?.ToArgb() == Ink(3).ToArgb() && bar.FlyoutOpen && typing.Focused, "colour pick not applied or closed a multi-part flyout");
                Click(bar.Flyout, Cell(bar, "3x").Bounds); Click(bar.Flyout, Cell(bar, "Dot").Bounds);
                Program.Check(owner.TextPen.ThicknessMultiplier == 3 && owner.TextPen.LineType == DashStyle.Dot && bar.FlyoutOpen, "thickness/line type not applied in one visit");
                Program.Check(Cell(bar, "Red").Checked && Cell(bar, "3x").Checked && Cell(bar, "Dot").Checked && !Cell(bar, "None").Checked, "flyout does not show the current pen");
                Click(bar.Flyout, Cell(bar, "None").Bounds);
                Program.Check(!owner.TextPen.StrokeVisible && Cell(bar, "None").Checked && !Cell(bar, "Red").Checked, "None not applied or shown");
                Click(bar, Item(bar, StyleBar.Part.TextColor).Bounds);
                Program.Check(bar.FlyoutOpen && bar.Flyout.Cells.All(c => c.Tip != "3x"), "flyout did not switch to text colour");
                bar.PickColor = start => Color.FromArgb(255, 12, 34, 56);
                Click(bar.Flyout, Cell(bar, "Custom…").Bounds);
                Program.Check(owner.TextPen.TextColorOverride?.ToArgb() == Color.FromArgb(255, 12, 34, 56).ToArgb() && !bar.FlyoutOpen && !bar.Flyout.Visible, "custom colour not applied or single-choice flyout stayed open");
                Click(bar, Item(bar, StyleBar.Part.TextColor).Bounds);
                Program.Check(bar.Flyout.Cells.Count(c => c.Tip != null && c.Tip.StartsWith("Recent") && c.Checked) == 1, "custom colour not offered as a recent swatch");
                Click(bar.Flyout, Cell(bar, "Component color").Bounds);
                Program.Check(!owner.TextPen.TextColorOverride.HasValue, "component colour not restored");
                Click(bar, Item(bar, StyleBar.Part.Preset).Bounds); Click(bar.Flyout, Cell(bar, "Title").Bounds);
                Program.Check(owner.TextPen.StylePreset == TextStylePreset.Title && !bar.FlyoutOpen, "preset not applied");
                Click(bar, Item(bar, StyleBar.Part.Fill).Bounds);
                Click(bar.Flyout, Cell(bar, "Soft amber").Bounds); Click(bar.Flyout, Cell(bar, "50%").Bounds); Click(bar.Flyout, Cell(bar, "Brick").Bounds);
                Program.Check(owner.TextPen.FillColor?.ToArgb() == Soft(4).ToArgb() && owner.TextPen.FillOpacity == .5 && owner.TextPen.Hatch == HatchPattern.Brick && bar.FlyoutOpen, "fill not applied in one visit");
                Click(bar.Flyout, Cell(bar, "Ink").Bounds);
                Program.Check(owner.TextPen.FillColor?.ToArgb() == Ink(1).ToArgb() && Cell(bar, "Ink").Checked && !Cell(bar, "Soft amber").Checked, "the fill flyout does not offer the inks for hatch lines");
                Click(bar, Item(bar, StyleBar.Part.Size).Bounds); Click(bar.Flyout, Cell(bar, "3x").Bounds);
                Program.Check(owner.TextPen.SizeMultiplier == 3 && bar.FlyoutOpen && Cell(bar, "3x").Checked && typing.Focused, "size flyout did not set the size");
                Click(bar, Item(bar, StyleBar.Part.Fill).Bounds);
                Click(bar, Item(bar, StyleBar.Part.Fill).Bounds);
                Program.Check(!bar.FlyoutOpen, "clicking the open button again did not close its flyout");
                Click(bar, Item(bar, StyleBar.Part.Stroke).Bounds); Program.Check(bar.FlyoutOpen, "flyout not reopened");
                PostMessage(host.Handle, 0x201, new IntPtr(1), new IntPtr((300 << 16) | 400)); PostMessage(host.Handle, 0x202, IntPtr.Zero, new IntPtr((300 << 16) | 400));
                Application.DoEvents();
                Program.Check(!bar.FlyoutOpen && !bar.Flyout.Visible, "a press on the canvas did not close the flyout");
                Click(bar, Item(bar, StyleBar.Part.Stroke).Bounds);
                Click(bar, Item(bar, StyleBar.Part.Tool, 2).Bounds);
                Program.Check(switched == 2 && !bar.FlyoutOpen && bar.Items.All(i => i.Part != StyleBar.Part.Bold) && typing.Focused, "tool click did not switch tool and bar");
                Program.Check(modal == 0, "stubbed colour picker reported a modal dialog");
                using (var shot = new Bitmap(bar.Width, bar.Height))
                {
                    bar.DrawToBitmap(shot, new Rectangle(Point.Empty, bar.Size));
                    var colours = new HashSet<int>();
                    for (int y = 0; y < shot.Height; y += 2) for (int x = 0; x < shot.Width; x += 2) colours.Add(shot.GetPixel(x, y).ToArgb());
                    Program.Check(colours.Count > 8, "bar painted blank");
                }
            }
            AnnotationMenuBuilder.Recent.Clear(); AnnotationMenuBuilder.Recent.AddRange(remembered);
        });
        Program.Test("key rows act by position on the pen, the selection and the text being typed, and decline what they cannot change", () =>
        {
            var remembered = AnnotationMenuBuilder.Recent.ToArray();
            StyleKey Key(Keys data, bool typing = false) { Program.Check(StyleKeys.Match(data, typing, out var key), "no style key for " + data); return key; }
            var owner = Drawing(2); var pen = new PenTarget(owner);
            Program.Check(pen.Run(Key(Keys.D3)) == "Color: Red" && Pen(owner).OverrideColor?.ToArgb() == Ink(3).ToArgb(), "3 did not make the stroke red");
            Program.Check(pen.Run(Key(Keys.D0)) == "Color: component color" && !Pen(owner).OverrideColor.HasValue, "0 did not restore the component colour");
            Program.Check(pen.Run(Key(Keys.Q)) == "Thickness 0.5x" && pen.Run(Key(Keys.R)) == "Thickness 3x" && Pen(owner).ThicknessMultiplier == 3 && pen.Run(Key(Keys.T)) == "Thickness 5x" &&
                pen.Run(Key(Keys.Y)) == null && pen.Run(Key(Keys.U)) == null && Pen(owner).ThicknessMultiplier == 5, "the Q row does not pick the thickness by position");
            Program.Check(pen.Run(Key(Keys.S)) == "Line type: Dash" && pen.Run(Key(Keys.D)) == "Line type: Dot" && Pen(owner).LineType == DashStyle.Dot && pen.Run(Key(Keys.G)) == "Line type: Dash-dot-dot" &&
                pen.Run(Key(Keys.A)) == "Line type: Solid" && Pen(owner).LineType == DashStyle.Solid && pen.Run(Key(Keys.H)) == null && pen.Run(Key(Keys.L)) == null, "the A row does not pick the line type by position");
            Program.Check(pen.Run(Key(Keys.Shift | Keys.E)) == null, "opacity of a fill that is not there was consumed");
            Program.Check(pen.Run(Key(Keys.Shift | Keys.D4)) == "Fill: Soft amber" && Pen(owner).FillColor?.ToArgb() == Soft(4).ToArgb() && !Pen(owner).OverrideColor.HasValue, "Shift+4 did not fill with the soft amber");
            Program.Check(pen.Run(Key(Keys.Shift | Keys.E)) == "Fill opacity 50%" && Pen(owner).FillOpacity == .5 && pen.Run(Key(Keys.Shift | Keys.T)) == "Fill opacity 100%" && pen.Run(Key(Keys.Shift | Keys.Y)) == null,
                "Shift + the Q row does not pick the opacity by position");
            Program.Check(pen.Run(Key(Keys.Shift | Keys.S)) == "Hatch: Diagonal" && pen.Run(Key(Keys.Shift | Keys.OemQuotes)) == "Hatch: Vertical" && Pen(owner).Hatch == HatchPattern.Vertical &&
                pen.Run(Key(Keys.Shift | Keys.A)) == "Hatch: solid" && Pen(owner).Hatch == HatchPattern.None && Pen(owner).LineType == DashStyle.Solid, "Shift + the A row does not pick the hatch by position");
            Program.Check(pen.Run(Key(Keys.Shift | Keys.D0)) == "Fill: none" && !Pen(owner).FillColor.HasValue, "Shift+0 kept the fill");
            pen.StrokeNone();
            Program.Check(pen.Run(Key(Keys.W)) == "Thickness 1x" && Pen(owner).StrokeVisible && Pen(owner).ThicknessMultiplier == 1, "a thickness key on a hidden stroke should bring it back");
            Program.Check(new PenTarget(Drawing(0)).Run(Key(Keys.Shift | Keys.D4)) == null && new PenTarget(Drawing(0)).Run(Key(Keys.Shift | Keys.S)) == null, "fill keys consumed for the Line tool");
            var hatched = Drawing(2); new PenTarget(hatched).Run(Key(Keys.Shift | Keys.K));
            Program.Check(Pen(hatched).Hatch == HatchPattern.Grid && Pen(hatched).FillColor?.ToArgb() == Ink(1).ToArgb(), "a hatch key without a fill should draw the hatch in ink");

            // Only text: the same rows mean the text's colour, size and style, and never show the box border.
            var writer = Drawing(5); var textPen = new PenTarget(writer);
            Program.Check(textPen.Run(Key(Keys.D6)) == "Text color: Blue" && writer.TextPen.TextColorOverride?.ToArgb() == Ink(6).ToArgb() && !writer.TextPen.StrokeVisible,
                "a digit on the Text tool should colour the glyphs, not show a border");
            Program.Check(textPen.Run(Key(Keys.Q)) == "Text size 0.5x" && textPen.Run(Key(Keys.U)) == "Text size 4x" && textPen.Run(Key(Keys.R)) == "Text size 1.5x" && writer.TextPen.SizeMultiplier == 1.5,
                "the Q row does not pick the text size by position");
            Program.Check(textPen.Run(Key(Keys.A)) == "Text style: Title" && writer.TextPen.StylePreset == TextStylePreset.Title && textPen.Run(Key(Keys.S)) == "Text style: Subtitle" &&
                textPen.Run(Key(Keys.D)) == "Text style: Body" && writer.TextPen.StylePreset == TextStylePreset.Body && !writer.TextPen.StrokeVisible, "the A row does not pick the text style by position");
            Program.Check(textPen.Run(Key(Keys.F)) == "Bold" && textPen.Run(Key(Keys.G)) == "Italic" && textPen.Run(Key(Keys.F)) == "Bold off" && writer.TextPen.Italic && !writer.TextPen.Bold &&
                textPen.Run(Key(Keys.H)) == null, "F / G do not toggle bold / italic after the three styles");
            Program.Check(textPen.Run(Key(Keys.Shift | Keys.D1)) == "Fill: White" && writer.TextPen.FillColor?.ToArgb() == Soft(1).ToArgb(), "text box fill key");

            var text = new TextShape { Text = "note" }; var line = Line(); var frame = Frame();
            var selection = new SelectionTarget(new[] { Selected(line, text), Selected(frame) });
            Program.Check(selection.Run(Key(Keys.D5)) == "Color: Green" && line.OverrideColor?.ToArgb() == Ink(5).ToArgb() && frame.OverrideColor.HasValue &&
                !text.TextColorOverride.HasValue && !text.StrokeVisible, "a digit on a mixed selection should recolour strokes and leave texts borderless");
            Program.Check(selection.Run(Key(Keys.E)) == "Thickness 2x" && line.ThicknessMultiplier == 2 && frame.ThicknessMultiplier == 2 && text.SizeMultiplier == 1 && !text.StrokeVisible &&
                selection.Run(Key(Keys.D)) == "Line type: Dot" && line.LineType == DashStyle.Dot && text.StylePreset == TextStylePreset.Body, "the rows on a mixed selection should stay with the strokes");
            Program.Check(selection.Run(Key(Keys.Shift | Keys.D3)) == "Fill: Soft red" && frame.FillColor.HasValue && text.FillColor.HasValue && !line.FillColor.HasValue, "fill key on a mixed selection");
            var solo = new TextShape { Text = "solo" }; var texts = new SelectionTarget(new[] { Selected(solo) });
            Program.Check(texts.Run(Key(Keys.D3)) == "Text color: Red" && solo.TextColorOverride?.ToArgb() == Ink(3).ToArgb() && texts.Run(Key(Keys.T)) == "Text size 2x" && solo.SizeMultiplier == 2 &&
                texts.Run(Key(Keys.A)) == "Text style: Title" && solo.StylePreset == TextStylePreset.Title && !solo.StrokeVisible, "the rows on selected text should restyle the text");
            Program.Check(new SelectionTarget(new[] { Selected(Line()) }).Run(Key(Keys.Shift | Keys.D3)) == null && new SelectionTarget(new[] { Selected(Line()) }).Run(Key(Keys.Y)) == null,
                "a key with nothing to change on a line was consumed");

            // Typing: plain keys are text, so only the usual chords exist.
            var session = new TextEditSession(new HopperDraw(), new object(), new TextShape { Text = "typed", Anchor = new Point3d(5, 5, 0) }, false);
            int changes = 0; var typed = new TextEditTarget(session, null, () => changes++);
            Program.Check(typed.Run(Key(Keys.Control | Keys.D3, true)) == "Text color: Red" && typed.Run(Key(Keys.Control | Keys.Shift | Keys.D4, true)) == "Fill: Soft amber" &&
                typed.Run(Key(Keys.Control | Keys.B, true)) == "Bold" && typed.Run(Key(Keys.Control | Keys.I, true)) == "Italic" &&
                typed.Run(Key(Keys.Control | Keys.OemPeriod, true)) == "Text size 1.5x" && typed.Run(Key(Keys.Control | Keys.Shift | Keys.Oemcomma, true)) == "Text size 1x",
                "Ctrl chords on the text being typed");
            Program.Check(changes == 6 && session.StyleEdited && session.Draft.TextColorOverride?.ToArgb() == Ink(3).ToArgb() && session.Draft.FillColor?.ToArgb() == Soft(4).ToArgb() &&
                session.Draft.Bold && session.Draft.Italic && !session.Draft.StrokeVisible, "chords did not reach the edit buffer");
            Program.Check(!StyleKeys.Match(Keys.Q, true, out _) && !StyleKeys.Match(Keys.Control | Keys.Q, true, out _) && !StyleKeys.Match(Keys.Control | Keys.S, true, out _) &&
                !StyleKeys.Match(Keys.Control | Keys.D7, true, out _), "a key row reached the text being typed");
            AnnotationMenuBuilder.Recent.Clear(); AnnotationMenuBuilder.Recent.AddRange(remembered);
        });
        Program.Test("palette is six inks and six soft fills, in number-key order, with distinct names", () =>
        {
            Program.Check(AnnotationMenuBuilder.Colors.Length == StyleKeys.Colors && AnnotationMenuBuilder.FillColors.Length == StyleKeys.Colors &&
                AnnotationMenuBuilder.ColorNames.Length == StyleKeys.Colors && AnnotationMenuBuilder.FillNames.Length == StyleKeys.Colors, "palette size differs from the number keys");
            var all = AnnotationMenuBuilder.Colors.Concat(AnnotationMenuBuilder.FillColors).ToArray();
            Program.Check(all.Select(c => c.ToArgb()).Distinct().Count() == 12 && all.All(c => c.A == 255) &&
                AnnotationMenuBuilder.ColorNames.Concat(AnnotationMenuBuilder.FillNames).Distinct().Count() == 12, "palette colours or names repeat");
            int Luma(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
            // Inks read on the light canvas and on every soft fill; soft fills stay light under ink text.
            Program.Check(AnnotationMenuBuilder.Colors.All(c => Luma(c) < 150) && AnnotationMenuBuilder.FillColors.All(c => Luma(c) > 200), "an ink is too pale or a soft fill too dark");
            for (int i = 2; i < StyleKeys.Colors; i++)
                Program.Check(Math.Abs(AnnotationMenuBuilder.Colors[i].GetHue() - AnnotationMenuBuilder.FillColors[i].GetHue()) < 25, "soft fill " + (i + 1) + " does not match its ink");
            Program.Check(StyleKeys.Upper.Length >= StyleTarget.Sizes.Length && StyleKeys.Upper.Length >= StyleTarget.Widths.Length && StyleKeys.Upper.Length >= StyleTarget.Opacities.Length &&
                StyleKeys.Home.Length >= StyleTarget.Hatches.Length && StyleKeys.Home.Length > StyleTarget.ItalicKey && StyleKeys.Home.Length >= AnnotationMenuBuilder.DashNames.Length,
                "a key row is shorter than the choices it picks");
            Program.Check(string.Concat(DrawingTool.All.Select(t => t.Key)) == "ZXCVBN" && DrawingTool.All.All(t => !StyleKeys.Match((Keys)t.Key, false, out _)), "tool keys are not the bottom row or collide with a style row");
        });
        Program.Test("style bar shows each key where it applies: digits on swatches, key caps under row choices, Ctrl while typing", () =>
        {
            using (var host = Host(new Size(1200, 600)))
            using (var bar = new StyleBar())
            {
                bar.Attach(host); host.Show();
                FlyoutCell Heading(string text) => bar.Flyout.Cells.Single(c => c.Click == null && c.Text == text);
                string Tip(StyleBar.Part part, int tool = -1) => Item(bar, part, tool).Tip;
                StyleBarState state = null; bar.Source = () => state;
                var frame = Drawing(2);
                state = new StyleBarState { Target = new PenTarget(frame), Tool = 2 }; bar.Sync();
                Program.Check(Tip(StyleBar.Part.Stroke) == "Stroke: color 1–6 · thickness Q–T · line type A–G" && Tip(StyleBar.Part.Fill) == "Fill: color Shift+1–6 · opacity Shift+Q–T · hatch Shift+A–'",
                    "bar tooltips do not name the keys: " + Tip(StyleBar.Part.Stroke) + " / " + Tip(StyleBar.Part.Fill));
                Program.Check(Tip(StyleBar.Part.Tool, 0) == "Line (Z)" && Tip(StyleBar.Part.Tool, 2) == "Frame (C)" && Tip(StyleBar.Part.Tool, 5) == "Text (N)", "tool tooltips do not name the bottom row");
                bar.Activate(Item(bar, StyleBar.Part.Stroke));
                Program.Check(Cell(bar, "Ink").Key == "1" && Cell(bar, "Blue").Key == "6" && Cell(bar, "Component color").Key == "0" && Cell(bar, "None").Key == null && Heading("Color").Hint == null,
                    "stroke swatches do not carry their keys");
                var row = new[] { "Ink", "Grey", "Red", "Amber", "Green", "Blue", "Component color" }.Select(n => Cell(bar, n).Bounds).ToArray();
                Program.Check(row.All(b => b.Top == row[0].Top) && Enumerable.Range(1, 6).All(i => row[i].Left > row[i - 1].Left) && Cell(bar, "None").Bounds.Top > row[0].Top &&
                    row[6].Right <= bar.Flyout.Width, "colours are not one row in number-key order: 1 to 6, then 0");
                Program.Check(StyleFlyout.Describe(Cell(bar, "Red")) == "Red (3)" && StyleFlyout.Describe(Cell(bar, "3x")) == "3x (R)" && StyleFlyout.Describe(Cell(bar, "Dash-dot-dot")) == "Dash-dot-dot (G)" &&
                    StyleFlyout.Describe(Cell(bar, "None")) == "None", "tooltips do not name the key");
                Program.Check(string.Concat(StyleTarget.Widths.Select(w => Cell(bar, w.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "x").Legend)) == "QWERT" &&
                    string.Concat(AnnotationMenuBuilder.DashNames.Select(n => Cell(bar, n).Legend)) == "ASDFG", "thickness / line type cells do not show their key caps in row order");
                bar.Activate(Item(bar, StyleBar.Part.Fill));
                Program.Check(Cell(bar, "Soft amber").Key == "Shift+4" && Cell(bar, "None").Key == "Shift+0" && Cell(bar, "Amber").Key == null && Heading("Color").Hint == "Shift +" &&
                    Heading("Opacity").Hint == "Shift +" && Heading("Hatch").Hint == "Shift +", "fill keys not shown");
                Program.Check(Cell(bar, "50%").Legend == "E" && StyleFlyout.Describe(Cell(bar, "50%")) == "50% (Shift+E)" && Cell(bar, "Solid").Legend == "A" && Cell(bar, "Vertical").Legend == "'" &&
                    StyleFlyout.Describe(Cell(bar, "Brick")) == "Brick (Shift+J)", "opacity / hatch cells do not show their key caps");
                using (var shot = new Bitmap(bar.Flyout.Width, bar.Flyout.Height))
                {
                    // The digit is painted inside its swatch: the swatch is no longer one flat colour.
                    bar.Flyout.DrawToBitmap(shot, new Rectangle(Point.Empty, bar.Flyout.Size));
                    var swatch = Rectangle.Round(RectangleF.Inflate(Cell(bar, "Soft blue").Bounds, -5 * bar.S, -5 * bar.S)); var seen = new HashSet<int>();
                    for (int y = swatch.Top; y < swatch.Bottom; y++) for (int x = swatch.Left; x < swatch.Right; x++) seen.Add(shot.GetPixel(x, y).ToArgb());
                    Program.Check(seen.Count > 1 && seen.Contains(Soft(6).ToArgb()), "no digit painted on the swatch");
                }
                var writer = Drawing(5);
                state = new StyleBarState { Target = new PenTarget(writer), Tool = 5 }; bar.Sync();
                Program.Check(Tip(StyleBar.Part.Bold) == "Bold (F)" && Tip(StyleBar.Part.Italic) == "Italic (G)" && Tip(StyleBar.Part.Size) == "Text size (Q W E R T Y U)" && Tip(StyleBar.Part.Preset) == "Text style (A S D)" &&
                    Tip(StyleBar.Part.Larger) == "Larger text" && Tip(StyleBar.Part.TextColor) == "Text color (1–6 · 0 component color)" && Tip(StyleBar.Part.Stroke) == "Box border", "text tool tooltips: " + Tip(StyleBar.Part.TextColor));
                bar.Activate(Item(bar, StyleBar.Part.Stroke));
                Program.Check(Cell(bar, "Red").Key == null && Cell(bar, "3x").Legend == null && Cell(bar, "Dot").Key == null, "box border cells claim the keys that restyle the text");
                bar.Activate(Item(bar, StyleBar.Part.TextColor));
                Program.Check(Cell(bar, "Red").Key == "3" && Cell(bar, "Component color").Key == "0" && Heading("Text color").Hint == null, "text colour swatches do not carry the digits");
                bar.Activate(Item(bar, StyleBar.Part.Size));
                Program.Check(bar.FlyoutOpen && string.Concat(StyleTarget.Sizes.Select(v => Cell(bar, v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x").Legend)) == "QWERTYU" &&
                    StyleFlyout.Describe(Cell(bar, "1.5x")) == "1.5x (R)", "size flyout does not show the Q row");
                bar.Choose(Cell(bar, "2x"));
                Program.Check(writer.TextPen.SizeMultiplier == 2 && bar.FlyoutOpen && Cell(bar, "2x").Checked, "size flyout did not set the size");
                bar.Activate(Item(bar, StyleBar.Part.Preset));
                Program.Check(Cell(bar, "Title").Hint == "A" && Cell(bar, "Subtitle").Hint == "S" && StyleFlyout.Describe(Cell(bar, "Body")) == "Body (D)", "style list does not show the A row");
                state = new StyleBarState { Target = new SelectionTarget(new[] { Selected(Line(), new TextShape { Text = "t" }) }), Tool = -1 }; bar.Sync();
                Program.Check(Tip(StyleBar.Part.Bold) == "Bold" && Tip(StyleBar.Part.Size) == "Text size" && Tip(StyleBar.Part.TextColor) == "Text color" && Tip(StyleBar.Part.Stroke).StartsWith("Stroke: color 1–6") &&
                    Cell(bar, "Title").Hint == null, "a mixed selection shows text keys although the rows restyle its strokes");
                var session = new TextEditSession(writer, new object(), new TextShape { Text = "typed" }, true);
                state = new StyleBarState { Target = new TextEditTarget(session, writer.TextPen, () => { }), Tool = 5, Typing = true }; bar.Sync();
                Program.Check(Tip(StyleBar.Part.Bold) == "Bold (Ctrl+B)" && Tip(StyleBar.Part.Italic) == "Italic (Ctrl+I)" && Tip(StyleBar.Part.Smaller) == "Smaller text (Ctrl+,)" && Tip(StyleBar.Part.Larger) == "Larger text (Ctrl+.)" &&
                    Tip(StyleBar.Part.Size) == "Text size" && Tip(StyleBar.Part.Preset) == "Text style" && Tip(StyleBar.Part.Fill) == "Fill: color Ctrl+Shift+1–6" &&
                    Tip(StyleBar.Part.TextColor) == "Text color (Ctrl+1–6 · Ctrl+0 component color)", "tooltips while typing do not show exactly the Ctrl chords");
                Program.Check(Cell(bar, "Title").Hint == null && Cell(bar, "Title").Key == null, "style list claims a plain key while typing");
                bar.Activate(Item(bar, StyleBar.Part.TextColor));
                Program.Check(Cell(bar, "Red").Key == "Ctrl+3" && Heading("Text color").Hint == "Ctrl +", "swatches while typing do not show the Ctrl chords");
                bar.Activate(Item(bar, StyleBar.Part.Fill));
                Program.Check(Cell(bar, "Soft amber").Key == "Ctrl+Shift+4" && Heading("Color").Hint == "Ctrl + Shift +" && Heading("Hatch").Hint == null && Cell(bar, "Brick").Legend == null && Cell(bar, "50%").Key == null,
                    "fill chords while typing");
                Program.Check(AnnotationFeedback.ToolHint(2).EndsWith(" · 1–6 color · Q–T thickness · A–G line type · Z–N tool") && AnnotationFeedback.ToolHint(5).EndsWith(" · 1–6 color · Q–U size · A–G style · Z–N tool") &&
                    AnnotationFeedback.EditingHint.Contains("Ctrl+1–6"), "hints do not teach the rows: " + AnnotationFeedback.ToolHint(2));
            }
        });
        Program.Test("canvas controller shows the text being typed in the bar, restyles the live editor and grows it without scrolling", () =>
        {
            using (var canvas = new GH_Canvas { Size = new Size(900, 520) })
            {
                var handle = canvas.Handle; // created, never shown: GH paints only inside Rhino
                canvas.Viewport.Zoom = 1f; canvas.Viewport.ComputeProjection();
                var owner = new HopperDraw();
                typeof(HopperDraw).GetField("_isActivated", Private).SetValue(owner, true);
                var text = new TextShape { Text = "Hello\nworld", Anchor = new Point3d(60, 40, 0) }; owner.Shapes.Add(text);
                var controller = CanvasAnnotationController.Register(canvas, owner);
                try
                {
                    var bar = controller.Bar;
                    controller.SyncBar(); Program.Check(!bar.Visible, "bar shown with nothing to style");
                    controller.BeginText(owner, text, false); controller.SyncBar();
                    var editor = Field<TextEditorControl>(controller, "_editor"); var session = Field<TextEditSession>(controller, "_text");
                    Program.Check(editor != null && editor.IsHandleCreated && editor.BorderStyle == BorderStyle.None, "editor missing or bordered");
                    Program.Check(ShapeRenderer.EditingText.Contains(text.PersistentId), "stored text would show through the editor");
                    using (var bitmap = new Bitmap(300, 200))
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        g.Clear(Color.White); ShapeRenderer.Render(g, text, Color.Black, 1); ShapeRenderer.Handles(g, text, -1, true, 1);
                        bool ink = false;
                        for (int y = 0; y < 200 && !ink; y++) for (int x = 0; x < 300; x++) if (bitmap.GetPixel(x, y).ToArgb() != Color.White.ToArgb()) { ink = true; break; }
                        Program.Check(!ink, "edited text or its selection outline still painted");
                        controller.PaintEditorFrame(g);
                        int frame = Enumerable.Range(1, 24).Count(dy => { var c = bitmap.GetPixel(editor.Left + 10, editor.Top - dy); return c.B > 180 && c.R < 100; });
                        Program.Check(frame >= 1 && frame <= 2, "editing frame not painted as one thin line above the editor: " + frame);
                    }
                    var layout = text.Layout; var origin = canvas.Viewport.ProjectPoint(new PointF(layout.Bounds.Left + layout.Padding, layout.Bounds.Top + layout.Padding));
                    Program.Check(Math.Abs(editor.Left - origin.X) <= 1 && Math.Abs(editor.Top - origin.Y) <= 1, "editor not over the text it replaces: " + editor.Bounds + " vs " + origin);
                    Program.Check(editor.Width < layout.Bounds.Width + layout.Em && editor.Height < layout.Bounds.Height + layout.Em, "editor much larger than its text: " + editor.Size);
                    Program.Check(editor.BackColor.ToArgb() == CanvasAnnotationController.CanvasBack().ToArgb() && editor.BackColor.A == 255, "editor not canvas-coloured");
                    Program.Check(bar.Visible && bar.State.Target is TextEditTarget && bar.State.Tool == -1 && bar.Items.Any(i => i.Part == StyleBar.Part.Bold), "bar does not target the text being edited");
                    int height = editor.Height, width = editor.Width;
                    bar.Activate(Item(bar, StyleBar.Part.Bold)); bar.Activate(Item(bar, StyleBar.Part.Larger));
                    Program.Check(editor.Font.Bold && session.Draft.Bold && session.Draft.SizeMultiplier == 1.5 && !text.Bold && text.SizeMultiplier == 1, "bar did not restyle the live editor only");
                    Program.Check(editor.Height > height && editor.Width > width, "editor did not grow with the larger font");
                    Program.Check(editor.Format(Keys.Control | Keys.I) && editor.Font.Italic && session.Draft.Italic, "Ctrl+I not applied while typing");
                    Program.Check(editor.Format(Keys.Control | Keys.Shift | Keys.Oemcomma) && session.Draft.SizeMultiplier == 1, "Ctrl+Shift+< not applied");
                    Program.Check(bar.State.Typing && editor.Format(Keys.Control | Keys.D6) && editor.ForeColor.ToArgb() == Ink(6).ToArgb() && !session.Draft.StrokeVisible, "Ctrl+6 did not colour the text being typed");
                    Program.Check(editor.Format(Keys.Control | Keys.Shift | Keys.D4) && editor.BackColor.ToArgb() == Soft(4).ToArgb(), "Ctrl+Shift+4 did not fill the box being typed");
                    Program.Check(editor.Format(Keys.Control | Keys.Shift | Keys.D0) && editor.BackColor.ToArgb() == CanvasAnnotationController.CanvasBack().ToArgb(), "Ctrl+Shift+0 kept the fill");
                    Program.Check(!editor.Format(Keys.Control | Keys.A) && !editor.Format(Keys.Control | Keys.S) && !editor.Format(Keys.Control | Keys.Q) && !editor.Format(Keys.Control | Keys.D7) &&
                        !editor.Format(Keys.Control | Keys.Alt | Keys.D2) && !editor.Format(Keys.D4), "an editing key, a key row, AltGr or a plain digit was taken as a style key");
                    Program.Check(!text.TextColorOverride.HasValue && !text.FillColor.HasValue, "chords wrote to the stored text before commit");
                    bar.Activate(Item(bar, StyleBar.Part.TextColor)); bar.Choose(Cell(bar, "Red"));
                    Program.Check(editor.ForeColor.ToArgb() == Ink(3).ToArgb() && session.Draft.TextColorOverride.HasValue, "text colour not live");
                    bar.Activate(Item(bar, StyleBar.Part.Fill)); bar.Choose(Cell(bar, "White"));
                    Program.Check(editor.BackColor.ToArgb() == Color.White.ToArgb() && bar.FlyoutOpen, "editor does not take the text box fill");
                    Program.Check(editor.Dismiss() && !bar.FlyoutOpen && !editor.Dismiss() && Field<TextEditSession>(controller, "_text") == session, "Escape did not close the flyout first");
                    editor.Select(editor.TextLength, 0); height = editor.Height; width = editor.Width;
                    foreach (char c in "\rthird line that is clearly the longest of them") SendMessage(editor.Handle, 0x102, new IntPtr(c), new IntPtr(1));
                    Application.DoEvents();
                    long first = SendMessage(editor.Handle, 0xD6, IntPtr.Zero, IntPtr.Zero).ToInt64();
                    Program.Check(editor.Height > height && editor.Width > width && editor.Lines.Length == 3, "editor did not grow with typed text: " + editor.Size);
                    Program.Check((short)(first & 0xffff) >= 0 && SendMessage(editor.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32() == 0, "typed text left the editor scrolled");
                    Program.Check(session.Draft.Text.EndsWith("of them") && text.Text == "Hello\nworld", "typing was not buffer-only");
                    controller.CancelOwner(owner); controller.SyncBar();
                    Program.Check(ShapeRenderer.EditingText.Count == 0 && Field<TextEditorControl>(controller, "_editor") == null && !canvas.Controls.Contains(editor) && !bar.Visible,
                        "ending the edit left the editor, the hidden text or the bar behind");
                    var boxed = new TextShape { Text = "Boxed note that wraps onto several lines", Anchor = new Point3d(60, 200, 0), BoxWidth = 120, FillColor = Color.Yellow, FillOpacity = .5 };
                    owner.Shapes.Add(boxed); controller.BeginText(owner, boxed, false);
                    editor = Field<TextEditorControl>(controller, "_editor"); layout = boxed.Layout;
                    Program.Check(editor.WordWrap && Math.Abs(editor.Width - (layout.Bounds.Width - 2 * layout.Padding)) <= 1, "box text editor not exactly as wide as its box: " + editor.Width);
                    Program.Check(editor.BackColor.ToArgb() == CanvasAnnotationController.EditorBack(boxed, CanvasAnnotationController.CanvasBack()).ToArgb() &&
                        editor.BackColor.ToArgb() != CanvasAnnotationController.CanvasBack().ToArgb(), "box text editor ignores a translucent fill");
                    height = editor.Height; editor.Select(editor.TextLength, 0);
                    foreach (char c in " and keeps going for a few more words") SendMessage(editor.Handle, 0x102, new IntPtr(c), new IntPtr(1));
                    Application.DoEvents();
                    Program.Check(editor.Height > height && SendMessage(editor.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32() == 0, "wrapped text did not grow downwards");
                    controller.CancelOwner(owner);
                }
                finally { controller.Remove(owner); }
                Program.Check(controller.Bar.IsDisposed && canvas.Controls.Count == 0 && ShapeRenderer.EditingText.Count == 0, "controller left its bar on the canvas");
            }
        });
    }
}
