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
                        Run("DDT immediately switches tool and consumes repeat, key-up and delayed characters", () =>
                        {
                            int mode = 0, changes = 0;
                            a.Command = key => _activatedA > 0 && ToolShortcut.Route(key, mode, false, m => { mode = m; changes++; }, () => { });
                            try
                            {
                                Down(canvas, Keys.D); Up(canvas, Keys.D); Down(canvas, Keys.D); Up(canvas, Keys.D);
                                Down(canvas, Keys.T); Down(canvas, Keys.T, true); Up(canvas, Keys.T); Application.DoEvents();
                                Check(_activatedA == 1 && mode == 5 && changes == 1 && _forwarded == 0 && _characters == 0, "DDT leaked or repeated");
                                Tap(canvas, Keys.T); Check(changes == 1 && _characters == 0, "reselection changed or leaked");
                                _activatedA = 0;
                                Tap(canvas, Keys.T);
                                Check(_forwarded == 1 && _characters == 1, "letter outside mode swallowed or stale character credit");
                            }
                            finally { a.Command = null; }
                        }, a);
                        Run("tool letters notify peers with independent scan credits", () =>
                        {
                            int mode = 0;
                            using (var peer = new ObservingPeer(canvas))
                            {
                                a.Command = key => ToolShortcut.Route(key, mode, false, m => mode = m, () => { });
                                try
                                {
                                    foreach (var key in new[] { Keys.T, Keys.L, Keys.P, Keys.R, Keys.C, Keys.F })
                                    { Down(canvas, key); Up(canvas, key); }
                                    Application.DoEvents();
                                    Check(mode == 4 && _forwarded == 0 && _characters == 0 && peer.Seen.Contains(Keys.T) && peer.Seen.Contains(Keys.F),
                                        "tool routing/peer failure");
                                    a.Command = null;
                                    Tap(canvas, Keys.L);
                                    Check(_characters == 1, "character credits outlived commands");
                                }
                                finally { a.Command = null; }
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
