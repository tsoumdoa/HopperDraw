using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace hopperdraw
{
    // What the bar shows right now. The host builds a fresh one for every refresh; null hides the bar.
    internal sealed class StyleBarState
    {
        internal StyleTarget Target;
        internal int Tool = -1;      // current drawing tool; -1 hides the tool group (not drawing)
        internal bool ToolsLocked;   // the M input is wired: tools are shown but cannot be switched here
        internal Action<int> SetTool;
        internal bool Typing;        // a text is being typed: plain keys are text, only the Ctrl chords remain
    }

    // Shared base of the bar and its flyout: a child window of the canvas that never takes keyboard focus, so
    // the canvas keeps its tool keys and an open text editor keeps its caret while styles are clicked.
    internal abstract class StyleSurface : Control
    {
        internal static readonly Color Panel = Color.FromArgb(251, 251, 251), Edge = Color.FromArgb(70, 0, 0, 0),
            Ink = Color.FromArgb(48, 48, 48), Muted = Color.FromArgb(120, 120, 120), Faint = Color.FromArgb(190, 190, 190),
            Hot = Color.FromArgb(20, 0, 0, 0), Down = Color.FromArgb(40, 0, 0, 0),
            Accent = Color.FromArgb(0, 99, 177), AccentFill = Color.FromArgb(40, 0, 120, 215);
        private static readonly string Family = MessageFamily();
        private static string MessageFamily() { using (var font = SystemFonts.MessageBoxFont) return font.FontFamily.Name; }
        private readonly Dictionary<(int dpi, FontStyle style, float size), Font> _fonts = new Dictionary<(int, FontStyle, float), Font>();
        private readonly ToolTip _tips = new ToolTip();
        private string _tip;
        // Painted behind the rounded corners: a child window cannot be translucent, so it borrows the canvas colour.
        internal Color Backdrop = SystemColors.Control;

        protected StyleSurface()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserMouse, true);
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardDoubleClick, false);
            TabStop = false; Visible = false;
            // Explicit: a child otherwise inherits the canvas cursor (the drawing crosshair).
            Cursor = Cursors.Default;
        }
        internal float S => Math.Max(1, DeviceDpi / 96f);
        protected Font UiFont(FontStyle style = FontStyle.Regular, float size = 12)
        {
            var key = (DeviceDpi, style, size);
            if (!_fonts.TryGetValue(key, out var font)) { font = new Font(Family, size * S, style, GraphicsUnit.Pixel); _fonts.Add(key, font); }
            return font;
        }
        protected void Tip(string text)
        {
            if (text == _tip) return;
            _tip = text; _tips.SetToolTip(this, text ?? "");
        }
        protected void PaintPanel(Graphics g)
        {
            g.Clear(Backdrop); g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var path = Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 8 * S))
            using (var fill = new SolidBrush(Panel))
            using (var edge = new Pen(Edge))
            { g.FillPath(fill, path); g.DrawPath(edge, path); }
        }
        internal static GraphicsPath Rounded(RectangleF b, float radius)
        {
            var path = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(b.Width, b.Height));
            if (d <= 0) { path.AddRectangle(b); return path; }
            path.AddArc(b.Left, b.Top, d, d, 180, 90); path.AddArc(b.Right - d, b.Top, d, d, 270, 90);
            path.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90); path.AddArc(b.Left, b.Bottom - d, d, d, 90, 90);
            path.CloseFigure(); return path;
        }
        protected static void FillRounded(Graphics g, RectangleF b, float radius, Color color)
        {
            using (var path = Rounded(b, radius)) using (var brush = new SolidBrush(color)) g.FillPath(brush, path);
        }
        protected static RectangleF Centered(RectangleF b, float width, float height) =>
            new RectangleF(b.Left + (b.Width - width) / 2, b.Top + (b.Height - height) / 2, width, height);
        protected override void WndProc(ref Message m)
        {
            // A right-click here must not bubble up as a context-menu request to the canvas.
            if (m.Msg == 0x7B) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _tips.Dispose(); foreach (var font in _fonts.Values) font.Dispose(); _fonts.Clear(); }
            base.Dispose(disposing);
        }
    }

    // One clickable (or heading) cell of a flyout. Bounds are device pixels inside the flyout.
    internal sealed class FlyoutCell
    {
        internal RectangleF Bounds;
        internal string Tip, Text;
        // Key: the shortcut of a clickable cell (added to its tooltip). Legend: its key cap, shown in a strip
        // under the cell. Hint: grey text at the right edge (the modifier of a heading's section, a row's key).
        internal string Key, Legend, Hint;
        internal bool Checked, Keep, Left;
        internal FontStyle Style;
        internal float TextSize = 12;
        internal Action Click;
        internal Action<Graphics, RectangleF> Draw;
    }

    // Flow layout in logical (96 dpi) units: cells wrap inside a fixed inner width, headings start a row.
    internal sealed class FlyoutLayout
    {
        internal const float Pad = 8, Gap = 4, Legend = 11;
        internal readonly List<FlyoutCell> Cells = new List<FlyoutCell>();
        internal readonly float Scale, Inner;
        private float _x = Pad, _y = Pad, _row;
        internal FlyoutLayout(float scale, float inner = 192) { Scale = scale; Inner = inner; }
        private RectangleF Scaled(float x, float y, float w, float h) => new RectangleF(x * Scale, y * Scale, w * Scale, h * Scale);
        private void NewRow() { if (_row > 0) { _y += _row + Gap; _row = 0; } _x = Pad; }
        internal void Heading(string text, string hint = null)
        {
            NewRow();
            if (Cells.Count > 0) _y += 2;
            Cells.Add(new FlyoutCell { Bounds = Scaled(Pad, _y, Inner, 16), Text = text, Hint = hint, Left = true, TextSize = 11 });
            _y += 18;
        }
        internal FlyoutCell Add(float width, float height)
        {
            if (_x + width > Pad + Inner + .01f) NewRow();
            var cell = new FlyoutCell { Bounds = Scaled(_x, _y, width, height) };
            Cells.Add(cell); _x += width + Gap; _row = Math.Max(_row, height);
            return cell;
        }
        // Equal cells filling the inner width, `count` to a row.
        internal float Share(int count) => (Inner - (count - 1) * Gap) / count;
        internal Size Size => new Size((int)Math.Ceiling((Inner + 2 * Pad) * Scale),
            (int)Math.Ceiling((_y + (_row > 0 ? _row : -Gap) + Pad) * Scale));
    }

    // Popup panel above the bar. Also a child of the canvas, so it never activates or takes focus.
    internal sealed class StyleFlyout : StyleSurface
    {
        private readonly StyleBar _bar;
        private List<FlyoutCell> _cells = new List<FlyoutCell>();
        private int _hover = -1, _pressed = -1;
        internal IReadOnlyList<FlyoutCell> Cells => _cells;
        internal StyleFlyout(StyleBar bar) { _bar = bar; }
        // anchor: centre of the opening button's top edge, in parent client pixels.
        internal void Open(FlyoutLayout layout, Point anchor)
        {
            _cells = layout.Cells; Backdrop = _bar.Backdrop;
            var size = layout.Size; var host = Parent?.ClientSize ?? size;
            int x = Math.Max(0, Math.Min(anchor.X - size.Width / 2, host.Width - size.Width));
            int y = Math.Max(0, anchor.Y - size.Height - (int)(6 * S));
            SetBounds(x, y, size.Width, size.Height);
            if (_hover >= _cells.Count) _hover = -1;
            if (!Visible) { Visible = true; BringToFront(); }
            Invalidate();
        }
        internal void Close()
        {
            Visible = false; _cells = new List<FlyoutCell>(); _hover = _pressed = -1; Tip(null);
        }
        private int At(Point p)
        {
            for (int i = 0; i < _cells.Count; i++) if (_cells[i].Click != null && _cells[i].Bounds.Contains(p)) return i;
            return -1;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int index = At(e.Location);
            if (index != _hover) { _hover = index; Tip(index < 0 ? null : Describe(_cells[index])); Invalidate(); }
        }
        internal static string Describe(FlyoutCell cell) => cell.Key == null ? cell.Tip : (cell.Tip ?? cell.Text) + " (" + cell.Key + ")";
        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Tip(null); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            _bar.PointerPressed?.Invoke();
            if (e.Button == MouseButtons.Left) { _pressed = At(e.Location); Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            int pressed = _pressed; _pressed = -1; Invalidate();
            if (e.Button == MouseButtons.Left && pressed >= 0 && pressed == At(e.Location)) _bar.Choose(_cells[pressed]);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; float s = S; PaintPanel(g);
            using (var ink = new SolidBrush(Ink))
            using (var muted = new SolidBrush(Muted))
            using (var keys = new SolidBrush(Color.FromArgb(150, 150, 150)))
            using (var ring = new Pen(Accent, 1.5f * s))
            using (var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            using (var left = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            using (var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                for (int i = 0; i < _cells.Count; i++)
                {
                    var cell = _cells[i];
                    if (cell.Click != null)
                    {
                        if (cell.Checked) FillRounded(g, cell.Bounds, 5 * s, AccentFill);
                        else if (i == _hover || i == _pressed) FillRounded(g, cell.Bounds, 5 * s, i == _pressed ? Down : Hot);
                    }
                    // The content sits above the key cap.
                    var body = cell.Bounds;
                    if (cell.Legend != null) body.Height -= FlyoutLayout.Legend * s;
                    cell.Draw?.Invoke(g, body);
                    if (cell.Text != null)
                    {
                        var box = body;
                        if (cell.Left && cell.Click != null) { box.X += 8 * s; box.Width -= 8 * s; }
                        g.DrawString(cell.Text, UiFont(cell.Style, cell.TextSize), cell.Click == null ? muted : ink, box, cell.Left ? left : centre);
                    }
                    if (cell.Legend != null)
                        g.DrawString(cell.Legend, UiFont(FontStyle.Regular, 9), keys, new RectangleF(body.Left, body.Bottom - 3 * s, body.Width, FlyoutLayout.Legend * s), centre);
                    if (cell.Hint != null)
                        g.DrawString(cell.Hint, UiFont(FontStyle.Regular, 10), keys, cell.Click == null ? cell.Bounds : new RectangleF(body.Left, body.Top, body.Width - 8 * s, body.Height), right);
                    if (cell.Checked) using (var path = Rounded(RectangleF.Inflate(cell.Bounds, -.75f * s, -.75f * s), 5 * s)) g.DrawPath(ring, path);
                }
        }
    }

    // Always-visible style controls at the bottom centre of the canvas, replacing the passive tool chip:
    // tool buttons while drawing, typography while a text is targeted, and stroke/fill for the pen, the
    // selection or the text being typed. One click opens a flyout, one more applies; nothing here commits
    // an open text edit or cancels a draft.
    internal sealed class StyleBar : StyleSurface, IMessageFilter
    {
        internal enum Part { Tool, Preset, Smaller, Size, Larger, Bold, Italic, TextColor, Stroke, Fill, Divider }
        private static readonly Keys[] Upper = StyleKeys.Upper, Home = StyleKeys.Home;
        internal sealed class Item
        {
            internal Part Part; internal int Tool = -1; internal RectangleF Bounds; internal string Tip; internal bool Enabled = true;
        }
        private const float Pad = 4, Button = 28, Gap = 2, Inset = 12;
        private static readonly FrameShape Sample = new FrameShape();
        private readonly List<Item> _items = new List<Item>();
        private Item _hover, _pressed;
        private Part? _open;
        private bool _filtering, _modal;
        internal readonly StyleFlyout Flyout;
        internal StyleBarState State { get; private set; }
        internal IReadOnlyList<Item> Items => _items;
        internal bool FlyoutOpen => _open.HasValue;
        // Host hook: builds the current state. The bar re-reads it after every action it performs.
        internal Func<StyleBarState> Source;
        // Raised around the modal custom-colour dialog so the host can keep an open text edit alive.
        internal Action<bool> Modal;
        // Every mouse-down on the bar or flyout interrupts a pending keyboard gesture, even on empty space.
        internal Action PointerPressed;
        // Returns the chosen custom colour, or null when cancelled. Replaceable for tests.
        internal Func<Color, Color?> PickColor;

        internal StyleBar() { Flyout = new StyleFlyout(this); PickColor = AskColor; }
        internal void Attach(Control host) { host.Controls.Add(Flyout); host.Controls.Add(this); }
        internal bool Owns(IntPtr window) => IsHandleCreated && window == Handle || Flyout.IsHandleCreated && window == Flyout.Handle;
        internal void Raise() { if (Visible) BringToFront(); if (Flyout.Visible) Flyout.BringToFront(); }
        internal void Sync() => Present(Source?.Invoke());

        internal void Present(StyleBarState state)
        {
            State = state;
            if (state?.Target == null || Parent == null)
            {
                State = null; CloseFlyout(); _items.Clear(); _hover = _pressed = null; Tip(null);
                Visible = false; return;
            }
            Arrange();
            if (_open.HasValue)
            {
                var item = _items.FirstOrDefault(i => i.Part == _open.Value && i.Enabled);
                if (item == null) CloseFlyout(); else ShowFlyout(item);
            }
            if (!Visible) { Visible = true; BringToFront(); if (Flyout.Visible) Flyout.BringToFront(); }
            Invalidate();
        }

        private void Arrange()
        {
            float s = S; var target = State.Target;
            // rows: the key rows mean text size and style here. Typing leaves only the Ctrl chords.
            bool tools = State.Tool >= 0, text = target.Texts.Length > 0, typing = State.Typing, allText = target.AllText, rows = allText && !typing;
            float available = Parent.ClientSize.Width / s - 2 * Inset, x = Pad;
            void Add(Part part, float span, string tip, int index = -1, bool enabled = true)
            {
                _items.Add(new Item { Part = part, Tool = index, Tip = tip, Enabled = enabled, Bounds = new RectangleF(x * s, Pad * s, span * s, Button * s) });
                x += span + Gap;
            }
            void Divide() { x += 3; Add(Part.Divider, 1, null); x += 3; }
            // A narrow canvas drops the tool group first (the keys still switch tools), then the preset list.
            for (int attempt = 0; ; attempt++)
            {
                _items.Clear(); x = Pad;
                if (tools && attempt < 1)
                {
                    foreach (var tool in DrawingTool.All)
                        Add(Part.Tool, Button, State.ToolsLocked ? "Disconnect the M input to switch tools" : tool.Name + " (" + tool.Key + ")", tool.Index, !State.ToolsLocked);
                    Divide();
                }
                if (text)
                {
                    if (attempt < 2) Add(Part.Preset, 82, "Text style" + (rows ? " (" + StyleKeys.Caps(Home, StyleTarget.Presets.Length) + ")" : ""));
                    Add(Part.Smaller, 24, "Smaller text" + (typing ? " (Ctrl+,)" : ""));
                    Add(Part.Size, 36, "Text size" + (rows ? " (" + StyleKeys.Caps(Upper, StyleTarget.Sizes.Length) + ")" : ""));
                    Add(Part.Larger, 24, "Larger text" + (typing ? " (Ctrl+.)" : ""));
                    Add(Part.Bold, Button, "Bold" + (typing ? " (Ctrl+B)" : rows ? " (" + StyleKeys.Cap(Home[StyleTarget.BoldKey]) + ")" : ""));
                    Add(Part.Italic, Button, "Italic" + (typing ? " (Ctrl+I)" : rows ? " (" + StyleKeys.Cap(Home[StyleTarget.ItalicKey]) + ")" : ""));
                    Add(Part.TextColor, Button, "Text color" + (allText ? " (" + Keyed(StyleKeys.Digits) + " · " + Keyed("0") + " component color)" : ""));
                    Divide();
                }
                // The key rows belong to the text when only text is targeted, otherwise to the stroke.
                Add(Part.Stroke, 40, allText ? "Box border" : "Stroke: color " + StyleKeys.Digits +
                    " · thickness " + StyleKeys.Span(Upper, StyleTarget.Widths.Length) + " · line type " + StyleKeys.Span(Home, AnnotationMenuBuilder.DashNames.Length));
                bool fill = target.FillShapes.Length > 0;
                Add(Part.Fill, Button, !fill ? "Fill applies to closed shapes and text boxes" : "Fill: color " + Keyed(StyleKeys.Digits, true) +
                    (typing ? "" : " · opacity " + Keyed(StyleKeys.Span(Upper, StyleTarget.Opacities.Length), true) + " · hatch " + Keyed(StyleKeys.Span(Home, StyleTarget.Hatches.Length), true)), -1, fill);
                if (x - Gap + Pad <= available || attempt >= 2) break;
            }
            var host = Parent.ClientSize;
            int width = Math.Min((int)Math.Ceiling((x - Gap + Pad) * s), Math.Max(0, host.Width)), height = (int)Math.Ceiling((Button + 2 * Pad) * s);
            SetBounds(Math.Max(0, (host.Width - width) / 2), Math.Max(0, host.Height - height - (int)(Inset * s)), width, height);
        }

        // A style key as it is pressed right now: on its own, or with Ctrl while a text is being typed; with
        // Shift for the fill.
        private string Keyed(string keys, bool fill = false) => StyleKeys.Label(keys, State != null && State.Typing, fill);
        // The modifier of a whole section, for its heading.
        private string Held(bool fill) => State.Typing ? (fill ? "Ctrl + Shift +" : "Ctrl +") : fill ? "Shift +" : null;
        // A cell reached by its position on a key row: the key cap goes under it.
        private FlyoutCell RowCell(FlyoutLayout layout, float width, float height, Keys[] row, int index, bool keyed, bool fill = false)
        {
            keyed &= index < row.Length;
            var cell = layout.Add(width, height + (keyed ? FlyoutLayout.Legend : 0));
            if (keyed) { cell.Legend = StyleKeys.Cap(row[index]); cell.Key = Keyed(cell.Legend, fill); }
            return cell;
        }
        private static bool Same(Item a, Item b) => a != null && b != null && a.Part == b.Part && a.Tool == b.Tool;
        private Item At(Point p) => _items.FirstOrDefault(i => i.Part != Part.Divider && i.Bounds.Contains(p));
        protected override void OnMouseMove(MouseEventArgs e)
        {
            var item = At(e.Location);
            if (!Same(item, _hover) && (item != null || _hover != null)) { _hover = item; Tip(item?.Tip); Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { _hover = null; Tip(null); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            PointerPressed?.Invoke();
            if (e.Button == MouseButtons.Left) { _pressed = At(e.Location); Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            var pressed = _pressed; _pressed = null; Invalidate();
            var released = At(e.Location);
            if (e.Button == MouseButtons.Left && Same(pressed, released)) Activate(released);
        }

        // Also the entry point for tests, which drive the bar without synthesising mouse input.
        internal void Activate(Item item)
        {
            if (State == null || item == null || !item.Enabled) return;
            var target = State.Target;
            switch (item.Part)
            {
                case Part.Tool: CloseFlyout(); State.SetTool?.Invoke(item.Tool); break;
                case Part.Smaller: target.StepSize(-1); break;
                case Part.Larger: target.StepSize(1); break;
                case Part.Bold: target.ToggleBold(); break;
                case Part.Italic: target.ToggleItalic(); break;
                case Part.Preset:
                case Part.Size:
                case Part.TextColor:
                case Part.Stroke:
                case Part.Fill:
                    if (_open == item.Part) CloseFlyout(); else _open = item.Part;
                    break;
                case Part.Divider:
                    return;
                default: throw new ArgumentOutOfRangeException(nameof(item));
            }
            Sync();
        }
        internal void Choose(FlyoutCell cell)
        {
            if (State == null || cell?.Click == null) return;
            cell.Click();
            if (!cell.Keep) CloseFlyout();
            Sync();
        }

        internal bool CloseFlyout()
        {
            if (!_open.HasValue) return false;
            _open = null; Flyout.Close();
            if (_filtering) { Application.RemoveMessageFilter(this); _filtering = false; }
            Invalidate(); return true;
        }
        public bool PreFilterMessage(ref Message m)
        {
            // A press anywhere else dismisses the flyout; the press itself carries on to its target.
            bool press = m.Msg == 0x201 || m.Msg == 0x204 || m.Msg == 0x207 || m.Msg == 0x20b || m.Msg == 0xA1 || m.Msg == 0xA4;
            if (press && _open.HasValue && !_modal && !Owns(m.HWnd)) CloseFlyout();
            return false;
        }
        private void ShowFlyout(Item item)
        {
            var target = State.Target;
            var layout = new FlyoutLayout(S, item.Part == Part.Preset ? 132 : 192);
            switch (item.Part)
            {
                case Part.Preset: BuildPresets(layout, target); break;
                case Part.Size: BuildSizes(layout, target); break;
                case Part.TextColor: BuildTextColor(layout, target); break;
                case Part.Stroke: BuildStroke(layout, target); break;
                case Part.Fill: BuildFill(layout, target); break;
                default: CloseFlyout(); return;
            }
            Flyout.Open(layout, new Point(Left + (int)(item.Bounds.Left + item.Bounds.Width / 2), Top));
            if (!_filtering) { Application.AddMessageFilter(this); _filtering = true; }
        }

        private Color? AskColor(Color initial)
        {
            _modal = true; Modal?.Invoke(true);
            try
            {
                using (var dialog = new ColorDialog { Color = initial, FullOpen = true })
                    return dialog.ShowDialog(FindForm()) == DialogResult.OK ? dialog.Color : (Color?)null;
            }
            finally { _modal = false; Modal?.Invoke(false); }
        }

        // One colour section: the six swatches of the number keys, then the cell of the 0 key (the component
        // colour, or no fill), then on the next row what has no key: None for a stroke, the inks for a fill
        // (a hatch is drawn in the fill colour and wants a strong one), recent colours and Custom.
        // keyed: the number keys pick these swatches here. selected: the uniform current colour may be ticked
        // (false for mixed values or a hidden stroke).
        private void ColorCells(FlyoutLayout layout, StyleTarget target, bool keep, bool fill, bool keyed, Action none, bool noneChecked,
            bool selected, int? current, Action<Color?> choose)
        {
            float s = S; var basis = target.Basis;
            FlyoutCell Cell(string tip, bool check, Action click, Action<Graphics, RectangleF> draw, string key = null)
            {
                var cell = layout.Add(24, 24);
                cell.Tip = tip; cell.Checked = check; cell.Keep = keep; cell.Click = click; cell.Draw = draw; cell.Key = keyed ? key : null;
                return cell;
            }
            void Plain(Color color, string tip) => Cell(tip, selected && current == color.ToArgb(), () => choose(color), (g, r) => Swatch(g, r, color, s));
            var palette = fill ? AnnotationMenuBuilder.FillColors : AnnotationMenuBuilder.Colors;
            var names = fill ? AnnotationMenuBuilder.FillNames : AnnotationMenuBuilder.ColorNames;
            for (int i = 0; i < palette.Length; i++)
            {
                var color = palette[i]; string digit = (i + 1).ToString(CultureInfo.InvariantCulture);
                Cell(names[i], selected && current == color.ToArgb(), () => choose(color),
                    (g, r) => { Swatch(g, r, color, s); if (keyed) Badge(g, r, color, digit); }, Keyed(digit, fill));
            }
            if (fill) Cell("None", noneChecked, none, (g, r) => { NoneSwatch(g, r, s); if (keyed) Badge(g, r, Color.White, "0"); }, Keyed("0", true));
            else
            {
                Cell("Component color", selected && !current.HasValue, () => choose(null), (g, r) => AutoSwatch(g, r, basis, s, keyed ? "0" : "A"), Keyed("0"));
                if (none != null) Cell("None", noneChecked, none, (g, r) => NoneSwatch(g, r, s));
            }
            if (fill) for (int i = 0; i < AnnotationMenuBuilder.Colors.Length; i++) Plain(AnnotationMenuBuilder.Colors[i], AnnotationMenuBuilder.ColorNames[i]);
            foreach (var recent in AnnotationMenuBuilder.Recent.Where(r => AnnotationMenuBuilder.Colors.Concat(AnnotationMenuBuilder.FillColors).All(c => c.ToArgb() != r.ToArgb())).ToArray())
                Plain(recent, "Recent #" + recent.ToArgb().ToString("X8"));
            var start = current.HasValue ? Color.FromArgb(current.Value) : basis;
            Cell("Custom…", false, () => { var picked = PickColor?.Invoke(start); if (picked.HasValue) choose(picked.Value); }, (g, r) => CustomSwatch(g, r, s));
        }
        private void BuildStroke(FlyoutLayout layout, StyleTarget target)
        {
            var shapes = target.Shapes; float s = S;
            bool live = StyleTarget.Uniform(shapes, x => x.StrokeVisible, out bool visible) && visible;
            bool hidden = StyleTarget.Uniform(shapes, x => x.StrokeVisible, out visible) && !visible;
            bool sameColor = StyleTarget.Uniform(shapes, x => x.OverrideColor?.ToArgb(), out int? color);
            // Only text: the key rows are the text's (colour, size, style), not its box border's.
            bool keyed = !target.AllText && !State.Typing;
            layout.Heading("Color");
            ColorCells(layout, target, true, false, keyed, target.StrokeNone, hidden, live && sameColor, color, target.StrokeColor);
            layout.Heading("Thickness");
            bool sameWidth = StyleTarget.Uniform(shapes, x => x.ThicknessMultiplier, out float width);
            for (int i = 0; i < StyleTarget.Widths.Length; i++)
            {
                float v = StyleTarget.Widths[i]; var cell = RowCell(layout, layout.Share(5), 24, Upper, i, keyed);
                cell.Tip = v.ToString("0.#", CultureInfo.InvariantCulture) + "x"; cell.Checked = live && sameWidth && width == v; cell.Keep = true;
                cell.Click = () => target.StrokeWidth(v);
                cell.Draw = (g, r) => LineSample(g, r, Ink, SampleWidth(v) * s, DashStyle.Solid, s);
            }
            layout.Heading("Line type");
            bool sameDash = StyleTarget.Uniform(shapes, x => x.LineType, out DashStyle dash);
            for (int i = 0; i < AnnotationMenuBuilder.DashNames.Length; i++)
            {
                var v = (DashStyle)i; var cell = RowCell(layout, layout.Share(5), 24, Home, i, keyed);
                cell.Tip = AnnotationMenuBuilder.DashNames[i]; cell.Checked = live && sameDash && dash == v; cell.Keep = true;
                cell.Click = () => target.StrokeDash(v);
                cell.Draw = (g, r) => LineSample(g, r, Ink, 2 * s, v, s);
            }
        }
        private void BuildFill(FlyoutLayout layout, StyleTarget target)
        {
            var shapes = target.FillShapes; float s = S; bool rows = !State.Typing;
            bool sameColor = StyleTarget.Uniform(shapes, x => x.FillColor?.ToArgb(), out int? color);
            layout.Heading("Color", Held(true));
            ColorCells(layout, target, true, true, true, () => target.FillColor(null), sameColor && !color.HasValue, sameColor, color, c => target.FillColor(c));
            layout.Heading("Opacity", rows ? Held(true) : null);
            bool sameOpacity = StyleTarget.Uniform(shapes, x => x.FillOpacity, out double opacity);
            for (int i = 0; i < StyleTarget.Opacities.Length; i++)
            {
                double v = StyleTarget.Opacities[i]; var cell = RowCell(layout, layout.Share(5), 22, Upper, i, rows, true);
                cell.Text = (v * 100).ToString("0", CultureInfo.InvariantCulture) + "%"; cell.TextSize = 11;
                cell.Checked = sameOpacity && opacity == v; cell.Keep = true; cell.Click = () => target.FillOpacity(v);
            }
            layout.Heading("Hatch", rows ? Held(true) : null);
            bool sameHatch = StyleTarget.Uniform(shapes, x => x.Hatch, out HatchPattern hatch);
            for (int i = 0; i < StyleTarget.Hatches.Length; i++)
            {
                var v = StyleTarget.Hatches[i]; var cell = RowCell(layout, layout.Share(4), 26, Home, i, rows, true);
                cell.Tip = v == HatchPattern.None ? "Solid" : v.ToString(); cell.Checked = sameHatch && hatch == v; cell.Keep = true;
                cell.Click = () => target.Hatch(v);
                cell.Draw = (g, r) => HatchSample(g, r, v, s);
            }
        }
        private void BuildTextColor(FlyoutLayout layout, StyleTarget target)
        {
            bool same = StyleTarget.Uniform(target.Texts, t => t.TextColorOverride?.ToArgb(), out int? color);
            // With shapes selected as well, the number keys colour the strokes.
            bool keyed = target.AllText;
            layout.Heading("Text color", keyed ? Held(false) : null);
            ColorCells(layout, target, false, false, keyed, null, false, same, color, target.TextColor);
        }
        private void BuildPresets(FlyoutLayout layout, StyleTarget target)
        {
            bool same = StyleTarget.Uniform(target.Texts, t => t.StylePreset, out TextStylePreset preset);
            bool keyed = target.AllText && !State.Typing;
            for (int i = 0; i < StyleTarget.Presets.Length; i++)
            {
                var v = StyleTarget.Presets[i]; var cell = layout.Add(layout.Inner, 30);
                cell.Text = v.ToString(); cell.Left = true; cell.Checked = same && preset == v;
                cell.Style = v == TextStylePreset.Title ? FontStyle.Bold : FontStyle.Regular;
                cell.TextSize = v == TextStylePreset.Title ? 16 : v == TextStylePreset.Subtitle ? 14 : 12;
                cell.Click = () => target.Preset(v);
                if (keyed && i < Home.Length) cell.Hint = cell.Key = StyleKeys.Cap(Home[i]);
            }
        }
        private void BuildSizes(FlyoutLayout layout, StyleTarget target)
        {
            bool same = StyleTarget.Uniform(target.Texts, t => t.SizeMultiplier, out double size);
            bool keyed = target.AllText && !State.Typing;
            layout.Heading("Text size");
            for (int i = 0; i < StyleTarget.Sizes.Length; i++)
            {
                double v = StyleTarget.Sizes[i]; var cell = RowCell(layout, layout.Share(StyleTarget.Sizes.Length), 24, Upper, i, keyed);
                cell.Text = v.ToString("0.##", CultureInfo.InvariantCulture); cell.Tip = cell.Text + "x"; cell.TextSize = 10;
                cell.Checked = same && size == v; cell.Keep = true; cell.Click = () => target.Size(v);
            }
        }

        private static float SampleWidth(float multiplier) => Math.Min(6, Math.Max(.7f, multiplier * 1.4f));
        private static bool Light(Color color) => color.A < 60 || (color.R * 299 + color.G * 587 + color.B * 114) / 1000 > 215;
        private static void Swatch(Graphics g, RectangleF r, Color color, float s)
        {
            using (var path = Rounded(RectangleF.Inflate(r, -3 * s, -3 * s), 4 * s))
            using (var brush = new SolidBrush(color))
            using (var edge = new Pen(Color.FromArgb(Light(color) ? 110 : 50, 0, 0, 0)))
            { g.FillPath(brush, path); g.DrawPath(edge, path); }
        }
        // The digit that picks this swatch, readable on any colour.
        private void Badge(Graphics g, RectangleF r, Color on, string text)
        {
            using (var brush = new SolidBrush(Light(on) ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(215, 255, 255, 255)))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, UiFont(FontStyle.Bold, 9), brush, r, format);
        }
        private static void NoneSwatch(Graphics g, RectangleF r, float s)
        {
            var b = RectangleF.Inflate(r, -3 * s, -3 * s);
            using (var path = Rounded(b, 4 * s))
            using (var edge = new Pen(Color.FromArgb(110, 0, 0, 0)))
            using (var slash = new Pen(Color.FromArgb(215, 60, 60), 1.5f * s))
            {
                g.FillPath(Brushes.White, path);
                var clip = g.Save(); g.SetClip(path, CombineMode.Intersect); g.DrawLine(slash, b.Left, b.Bottom, b.Right, b.Top); g.Restore(clip);
                g.DrawPath(edge, path);
            }
        }
        // letter: "A" (automatic), or the digit that picks the component colour where the digits apply.
        private void AutoSwatch(Graphics g, RectangleF r, Color color, float s, string letter)
        {
            Swatch(g, r, color, s);
            using (var brush = new SolidBrush(Light(color) ? Ink : Color.White))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(letter, UiFont(FontStyle.Bold, 10), brush, r, format);
        }
        private static void CustomSwatch(Graphics g, RectangleF r, float s)
        {
            var b = RectangleF.Inflate(r, -3 * s, -3 * s); float cx = b.Left + b.Width / 2, cy = b.Top + b.Height / 2;
            using (var path = Rounded(b, 4 * s))
            using (var edge = new Pen(Muted) { DashStyle = DashStyle.Dash })
            using (var plus = new Pen(Ink, 1.5f * s))
            { g.DrawPath(edge, path); g.DrawLine(plus, cx - 4 * s, cy, cx + 4 * s, cy); g.DrawLine(plus, cx, cy - 4 * s, cx, cy + 4 * s); }
        }
        private static void LineSample(Graphics g, RectangleF r, Color color, float width, DashStyle dash, float s)
        {
            float y = r.Top + r.Height / 2;
            // A near-white stroke would vanish on the panel: back it with a slightly wider grey line.
            if (Light(color)) using (var back = new Pen(Color.FromArgb(120, 0, 0, 0), width + 2 * s)) g.DrawLine(back, r.Left + 6 * s, y, r.Right - 6 * s, y);
            using (var pen = new Pen(color, width) { DashStyle = StyleValidation.SafeDash(dash) }) g.DrawLine(pen, r.Left + 6 * s, y, r.Right - 6 * s, y);
        }
        private static void FillSample(Graphics g, RectangleF box, DrawShape style, double hatchScale)
        {
            Sample.TopLeft = new Rhino.Geometry.Point3d(box.Left, box.Top, 0); Sample.TopRight = new Rhino.Geometry.Point3d(box.Right, box.Top, 0);
            Sample.BottomRight = new Rhino.Geometry.Point3d(box.Right, box.Bottom, 0); Sample.BottomLeft = new Rhino.Geometry.Point3d(box.Left, box.Bottom, 0);
            style.CopyStyleTo(Sample); Sample.StrokeVisible = false; Sample.HatchScale = hatchScale;
            using (var path = AnnotationGeometry.Boundary(Sample)) HatchRenderer.Paint(g, Sample, path);
        }
        private static void HatchSample(Graphics g, RectangleF r, HatchPattern pattern, float s)
        {
            var box = RectangleF.Inflate(r, -4 * s, -4 * s);
            g.FillRectangle(Brushes.White, box);
            var style = new LineShape { FillColor = pattern == HatchPattern.None ? Color.FromArgb(150, 150, 150) : Ink, Hatch = pattern };
            FillSample(g, box, style, .6 * s);
            using (var edge = new Pen(Color.FromArgb(90, 0, 0, 0))) g.DrawRectangle(edge, box.X, box.Y, box.Width, box.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintPanel(e.Graphics);
            if (State == null) return;
            foreach (var item in _items) PaintItem(e.Graphics, item);
        }
        private void PaintItem(Graphics g, Item item)
        {
            float s = S; var b = item.Bounds; var target = State.Target; var texts = target.Texts;
            if (item.Part == Part.Divider)
            {
                using (var line = new Pen(Color.FromArgb(40, 0, 0, 0))) g.DrawLine(line, b.Left, b.Top + 5 * s, b.Left, b.Bottom - 5 * s);
                return;
            }
            bool on = item.Part == Part.Tool ? item.Tool == State.Tool
                : item.Part == Part.Bold ? StyleTarget.Uniform(texts, t => t.Bold, out bool bold) && bold
                : item.Part == Part.Italic ? StyleTarget.Uniform(texts, t => t.Italic, out bool italic) && italic
                : _open == item.Part;
            bool pressed = Same(item, _pressed), hot = item.Enabled && (pressed || Same(item, _hover));
            if (on) FillRounded(g, b, 5 * s, AccentFill);
            else if (hot) FillRounded(g, b, 5 * s, pressed ? Down : Hot);
            var ink = !item.Enabled ? Faint : on ? Accent : Ink;
            float cx = b.Left + b.Width / 2, cy = b.Top + b.Height / 2;
            using (var brush = new SolidBrush(ink))
            using (var muted = new SolidBrush(Muted))
            using (var pen = new Pen(ink, 1.5f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                switch (item.Part)
                {
                    case Part.Tool:
                        ToolGlyph.Draw(g, item.Tool, Centered(b, 16 * s, 16 * s), ink, 1.5f * s);
                        break;
                    case Part.Preset:
                        string name = StyleTarget.Uniform(texts, t => t.StylePreset, out TextStylePreset preset) ? preset.ToString() : "Mixed";
                        using (var left = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
                            g.DrawString(name, UiFont(), brush, new RectangleF(b.Left + 8 * s, b.Top, b.Width - 24 * s, b.Height), left);
                        using (var chevron = new Pen(Muted, 1.3f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                            g.DrawLines(chevron, new[] { new PointF(b.Right - 15 * s, cy - 1.5f * s), new PointF(b.Right - 11.5f * s, cy + 2 * s), new PointF(b.Right - 8 * s, cy - 1.5f * s) });
                        break;
                    case Part.Smaller:
                        g.DrawLine(pen, cx - 4 * s, cy, cx + 4 * s, cy);
                        break;
                    case Part.Larger:
                        g.DrawLine(pen, cx - 4 * s, cy, cx + 4 * s, cy); g.DrawLine(pen, cx, cy - 4 * s, cx, cy + 4 * s);
                        break;
                    case Part.Size:
                        string size = StyleTarget.Uniform(texts, t => t.SizeMultiplier, out double multiplier)
                            ? multiplier.ToString("0.##", CultureInfo.InvariantCulture) + "x" : "–";
                        g.DrawString(size, UiFont(FontStyle.Regular, 11), brush, b, centre);
                        break;
                    case Part.Bold:
                        g.DrawString("B", UiFont(FontStyle.Bold, 14), brush, b, centre);
                        break;
                    case Part.Italic:
                        g.DrawString("I", UiFont(FontStyle.Italic, 14), brush, b, centre);
                        break;
                    case Part.TextColor:
                        g.DrawString("A", UiFont(FontStyle.Regular, 13), brush, new RectangleF(b.Left, b.Top - 2 * s, b.Width, b.Height), centre);
                        var colour = texts.Length > 0 ? texts[0].TextColorOverride ?? target.Basis : target.Basis;
                        var bar = new RectangleF(cx - 7 * s, b.Bottom - 8 * s, 14 * s, 3.5f * s);
                        using (var fill = new SolidBrush(colour)) g.FillRectangle(fill, bar);
                        if (Light(colour)) using (var edge = new Pen(Color.FromArgb(110, 0, 0, 0))) g.DrawRectangle(edge, bar.X, bar.Y, bar.Width, bar.Height);
                        break;
                    case Part.Stroke:
                        var stroke = target.Shapes.FirstOrDefault();
                        if (stroke == null) break;
                        if (!stroke.StrokeVisible)
                            using (var none = new Pen(Muted, 1.3f * s)) { g.DrawEllipse(none, cx - 6 * s, cy - 6 * s, 12 * s, 12 * s); g.DrawLine(none, cx - 4.2f * s, cy + 4.2f * s, cx + 4.2f * s, cy - 4.2f * s); }
                        else LineSample(g, b, stroke.OverrideColor ?? target.Basis, SampleWidth(stroke.ThicknessMultiplier) * s, stroke.LineType, s);
                        break;
                    case Part.Fill:
                        var box = Centered(b, 16 * s, 16 * s); var filled = target.FillShapes.FirstOrDefault();
                        if (filled != null && filled.FillColor.HasValue)
                        {
                            g.FillRectangle(Brushes.White, box); FillSample(g, box, filled, .4 * s);
                            using (var edge = new Pen(Color.FromArgb(110, 0, 0, 0))) g.DrawRectangle(edge, box.X, box.Y, box.Width, box.Height);
                        }
                        else
                            using (var edge = new Pen(item.Enabled ? Muted : Faint, 1.3f * s))
                            { g.DrawRectangle(edge, box.X, box.Y, box.Width, box.Height); g.DrawLine(edge, box.Left, box.Bottom, box.Right, box.Top); }
                        break;
                    case Part.Divider:
                        break;
                    default: throw new ArgumentOutOfRangeException(nameof(item));
                }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_filtering) { Application.RemoveMessageFilter(this); _filtering = false; }
                Flyout.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
