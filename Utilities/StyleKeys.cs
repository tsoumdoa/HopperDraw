using System;
using System.Windows.Forms;

namespace hopperdraw
{
    // Number: the digit row (Index is the digit; 0 is "back to the default"). Upper: the Q row, Home: the A row
    // (Index is the position from the left). The last four exist only as Ctrl chords inside the text editor.
    internal enum StyleKeyRow { Number, Upper, Home, Bold, Italic, Smaller, Larger }

    // One style shortcut. Fill: Shift was held, which addresses the fill instead of the stroke or the text.
    internal struct StyleKey
    {
        internal StyleKeyRow Row;
        internal int Index;
        internal bool Fill;
        internal StyleKey(StyleKeyRow row, int index = 0, bool fill = false) { Row = row; Index = index; Fill = fill; }
    }

    // The keyboard as a palette. On the canvas (drawing, or annotations selected) three rows are read by
    // position, left to right, exactly as the style bar lays its choices out:
    //   1 2 3 4 5 6 … 0   colour (0: the default)
    //   Q W E R T (Y U)   thickness, or the size of text
    //   A S D F G …       line type, or the style of text
    // and the row below (Z X C V B N) picks the tool, which is DrawingTool's business. Shift moves the three
    // rows to the fill: colour, opacity, hatch. What a position means is the target's decision (StyleTarget.Run).
    // Inside the text editor a plain key is text, so only the usual chords exist there: Ctrl+digit for colour
    // (with Shift, the box fill), Ctrl+B, Ctrl+I and Ctrl+, / Ctrl+. for the size.
    // No Grasshopper reference: the text editor control uses this table as well.
    internal static class StyleKeys
    {
        internal static readonly Keys[] Upper = { Keys.Q, Keys.W, Keys.E, Keys.R, Keys.T, Keys.Y, Keys.U };
        internal static readonly Keys[] Home = { Keys.A, Keys.S, Keys.D, Keys.F, Keys.G, Keys.H, Keys.J, Keys.K, Keys.L, Keys.OemSemicolon, Keys.OemQuotes };
        internal const int Colors = 6;

        internal static bool Match(Keys data, bool typing, out StyleKey key)
        {
            key = default(StyleKey);
            Keys code = data & Keys.KeyCode, mods = data & Keys.Modifiers;
            // Exactly Ctrl while typing, nothing on the canvas. AltGr arrives as Ctrl+Alt and types characters
            // on many layouts, so it is never a shortcut.
            if ((mods & ~Keys.Shift) != (typing ? Keys.Control : Keys.None)) return false;
            bool shift = (mods & Keys.Shift) != 0;
            if (code >= Keys.D0 && code <= Keys.D0 + Colors) { key = new StyleKey(StyleKeyRow.Number, code - Keys.D0, shift); return true; }
            if (typing)
            {
                switch (code)
                {
                    case Keys.B: if (shift) return false; key = new StyleKey(StyleKeyRow.Bold); return true;
                    case Keys.I: if (shift) return false; key = new StyleKey(StyleKeyRow.Italic); return true;
                    // With or without Shift: the keys are "<" and ">" either way.
                    case Keys.Oemcomma: key = new StyleKey(StyleKeyRow.Smaller); return true;
                    case Keys.OemPeriod: key = new StyleKey(StyleKeyRow.Larger); return true;
                    default: return false;
                }
            }
            int upper = Array.IndexOf(Upper, code);
            if (upper >= 0) { key = new StyleKey(StyleKeyRow.Upper, upper, shift); return true; }
            int home = Array.IndexOf(Home, code);
            if (home >= 0) { key = new StyleKey(StyleKeyRow.Home, home, shift); return true; }
            return false;
        }
        // The key cap of a position, for legends and tooltips.
        internal static string Cap(Keys key) => key == Keys.OemSemicolon ? ";" : key == Keys.OemQuotes ? "'" : key.ToString();
        // The first `count` keys of a row: "Q–T" as a range, "A S D" spelled out.
        internal static string Span(Keys[] row, int count) => Cap(row[0]) + "–" + Cap(row[Math.Min(count, row.Length) - 1]);
        internal static string Caps(Keys[] row, int count) => string.Join(" ", Array.ConvertAll(row, Cap), 0, Math.Min(count, row.Length));
        internal static readonly string Digits = "1–" + Colors;
        // How a chord is written in tooltips: "3" on the canvas, "Ctrl+3" while typing; with Shift for the fill.
        internal static string Label(string key, bool typing, bool fill = false) => (typing ? "Ctrl+" : "") + (fill ? "Shift+" : "") + key;
    }
}
