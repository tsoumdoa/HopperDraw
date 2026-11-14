using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace hopperdraw
{
    // Targeted editor route before form KeyPreview and menu preprocessing. Native EDIT still
    // receives every ordinary message (including clipboard, local undo, navigation and IME).
    // It is dressed as in-place text: no border, no margins, sized to its content by the host.
    internal sealed class TextEditorControl : TextBox, IMessageFilter
    {
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string text);
        internal Action Commit;
        // Escape asks this first: true means it closed something of the host's (a style flyout) and the edit stays open.
        internal Func<bool> Dismiss;
        // The style shortcuts (StyleKeys, held with Ctrl here) are offered to the host; true means handled.
        internal Func<Keys, bool> Format;
        // Raised when the text has changed but before it is displayed, so the host can grow the box first.
        internal Action Updating;
        private bool _finishing, _updating, _dismissed, _formatHandled;
        private int _suppressedScan = -1, _formatScan = -1;
        internal TextEditorControl()
        {
            Multiline = true; AcceptsReturn = true; AcceptsTab = true; BorderStyle = BorderStyle.None;
            MaxLength = 100000; Application.AddMessageFilter(this);
        }
        internal static bool IsFormatKey(Keys data) => StyleKeys.Match(data, true, out _);
        // One action per physical press, as on the canvas: a held chord stays consumed without repeating.
        private bool OfferFormat(Keys data, Message m)
        {
            if (Format == null || !IsFormatKey(data)) return false;
            if ((m.LParam.ToInt64() & (1L << 30)) == 0) _formatHandled = Format(data);
            return _formatHandled;
        }
        // EDIT keeps font-dependent side margins, which would shift the text off the canvas glyphs it replaces.
        private void ZeroMargins() { if (IsHandleCreated) SendMessage(Handle, 0xD3, new IntPtr(3), IntPtr.Zero); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ZeroMargins(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ZeroMargins(); }
        // Pixel size of the content as the control itself lays it out (GDI metrics and its own wrapping), which
        // is what must fit for nothing to scroll. Width counts only for unwrapped text.
        internal Size ContentSize
        {
            get
            {
                const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                var unbounded = new Size(int.MaxValue, int.MaxValue);
                int line = TextRenderer.MeasureText("Xg", Font, unbounded, flags).Height;
                int lines = IsHandleCreated ? Math.Max(1, SendMessage(Handle, 0xBA, IntPtr.Zero, IntPtr.Zero).ToInt32()) : 1;
                int width = 0;
                if (!WordWrap)
                    // Very long texts fall back to the host's own layout width.
                    foreach (string text in Text.Split('\n').Take(400))
                        width = Math.Max(width, TextRenderer.MeasureText(text.TrimEnd('\r').Replace("\t", "        "), Font, unbounded, flags).Width);
                return new Size(width, lines * line);
            }
        }
        // A character or line that did not fit scrolls the view before the host can grow the box; undo that.
        internal void ResetScroll()
        {
            if (!IsHandleCreated || TextLength == 0) return;
            long first = SendMessage(Handle, 0xD6, IntPtr.Zero, IntPtr.Zero).ToInt64();
            if ((short)(first & 0xffff) < 0) SendMessage(Handle, 0xB6, new IntPtr(-100000), IntPtr.Zero);
            int line = SendMessage(Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32();
            if (line > 0) SendMessage(Handle, 0xB6, IntPtr.Zero, new IntPtr(-line));
        }
        protected override bool ProcessKeyMessage(ref Message m) => ProcessKeyEventArgs(ref m);
        protected override bool ProcessCmdKey(ref Message m, Keys keyData)
        {
            // TextBoxBase normally implements these in preprocessing, which we bypass to
            // avoid bubbling to the parent menu. These methods send native EDIT commands.
            switch (keyData)
            {
                case Keys.Control | Keys.Back: DeleteWord(true); return true;
                case Keys.Control | Keys.Delete: DeleteWord(false); return true;
                case Keys.Control | Keys.A: SelectAll(); return true;
                case Keys.Control | Keys.C:
                case Keys.Control | Keys.Insert: Copy(); return true;
                case Keys.Control | Keys.X:
                case Keys.Shift | Keys.Delete: Cut(); return true;
                case Keys.Control | Keys.V:
                case Keys.Shift | Keys.Insert: Paste(); return true;
                case Keys.Control | Keys.Z:
                case Keys.Alt | Keys.Back: Undo(); return true;
            }
            return OfferFormat(keyData, m);
        }
        private void DeleteWord(bool backwards)
        {
            if (SelectionLength == 0)
            {
                string text = Text;
                int start = SelectionStart, end = start;
                if (backwards)
                {
                    while (start > 0 && char.IsWhiteSpace(text[start - 1])) start--;
                    while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
                }
                else
                {
                    while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
                    while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
                }
                Select(start, end - start);
            }
            // Unlike SelectedText's setter, EM_REPLACESEL with canUndo preserves local undo.
            SendMessage(Handle, 0xC2, new IntPtr(1), "");
        }
        protected override bool IsInputKey(Keys keyData) => true;
        private bool FinishMessage(Message m)
        {
            int scan = (int)(m.LParam.ToInt64() & 0xff0000);
            if (m.Msg == 0x102 && scan == _suppressedScan) { _suppressedScan = -1; return true; }
            if (m.Msg != 0x100) return false;
            var key = (Keys)m.WParam.ToInt32();
            if (key != Keys.Escape && !(key == Keys.Enter && (ModifierKeys & Keys.Control) != 0)) return false;
            _suppressedScan = scan;
            if (key == Keys.Escape && !_finishing)
            {
                // A held Escape keeps belonging to what its first press dismissed; it never goes on to commit.
                if ((m.LParam.ToInt64() & (1L << 30)) == 0) _dismissed = Dismiss?.Invoke() == true;
                if (_dismissed) return true;
            }
            if (!_finishing) { _finishing = true; Commit?.Invoke(); }
            return true;
        }
        protected override void WndProc(ref Message m)
        {
            // Some host dispatch bypasses the application filter. Keep the finish key
            // and its delayed WM_CHAR paired there as well, without touching other messages.
            if (m.Msg == 0x102 && char.IsControl((char)m.WParam.ToInt32()) &&
                m.WParam.ToInt32() != 8 && m.WParam.ToInt32() != 9 && m.WParam.ToInt32() != 13)
            { m.Result = IntPtr.Zero; return; }
            if (FinishMessage(m)) { m.Result = IntPtr.Zero; return; }
            // Unfiltered dispatch never reaches ProcessCmdKey, and the real loop still translates the key:
            // Ctrl+I arrives as a tab from the I key. Handle the format
            // key here and drop that control character; a letter typed later with the same key is a
            // different character and is left alone.
            int scan = (int)(m.LParam.ToInt64() & 0xff0000);
            if (m.Msg == 0x102 && m.WParam.ToInt32() < 32 && scan == _formatScan) { m.Result = IntPtr.Zero; return; }
            if (m.Msg == 0x100 && OfferFormat((Keys)m.WParam.ToInt32() | ModifierKeys, m))
            { _formatScan = scan; m.Result = IntPtr.Zero; return; }
            // Reflected EN_UPDATE: the new text is formatted but not yet drawn.
            if (m.Msg == 0x2111 && ((m.WParam.ToInt64() >> 16) & 0xffff) == 0x400 && !_updating && Updating != null)
            {
                _updating = true;
                try { Updating(); } finally { _updating = false; }
            }
            base.WndProc(ref m);
        }
        public bool PreFilterMessage(ref Message m)
        {
            if (!IsHandleCreated || m.HWnd != Handle) return false;
            if (FinishMessage(m)) return true;
            if (m.Msg != 0x100 && m.Msg != 0x101 && m.Msg != 0x104 && m.Msg != 0x105) return false;
            var key = (Keys)m.WParam.ToInt32();
            if ((m.Msg == 0x100 || m.Msg == 0x104) && ProcessCmdKey(ref m, key | ModifierKeys)) return true;
            // Translate only the down: the real loop will not translate a filtered message.
            if (m.Msg == 0x100 || m.Msg == 0x104) TranslateMessage(ref m);
            SendMessage(Handle, m.Msg, m.WParam, m.LParam);
            return true;
        }
        protected override void Dispose(bool disposing)
        { if (disposing) Application.RemoveMessageFilter(this); base.Dispose(disposing); }
    }
}
