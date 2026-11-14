using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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

    // Records the shared key-observed broadcast without consuming anything.
    private sealed class ObservingPeer : NativeWindow, IDisposable
    {
        private readonly int _observed = RegisterWindowMessage("Hopper.Grasshopper.CanvasShortcut.KeyObserved.v1");
        internal readonly System.Collections.Generic.List<Keys> Seen = new System.Collections.Generic.List<Keys>();
        internal ObservingPeer(Control canvas) { AssignHandle(canvas.Handle); }
        public void Dispose() { ReleaseHandle(); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == _observed && message.LParam.ToInt32() == 0x100) Seen.Add((Keys)message.WParam.ToInt32());
            base.WndProc(ref message);
        }
    }

    private sealed class Canvas : Control
    {
        internal Canvas()
        {
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick | ControlStyles.Selectable, true);
            TabStop = true;
        }
        internal void Recreate() { RecreateHandle(); }
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
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--failure-exit-probe") >= 0)
        {
            Run("intentional failure exit probe", () => Check(false, "Intentional failure to verify process exit status."));
            return Finish();
        }
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
                    Run("plain menu mnemonic opens submenu and activates while canvas commands yield", () =>
                    {
                        int commands = 0, clicks = 0;
                        using (var registration = CanvasDrawingShortcut.Register(canvas, () => true, () => _activatedA++))
                        using (var menu = new ContextMenuStrip())
                        {
                            registration.Command = k => { commands++; return true; };
                            var tool = new ToolStripMenuItem("Tool") { Name = "Tool" };
                            var textTool = new ToolStripMenuItem("Text", null, (s, args) => clicks++) { Name = "Text" };
                            tool.DropDownItems.Add(textTool); menu.Items.Add(tool); AnnotationMenuKeys.Apply(menu.Items);
                            menu.Show(canvas, new Point(20, 20)); Application.DoEvents();
                            Check(CanvasDrawingShortcut.MenuActive, "Open menu not detected");
                            // Real HWND key/character messages through the WinForms loop, without Alt.
                            Tap(menu, Keys.T);
                            Check(tool.DropDown.Visible, "Plain T did not open Tool");
                            Tap(canvas, Keys.L); Check(commands == 0, "Canvas tool routing stole a menu letter");
                            Tap(tool.DropDown, Keys.T);
                            Check(clicks == 1 && !menu.Visible, "Plain T did not activate Text");
                            Application.DoEvents(); Check(!AnnotationMenuKeys.Active, "Closed dropdown retained menu routing");
                        }
                        canvas.Focus();
                    });
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
                        Run("mouse hook precedes WinForms down and observes native double-click before trailing up", () =>
                        {
                            var log = new System.Collections.Generic.List<string>();
                            MouseEventHandler down = (s, args) => log.Add("down:" + args.Clicks);
                            MouseEventHandler dbl = (s, args) => log.Add("double");
                            MouseEventHandler up = (s, args) => log.Add("up");
                            canvas.MouseDown += down; canvas.MouseDoubleClick += dbl; canvas.MouseUp += up;
                            a.Mouse = (msg, args, after) =>
                            {
                                if (!after && (msg == 0x201 || msg == 0x203))
                                {
                                    Check(args.X == 5 && args.Y == 25, "Signed coordinates lost");
                                    log.Add("hook:" + args.Clicks);
                                }
                            };
                            try
                            {
                                long location = (25L << 16) | 5;
                                foreach (int msg in new[] { 0x201, 0x202, 0x203, 0x202 }) PostMessage(canvas.Handle, msg, new IntPtr(msg == 0x202 ? 0 : 1), new IntPtr(location));
                                Application.DoEvents();
                                Check(string.Join(",", log) == "hook:1,down:1,up,hook:2,down:2,double,up", "Incorrect native sequence: " + string.Join(",", log));
                            }
                            finally { a.Mouse = null; canvas.MouseDown -= down; canvas.MouseDoubleClick -= dbl; canvas.MouseUp -= up; }
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
                        Run("consumed annotation commands still notify peer hooks; declined Ctrl+A reaches GH's menu", () =>
                        {
                            int native = 0;
                            using (var peer = new ObservingPeer(canvas))
                            using (var menu = new MenuStrip())
                            {
                                var all = new ToolStripMenuItem("All") { ShortcutKeys = Keys.Control | Keys.A };
                                all.Click += (s, args) => native++;
                                menu.Items.Add(all); form.Controls.Add(menu); form.MainMenuStrip = menu;
                                // Mirrors the controller: Delete is consumed, Ctrl+A extends selection then falls through.
                                a.Command = key => key == Keys.Delete;
                                var state = new byte[256]; GetKeyboardState(state);
                                try
                                {
                                    Tap(canvas, Keys.Delete); Application.DoEvents();
                                    Check(peer.Seen.Contains(Keys.Delete), "Consumed Delete was not broadcast to peer hooks");
                                    var modified = (byte[])state.Clone(); modified[(int)Keys.ControlKey] = 0x80; SetKeyboardState(modified);
                                    Tap(canvas, Keys.A); Application.DoEvents();
                                    Check(native == 1, "Declined Ctrl+A did not reach GH's Select All menu");
                                }
                                finally { SetKeyboardState(state); a.Command = null; form.MainMenuStrip = null; form.Controls.Remove(menu); }
                            }
                        }, a);
                        Run("physical Escape repeats cannot consume multiple layers", () =>
                        {
                            int layers = 0; a.Command = key => { if (key != Keys.Escape) return false; layers++; return true; };
                            try
                            {
                                Down(canvas, Keys.Escape); Down(canvas, Keys.Escape, true); Up(canvas, Keys.Escape); Application.DoEvents();
                                Check(layers == 1, "One Escape consumed multiple layers"); Tap(canvas, Keys.Escape); Check(layers == 2, "Second press lost");
                            }
                            finally { a.Command = null; }
                        }, a);
                        Run("DDN immediately switches tool and consumes repeat, key-up and delayed characters", () =>
                        {
                            int mode = 0, changes = 0;
                            a.Command = key => _activatedA > 0 && ToolShortcut.Route(key, mode, false, m => { mode = m; changes++; }, () => { });
                            try
                            {
                                Down(canvas, Keys.D); Up(canvas, Keys.D); Down(canvas, Keys.D); Up(canvas, Keys.D);
                                Down(canvas, Keys.N); Down(canvas, Keys.N, true); Up(canvas, Keys.N); Application.DoEvents();
                                Check(_activatedA == 1 && mode == 5 && changes == 1 && _forwarded == 0 && _characters == 0, "DDN leaked or repeated");
                                Tap(canvas, Keys.N); Check(changes == 1 && _characters == 0, "reselection changed or leaked");
                                _activatedA = 0;
                                Tap(canvas, Keys.N);
                                Check(_forwarded == 1 && _characters == 1, "letter outside mode swallowed or stale character credit");
                            }
                            finally { a.Command = null; }
                        }, a);
                        Run("tool keys notify peers with independent scan credits", () =>
                        {
                            int mode = 0;
                            using (var peer = new ObservingPeer(canvas))
                            {
                                a.Command = key => ToolShortcut.Route(key, mode, false, m => mode = m, () => { });
                                try
                                {
                                    foreach (var key in new[] { Keys.N, Keys.Z, Keys.X, Keys.C, Keys.V, Keys.B })
                                    { Down(canvas, key); Up(canvas, key); }
                                    Application.DoEvents();
                                    Check(mode == 4 && _forwarded == 0 && _characters == 0 && peer.Seen.Contains(Keys.N) && peer.Seen.Contains(Keys.B),
                                        "tool routing/peer failure");
                                    a.Command = null;
                                    Tap(canvas, Keys.Z);
                                    Check(_characters == 1, "character credits outlived commands");
                                }
                                finally { a.Command = null; }
                            }
                        }, a);
                        Run("style keys: rows by position on the canvas, a few Ctrl chords while typing; consumed keys leave no character and a consumed D is not D D", () =>
                        {
                            StyleKey hit;
                            Check(StyleKeys.Match(Keys.D4, false, out hit) && hit.Row == StyleKeyRow.Number && hit.Index == 4 && !hit.Fill, "canvas digit");
                            Check(StyleKeys.Match(Keys.Shift | Keys.D0, false, out hit) && hit.Row == StyleKeyRow.Number && hit.Index == 0 && hit.Fill, "canvas Shift+digit");
                            Check(StyleKeys.Match(Keys.Control | Keys.D4, true, out hit) && hit.Row == StyleKeyRow.Number && hit.Index == 4 && !hit.Fill, "typing Ctrl+digit");
                            Check(StyleKeys.Match(Keys.Control | Keys.Shift | Keys.D6, true, out hit) && hit.Row == StyleKeyRow.Number && hit.Index == 6 && hit.Fill, "typing Ctrl+Shift+digit");
                            Check(!StyleKeys.Match(Keys.D4, true, out hit) && !StyleKeys.Match(Keys.Control | Keys.D4, false, out hit), "a plain digit while typing or a Ctrl digit on the canvas matched");
                            Check(!StyleKeys.Match(Keys.D7, false, out hit) && !StyleKeys.Match(Keys.D9, false, out hit) && !StyleKeys.Match(Keys.Shift | Keys.D8, false, out hit) &&
                                !StyleKeys.Match(Keys.Control | Keys.D7, true, out hit), "a digit past the six colours matched");
                            // AltGr is Ctrl+Alt and types characters on many layouts; Alt alone belongs to menus.
                            Check(!StyleKeys.Match(Keys.Control | Keys.Alt | Keys.D2, true, out hit) && !StyleKeys.Match(Keys.Alt | Keys.D2, false, out hit) && !StyleKeys.Match(Keys.Alt | Keys.Q, false, out hit) &&
                                !StyleKeys.Match(Keys.Control | Keys.Alt | Keys.B, true, out hit), "AltGr or Alt matched a style key");
                            var upper = new[] { Keys.Q, Keys.W, Keys.E, Keys.R, Keys.T, Keys.Y, Keys.U };
                            var home = new[] { Keys.A, Keys.S, Keys.D, Keys.F, Keys.G, Keys.H, Keys.J, Keys.K, Keys.L, Keys.OemSemicolon, Keys.OemQuotes };
                            for (int i = 0; i < upper.Length; i++)
                                Check(StyleKeys.Match(upper[i], false, out hit) && hit.Row == StyleKeyRow.Upper && hit.Index == i && !hit.Fill &&
                                    StyleKeys.Match(Keys.Shift | upper[i], false, out hit) && hit.Row == StyleKeyRow.Upper && hit.Index == i && hit.Fill, "Q row position " + i);
                            for (int i = 0; i < home.Length; i++)
                                Check(StyleKeys.Match(home[i], false, out hit) && hit.Row == StyleKeyRow.Home && hit.Index == i && !hit.Fill &&
                                    StyleKeys.Match(Keys.Shift | home[i], false, out hit) && hit.Row == StyleKeyRow.Home && hit.Index == i && hit.Fill, "A row position " + i);
                            Check(StyleKeys.Span(StyleKeys.Upper, 5) == "Q–T" && StyleKeys.Caps(StyleKeys.Home, 3) == "A S D" && StyleKeys.Span(StyleKeys.Home, 11) == "A–'" &&
                                StyleKeys.Label("4", true, true) == "Ctrl+Shift+4" && StyleKeys.Label("Q", false, true) == "Shift+Q" && StyleKeys.Label("3", false) == "3", "key names");
                            // Never the tool row below, the rest of the keyboard, or with Ctrl (undo, copy, paste, GH's own).
                            Check(string.Concat(DrawingTool.All.Select(t => t.Key)) == "ZXCVBN", "tools are not the bottom row in button order");
                            foreach (var free in new[] { Keys.Z, Keys.X, Keys.C, Keys.V, Keys.B, Keys.N, Keys.M, Keys.I, Keys.O, Keys.P, Keys.OemOpenBrackets, Keys.Oemcomma, Keys.OemPeriod, Keys.OemQuestion,
                                Keys.Delete, Keys.Back, Keys.Enter, Keys.Escape, Keys.Space })
                                Check(!StyleKeys.Match(free, false, out hit) && !StyleKeys.Match(Keys.Shift | free, false, out hit), "style key shadows " + free);
                            foreach (var chord in upper.Concat(home).Concat(new[] { Keys.D3 }))
                                Check(!StyleKeys.Match(Keys.Control | chord, false, out hit), "style row shadows Ctrl+" + chord + " on the canvas");
                            // Typing: Ctrl+B, Ctrl+I, Ctrl+, and Ctrl+. besides the digits; never what the text box needs for editing.
                            Check(StyleKeys.Match(Keys.Control | Keys.B, true, out hit) && hit.Row == StyleKeyRow.Bold && StyleKeys.Match(Keys.Control | Keys.I, true, out hit) && hit.Row == StyleKeyRow.Italic &&
                                !StyleKeys.Match(Keys.Control | Keys.Shift | Keys.B, true, out hit), "bold or italic chord");
                            Check(StyleKeys.Match(Keys.Control | Keys.Oemcomma, true, out hit) && hit.Row == StyleKeyRow.Smaller &&
                                StyleKeys.Match(Keys.Control | Keys.Shift | Keys.OemPeriod, true, out hit) && hit.Row == StyleKeyRow.Larger, "size chords");
                            foreach (var edit in upper.Concat(home).Concat(new[] { Keys.C, Keys.X, Keys.V, Keys.Z, Keys.Back, Keys.Delete, Keys.Enter, Keys.Insert, Keys.Left, Keys.Home }))
                                Check(!StyleKeys.Match(Keys.Control | edit, true, out hit), "style chord shadows Ctrl+" + edit);
                            Check(CanvasDrawingShortcut.TypesCharacter(Keys.D3) && upper.Concat(home).All(CanvasDrawingShortcut.TypesCharacter) &&
                                !CanvasDrawingShortcut.TypesCharacter(Keys.Delete) && !CanvasDrawingShortcut.TypesCharacter(Keys.Left), "character ownership");
                            int colour = -1, fill = -1, thickness = -1, lineType = -1, presses = 0; bool takeD = true;
                            a.Command = data =>
                            {
                                if (!StyleKeys.Match(data, false, out var key) || !takeD && data == Keys.D) return false;
                                presses++;
                                if (key.Row == StyleKeyRow.Number) { if (key.Fill) fill = key.Index; else colour = key.Index; }
                                else if (key.Row == StyleKeyRow.Upper) thickness = key.Index; else lineType = key.Index;
                                return true;
                            };
                            var state = new byte[256]; GetKeyboardState(state);
                            try
                            {
                                Tap(canvas, Keys.D3); Tap(canvas, Keys.E); Tap(canvas, Keys.S);
                                Down(canvas, Keys.R); Down(canvas, Keys.R, true); Up(canvas, Keys.R); Application.DoEvents();
                                Check(colour == 3 && thickness == 3 && lineType == 1 && presses == 4 && _forwarded == 0 && _characters == 0,
                                    "style keys not routed once each or leaked: " + colour + "/" + thickness + "/" + lineType + "/" + presses + "/" + _forwarded + "/" + _characters);
                                // While drawing the controller takes D as the third line type: it is then not half of D D.
                                Tap(canvas, Keys.D); Tap(canvas, Keys.D);
                                Check(lineType == 2 && presses == 6 && _activatedA == 0 && _forwarded == 0 && _characters == 0, "a D consumed as a line type still counted towards D D or leaked");
                                // With a selection outside drawing mode the controller leaves D alone: D D still enters drawing mode.
                                takeD = false; Tap(canvas, Keys.D); Tap(canvas, Keys.D);
                                Check(_activatedA == 1 && presses == 6 && _forwarded == 0 && _characters == 0, "a declined D no longer starts D D");
                                takeD = true;
                                var shifted = (byte[])state.Clone(); shifted[(int)Keys.ShiftKey] = 0x80; SetKeyboardState(shifted);
                                Tap(canvas, Keys.D6); Tap(canvas, Keys.OemQuotes); SetKeyboardState(state);
                                Check(fill == 6 && lineType == 10 && _characters == 0, "Shift+digit / Shift+' not routed as fill keys or leaked a character: " + fill + "/" + lineType + "/" + _characters);
                                a.Command = null;
                                Tap(canvas, Keys.D3); Tap(canvas, Keys.E);
                                Check(_characters == 2, "character credits outlived the style keys: " + _characters);
                            }
                            finally { SetKeyboardState(state); a.Command = null; }
                        }, a);
                        Run("delayed action runs once after its delay, and not at all when cancelled or replaced", () =>
                        {
                            Action<int> pump = ms => { var watch = System.Diagnostics.Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < ms) { Application.DoEvents(); System.Threading.Thread.Sleep(5); } };
                            using (var delayed = new DelayedAction())
                            {
                                int first = 0, second = 0;
                                delayed.Arm(60, () => first++); Check(delayed.Pending && first == 0, "armed action ran at once or is not pending");
                                pump(15); Check(first == 0, "action ran before its delay");
                                pump(250); Check(first == 1 && !delayed.Pending, "action did not run once after its delay: " + first);
                                pump(150); Check(first == 1, "action repeated");
                                delayed.Arm(60, () => first++); delayed.Cancel(); pump(200); Check(first == 1 && !delayed.Pending, "cancelled action ran");
                                delayed.Arm(60, () => first++); delayed.Arm(60, () => second++); pump(250); Check(first == 1 && second == 1, "replaced action ran or its replacement did not");
                                delayed.Arm(40, () => { throw new InvalidOperationException("boom"); }); pump(200); Check(!delayed.Pending, "a failing action stayed pending");
                                delayed.Arm(5000, () => first++);
                            }
                            pump(50); Check(true, "disposed with a pending action");
                        });
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
                        Run("inline editor word delete stays local and rejects DEL characters", () =>
                        {
                            using (var editor = new TextEditorControl { Location = new Point(0, 40), Text = "one two three" })
                            {
                                form.Controls.Add(editor);
                                editor.Focus();
                                var state = new byte[256];
                                GetKeyboardState(state);
                                try
                                {
                                    var modified = (byte[])state.Clone();
                                    modified[(int)Keys.ControlKey] = 0x80;
                                    editor.SelectionStart = editor.TextLength;
                                    SetKeyboardState(modified);
                                    Tap(editor, Keys.Back);
                                    Check(editor.Text == "one two ", "Ctrl+Backspace did not delete word: " + editor.Text);
                                    SetKeyboardState(modified);
                                    Tap(editor, Keys.Z);
                                    Check(editor.Text == "one two three", "word deletion lost native undo");
                                    SetKeyboardState(modified);
                                    Tap(editor, Keys.Back);
                                    editor.SelectionStart = 4;
                                    SetKeyboardState(modified);
                                    Tap(editor, Keys.Delete);
                                    Check(editor.Text == "one  ", "Ctrl+Delete did not delete word: " + editor.Text);
                                    PostMessage(editor.Handle, 0x102, new IntPtr(127), IntPtr.Zero);
                                    Application.DoEvents();
                                    Check(!editor.Text.Contains(((char)127).ToString()), "DEL inserted into buffer");
                                }
                                finally { SetKeyboardState(state); form.Controls.Remove(editor); canvas.Focus(); }
                            }
                        }, a);
                        Run("inline editor clipboard and arrow commands stay local ahead of parent shortcuts", () =>
                        {
                            var clipboard = Clipboard.GetDataObject();
                            int native = 0;
                            using (var editor = new TextEditorControl { Location = new Point(0, 40), Text = "abc" })
                            using (var menu = new MenuStrip())
                            {
                                foreach (var key in new[] { Keys.C, Keys.X, Keys.V })
                                {
                                    var item = new ToolStripMenuItem(key.ToString())
                                    {
                                        ShortcutKeys = Keys.Control | key
                                    };
                                    item.Click += (s, args) => native++;
                                    menu.Items.Add(item);
                                }
                                form.Controls.Add(menu); form.MainMenuStrip = menu; form.Controls.Add(editor); editor.Focus();
                                var state = new byte[256]; GetKeyboardState(state);
                                try
                                {
                                    var modified = (byte[])state.Clone(); modified[(int)Keys.ControlKey] = 0x80; SetKeyboardState(modified);
                                    Tap(editor, Keys.A); SetKeyboardState(modified); Tap(editor, Keys.C);
                                    Check(Clipboard.ContainsText() && Clipboard.GetText() == "abc", "native copy did not run");
                                    SetKeyboardState(modified); Tap(editor, Keys.X);
                                    Check(editor.Text == "", "native cut did not run: " + editor.Text +
                                        " selected=" + editor.SelectionLength + " modifiers=" + Control.ModifierKeys + " menus=" + native);
                                    SetKeyboardState(modified);
                                    Tap(editor, Keys.V);
                                    Check(editor.Text == "abc" && native == 0, "paste/clipboard hit parent menu");
                                    SetKeyboardState(state); editor.SelectionStart = 3; editor.SelectionLength = 0; Tap(editor, Keys.Left);
                                    Check(editor.SelectionStart == 2, "arrow did not remain local");
                                }
                                finally
                                {
                                    SetKeyboardState(state);
                                    form.MainMenuStrip = null;
                                    form.Controls.Remove(menu);
                                    form.Controls.Remove(editor);
                                    canvas.Focus();
                                    if (clipboard != null) Clipboard.SetDataObject(clipboard, true); else Clipboard.Clear();
                                }
                            }
                        }, a);
                        Run("inline editor Escape and Ctrl+Enter commit once without trailing newline", () =>
                        {
                            foreach (var key in new[] { Keys.Escape, Keys.Enter })
                                using (var editor = new TextEditorControl { Location = new Point(0, 40), Text = "buffer" })
                                {
                                    int commits = 0; editor.Commit = () => commits++; form.Controls.Add(editor); editor.BringToFront(); editor.Focus();
                                    var state = new byte[256]; GetKeyboardState(state);
                                    try
                                    {
                                        if (key == Keys.Enter)
                                        {
                                            var modified = (byte[])state.Clone();
                                            modified[(int)Keys.ControlKey] = 0x80;
                                            SetKeyboardState(modified);
                                        }
                                        Down(editor, key); Down(editor, key, true); Up(editor, key);
                                        // A host may have translated before our filter; consume that delayed char too.
                                        long flags = 1 | ((long)MapVirtualKey((uint)key, 0) << 16);
                                        PostMessage(editor.Handle, 0x102, new IntPtr(key == Keys.Enter ? 13 : 27), new IntPtr(flags));
                                        Application.DoEvents();
                                        Check(commits == 1 && editor.Text == "buffer", "commit repeated or inserted character");
                                    }
                                    finally { SetKeyboardState(state); form.Controls.Remove(editor); canvas.Focus(); }
                                }
                        }, a);
                        Run("inline editor format keys reach the host without typing; Escape dismisses before it commits", () =>
                        {
                            using (var editor = new TextEditorControl { Location = new Point(0, 40), Text = "ab" })
                            {
                                int commits = 0, dismissed = 0; bool flyout = true; string formats = "";
                                editor.Commit = () => commits++;
                                editor.Dismiss = () => { if (!flyout) return false; flyout = false; dismissed++; return true; };
                                editor.Format = key => { formats += key + ";"; return true; };
                                form.Controls.Add(editor); editor.BringToFront(); editor.Focus(); editor.SelectionStart = 2;
                                var state = new byte[256]; GetKeyboardState(state);
                                try
                                {
                                    var control = (byte[])state.Clone(); control[(int)Keys.ControlKey] = 0x80;
                                    var shifted = (byte[])control.Clone(); shifted[(int)Keys.ShiftKey] = 0x80;
                                    SetKeyboardState(control); Tap(editor, Keys.B);
                                    SetKeyboardState(control); Tap(editor, Keys.I);
                                    SetKeyboardState(shifted); Tap(editor, Keys.OemPeriod);
                                    SetKeyboardState(shifted); Tap(editor, Keys.Oemcomma);
                                    string expected = string.Concat(new[] { Keys.Control | Keys.B, Keys.Control | Keys.I,
                                        Keys.Control | Keys.Shift | Keys.OemPeriod, Keys.Control | Keys.Shift | Keys.Oemcomma }.Select(k => k + ";"));
                                    Check(formats == expected && editor.Text == "ab", "format keys not routed or typed text: " + formats + " / " + editor.Text);
                                    // A host that dispatches without the filter still translates: Ctrl+I arrives with a tab character.
                                    long flags = 1 | ((long)MapVirtualKey((uint)Keys.I, 0) << 16);
                                    SetKeyboardState(control);
                                    SendMessage(editor.Handle, 0x100, new IntPtr((int)Keys.I), new IntPtr(flags));
                                    SendMessage(editor.Handle, 0x102, new IntPtr(9), new IntPtr(flags));
                                    SendMessage(editor.Handle, 0x101, new IntPtr((int)Keys.I), new IntPtr(flags | (3L << 30)));
                                    SetKeyboardState(state);
                                    Check(formats == expected + (Keys.Control | Keys.I) + ";" && editor.Text == "ab", "unfiltered Ctrl+I typed a tab: " + editor.Text.Length);
                                    Tap(editor, Keys.I);
                                    Check(editor.Text == "abi", "format key swallowed a later letter: " + editor.Text);
                                    // Without a host the keys keep their old meaning (no character for Ctrl+B).
                                    editor.Format = null; SetKeyboardState(control); Tap(editor, Keys.B); SetKeyboardState(state);
                                    Check(editor.Text == "abi", "unhandled Ctrl+B typed a control character");
                                    Down(editor, Keys.Escape); Down(editor, Keys.Escape, true); Up(editor, Keys.Escape); Application.DoEvents();
                                    Check(dismissed == 1 && commits == 0, "Escape committed instead of dismissing: " + dismissed + "/" + commits);
                                    Tap(editor, Keys.Escape);
                                    Check(dismissed == 1 && commits == 1 && editor.Text == "abi", "second Escape did not commit once");
                                }
                                finally { SetKeyboardState(state); form.Controls.Remove(editor); canvas.Focus(); }
                            }
                        }, a);
                        Run("inline editor offers every Ctrl style chord to the host; Ctrl+I never types a tab and AltGr is left alone", () =>
                        {
                            using (var editor = new TextEditorControl { Location = new Point(0, 40), Text = "ab" })
                            {
                                var seen = new List<Keys>(); editor.Format = key => { seen.Add(key); return true; };
                                form.Controls.Add(editor); editor.BringToFront(); editor.Focus(); editor.SelectionStart = 2;
                                var state = new byte[256]; GetKeyboardState(state);
                                try
                                {
                                    var control = (byte[])state.Clone(); control[(int)Keys.ControlKey] = 0x80;
                                    var shifted = (byte[])control.Clone(); shifted[(int)Keys.ShiftKey] = 0x80;
                                    var chords = new[] { Keys.D3, Keys.B, Keys.I, Keys.Oemcomma, Keys.OemPeriod };
                                    foreach (var key in chords) { SetKeyboardState(control); Tap(editor, key); }
                                    SetKeyboardState(shifted); Tap(editor, Keys.D0);
                                    SetKeyboardState(state);
                                    var expected = chords.Select(k => Keys.Control | k).Concat(new[] { Keys.Control | Keys.Shift | Keys.D0 }).ToArray();
                                    Check(seen.SequenceEqual(expected) && editor.Text == "ab", "style chords not offered once each or typed text: " + string.Join(",", seen) + " / " + editor.Text);
                                    SetKeyboardState(control);
                                    Down(editor, Keys.OemPeriod); Down(editor, Keys.OemPeriod, true); Down(editor, Keys.OemPeriod, true); Up(editor, Keys.OemPeriod);
                                    Application.DoEvents(); SetKeyboardState(state);
                                    Check(seen.Count == expected.Length + 1 && editor.Text == "ab", "a held chord repeated or typed: " + seen.Count + " / " + editor.Text);
                                    seen.RemoveAt(seen.Count - 1);
                                    // A host that dispatches without the filter still translates: Ctrl+I arrives with a tab.
                                    long flags = 1 | ((long)MapVirtualKey((uint)Keys.I, 0) << 16);
                                    SetKeyboardState(control);
                                    SendMessage(editor.Handle, 0x100, new IntPtr((int)Keys.I), new IntPtr(flags));
                                    SendMessage(editor.Handle, 0x102, new IntPtr(9), new IntPtr(flags));
                                    SendMessage(editor.Handle, 0x101, new IntPtr((int)Keys.I), new IntPtr(flags | (3L << 30)));
                                    SetKeyboardState(state);
                                    Check(seen.Count == expected.Length + 1 && editor.Text == "ab", "unfiltered Ctrl+I typed a tab: " + editor.Text);
                                    Tap(editor, Keys.I);
                                    Check(editor.Text == "abi", "style chord swallowed a later letter: " + editor.Text);
                                    Tap(editor, Keys.Back);
                                    Check(editor.Text == "ab", "Backspace no longer deletes: " + editor.Text);
                                    int offered = seen.Count;
                                    SetKeyboardState(control); Tap(editor, Keys.A); SetKeyboardState(state);
                                    Check(editor.SelectionLength == 2, "Ctrl+A no longer selects all");
                                    editor.Select(editor.TextLength, 0);
                                    // The canvas rows are plain text here, and are no chords either.
                                    foreach (var key in new[] { Keys.Q, Keys.S, Keys.D7 }) { SetKeyboardState(control); Tap(editor, key); }
                                    var altGr = (byte[])control.Clone(); altGr[(int)Keys.Menu] = 0x80;
                                    SetKeyboardState(altGr); Tap(editor, Keys.D2); SetKeyboardState(state);
                                    Check(seen.Count == offered && editor.Text.StartsWith("ab"), "an editing key, a key row with Ctrl or AltGr was offered as a style chord: " + editor.Text);
                                    string before = editor.Text;
                                    foreach (var key in new[] { Keys.Q, Keys.A, Keys.D, Keys.Z, Keys.D4 }) Tap(editor, key);
                                    Check(seen.Count == offered && editor.Text == before + "qadz4", "the canvas key rows were not typed as text: " + editor.Text);
                                }
                                finally { SetKeyboardState(state); form.Controls.Remove(editor); canvas.Focus(); }
                            }
                        }, a);
                        Run("inline editor is borderless and reports its own content size and scroll", () =>
                        {
                            using (var editor = new TextEditorControl { Location = new Point(0, 40), Size = new Size(60, 20), WordWrap = false })
                            {
                                int updates = 0; editor.Updating = () => updates++;
                                form.Controls.Add(editor); editor.BringToFront(); editor.Focus();
                                try
                                {
                                    Check(editor.BorderStyle == BorderStyle.None && editor.ClientSize == editor.Size, "editor still has a border");
                                    int margins = SendMessage(editor.Handle, 0xD4, IntPtr.Zero, IntPtr.Zero).ToInt32();
                                    Check(margins == 0, "editor keeps side margins: " + margins);
                                    int single = editor.ContentSize.Height;
                                    foreach (char c in "a much longer line than the box\r\rthird") PostMessage(editor.Handle, 0x102, new IntPtr(c), new IntPtr(1));
                                    Application.DoEvents();
                                    var content = editor.ContentSize;
                                    Check(updates > 0, "no pre-display update notification");
                                    Check(content.Height == 3 * single && content.Width > 60, "content size wrong: " + content + " line " + single);
                                    Check((short)(SendMessage(editor.Handle, 0xD6, IntPtr.Zero, IntPtr.Zero).ToInt64() & 0xffff) < 0 ||
                                        SendMessage(editor.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32() > 0, "small box did not scroll (test premise)");
                                    editor.Size = new Size(content.Width + 8, content.Height); editor.ResetScroll();
                                    long first = SendMessage(editor.Handle, 0xD6, IntPtr.Zero, IntPtr.Zero).ToInt64();
                                    Check((short)(first & 0xffff) >= 0 && (short)((first >> 16) & 0xffff) >= 0 &&
                                        SendMessage(editor.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32() == 0, "grown box stayed scrolled");
                                    editor.Font = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel);
                                    Check(SendMessage(editor.Handle, 0xD4, IntPtr.Zero, IntPtr.Zero).ToInt32() == 0, "font change restored side margins");
                                    Check(editor.ContentSize.Height > content.Height, "content size ignores the font");
                                }
                                finally { var font = editor.Font; form.Controls.Remove(editor); canvas.Focus(); editor.Dispose(); font.Dispose(); }
                            }
                        }, a);
                        Run("right-drag transport preserves the threshold-crossing move for pan handoff", () =>
                        {
                            bool context = false, handed = false; int delivered = 0;
                            a.Mouse = (msg, args, after) =>
                            {
                                if (after) return;
                                if (msg == 0x204) context = true;
                                if (msg == 0x200 && context) { handed = true; delivered++; }
                                if (msg == 0x205) context = false;
                            };
                            try
                            {
                                foreach (int msg in new[] { 0x204, 0x200, 0x205 }) PostMessage(canvas.Handle, msg, new IntPtr(2), new IntPtr(msg == 0x200 ? 100 : 0));
                                Application.DoEvents(); Check(handed && delivered == 1 && !context, "Handoff move/up lost");
                            }
                            finally { a.Mouse = null; }
                        }, a);
                        Run("actual capture loss reaches the input cancellation boundary", () =>
                        {
                            int lost = 0; a.Lost = () => lost++;
                            try { canvas.Capture = true; canvas.Capture = false; Application.DoEvents(); Check(lost > 0, "Capture loss not observed"); }
                            finally { a.Lost = null; }
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
        return Finish();
    }
    private static int Finish()
    {
        Console.WriteLine(_failures + " failure(s)");
        return _failures == 0 ? 0 : 1;
    }
}
