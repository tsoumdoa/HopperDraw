using System;
using System.Windows.Forms;

namespace hopperdraw
{
    // One instance (and one UI timer) per controller. No document or owner stores fade timestamps.
    // The timer only runs when the picture will actually change: it is armed once for the moment a fade
    // starts (3.0 s), ticks at 40 ms only during the 0.5 s fade, and is stopped while a hint is held
    // (draft or text editor active) or nothing is visible.
    internal sealed class AnnotationHintClock : IDisposable
    {
        internal const double Steady = 3.0, End = 3.5;
        private const int FadeStep = 40;
        private readonly Timer _timer;
        private readonly Action _invalidate;
        private readonly Func<DateTime> _now;
        private object _owner;
        private int _tool=-1;
        private DateTime _hintAt,_noticeAt,_due;
        private string _notice;
        private bool _hold;
        internal bool IsDisposed { get; private set; }
        internal bool Running => !IsDisposed && _timer.Enabled;
        internal int Interval => _timer.Interval;
        internal AnnotationHintClock(Action invalidate,Func<DateTime> now=null)
        {
            _invalidate=invalidate; _now=now??(()=>DateTime.UtcNow);
            _timer=new Timer{Interval=FadeStep}; _timer.Tick+=Tick;
        }
        internal void Update(object owner,int tool,bool hold)
        {
            if(IsDisposed) return;
            if(!ReferenceEquals(owner,_owner) || tool!=_tool) { _hintAt=_now(); _notice=null; }
            if(_hold && !hold) _hintAt=_now();
            _owner=owner; _tool=tool; _hold=hold;
            Schedule();
        }
        internal void Notice(string text) { if(IsDisposed) return; _notice=text; _noticeAt=_now(); Schedule(); _invalidate(); }
        internal static float Fade(double seconds) => (float)Math.Max(0,Math.Min(1,(End-seconds)/(End-Steady)));
        private bool NoticeVisible => _notice!=null && Fade((_now()-_noticeAt).TotalSeconds)>0;
        internal float Alpha => _owner==null ? 0 : NoticeVisible ? Fade((_now()-_noticeAt).TotalSeconds) : _hold ? 1 : Fade((_now()-_hintAt).TotalSeconds);
        internal string Text(string hint) => NoticeVisible ? _notice : hint;
        // Milliseconds until the next visible change of one phase, or null when that phase is static.
        private static double? Wait(double seconds) => seconds < Steady ? (Steady-seconds)*1000 : seconds < End ? FadeStep : (double?)null;
        internal int? NextInterval()
        {
            if(IsDisposed || _owner==null) return null;
            var now=_now(); double? next=null;
            if(_notice!=null) next=Wait((now-_noticeAt).TotalSeconds);
            if(!_hold) { var hint=Wait((now-_hintAt).TotalSeconds); if(hint.HasValue && (!next.HasValue || hint<next)) next=hint; }
            return next.HasValue ? (int?)Math.Max(15,(int)Math.Ceiling(next.Value)) : null;
        }
        private void Schedule()
        {
            var next=NextInterval();
            if(!next.HasValue) { _timer.Stop(); return; }
            // Update() runs on every hover; re-arming an already-correct timer would postpone it forever.
            var due=_now().AddMilliseconds(next.Value);
            if(_timer.Enabled && Math.Abs((due-_due).TotalMilliseconds)<20) return;
            _due=due; _timer.Stop(); _timer.Interval=next.Value; _timer.Start();
        }
        internal void Tick(object sender,EventArgs e) { if(IsDisposed) return; _timer.Stop(); _invalidate(); Schedule(); }
        public void Dispose() { if(IsDisposed) return; IsDisposed=true; _timer.Stop(); _timer.Tick-=Tick; _timer.Dispose(); _owner=null; }
    }
}
