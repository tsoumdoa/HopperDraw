using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace hopperdraw
{
    // One hook per canvas, shared by every HopperDraw component. The canvas HWND
    // receives the message before WinForms invokes the editor's KeyPreview handler.
    internal sealed class CanvasDrawingShortcut : NativeWindow, IMessageFilter
    {
        private const int KeyDownMessage = 0x100;
        private const int KeyUpMessage = 0x101;
        private const int CharacterMessage = 0x102;
        private const int SystemKeyDownMessage = 0x104;
        private const int SystemKeyUpMessage = 0x105;
        private const int SystemCharacterMessage = 0x106;
        private const int KillFocusMessage = 0x8;
        private static readonly Dictionary<Control, CanvasDrawingShortcut> Hooks = new Dictionary<Control, CanvasDrawingShortcut>();
        private static readonly int KeyObservedMessage = Environment.OSVersion.Platform == PlatformID.Win32NT
            ? RegisterWindowMessage("Hopper.Grasshopper.CanvasShortcut.KeyObserved.v1") : 0;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            internal int Size, Flags;
            internal IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            internal System.Drawing.Rectangle CaretRect;
        }
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        internal static bool MenuActive
        {
            get { var info = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) }; return AnnotationMenuKeys.Active || GetGUIThreadInfo(0, ref info) && (info.Flags & 0x1c) != 0; }
        }
        private readonly Control _canvas;
        private readonly List<Registration> _owners = new List<Registration>();
        private readonly DrawingKeyGesture _gesture = new DrawingKeyGesture();
        private Registration _gestureOwner;
        private bool _claimedPress;
        private readonly Dictionary<int, int> _ownedCharacters = new Dictionary<int, int>();

        internal sealed class Registration : IDisposable
        {
            private CanvasDrawingShortcut _hook;
            internal readonly Func<bool> CanActivate;
            internal readonly Action Activate;
            internal Action<int, MouseEventArgs, bool> Mouse;
            internal Func<Keys, bool> Command;
            internal Action Lost;
            internal Action<Keys> BeforeKey;


            internal Registration(CanvasDrawingShortcut hook, Func<bool> canActivate, Action activate)
            {
                _hook = hook;
                CanActivate = canActivate;
                Activate = activate;
            }

            internal void Reset() { _hook?.Reset(); }

            public void Dispose()
            {
                var hook = _hook;
                _hook = null;
                hook?.Remove(this);
            }
        }

        internal static Registration Register(Control canvas, Func<bool> canActivate, Action activate)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            if (!Hooks.TryGetValue(canvas, out var hook))
            {
                hook = new CanvasDrawingShortcut(canvas);
                Hooks.Add(canvas, hook);
            }
            var registration = new Registration(hook, canActivate, activate);
            hook._owners.Add(registration);
            return registration;
        }

        private CanvasDrawingShortcut(Control canvas)
        {
            _canvas = canvas;
            Application.AddMessageFilter(this);
            canvas.HandleCreated += HandleCreated;
            canvas.HandleDestroyed += HandleDestroyed;
            canvas.Disposed += CanvasDisposed;
            canvas.PreviewKeyDown += PreviewKeyDown;
            if (canvas.IsHandleCreated) AssignHandle(canvas.Handle);
        }

        private void HandleCreated(object sender, EventArgs args) { AssignHandle(_canvas.Handle); }
        private void HandleDestroyed(object sender, EventArgs args)
        {
            Reset(true);
            _commandsDown.Clear();
            foreach (var owner in _owners.ToArray()) owner.Lost?.Invoke();
            _ownedCharacters.Clear();
            if (Handle != IntPtr.Zero) ReleaseHandle();
        }
        private void CanvasDisposed(object sender, EventArgs args) { Detach(); }

        private void PreviewKeyDown(object sender, PreviewKeyDownEventArgs args)
        {
            // WinForms emits this before menu/dialog command preprocessing. Only
            // observe here: Rhino's native loop may skip this stage, so capture
            // still belongs to the HWND hook. Do not change IsInputKey.
            if (args.KeyCode != Keys.D || args.Modifiers != Keys.None) Reset();
            if (_canvas.IsHandleCreated)
                SendMessage(_canvas.Handle, KeyObservedMessage, new IntPtr((int)args.KeyCode), new IntPtr(KeyDownMessage));
        }

        private void Remove(Registration registration)
        {
            _owners.Remove(registration);
            if (ReferenceEquals(_gestureOwner, registration)) Reset();
            if (_owners.Count == 0) Detach();
        }

        private void Detach()
        {
            Application.RemoveMessageFilter(this);
            Hooks.Remove(_canvas);
            _canvas.HandleCreated -= HandleCreated;
            _canvas.HandleDestroyed -= HandleDestroyed;
            _canvas.Disposed -= CanvasDisposed;
            _canvas.PreviewKeyDown -= PreviewKeyDown;
            _owners.Clear();
            Reset(true);
            _ownedCharacters.Clear();
            if (Handle != IntPtr.Zero) ReleaseHandle();
        }

        private void Reset(bool releaseKey = false)
        {
            _gesture.Reset(releaseKey);
            _gestureOwner = null;
            if (releaseKey)
            {
                _claimedPress = false;
            }
        }

        private Registration InputOwner()
        {
            if (!_canvas.Focused || MenuActive || Control.ModifierKeys != Keys.None ||
                Control.MouseButtons != MouseButtons.None) return null;
            foreach (var owner in _owners)
                if (owner.CanActivate()) return owner;
            return null;
        }

        private readonly HashSet<Keys> _commandsDown = new HashSet<Keys>();
        private bool RouteCommand(Keys key, bool repeat = false)
        {
            if (!_canvas.Focused || MenuActive) return false;
            if (_commandsDown.Contains(key)) return true;
            if (repeat) return false;
            foreach (var owner in _owners.ToArray())
                if (owner.Command?.Invoke(key | Control.ModifierKeys) == true)
                {
                    // Notify every plugin hook before this layer consumes the key (same rule as WndProc).
                    Observe(key, KeyDownMessage);
                    Reset(); _commandsDown.Add(key); return true;
                }
            return false;
        }
        private bool ReleaseCommand(Keys key)
        {
            if (!_commandsDown.Remove(key)) return false;
            Observe(key, KeyUpMessage);
            return true;
        }
        private void Observe(Keys key, int message)
        {
            if (_canvas.IsHandleCreated)
                SendMessage(_canvas.Handle, KeyObservedMessage, new IntPtr((int)key), new IntPtr(message));
        }
        private void OwnCommandCharacter(Keys key, IntPtr flags)
        {
            if (!(key >= Keys.A && key <= Keys.Z) && key != Keys.Back && key != Keys.Enter && key != Keys.Escape && key != Keys.Space) return;
            int scan = (int)(flags.ToInt64() & 0xff0000);
            int count = Math.Max(1, (int)(flags.ToInt64() & 0xffff));
            _ownedCharacters.TryGetValue(scan, out int pending);
            _ownedCharacters[scan] = (int)Math.Min(int.MaxValue, (long)pending + count);
        }
        private bool ReleaseCharacter(Message message)
        {
            if (message.Msg != CharacterMessage && message.Msg != SystemCharacterMessage) return false;
            int scan = (int)(message.LParam.ToInt64() & 0xff0000);
            if (!_ownedCharacters.TryGetValue(scan, out int pending)) return false;
            int count = Math.Max(1, (int)(message.LParam.ToInt64() & 0xffff));
            if (pending <= count) _ownedCharacters.Remove(scan); else _ownedCharacters[scan] = pending - count;
            return true;
        }
        public bool PreFilterMessage(ref Message m)
        {
            if (m.HWnd != Handle) return false;
            if (m.Msg == KeyDownMessage || m.Msg == SystemKeyDownMessage) BeforeKey((Keys)m.WParam.ToInt32());
            if (ReleaseCharacter(m)) return true; // Before dialog/mnemonic preprocessing, too.
            if (m.Msg == KeyDownMessage && RouteCommand((Keys)m.WParam.ToInt32(), (m.LParam.ToInt64() & (1L << 30)) != 0))
            {
                OwnCommandCharacter((Keys)m.WParam.ToInt32(), m.LParam);
                TranslateMessage(ref m); // Filtered downs otherwise never get their WM_CHAR from the loop.
                return true;
            }
            if (m.Msg == KeyUpMessage && ReleaseCommand((Keys)m.WParam.ToInt32())) return true;
            return false;
        }
        private void BeforeKey(Keys key)
        { foreach (var owner in _owners.ToArray()) owner.BeforeKey?.Invoke(key | Control.ModifierKeys); }
        private void RouteMouse(int message, IntPtr location, bool after)
        {
            if (message < 0x200 || message > 0x215) return;
            int packed = unchecked((int)location.ToInt64());
            var button = message >= 0x204 && message <= 0x206 ? MouseButtons.Right :
                message >= 0x201 && message <= 0x203 ? MouseButtons.Left : Control.MouseButtons;
            var args = new MouseEventArgs(button, message == 0x203 ? 2 : 1,
                (short)(packed & 0xffff), (short)((packed >> 16) & 0xffff), 0);
            foreach (var owner in _owners.ToArray()) owner.Mouse?.Invoke(message, args, after);
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == KeyDownMessage || message.Msg == SystemKeyDownMessage) BeforeKey((Keys)message.WParam.ToInt32());
            RouteMouse(message.Msg, message.LParam, false);
            if (message.Msg == KillFocusMessage || message.Msg == 0x215 || message.Msg == 0x1f)
            {
                _commandsDown.Clear();
                foreach (var registration in _owners.ToArray()) registration.Lost?.Invoke();
            }
            if (message.Msg == KeyDownMessage && RouteCommand((Keys)message.WParam.ToInt32(), (message.LParam.ToInt64() & (1L << 30)) != 0)) { OwnCommandCharacter((Keys)message.WParam.ToInt32(), message.LParam); message.Result = IntPtr.Zero; return; }
            if (message.Msg == KeyUpMessage && ReleaseCommand((Keys)message.WParam.ToInt32())) { message.Result = IntPtr.Zero; return; }
            if (message.Msg == KeyObservedMessage)
            {
                var key = (Keys)message.WParam.ToInt32();
                if (key != Keys.D) Reset();
                base.WndProc(ref message);
                return;
            }
            if (message.Msg == KeyDownMessage || message.Msg == SystemKeyDownMessage)
            {
                var key = (Keys)message.WParam.ToInt32();
                // Notify every plugin hook before any layer consumes the key.
                // Otherwise an outer G hook could hide G from an inner D hook.
                SendMessage(message.HWnd, KeyObservedMessage, message.WParam, new IntPtr(message.Msg));
                var owner = message.Msg == KeyDownMessage && key == Keys.D ? InputOwner() : null;
                if (!ReferenceEquals(owner, _gestureOwner)) Reset();
                _gestureOwner = owner;
                bool repeat = (message.LParam.ToInt64() & (1L << 30)) != 0;
                bool activate = _gesture.KeyDown(key, owner != null && !repeat,
                    Environment.TickCount, SystemInformation.DoubleClickTime);
                if (key == Keys.D)
                {
                    // A repeat never starts a new gesture, including after focus loss.
                    // Continue swallowing a press already claimed even if eligibility
                    // changes before its release, so its repeats cannot leak to Rhino.
                    _claimedPress = owner != null || (repeat && _claimedPress);
                    if (_claimedPress)
                    {
                        int scan = (int)(message.LParam.ToInt64() & 0xff0000);
                        int count = Math.Max(1, (int)(message.LParam.ToInt64() & 0xffff));
                        _ownedCharacters.TryGetValue(scan, out int pending);
                        _ownedCharacters[scan] = (int)Math.Min(int.MaxValue, (long)pending + count);
                        message.Result = IntPtr.Zero;
                        if (activate) owner.Activate();
                        return;
                    }
                }
            }
            else if (message.Msg == KeyUpMessage || message.Msg == SystemKeyUpMessage)
            {
                var key = (Keys)message.WParam.ToInt32();
                SendMessage(message.HWnd, KeyObservedMessage, message.WParam, new IntPtr(message.Msg));
                // WinForms may consume a menu/dialog command during preprocessing,
                // before HWND dispatch. Its key-up still breaks the tap sequence.
                if (key != Keys.D) Reset();
                _gesture.KeyUp(key);
                if (key == Keys.D && _claimedPress)
                {
                    _claimedPress = false;
                    message.Result = IntPtr.Zero;
                    return;
                }
            }
            else if (ReleaseCharacter(message))
            {
                // TranslateMessage may enqueue WM_CHAR after the corresponding key-up.
                message.Result = IntPtr.Zero;
                return;
            }
            else if (message.Msg == KillFocusMessage)
                Reset(true);
            else if (message.Msg == 0x201 || message.Msg == 0x204 || message.Msg == 0x207 || message.Msg == 0x20b || message.Msg == 0x20a)
                Reset(); // Mouse presses or wheel movement separate gestures.

            base.WndProc(ref message);
            RouteMouse(message.Msg, message.LParam, true);
            if (message.Msg == KeyDownMessage || message.Msg == KeyUpMessage || message.Msg == SystemKeyDownMessage || message.Msg == SystemKeyUpMessage)
                foreach (var registration in _owners.ToArray()) registration.Mouse?.Invoke(message.Msg, null, true);
        }
    }
}
