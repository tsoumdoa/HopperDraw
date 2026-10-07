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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    // Emulates another plugin that consumes G before an inner D hook can see it.
    private sealed class PeerShortcut : NativeWindow, IDisposable
    {
        private readonly int _observed = RegisterWindowMessage("Hopper.Grasshopper.CanvasShortcut.KeyObserved.v1");
        private int _characters;
        internal PeerShortcut(Control canvas) { AssignHandle(canvas.Handle); }
        public void Dispose() { ReleaseHandle(); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x100 || message.Msg == 0x104)
            {
                SendMessage(message.HWnd, _observed, message.WParam, new IntPtr(message.Msg));
                if ((Keys)(int)message.WParam == Keys.G)
                {
                    _characters++; message.Result = IntPtr.Zero; return;
                }
            }
            else if (message.Msg == 0x101 && (Keys)(int)message.WParam == Keys.G)
            { message.Result = IntPtr.Zero; return; }
            else if (message.Msg == 0x102 && _characters > 0 &&
                ((message.LParam.ToInt64() >> 16) & 0xff) == MapVirtualKey((uint)Keys.G, 0))
            { _characters--; message.Result = IntPtr.Zero; return; }
            base.WndProc(ref message);
        }
    }

    private sealed class Canvas : Control
    {
        internal Canvas() { SetStyle(ControlStyles.Selectable, true); TabStop = true; }
        internal void Recreate() { RecreateHandle(); }
        protected override bool IsInputKey(Keys keyData) { return true; }
    }

    private sealed class PointerReceiver : NativeWindow, IDisposable
    {
        internal IntPtr Received;
        internal PointerReceiver(Control canvas) { AssignHandle(canvas.Handle); }
        public void Dispose() { ReleaseHandle(); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x8001)
            {
                Received = message.WParam;
                message.Result = new IntPtr(87);
                return;
            }
            base.WndProc(ref message);
        }
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
                    Run("baseline editor KeyPreview forwards D before canvas", () =>
                    {
                        Tap(canvas, Keys.D);
                        Check(_forwarded == 1, "Baseline did not exercise form preview");
                    });
                    Run("non-key messages preserve full pointer width through hook", () =>
                    {
                        using (var receiver = new PointerReceiver(canvas))
                        using (CanvasDrawingShortcut.Register(canvas, () => true, () => { }))
                        {
                            var pointer = IntPtr.Size == 8 ? new IntPtr(0x100000000L) : new IntPtr(0x12345678);
                            var result = SendMessage(canvas.Handle, 0x8001, pointer, IntPtr.Zero);
                            Check(receiver.Received == pointer && result == new IntPtr(87), "Pointer-sized message was truncated or intercepted");
                        }
                    });
                    foreach (bool peerOutside in new[] { false, true })
                        Run("foreign consumed G resets D in hook order peerOutside=" + peerOutside, () =>
                        {
                            PeerShortcut peer = null;
                            CanvasDrawingShortcut.Registration owner = null;
                            try
                            {
                                if (!peerOutside) peer = new PeerShortcut(canvas);
                                owner = CanvasDrawingShortcut.Register(canvas, () => true, () => _activatedA++);
                                if (peerOutside) peer = new PeerShortcut(canvas);
                                Down(canvas, Keys.D); Up(canvas, Keys.D); Down(canvas, Keys.G); Up(canvas, Keys.G);
                                Down(canvas, Keys.D); Up(canvas, Keys.D); Application.DoEvents();
                                Check(_activatedA == 0 && _forwarded == 0 && _characters == 0,
                                    "Outer plugin hid G, or deferred characters leaked");
                                Tap(canvas, Keys.D); Check(_activatedA == 1, "Fresh DD did not activate");
                            }
                            finally { owner?.Dispose(); peer?.Dispose(); }
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
                        Run("hold and native repeat cannot activate", () =>
                        {
                            Down(canvas, Keys.D); Down(canvas, Keys.D, true); Down(canvas, Keys.D, true); Up(canvas, Keys.D);
                            Application.DoEvents();
                            Check(_activatedA == 0 && _forwarded == 0 && _characters == 0, "Held key activated or leaked");
                        }, a);
                        Run("alternate character values and scan changes retain independent suppression credits", () =>
                        {
                            // Native sends isolate the accounting for layouts whose
                            // virtual D produces a non-Latin character or another scan.
                            // The queue tests above separately prove TranslateMessage.
                            SendMessage(canvas.Handle, 0x100, new IntPtr((int)Keys.D), new IntPtr(1 | (0x20 << 16)));
                            SendMessage(canvas.Handle, 0x101, new IntPtr((int)Keys.D), new IntPtr(1 | (0x20 << 16)));
                            SendMessage(canvas.Handle, 0x100, new IntPtr((int)Keys.D), new IntPtr(1 | (0x21 << 16)));
                            SendMessage(canvas.Handle, 0x101, new IntPtr((int)Keys.D), new IntPtr(1 | (0x21 << 16)));
                            SendMessage(canvas.Handle, 0x102, new IntPtr(0x432), new IntPtr(1 | (0x20 << 16)));
                            SendMessage(canvas.Handle, 0x102, new IntPtr(0x43f), new IntPtr(1 | (0x21 << 16)));
                            Check(_activatedA == 1 && _characters == 0 && _forwarded == 0,
                                "Character suppression relied on literal D or only the last scan");
                        }, a);
                        Run("forwarded foreign keys break D gesture and retain deferred character suppression", () =>
                        {
                            Down(canvas, Keys.D); Up(canvas, Keys.D); Down(canvas, Keys.H); Up(canvas, Keys.H);
                            Down(canvas, Keys.D); Up(canvas, Keys.D); Application.DoEvents();
                            Check(_activatedA == 0 && _forwarded == 1 && _characters == 1, "D-H-D combined or leaked D characters");
                        }, a);
                        Run("disabled owners release D to the editor", () =>
                        {
                            _eligibleA = false; Tap(canvas, Keys.D); Tap(canvas, Keys.D);
                            Check(_activatedA == 0 && _forwarded == 2, "Disabled component retained keyboard interception");
                            _eligibleA = true; Tap(canvas, Keys.D);
                            Check(_activatedA == 0, "Disabled press joined enabled gesture");
                        }, a);
                        Run("modified D is preserved and modifier release cannot count a held D", () =>
                        {
                            var state = new byte[256];
                            Check(GetKeyboardState(state), "Could not save keyboard state");
                            var modified = (byte[])state.Clone(); modified[(int)Keys.ControlKey] = 0x80;
                            try
                            {
                                Check(SetKeyboardState(modified), "Could not set modifier state");
                                Down(canvas, Keys.D); Application.DoEvents();
                                Check(_forwarded == 1 && _activatedA == 0, "Ctrl+D was intercepted");
                            }
                            finally { SetKeyboardState(state); }
                            Down(canvas, Keys.D, true); Up(canvas, Keys.D); Application.DoEvents();
                            Tap(canvas, Keys.D); Check(_activatedA == 0, "Modified held D joined plain gesture");
                            Tap(canvas, Keys.D); Check(_activatedA == 1, "Two fresh unmodified presses failed");
                        }, a);
                        Run("native menu preprocessing retains assigned Ctrl+D command", () =>
                        {
                            int invoked = 0;
                            using (var menu = new MenuStrip())
                            using (var command = new ToolStripMenuItem("Command") { ShortcutKeys = Keys.Control | Keys.D })
                            {
                                command.Click += (s, args) => invoked++;
                                menu.Items.Add(command); form.Controls.Add(menu); form.MainMenuStrip = menu;
                                var state = new byte[256]; GetKeyboardState(state);
                                var modified = (byte[])state.Clone(); modified[(int)Keys.ControlKey] = 0x80;
                                try { SetKeyboardState(modified); Tap(canvas, Keys.D); }
                                finally { SetKeyboardState(state); }
                                Check(invoked == 1 && _activatedA == 0 && _forwarded == 0, "Ctrl+D menu shortcut was stolen");
                                form.MainMenuStrip = null; form.Controls.Remove(menu);
                            }
                        }, a);
                        Run("native menu function key breaks pending double D", () =>
                        {
                            int invoked = 0;
                            using (var menu = new MenuStrip())
                            using (var command = new ToolStripMenuItem("Command") { ShortcutKeys = Keys.F5 })
                            {
                                command.Click += (s, args) => invoked++;
                                menu.Items.Add(command); form.Controls.Add(menu); form.MainMenuStrip = menu;
                                Tap(canvas, Keys.D); Tap(canvas, Keys.F5); Tap(canvas, Keys.D);
                                Check(invoked == 1 && _activatedA == 0, "Native-preprocessed foreign key did not interrupt DD");
                                form.MainMenuStrip = null; form.Controls.Remove(menu);
                            }
                        }, a);
                        Run("held native menu function key interrupts before its release", () =>
                        {
                            int invoked = 0;
                            using (var menu = new MenuStrip())
                            using (var command = new ToolStripMenuItem("Command") { ShortcutKeys = Keys.F5 })
                            {
                                command.Click += (s, args) => invoked++;
                                menu.Items.Add(command); form.Controls.Add(menu); form.MainMenuStrip = menu;
                                Tap(canvas, Keys.D); Down(canvas, Keys.F5); Application.DoEvents();
                                Tap(canvas, Keys.D);
                                Check(invoked == 1 && _activatedA == 0, "Preprocessed key held across next D retained tap");
                                Up(canvas, Keys.F5); Application.DoEvents();
                                form.MainMenuStrip = null; form.Controls.Remove(menu);
                            }
                        }, a);
                        Run("switching selected owners cannot combine taps", () =>
                        {
                            Tap(canvas, Keys.D); _eligibleA = false; _eligibleB = true; Tap(canvas, Keys.D);
                            Check(_activatedA == 0 && _activatedB == 0, "Tap crossed owner selection");
                            Tap(canvas, Keys.D); Check(_activatedB == 1, "New owner failed to activate");
                        }, a);
                        Run("text editing bypasses canvas and focus switches reset taps", () =>
                        {
                            Tap(canvas, Keys.D); text.Focus(); Tap(text, Keys.D);
                            Check(text.Text == "d" && _forwarded == 0, "Typing in text editor was intercepted");
                            canvas.Focus(); Tap(canvas, Keys.D); Check(_activatedA == 0, "Tap crossed focus change");
                        }, a);
                        Run("native held-key repeat after focus return does not become a first tap", () =>
                        {
                            Down(canvas, Keys.D); Application.DoEvents(); text.Focus(); canvas.Focus();
                            Down(canvas, Keys.D, true); Up(canvas, Keys.D); Application.DoEvents(); Tap(canvas, Keys.D);
                            Check(_activatedA == 0, "Focus-return repeat counted as a first tap");
                        }, a);
                        Run("mouse input interrupts pending activation", () =>
                        {
                            Tap(canvas, Keys.D);
                            SendMessage(canvas.Handle, 0x201, IntPtr.Zero, IntPtr.Zero);
                            SendMessage(canvas.Handle, 0x202, IntPtr.Zero, IntPtr.Zero);
                            Tap(canvas, Keys.D); Check(_activatedA == 0, "Mouse-separated taps activated");
                        }, a);
                        Run("explicit document/reset and handle recreation drop pending tap", () =>
                        {
                            Tap(canvas, Keys.D); a.Reset(); Tap(canvas, Keys.D);
                            Check(_activatedA == 0, "Explicit reset retained tap");
                            canvas.Recreate(); canvas.Focus(); Tap(canvas, Keys.D);
                            Check(_activatedA == 0 && _forwarded == 0, "Recreated HWND lost hook or retained tap");
                            Tap(canvas, Keys.D); Check(_activatedA == 1, "Hook did not recover after recreation");
                        }, a);
                        Run("disposing one owner retains remaining owner's hook", () =>
                        {
                            a.Dispose(); _eligibleA = false; _eligibleB = true;
                            Tap(canvas, Keys.D); Tap(canvas, Keys.D);
                            Check(_activatedB == 1 && _forwarded == 0, "Owner removal detached shared hook");
                        }, b);
                    }
                    Run("last owner removal restores form preview", () =>
                    {
                        Tap(canvas, Keys.D); Check(_forwarded == 1, "Hook remained after removal");
                    });
                    Run("canvas disposal releases native handle and registrations safely", () =>
                    {
                        using (var disposable = new Canvas())
                        using (CanvasDrawingShortcut.Register(disposable, () => true, () => { }))
                        {
                            var handle = disposable.Handle;
                            disposable.Dispose();
                        }
                    });
                }
                catch (Exception error) { _failures++; Console.WriteLine("FAIL: harness: " + error); }
                finally { form.Close(); }
            }));
            Application.Run(form);
        }
        Console.WriteLine(_failures + " failure(s)");
        return _failures == 0 ? 0 : 1;
    }
}
