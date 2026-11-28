using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using hopperdraw;

// A real WinForms message loop deliberately emulates GH's form-level forwarding.
// PostMessage, not direct event callbacks, exercises preprocessing, TranslateMessage,
// NativeWindow dispatch, and KeyPreview in their production order. No Rhino needed.
internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint key, uint mapType);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")]
    private static extern bool SetKeyboardState(byte[] state);

    private sealed class Canvas : Control
    {
        internal Canvas()
        {
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick | ControlStyles.Selectable, true);
            TabStop = true;
        }
        protected override bool IsInputKey(Keys keyData) { return true; }
    }

    // GH_Canvas uses Control's default dialog-key classification (unlike Canvas above).
    private sealed class HostCanvas : Control
    {
        internal HostCanvas() { SetStyle(ControlStyles.Selectable, true); TabStop = true; }
    }

    private static void HostKey(Control canvas, Keys key, bool up = false, bool repeat = false)
    {
        long flags = 1 | ((long)MapVirtualKey((uint)key, 0) << 16);
        if (repeat || up) flags |= 1L << 30;
        if (up) flags |= 1L << 31;
        var message = Message.Create(canvas.Handle, up ? 0x101 : 0x100, new IntPtr((int)key), new IntPtr(flags));
        // Rhino's native message pump can preprocess without Application's IMessageFilters.
        if (canvas.PreProcessControlMessage(ref message) != PreProcessControlState.MessageProcessed)
            SendMessage(message.HWnd, message.Msg, message.WParam, message.LParam);
    }

    private static int _failures;
    private static int _activatedA, _activatedB, _forwarded, _characters;
    private static bool _eligibleA, _eligibleB;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Down(Control canvas, Keys key, bool repeat = false)
    {
        long flags = 1 | ((long)MapVirtualKey((uint)key, 0) << 16);
        if (repeat) flags |= 1L << 30;
        Check(PostMessage(canvas.Handle, 0x100, new IntPtr((int)key), new IntPtr(flags)), "PostMessage down failed");
    }

    private static void Up(Control canvas, Keys key)
    {
        long flags = 1 | ((long)MapVirtualKey((uint)key, 0) << 16) | (3L << 30);
        Check(PostMessage(canvas.Handle, 0x101, new IntPtr((int)key), new IntPtr(flags)), "PostMessage up failed");
    }

    private static void Tap(Control canvas, Keys key) { Down(canvas, key); Up(canvas, key); Application.DoEvents(); }

    private static void Run(string name, Action action, CanvasDrawingShortcut.Registration registration = null)
    {
        registration?.Reset();
        _activatedA = _activatedB = _forwarded = _characters = 0;
        _eligibleA = true; _eligibleB = false;
        try { action(); Console.WriteLine("PASS: " + name); }
        catch (Exception error) { _failures++; Console.WriteLine("FAIL: " + name + ": " + error.Message); }
        Application.DoEvents();
    }

    [STAThread]
    private static int Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        using (var form = new Form { KeyPreview = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) })
        using (var canvas = new Canvas { Size = new Size(200, 150) })
        using (var text = new TextBox { Location = new Point(0, 160) })
        {
            form.Controls.Add(canvas); form.Controls.Add(text);
            form.KeyDown += (sender, e) =>
            {
                if (canvas.Focused && e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z)
                {
                    _forwarded++;
                    e.Handled = true; // GH's editor forwards letters before the canvas sees them.
                }
            };
            canvas.KeyPress += (sender, e) => { _characters++; };
            form.Shown += (sender, e) => form.BeginInvoke(new Action(() =>
            {
                try
                {
                    canvas.Focus();
                    Check(canvas.Focused, "Canvas could not acquire keyboard focus");
                    Run("native host preprocessing lets Escape cancel then exit drawing", () =>
                    {
                        using (var host = new HostCanvas { Size = new Size(100, 100) })
                        using (var cancel = new Button { Text = "Cancel" })
                        {
                            form.Controls.Add(host); form.Controls.Add(cancel); form.CancelButton = cancel;
                            int native = 0, layers = 0;
                            cancel.Click += (s, args) => native++;
                            using (var registration = CanvasDrawingShortcut.Register(host, () => false, () => { }))
                            {
                                registration.Command = key => { if (key != Keys.Escape || layers == 2) return false; layers++; return true; };
                                host.Focus(); Check(host.Focused, "Host canvas cannot focus");
                                using (var menu = new ContextMenuStrip())
                                {
                                    menu.Items.Add("Tool"); AnnotationMenuKeys.Configure(menu);
                                    menu.Show(host, new Point(10, 10)); Application.DoEvents();
                                    HostKey(host, Keys.Escape); HostKey(host, Keys.Escape, up: true);
                                    Check(layers == 0, "Menu Escape cancelled drawing");
                                    menu.Close();
                                }
                                host.Focus(); native = 0;
                                HostKey(host, Keys.Escape); HostKey(host, Keys.Escape, repeat: true); HostKey(host, Keys.Escape, up: true);
                                Check(layers == 1 && native == 0, "Escape never reached draft cancellation or consumed two layers");
                                HostKey(host, Keys.Escape); HostKey(host, Keys.Escape, up: true);
                                Check(layers == 2 && native == 0, "Second Escape could not exit drawing");
                                HostKey(host, Keys.Escape); HostKey(host, Keys.Escape, up: true);
                                Check(native == 1, "Idle Escape did not return to native dialog handling");
                            }
                            form.CancelButton = null;
                            form.Controls.Remove(host); form.Controls.Remove(cancel);
                        }
                        canvas.Focus();
                    });
                    using (var a = CanvasDrawingShortcut.Register(canvas, () => _eligibleA, () => _activatedA++))
                    using (var b = CanvasDrawingShortcut.Register(canvas, () => _eligibleB, () => _activatedB++))
                    {
                        Run("queued double D preempts forwarding and translated characters", () =>
                        {
                            // Both up messages precede the translated characters in the queue.
                            Down(canvas, Keys.D); Up(canvas, Keys.D); Down(canvas, Keys.D); Up(canvas, Keys.D);
                            Application.DoEvents();
                            Check(_activatedA == 1 && _activatedB == 0 && _forwarded == 0 && _characters == 0,
                                "DD activated twice, forwarded, or leaked WM_CHAR");
                        }, a);
                        Run("annotation Delete and Ctrl+A preempt real menu preprocessing", () =>
                        {
                            int native = 0, routed = 0;
                            using (var menu = new MenuStrip())
                            {
                                var del = new ToolStripMenuItem("Delete") { ShortcutKeys = Keys.Delete };
                                var all = new ToolStripMenuItem("All") { ShortcutKeys = Keys.Control | Keys.A };
                                del.Click += (s, args) => native++; all.Click += (s, args) => native++;
                                menu.Items.Add(del); menu.Items.Add(all); form.Controls.Add(menu); form.MainMenuStrip = menu;
                                a.Command = key => { if (key == Keys.Delete || key == (Keys.Control | Keys.A)) { routed++; return true; } return false; };
                                var state = new byte[256]; GetKeyboardState(state);
                                try
                                {
                                    Down(canvas, Keys.Delete); Down(canvas, Keys.Delete, true); Up(canvas, Keys.Delete); Application.DoEvents();
                                    var modified = (byte[])state.Clone(); modified[(int)Keys.ControlKey] = 0x80; SetKeyboardState(modified);
                                    Tap(canvas, Keys.A);
                                    Check(routed == 2 && native == 0, "Command reached GH menu or repeated");
                                    text.Focus(); Tap(text, Keys.Delete); Check(routed == 2, "Text editing intercepted"); canvas.Focus();
                                }
                                finally { SetKeyboardState(state); a.Command = null; form.MainMenuStrip = null; form.Controls.Remove(menu); }
                            }
                        }, a);
                        Run("inline editor bypasses hostile KeyPreview and menus while letters and editing stay native", () =>
                        {
                            int previews = 0, native = 0, commands = 0;
                            using (var editor = new TextEditorControl { Location = new Point(0, 40), Size = new Size(190, 80) })
                            using (var menu = new MenuStrip())
                            {
                                foreach (var shortcut in new[]{Keys.Delete,Keys.Control|Keys.A,Keys.Control|Keys.Z,
                                    Keys.Control|Keys.C,Keys.Control|Keys.V,Keys.Control|Keys.X,Keys.Control|Keys.T})
                                {
                                    var item = new ToolStripMenuItem("Native " + shortcut)
                                    {
                                        ShortcutKeys = shortcut
                                    };
                                    item.Click += (s, args) => native++;
                                    menu.Items.Add(item);
                                }
                                form.Controls.Add(menu); form.MainMenuStrip = menu; form.Controls.Add(editor); editor.BringToFront();
                                KeyEventHandler hostile = (s, args) => { if (editor.Focused) { previews++; args.Handled = true; } };
                                form.KeyDown += hostile;
                                a.Command = key => { commands++; return true; };
                                var state = new byte[256]; GetKeyboardState(state);
                                try
                                {
                                    editor.Focus(); Check(editor.Focused, "editor cannot focus");
                                    foreach (var key in new[] { Keys.T, Keys.D, Keys.D, Keys.L }) Tap(editor, key);
                                    Check(editor.Text == "tddl" && _activatedA == 0 && previews == 0 && native == 0 &&
                                         commands == 0, "editor letters reached parent/menu/commands: " + editor.Text);
                                    var modified = (byte[])state.Clone(); modified[(int)Keys.ControlKey] = 0x80; SetKeyboardState(modified);
                                    Tap(editor, Keys.A); Check(editor.SelectionLength == 4, "Ctrl+A not native");
                                    SetKeyboardState(state); Tap(editor, Keys.Delete); Check(editor.Text == "", "Delete not native");
                                    SetKeyboardState(modified); Tap(editor, Keys.Z); SetKeyboardState(state);
                                    Check(editor.Text == "tddl" && previews == 0 && native == 0, "local undo routed to parent");
                                    editor.SelectionStart = editor.TextLength;
                                    editor.SelectionLength = 0;
                                    Tap(editor, Keys.Back);
                                    Check(editor.Text == "tdd", "Backspace not native");
                                    Tap(editor, Keys.Enter); Tap(editor, Keys.T); Check(editor.Text.Replace("\r\n", "\n") == "tdd\nt", "Enter not newline");
                                }
                                finally
                                {
                                    SetKeyboardState(state);
                                    a.Command = null;
                                    form.KeyDown -= hostile;
                                    form.MainMenuStrip = null;
                                    form.Controls.Remove(menu);
                                    form.Controls.Remove(editor);
                                    canvas.Focus();
                                }
                            }
                        }, a);
                    }
                    Run("last owner removal restores form preview", () =>
                    {
                        Tap(canvas, Keys.D); Check(_forwarded == 1, "Hook remained after removal");
                    });
                }
                catch (Exception error) { _failures++; Console.WriteLine("FAIL: harness: " + error); }
                finally { form.Close(); }
            }));
            Application.Run(form);
        }
        return Finish();
    }
    private static int Finish()
    {
        Console.WriteLine(_failures + " failure(s)");
        return _failures == 0 ? 0 : 1;
    }
}
