using System;
using System.Windows.Forms;

namespace hopperdraw
{
    // One instance (and one UI timer) per controller. No document or owner stores fade timestamps.
    internal sealed class AnnotationHintClock : IDisposable
    {
        private readonly Timer _timer;
        private readonly Action _invalidate;
        private readonly Func<DateTime> _now;
        private object _owner;
        private int _tool=-1;
        private DateTime _hintAt,_noticeAt;
        private string _notice;
        private bool _hold;
        internal bool IsDisposed { get; private set; }
        internal bool Running => !IsDisposed && _timer.Enabled;
        internal AnnotationHintClock(Action invalidate,Func<DateTime> now=null)
        {
            _invalidate=invalidate; _now=now??(()=>DateTime.UtcNow);
            _timer=new Timer{Interval=40}; _timer.Tick+=Tick;
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
        internal static float Fade(double seconds) => (float)Math.Max(0,Math.Min(1,(3.5-seconds)/.5));
        internal float Alpha => _owner==null ? 0 : _notice!=null && Fade((_now()-_noticeAt).TotalSeconds)>0 ? Fade((_now()-_noticeAt).TotalSeconds) : _hold ? 1 : Fade((_now()-_hintAt).TotalSeconds);
        internal string Text(string hint) => _notice!=null && Fade((_now()-_noticeAt).TotalSeconds)>0 ? _notice : hint;
        private void Schedule() { if(_owner!=null && Alpha>0) _timer.Start(); else _timer.Stop(); }
        internal void Tick(object sender,EventArgs e) { if(IsDisposed) return; Schedule(); _invalidate(); }
        public void Dispose() { if(IsDisposed) return; IsDisposed=true; _timer.Stop(); _timer.Tick-=Tick; _timer.Dispose(); _owner=null; }
    }
}
