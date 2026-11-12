using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.CompilerServices;

namespace hopperdraw
{
    internal sealed class TextLayout
    {
        internal RectangleF Bounds;
        internal string[] Lines;
        internal TextRun[][] Runs;
        internal float Em, Padding, LineHeight;
    }

    internal sealed class TextRun
    {
        internal string Text;
        internal float Width;
    }

    // Single canvas-unit layout for paint, picking, bounds, handles and editor placement.
    // Per-shape positioned layouts are keyed on every layout field incl. the anchor; line layout is shared by content.
    internal static class TextLayoutService
    {
        internal const int MaxTextLength = 100000, MaxLines = 10000;
        private sealed class Entry { internal TextShape Key; internal TextLayout Value; }
        private static readonly ConditionalWeakTable<TextShape, Entry> Cache = new ConditionalWeakTable<TextShape, Entry>();
        private static readonly HashSet<string> Families = InstalledFamilies();
        private static HashSet<string> InstalledFamilies()
        {
            using (var fonts = new InstalledFontCollection()) return new HashSet<string>(fonts.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        }
        private static readonly string DefaultFamily = GetDefaultFamily();
        private static string GetDefaultFamily()
        { using (var font = SystemFonts.MessageBoxFont) return font.FontFamily.Name; }
        internal static float Em(TextShape shape) => (float)((shape.StylePreset == TextStylePreset.Title ? 48 : shape.StylePreset == TextStylePreset.Subtitle ? 28 : 16) * shape.SizeMultiplier);
        internal static Font Font(TextShape shape, float scale = 1)
        {
            string family = Families.Contains("Segoe UI") ? "Segoe UI" : DefaultFamily;
            bool semibold = shape.StylePreset == TextStylePreset.Title && shape.Bold && Families.Contains("Segoe UI Semibold");
            if (semibold) family = "Segoe UI Semibold";
            var style = (shape.Bold && !semibold ? FontStyle.Bold : FontStyle.Regular) | (shape.Italic ? FontStyle.Italic : FontStyle.Regular);
            return new Font(family, Math.Max(.1f, Em(shape) * scale), style, GraphicsUnit.Pixel);
        }
        // Paint-time typography is cached: fetched per paint (never stored in a layout), so clearing the cache
        // can dispose fonts safely between paints. The canvas UI is single-threaded.
        private static readonly Dictionary<(TextStylePreset, double, bool, bool), Font> Fonts = new Dictionary<(TextStylePreset, double, bool, bool), Font>();
        private static readonly StringFormat PaintFormat = Format();
        internal static Font PaintFont(TextShape shape)
        {
            var key = (shape.StylePreset, shape.SizeMultiplier, shape.Bold, shape.Italic);
            if (Fonts.TryGetValue(key, out var font)) return font;
            if (Fonts.Count >= 128) { foreach (var f in Fonts.Values) f.Dispose(); Fonts.Clear(); }
            font = Font(shape); Fonts[key] = font; return font;
        }
        internal static StringFormat Format()
        {
            var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            return format;
        }
        // Line breaking and size depend only on content and typography, never on the anchor. Drag/move snapshots
        // clone shapes every frame, so the expensive measurement is shared by content and only positioned per shape.
        private static readonly Dictionary<(string text, double? box, TextStylePreset preset, double size, bool bold, bool italic), TextLayout> Shared =
            new Dictionary<(string, double?, TextStylePreset, double, bool, bool), TextLayout>();
        internal static TextLayout Get(TextShape shape)
        {
            var entry = Cache.GetValue(shape, _ => new Entry());
            if (entry.Key != null && entry.Key.SameText(shape)) return entry.Value;
            var key = (shape.Text ?? "", shape.BoxWidth, shape.StylePreset, shape.SizeMultiplier, shape.Bold, shape.Italic);
            if (!Shared.TryGetValue(key, out var basis))
            {
                if (Shared.Count >= 512) Shared.Clear();
                try { basis = Build(shape); }
                catch (Exception)
                {
                    // Cache the fallback too: a broken font/GDI resource must not fail on every mouse move.
                    basis = BuildLines(shape, text => text.Length * Em(shape) * 1.2f,
                        c => Em(shape) * (c == '\t' ? 4.8f : 1.2f));
                }
                Shared[key] = basis;
            }
            entry.Value = new TextLayout { Em = basis.Em, Padding = basis.Padding, LineHeight = basis.LineHeight, Lines = basis.Lines, Runs = basis.Runs,
                Bounds = new RectangleF((float)shape.Anchor.X, (float)shape.Anchor.Y, basis.Bounds.Width, basis.Bounds.Height) };
            entry.Key = new TextShape { Text = shape.Text, Anchor = shape.Anchor, BoxWidth = shape.BoxWidth };
            shape.CopyTextStyleTo(entry.Key);
            return entry.Value;
        }
        private static TextLayout Build(TextShape shape)
        {
            using (var bitmap = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bitmap))
            using (var font = Font(shape))
            using (var format = Format())
            {
                var widths = new Dictionary<char, float>();
                float Width(char c)
                {
                    if (!widths.TryGetValue(c, out float width))
                    { width = g.MeasureString(c == '\t' ? "    " : c.ToString(), font, int.MaxValue, format).Width; widths.Add(c, width); }
                    return width;
                }
                return BuildLines(shape, text => g.MeasureString(text, font, int.MaxValue, format).Width, Width);
            }
        }
        // GDI+ has paragraph length and fallback-font width limits. Use the same bounded runs for
        // measuring and painting so unboxed bounds cannot silently cap below the painted extent.
        private static IEnumerable<string> Chunks(string text)
        {
            for (int start = 0; start < text.Length;)
            {
                int count = Math.Min(1024, text.Length - start);
                if (start + count < text.Length && char.IsHighSurrogate(text[start + count - 1]) &&
                    char.IsLowSurrogate(text[start + count])) count--;
                yield return text.Substring(start, count);
                start += count;
            }
        }
        private static TextLayout BuildLines(TextShape shape, Func<string, float> measure, Func<char, float> widthOf)
        {
            float em = Em(shape), pad = .4f * em;
            float available = shape.BoxWidth.HasValue ? (float)Math.Max(em, shape.BoxWidth.Value - 2 * pad) : float.MaxValue;
            var lines = new List<string>();
            string text = shape.Text ?? "";
            if (text.Length > MaxTextLength) text = text.Substring(0, MaxTextLength);
            foreach (string paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (lines.Count >= MaxLines) break;
                if (!shape.BoxWidth.HasValue || paragraph.Length == 0) { lines.Add(paragraph); continue; }
                int start = 0;
                while (start < paragraph.Length && lines.Count < MaxLines)
                {
                    float width = 0;
                    int lastBreak = -1, end = start;
                    for (; end < paragraph.Length; end++)
                    {
                        char c = paragraph[end];
                        float next = width + widthOf(c);
                        if (!char.IsWhiteSpace(c) && next > available && end > start) break;
                        width = next;
                        if (char.IsWhiteSpace(c)) lastBreak = end + 1;
                    }
                    if (end < paragraph.Length && lastBreak > start) end = lastBreak;
                    if (end == start) end++;
                    if (end < paragraph.Length && char.IsHighSurrogate(paragraph[end - 1]) && char.IsLowSurrogate(paragraph[end])) end++;
                    lines.Add(paragraph.Substring(start, end - start));
                    start = end;
                }
            }
            if (lines.Count == 0) lines.Add("");
            var runs = lines.Select(line => Chunks(line.TrimEnd().Replace("\t", "    "))
                .Select(chunk => new TextRun { Text = chunk, Width = measure(chunk) }).ToArray()).ToArray();
            float maxWidth = runs.Max(line => line.Sum(run => run.Width));
            return new TextLayout { Em = em, Padding = pad, LineHeight = 1.3f * em, Lines = lines.ToArray(), Runs = runs,
                Bounds = new RectangleF((float)shape.Anchor.X, (float)shape.Anchor.Y,
                    shape.BoxWidth.HasValue ? available + 2 * pad : Math.Max(1, maxWidth) + 2 * pad,
                    lines.Count * 1.3f * em + 2 * pad) };
        }
        internal static bool Greek(float em, float zoom) => em * zoom < 3;
        internal static void Paint(Graphics g, TextShape shape, Color basis)
        {
            var layout = Get(shape);
            float[] elements; using (var transform = g.Transform) elements = transform.Elements;
            float zoom = (float)Math.Sqrt(elements[0] * elements[0] + elements[1] * elements[1]);
            bool greek = Greek(layout.Em, zoom);
            var state = g.Save();
            try
            {
                // Stored narrow boxes get at least one em of content, including for clipping.
                if (shape.BoxWidth.HasValue)
                    g.SetClip(new RectangleF(layout.Bounds.X + layout.Padding, layout.Bounds.Y + layout.Padding,
                        Math.Max(0, layout.Bounds.Width - 2 * layout.Padding), layout.Bounds.Height - 2 * layout.Padding), System.Drawing.Drawing2D.CombineMode.Intersect);
                var font = PaintFont(shape); var format = PaintFormat;
                using (var brush = new SolidBrush(greek ? Color.FromArgb((shape.TextColorOverride ?? basis).A, Color.Gray) : shape.TextColorOverride ?? basis))
                {
                    var clip = g.VisibleClipBounds;
                    for (int i = 0; i < layout.Lines.Length; i++)
                    {
                        float y = layout.Bounds.Y + layout.Padding + i * layout.LineHeight;
                        if (y + layout.LineHeight < clip.Top || y > clip.Bottom) continue;
                        if (greek)
                        {
                            if (layout.Lines[i].Trim().Length > 0)
                                g.FillRectangle(brush, layout.Bounds.X + layout.Padding, y + layout.Em * .3f,
                                    Math.Max(0, Math.Min(layout.Bounds.Width - 2 * layout.Padding, layout.Lines[i].TrimEnd().Length * layout.Em * .5f)), layout.Em * .4f);
                        }
                        else
                        {
                            float x = layout.Bounds.X + layout.Padding;
                            foreach (var run in layout.Runs[i])
                            {
                                if (x > clip.Right) break;
                                if (x + run.Width >= clip.Left) g.DrawString(run.Text, font, brush, x, y, format);
                                x += run.Width;
                            }
                        }
                    }
                }
            }
            finally { g.Restore(state); }
        }
    }
}
