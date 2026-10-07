using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace hopperdraw
{
    // One hook per canvas, shared by every HopperDraw component. The canvas HWND
    // receives the message before WinForms invokes the editor's KeyPreview handler.
    internal sealed class CanvasDrawingShortcut : NativeWindow
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
            if (!_canvas.Focused || Control.ModifierKeys != Keys.None ||
                Control.MouseButtons != MouseButtons.None) return null;
            foreach (var owner in _owners)
                if (owner.CanActivate()) return owner;
            return null;
        }

        protected override void WndProc(ref Message message)
        {
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
            else if ((message.Msg == CharacterMessage || message.Msg == SystemCharacterMessage) &&
                _ownedCharacters.TryGetValue((int)(message.LParam.ToInt64() & 0xff0000), out int pending))
            {
                // TranslateMessage may enqueue WM_CHAR after the corresponding key-up.
                int scan = (int)(message.LParam.ToInt64() & 0xff0000);
                int count = Math.Max(1, (int)(message.LParam.ToInt64() & 0xffff));
                if (pending <= count) _ownedCharacters.Remove(scan);
                else _ownedCharacters[scan] = pending - count;
                message.Result = IntPtr.Zero;
                return;
            }
            else if (message.Msg == KillFocusMessage)
                Reset(true);
            else if (message.Msg == 0x201 || message.Msg == 0x204 || message.Msg == 0x207 || message.Msg == 0x20b || message.Msg == 0x20a)
                Reset(); // Mouse presses or wheel movement separate gestures.

            base.WndProc(ref message);
        }
    }
}
