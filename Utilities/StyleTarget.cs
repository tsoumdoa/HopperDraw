using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;

namespace hopperdraw
{
    // What the style bar edits: the session pen, the selected annotations, or the text being typed.
    // Shapes are read for display; every write is one named change, so each target keeps its own
    // transaction rule (session-only pen, one undo record per command, or the uncommitted edit buffer).
    internal abstract class StyleTarget
    {
        internal static readonly float[] Widths = { .5f, 1f, 2f, 3f, 5f };
        internal static readonly double[] Sizes = { .5, .75, 1, 1.5, 2, 3, 4 };
        internal static readonly double[] Opacities = { .1, .25, .5, .75, 1 };

        internal abstract Color Basis { get; }
        internal abstract DrawShape[] Shapes { get; }
        internal virtual DrawShape[] FillShapes => Shapes.Where(s => s.SupportsFill).ToArray();
        internal virtual TextShape[] Texts => Shapes.OfType<TextShape>().ToArray();
        // fill: the change belongs to the fill layer and only reaches shapes that can be filled.
        internal abstract void Apply(string name, Action<DrawShape> change, bool fill = false);
        internal abstract void ApplyText(string name, Action<TextShape> change);

        // True when every shape agrees; value is the first shape's either way (shown as the preview).
        internal static bool Uniform<TShape, T>(TShape[] shapes, Func<TShape, T> get, out T value)
        {
            value = shapes.Length == 0 ? default(T) : get(shapes[0]);
            var first = value;
            return shapes.Length > 0 && shapes.All(s => EqualityComparer<T>.Default.Equals(get(s), first));
        }

        // Choosing a stroke attribute for something that has no stroke at all (a text box, a pen set to None)
        // also shows the stroke. A mixed selection keeps each shape's visibility: recolouring lines must not
        // put borders on the texts selected with them.
        private void Stroke(string name, Action<DrawShape> change)
        {
            var shapes = Shapes;
            bool reveal = shapes.Length > 0 && shapes.All(s => !s.StrokeVisible);
            Apply(name, s => { change(s); if (reveal) s.StrokeVisible = true; });
        }
        internal void StrokeColor(Color? color)
        {
            AnnotationMenuBuilder.Remember(color);
            Stroke("Change color", s => s.OverrideColor = color);
        }
        internal void StrokeNone() => Apply("Change stroke visibility", s => s.StrokeVisible = false);
        internal void StrokeWidth(float width) => Stroke("Change thickness", s => s.ThicknessMultiplier = width);
        internal void StrokeDash(DashStyle dash) => Stroke("Change line type", s => s.LineType = dash);
        internal void FillColor(Color? color)
        {
            AnnotationMenuBuilder.Remember(color);
            Apply("Change fill color", s => s.FillColor = color, true);
        }
        internal void FillOpacity(double opacity) => Apply("Change fill opacity", s => s.FillOpacity = opacity, true);
        // The fill colour is the hatch ink, so a hatch chosen with no fill gets the palette's ink instead of staying invisible.
        internal void Hatch(HatchPattern pattern) => Apply("Change hatch", s =>
        {
            s.Hatch = pattern;
            if (pattern != HatchPattern.None && !s.FillColor.HasValue) s.FillColor = AnnotationMenuBuilder.Colors[0];
        }, true);

        private static void FitBox(TextShape text) { if (text.BoxWidth.HasValue) text.BoxWidth = text.ClampBoxWidth(text.BoxWidth.Value); }
        internal void Preset(TextStylePreset preset) => ApplyText("Change text style", t => { t.SetPreset(preset); FitBox(t); });
        internal void Size(double size) => ApplyText("Change text size", t => { t.SizeMultiplier = size; FitBox(t); });
        // Steps through the preset sizes from the first text's size; a no-op at either end.
        internal void StepSize(int direction)
        {
            var texts = Texts;
            if (texts.Length == 0) return;
            double current = texts[0].SizeMultiplier;
            var next = direction > 0 ? Sizes.Where(s => s > current + 1e-9).Cast<double?>().FirstOrDefault()
                : Sizes.Where(s => s < current - 1e-9).Cast<double?>().LastOrDefault();
            if (next.HasValue) Size(next.Value);
        }
        internal void ToggleBold()
        {
            bool on = !(Uniform(Texts, t => t.Bold, out bool bold) && bold);
            ApplyText("Change text bold", t => t.Bold = on);
        }
        internal void ToggleItalic()
        {
            bool on = !(Uniform(Texts, t => t.Italic, out bool italic) && italic);
            ApplyText("Change text italic", t => t.Italic = on);
        }
        internal void TextColor(Color? color)
        {
            AnnotationMenuBuilder.Remember(color);
            ApplyText("Change text color", t => t.TextColorOverride = color);
        }

