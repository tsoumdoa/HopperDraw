using System;
using System.Windows.Forms;

namespace hopperdraw
{
    // One pending UI-thread action that runs after a delay unless it is cancelled or replaced first.
    internal sealed class DelayedAction : IDisposable
    {
        private readonly Timer _timer = new Timer();
        private Action _action;
        internal DelayedAction()
        {
            _timer.Tick += (s, e) =>
            {
                var action = _action; Cancel();
                try { action?.Invoke(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("HopperDraw delayed action skipped: " + ex); }
            };
        }
        internal bool Pending => _action != null;
        internal void Arm(int milliseconds, Action action)
        {
            Cancel();
            _action = action; _timer.Interval = Math.Max(1, milliseconds); _timer.Start();
        }
        internal void Cancel() { _timer.Stop(); _action = null; }
        public void Dispose() { Cancel(); _timer.Dispose(); }
    }
}
