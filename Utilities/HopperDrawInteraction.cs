using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Canvas.Interaction;
using Rhino.Geometry;

namespace hopperdraw
{
    internal sealed class HopperDrawInteraction : GH_AbstractInteraction
    {
        private readonly CanvasAnnotationController _controller;
        private readonly HopperDraw _owner, _drawing;
        private readonly int _shape, _handle;
        private readonly GH_CanvasMouseEvent _press;
        private readonly bool _context;
        private AnnotationMove _move;
        private bool _done, _moving, _autoPan, _pressedHandled;
        internal HopperDrawInteraction(CanvasAnnotationController controller, GH_Canvas canvas, GH_CanvasMouseEvent e,
            HopperDraw owner, int shape, int handle, HopperDraw drawing) : base(canvas,e,false)
        {
            _controller=controller; _owner=owner; _shape=shape; _handle=handle; _drawing=drawing; _press=e;
            _context=e.Button==MouseButtons.Right;
        }
        public override bool DeactivateOnFocusLoss => !_done;
        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (_pressedHandled || e.Button != _press.Button) return GH_ObjectResponse.Handled;
            _pressedHandled=true;
            if (_context) return GH_ObjectResponse.Handled;
            if (_drawing != null)
            {
                _drawing.CompleteDraft(_drawing.Draft.Down(_drawing.DrawMode,Point(e.CanvasLocation),e.ControlLocation,e.Clicks,
                    (Control.ModifierKeys & Keys.Shift)!=0,_drawing.CanvasScale));
            }
            else if (_owner != null && e.Clicks < 2)
            {
                bool shift=(Control.ModifierKeys & Keys.Shift)!=0, control=(Control.ModifierKeys & Keys.Control)!=0;
                if (_handle < 0) _controller.Select(_owner,_shape,shift,control);
                if (_handle >= 0 || _owner.SelectedShapeIndices.Contains(_shape) && !(control && !shift))
                    _move=new AnnotationMove(_handle>=0 ? new[]{_owner} : _controller.Owners,
                        _handle>=0 ? Enumerable.Empty<Grasshopper.Kernel.IGH_DocumentObject>() : CanvasAnnotationController.NativeSelection(sender.Document),sender.Document);
            }
            sender.Invalidate(); return GH_ObjectResponse.Handled;
        }
        public override GH_ObjectResponse RespondToMouseMove(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (!_moving && AnnotationDraft.Moved(ControlPointDown,e.ControlLocation,SystemInformation.DragSize)) _moving=true;
            if (_context && _moving)
            {
                _done=true;
                GH_AbstractInteraction navigation=(Control.ModifierKeys & Keys.Control)!=0 ? (GH_AbstractInteraction)new GH_ZoomInteraction(sender,_press) : new GH_PanInteraction(sender,_press);
                sender.ActiveInteraction=navigation;
                navigation.RespondToMouseMove(sender,e);
                return GH_ObjectResponse.Handled;
            }
            if (_moving && _move != null)
            {
                if (!_autoPan) { sender.StartAutoPan(); _autoPan=true; }
                if (_handle>=0) _move.Edit(_owner,_shape,_handle,Point(e.CanvasLocation),(Control.ModifierKeys & Keys.Shift)!=0);
                else
                {
                    var delta=Point(e.CanvasLocation)-Point(CanvasPointDown);
                    if ((Control.ModifierKeys & Keys.Shift)!=0)
                    { if (Math.Abs(delta.X)>=Math.Abs(delta.Y)) delta.Y=0; else delta.X=0; }
                    _move.Translate(new PointF((float)delta.X,(float)delta.Y),true);
                }
                Instances.CursorServer.AttachCursor(sender,"GH_HandClosed");
            }
            sender.Invalidate(); return GH_ObjectResponse.Handled;
        }
        public override GH_ObjectResponse RespondToMouseDoubleClick(GH_Canvas sender, GH_CanvasMouseEvent e) => GH_ObjectResponse.Handled;
        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (_done || e.Button != _press.Button) return GH_ObjectResponse.Handled;
            _done=true;
            if (_context)
            {
                if (_owner!=null && !_owner.SelectedShapeIndices.Contains(_shape)) _controller.Select(_owner,_shape,(Control.ModifierKeys & Keys.Shift)!=0,false);
                var menu=new ContextMenuStrip();
                if (_owner!=null) AnnotationMenuBuilder.Selection(menu.Items,_controller.Owners.ToArray());
                else if (_drawing!=null) AnnotationMenuBuilder.Drawing(menu.Items,_drawing);
                menu.Closed+=(s,a)=>menu.Dispose();
                // Show only after GH has consumed this up and destroyed the interaction.
                sender.BeginInvoke(new Action(() => { if (!sender.IsDisposed) menu.Show(sender,e.ControlLocation); else menu.Dispose(); }));
            }
            else if (_drawing!=null)
                _drawing.CompleteDraft(_drawing.Draft.Up(_drawing.DrawMode,Point(e.CanvasLocation),e.ControlLocation,(Control.ModifierKeys & Keys.Shift)!=0,SystemInformation.DragSize));
            else _move?.Commit(sender.Document);
            sender.Invalidate(); return GH_ObjectResponse.Release;
        }
        internal bool Owns(HopperDraw owner) => ReferenceEquals(_owner,owner) || ReferenceEquals(_drawing,owner) || _move?.Actions.ContainsKey(owner)==true;
        internal void Cancel()
        {
            if (_done) return;
            _done=true; _move?.Revert(true); _drawing?.Draft.Cancel(); m_canvas?.Invalidate();
        }
        public override void Destroy()
        {
            Cancel();
            if (_autoPan) { m_canvas.StopAutoPan(); _autoPan=false; }
            Instances.CursorServer.ResetCursor(m_canvas); base.Destroy();
        }
        private static Point3d Point(PointF p)=>new Point3d(p.X,p.Y,0);
    }
}
