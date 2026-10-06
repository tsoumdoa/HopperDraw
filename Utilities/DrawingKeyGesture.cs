using System.Windows.Forms;

namespace hopperdraw
{
    internal sealed class DrawingKeyGesture
    {
        private bool _dKeyDown;
        private int? _lastPressTick;

        // Observe every press, including modified keys and keys handled by another owner.
        internal bool KeyDown(Keys key, bool canActivate, int tick, int doubleClickTime)
        {
            if (key != Keys.D)
            {
                _lastPressTick = null;
                return false;
            }

            bool wasDown = _dKeyDown;
            _dKeyDown = true;
            if (!canActivate)
            {
                _lastPressTick = null;
                return false;
            }
            if (wasDown) return false;

            // Unsigned subtraction preserves elapsed time across TickCount wraparound.
            bool doublePress = _lastPressTick.HasValue &&
                unchecked((uint)(tick - _lastPressTick.Value)) <= (uint)doubleClickTime;
            _lastPressTick = doublePress ? (int?)null : tick;
            return doublePress;
        }

        internal void KeyUp(Keys key)
        {
            if (key == Keys.D) _dKeyDown = false;
        }

        internal void Reset(bool releaseKey = false)
        {
            _lastPressTick = null;
            if (releaseKey) _dKeyDown = false;
        }
    }
}
