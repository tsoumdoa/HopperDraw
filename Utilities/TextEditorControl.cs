using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace hopperdraw
{
    // Targeted editor route before form KeyPreview and menu preprocessing. Native EDIT still
    // receives every ordinary message (including clipboard, local undo, navigation and IME).
    internal sealed class TextEditorControl : TextBox, IMessageFilter
    {
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string text);
        internal Action Commit;
        private bool _finishing;
        private int _suppressedScan = -1;
        internal TextEditorControl()
        {
            Multiline = true; AcceptsReturn = true; AcceptsTab = true; BorderStyle = BorderStyle.FixedSingle;
            MaxLength = 100000; Application.AddMessageFilter(this);
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
            return false;
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