        // Only text: the key rows then mean glyph colour, size and style instead of stroke colour, thickness
        // and line type (a text's box border is hidden by default and stays a matter for the bar).
        internal bool AllText { get { var shapes = Shapes; return shapes.Length > 0 && shapes.All(s => s is TextShape); } }
        internal static readonly HatchPattern[] Hatches = (HatchPattern[])Enum.GetValues(typeof(HatchPattern));
        internal static readonly TextStylePreset[] Presets = (TextStylePreset[])Enum.GetValues(typeof(TextStylePreset));
        // Positions on the A row after the text styles.
        internal static int BoldKey => Presets.Length;
        internal static int ItalicKey => Presets.Length + 1;
        private static string Times(double value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "x";
        private string Bold() { ToggleBold(); return Texts[0].Bold ? "Bold" : "Bold off"; }
        private string Italic() { ToggleItalic(); return Texts[0].Italic ? "Italic" : "Italic off"; }

        // Runs a style shortcut: a position on a key row means whatever the bar shows at that position for this
        // target. Returns a short description of the result for the hint line, or null when the key has
        // nothing to act on here (a position past the end of its row, fill on an open line): it is then not ours.
        internal string Run(StyleKey key)
        {
            if (Shapes.Length == 0) return null;
            var fills = FillShapes; bool text = AllText; int i = key.Index;
            switch (key.Row)
            {
                case StyleKeyRow.Number:
                    if (i < 0 || i > StyleKeys.Colors) return null;
                    if (key.Fill)
                    {
                        if (fills.Length == 0) return null;
                        if (i == 0) { FillColor(null); return "Fill: none"; }
                        FillColor(AnnotationMenuBuilder.FillColors[i - 1]); return "Fill: " + AnnotationMenuBuilder.FillNames[i - 1];
                    }
                    Color? color = i == 0 ? (Color?)null : AnnotationMenuBuilder.Colors[i - 1];
                    string name = i == 0 ? "component color" : AnnotationMenuBuilder.ColorNames[i - 1];
                    if (text) { TextColor(color); return "Text color: " + name; }
                    StrokeColor(color); return "Color: " + name;
                case StyleKeyRow.Upper:
                    if (key.Fill)
                    {
                        // Opacity of a fill that is not there would change nothing visible.
                        if (i >= Opacities.Length || !fills.Any(f => f.FillColor.HasValue)) return null;
                        FillOpacity(Opacities[i]); return "Fill opacity " + (Opacities[i] * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
                    }
                    if (text)
                    {
                        if (i >= Sizes.Length) return null;
                        Size(Sizes[i]); return "Text size " + Times(Sizes[i]);
                    }
                    if (i >= Widths.Length) return null;
                    StrokeWidth(Widths[i]); return "Thickness " + Times(Widths[i]);
                case StyleKeyRow.Home:
                    if (key.Fill)
                    {
                        if (i >= Hatches.Length || fills.Length == 0) return null;
                        Hatch(Hatches[i]); return "Hatch: " + (Hatches[i] == HatchPattern.None ? "solid" : Hatches[i].ToString());
                    }
                    if (text)
                    {
                        if (i < Presets.Length) { Preset(Presets[i]); return "Text style: " + Presets[i]; }
                        return i == BoldKey ? Bold() : i == ItalicKey ? Italic() : null;
                    }
                    if (i >= AnnotationMenuBuilder.DashNames.Length) return null;
                    StrokeDash((DashStyle)i); return "Line type: " + AnnotationMenuBuilder.DashNames[i];
                case StyleKeyRow.Bold: return Texts.Length == 0 ? null : Bold();
                case StyleKeyRow.Italic: return Texts.Length == 0 ? null : Italic();
                case StyleKeyRow.Smaller:
                case StyleKeyRow.Larger:
                    if (Texts.Length == 0) return null;
                    StepSize(key.Row == StyleKeyRow.Larger ? 1 : -1); return "Text size " + Times(Texts[0].SizeMultiplier);
                default: throw new ArgumentOutOfRangeException(nameof(key));
            }
        }
    }

    // Session pen of the drawing owner: the text pen for the Text tool, otherwise the geometric pen.
    internal sealed class PenTarget : StyleTarget
    {
        private readonly HopperDraw _owner;
        internal PenTarget(HopperDraw owner) { _owner = owner; }
        private bool Text => _owner.DrawMode == 5;
        // The geometric pen keeps colour/width/dash in component fields (they follow the inputs until
        // overridden); a frame sample reads the effective pen, fill included.
        private DrawShape Sample() { var sample = new FrameShape(); _owner.ApplyPen(sample); return sample; }
        internal override Color Basis => _owner.BorderColor;
        internal override DrawShape[] Shapes => new[] { Text ? _owner.TextPen : Sample() };
        internal override DrawShape[] FillShapes => _owner.DrawMode == 0 ? new DrawShape[0] : Shapes;
        internal override TextShape[] Texts => Text ? new[] { _owner.TextPen } : new TextShape[0];
        internal override void Apply(string name, Action<DrawShape> change, bool fill = false)
        {
            if (Text) { change(_owner.TextPen); _owner.Redraw(); return; }
            var pen = Sample();
            int? color = pen.OverrideColor?.ToArgb(); float width = pen.ThicknessMultiplier; var dash = pen.LineType;
            change(pen);
            pen.CopyStyleTo(_owner.PenStyle);
            // Only a real stroke change overrides the component's line type input.
            if (color != pen.OverrideColor?.ToArgb() || width != pen.ThicknessMultiplier || dash != pen.LineType)
                _owner.Pen(pen.OverrideColor, pen.ThicknessMultiplier, pen.LineType);
            else _owner.Redraw();
        }
        internal override void ApplyText(string name, Action<TextShape> change) { change(_owner.TextPen); _owner.Redraw(); }
    }

    // Selected leaves across owners: the same one-record transactions as the right-click menu.
    internal sealed class SelectionTarget : StyleTarget
    {
        private readonly HopperDraw[] _owners;
        private DrawShape[] _shapes;
        internal SelectionTarget(IEnumerable<HopperDraw> owners) { _owners = owners.Where(o => o.HasSelection).ToArray(); }
        internal override Color Basis => _owners.Length > 0 ? _owners[0].BorderColor : Color.Black;
        // Read once per target: the bar asks many times per paint, and the host builds a new target per refresh.
        internal override DrawShape[] Shapes => _shapes ?? (_shapes = _owners.SelectMany(o => o.SelectionLeaves).ToArray());
        internal override void Apply(string name, Action<DrawShape> change, bool fill = false)
        {
            AnnotationCommands.Apply(_owners, name, leaf =>
            {
                if (fill && !leaf.SupportsFill) return false;
                var candidate = new LineShape();
                leaf.CopyStyleTo(candidate);
                change(candidate);
                return !leaf.SameStyle(candidate);
            }, change);
            _shapes = null;
        }
        internal override void ApplyText(string name, Action<TextShape> change)
        {
            AnnotationCommands.Apply(_owners, name, leaf =>
            {
                if (!(leaf is TextShape text)) return false;
                var copy = (TextShape)text.Clone();
                change(copy);
                return !text.SameText(copy);
            }, leaf => change((TextShape)leaf));
            _shapes = null;
        }
    }

    // The text being typed. Changes stay in the edit buffer and are written with the text on commit.
    internal sealed class TextEditTarget : StyleTarget
    {
        private readonly TextEditSession _session;
        private readonly TextShape _pen;
        private readonly Action _changed;
        // pen: the text pen kept in step while typing a new text with the Text tool, so the next text starts
        // the same way. Editing an existing text leaves the pen alone, like every other selection styling.
        internal TextEditTarget(TextEditSession session, TextShape pen, Action changed) { _session = session; _pen = pen; _changed = changed; }
        internal override Color Basis => _session.Owner.BorderColor;
        internal override DrawShape[] Shapes => new DrawShape[] { _session.Draft };
        internal override void Apply(string name, Action<DrawShape> change, bool fill = false)
        {
            change(_session.Draft);
            if (_pen != null) change(_pen);
            _session.StyleEdited = true; _changed();
        }
        internal override void ApplyText(string name, Action<TextShape> change)
        {
            change(_session.Draft);
            if (_pen != null) change(_pen);
            _session.StyleEdited = true; _changed();
        }
    }
}
