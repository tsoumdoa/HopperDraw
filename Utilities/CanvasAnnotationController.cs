using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Canvas.Interaction;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;

namespace hopperdraw
{
    internal sealed class CanvasAnnotationController
    {
        private static readonly Dictionary<GH_Canvas, CanvasAnnotationController> Controllers = new Dictionary<GH_Canvas, CanvasAnnotationController>();
        private readonly GH_Canvas _canvas;
        private readonly HashSet<HopperDraw> _registered = new HashSet<HopperDraw>();
        private readonly CanvasDrawingShortcut.Registration _hook;
        private GH_WindowSelectInteraction _window;
        private Rectangle _windowRect;
        private bool _windowCrossing;
        private GH_DragInteraction _native;
        private AnnotationMove _follow;
        private IGH_DocumentObject _anchor;
        private PointF _anchorPoint;
        private GH_Document _document;
        private DateTime _pressed;
        private bool _releasing, _attached, _windowActive;
        private readonly HashSet<Guid> _seen = new HashSet<Guid>();
        private HashSet<IGH_DocumentObject> _prePress;
        private Cursor _ownedCursor;
        private const GH_RelevantObjectFilter NativeFilter = GH_RelevantObjectFilter.Attributes | GH_RelevantObjectFilter.Grips;
        internal IEnumerable<HopperDraw> Owners => _canvas.Document?.Objects.OfType<HopperDraw>().Where(o => o.Eligible) ?? Enumerable.Empty<HopperDraw>();
        internal HopperDraw DrawingOwner => Owners.FirstOrDefault(o => o.DrawingModeEnabled);
        internal static CanvasAnnotationController Register(GH_Canvas canvas, HopperDraw owner)
        {
            if (!Controllers.TryGetValue(canvas, out var controller))
            { controller = new CanvasAnnotationController(canvas); Controllers.Add(canvas,controller); }
            controller._registered.Add(owner); return controller;
        }
        private CanvasAnnotationController(GH_Canvas canvas)
        {
            _canvas = canvas;
            _hook = CanvasDrawingShortcut.Register(canvas, () => false, () => {});
            _hook.Mouse = Message; _hook.Command = Command; _hook.Lost = Lost;
            canvas.DocumentChanged += DocumentChanged;
            canvas.Disposed += Disposed;
            canvas.MouseMove += Hover;
            canvas.KeyDown += NativeKey; canvas.KeyUp += NativeKey;
            canvas.CanvasPrePaintGroups += SyncPaint;
            canvas.CanvasPostPaintOverlay += PaintHud;
        }
        internal void Remove(HopperDraw owner)
        {
            Lost(); _registered.Remove(owner);
            if (_registered.Count != 0) return;
            _hook.Dispose(); Controllers.Remove(_canvas);
            _canvas.Disposed -= Disposed;
            _canvas.DocumentChanged -= DocumentChanged; _canvas.MouseMove -= Hover;
            _canvas.KeyDown -= NativeKey; _canvas.KeyUp -= NativeKey;
            _canvas.CanvasPrePaintGroups -= SyncPaint; _canvas.CanvasPostPaintOverlay -= PaintHud;
            if (!_canvas.IsDisposed) Instances.CursorServer.ResetCursor(_canvas);
        }
        private void Disposed(object sender, EventArgs e)
        { foreach (var owner in _registered.ToArray()) Remove(owner); }
        internal static IEnumerable<IGH_DocumentObject> NativeSelection(GH_Document doc) => doc.Objects.Where(o => o.Attributes.Selected)
            .SelectMany(o => o is GH_Group group ? group.ObjectsRecursive().Where(c => !(c is GH_Group)) : new[] { o }).Distinct();
        internal (HopperDraw owner, int shape, int handle) Hit(PointF p, bool handles) => AnnotationPicking.Hit(Owners,p,handles);
        // Components, parameters and grips are native. A GH_Group picks its whole filled outline, but annotations are
        // routinely drawn inside groups, so a group interior is empty canvas and only its edge band is native.
        internal static bool OverObject(GH_Document doc, PointF p) => doc?.RelevantObjectAtPoint(p, NativeFilter) != null;
        internal static bool OverNative(GH_Document doc, PointF p) => doc != null && (OverObject(doc, p) || OverGroupEdge(doc, p));
        internal static bool OverGroupEdge(GH_Document doc, PointF p)
        {
            foreach (var group in doc.Objects.OfType<GH_Group>())
            {
                var attributes = group.Attributes; if (attributes == null) continue;
                var b = attributes.Bounds;
                if (!b.Contains(p) || !attributes.IsPickRegion(p)) continue;
                if (p.X - b.Left < 12 || b.Right - p.X < 12 || p.Y - b.Top < 20 || b.Bottom - p.Y < 12) return true;
            }
            return false;
        }
        private HashSet<IGH_DocumentObject> SelectedTopLevel() =>
            new HashSet<IGH_DocumentObject>(_canvas.Document?.Objects.Where(o => o.Attributes?.Selected == true) ?? Enumerable.Empty<IGH_DocumentObject>());
        private bool Eligible(Point client)
        {
            if (_canvas.Document == null || !_canvas.ModifiersEnabled || _canvas.ActiveInteraction != null || _canvas.ActiveWidget != null || _canvas.ActiveObject != null ||
                _canvas.RecordPreviewBoundary || !_canvas.ClientRectangle.Contains(client) || (Control.ModifierKeys & Keys.Alt) != 0) return false;
            var p = _canvas.Viewport.UnprojectPoint(client);
            if (_canvas.Widgets.Any(w => w.Visible && w.Contains(client,p))) return false;
            // GH's abort/cluster icons have no public hit API. Reserve their top-right screen region.
            if (_canvas.Document.SolutionState == GH_ProcessStep.Process || _canvas.Document.SolutionState == GH_ProcessStep.Aborted || _canvas.Document.Owner != null && client.X < 100 * _canvas.DeviceDpi / 96f && client.Y < 100 * _canvas.DeviceDpi / 96f) return false;
            // MRU panels are shown only for empty documents; no editable owner can exist there.
            return Owners.Any();
        }
        private void Message(int message, MouseEventArgs e, bool after)
        {
            if (!after && (message == 0x201 || message == 0x203 || message == 0x204))
            {
                _prePress = null;
                if (!Eligible(e.Location)) return;
                _pressed = DateTime.UtcNow;
                var mouse = new GH_CanvasMouseEvent(_canvas.Viewport,e);
                var p = mouse.CanvasLocation;
                var hit = Hit(p,true);
                if (hit.owner == null && !OverNative(_canvas.Document,p)) hit = Hit(p,false);
                var drawing = DrawingOwner;
                if (hit.owner == null && (drawing == null || OverNative(_canvas.Document,p)))
                {
                    // GH owns this press. Snapshot its selection: if GH replaces it, annotations are replaced too.
                    if (e.Button == MouseButtons.Left) _prePress = SelectedTopLevel();
                    return;
                }
                _canvas.ActiveInteraction = new HopperDrawInteraction(this,_canvas,mouse,hit.owner,hit.shape,hit.handle,drawing);
            }
            if (after && (message == 0x201 || message == 0x203))
            {
                // Grips, sliders, toggles etc. keep GH's selection; only a real replacement clears annotations.
                if (_prePress != null && Control.ModifierKeys == Keys.None && _canvas.Document != null && !_prePress.SetEquals(SelectedTopLevel())) Clear();
                _prePress = null;
                _window = _canvas.ActiveInteraction as GH_WindowSelectInteraction;
                if (_window != null) { _windowRect=Rectangle.Empty; _windowCrossing=false; }
                var drag = _canvas.ActiveInteraction as GH_DragInteraction;
                if (drag != null && Owners.Any(o=>o.HasSelection))
                {
                    var natives = NativeSelection(_canvas.Document).ToArray();
                    _anchor = natives.FirstOrDefault();
                    if (_anchor == null) return;
                    _native = drag; _anchorPoint = _anchor.Attributes.Pivot; _document = _canvas.Document;
                    _follow = new AnnotationMove(Owners,Enumerable.Empty<IGH_DocumentObject>(),_document);
                    if (!_follow.Allowed) { EndFollow(true); return; }
                    _seen.Clear(); _document.UndoStateChanged += UndoChanged;
                }
            }
            if (!after && (message == 0x202 || message == 0x205))
            { if (_window != null && !ReferenceEquals(_canvas.ActiveInteraction,_window)) _window = null; _releasing = true; _windowActive = _window != null && ReferenceEquals(_canvas.ActiveInteraction,_window) && _window.IsActive; SyncFollow(); }
            if (after && (message == 0x202 || message == 0x205))
            {
                if (_window != null)
                {
                    // Only a normal release of the observed native window session reaches here.
                    var rect = (RectangleF)_windowRect;
                    Keys mods = Control.ModifierKeys;
                    if (mods != Keys.Shift && mods != Keys.Control) Clear();
                    if (_windowActive)
                        foreach (var owner in Owners)
                            for (int i=0;i<owner.Shapes.Count;i++) if (AnnotationGeometry.InRectangle(owner.Shapes[i],rect,_windowCrossing,owner.LineThickness))
                            { if (mods == Keys.Control) owner.SelectedShapeIndices.Remove(i); else if (!owner.SelectedShapeIndices.Contains(i)) owner.SelectedShapeIndices.Add(i); }
                    _window = null;
                }
                EndFollow(!_attached); _releasing = false; _canvas.Invalidate();
            }
            if (after && (message == 0x200 || message == 0x100 || message == 0x101 || message == 0x104 || message == 0x105))
            {
                SyncFollow();
                if (_native != null && !ReferenceEquals(_canvas.ActiveInteraction,_native)) EndFollow(true);
                if (_window != null && !ReferenceEquals(_canvas.ActiveInteraction,_window)) _window = null;
            }
        }
        private void NativeKey(object sender, KeyEventArgs e)
        {
            SyncFollow();
            if (_native != null && !ReferenceEquals(_canvas.ActiveInteraction,_native)) EndFollow(true);
            if (_window != null && !ReferenceEquals(_canvas.ActiveInteraction,_window)) _window = null;
        }
        private void SyncFollow()
        {
            if (_follow == null || _anchor == null || !ReferenceEquals(_canvas.Document,_document)) return;
            var p = _anchor.Attributes.Pivot;
            _follow.Translate(new PointF(p.X-_anchorPoint.X,p.Y-_anchorPoint.Y),false);
        }
        private void UndoChanged(object sender, GH_DocUndoEventArgs e)
        {
            if (e.Operation != GH_UndoOperation.RecordAdded || e.Record == null || !_seen.Add(e.Record.Guid)) return;
            if (_attached || !_releasing || e.Record.ActionCount != _native?.AttributeCount || e.Record.Name != "Drag" || !ReferenceEquals(e.Document,_document) || !ReferenceEquals(_canvas.ActiveInteraction,_native) ||
                e.Record.Time.ToUniversalTime() < _pressed || !e.Record.Actions.All(a => a is GH_PivotAction)) return;
            SyncFollow();
            if (_follow?.Changed == true) foreach (var action in _follow.Actions.Values) e.Record.AddAction(action);
            _attached = true;
        }
        private void EndFollow(bool revert)
        {
            if (_document != null) _document.UndoStateChanged -= UndoChanged;
            if (revert) _follow?.Revert(false);
            _follow = null; _native = null; _anchor = null; _document = null; _attached = false;
        }
        internal void CancelOwner(HopperDraw owner)
        {
            if (_canvas.ActiveInteraction is HopperDrawInteraction interaction && interaction.Owns(owner) || _follow?.Actions.ContainsKey(owner)==true) Lost();
            owner.Draft.Cancel();
        }
        // Focus/capture loss ends only gestures HopperDraw is part of. Multi-click drafts (polyline/curve) survive
        // between clicks; an unrelated native drag is left to GH.
        private void Lost()
        {
            if (_releasing) return;
            bool changed = false;
            if (_canvas.ActiveInteraction is HopperDrawInteraction interaction) { interaction.Cancel(); _canvas.ActiveInteraction = null; changed = true; }
            if (_follow != null && _native != null && ReferenceEquals(_canvas.ActiveInteraction,_native))
            {
                _native.RespondToKeyDown(_canvas,new KeyEventArgs(Keys.Escape));
                _canvas.ActiveInteraction=null; changed = true;
            }
            if (_follow != null) { EndFollow(true); changed = true; }
            _window = null; _prePress = null;
            if (changed) _canvas.Invalidate();
        }
        private void DocumentChanged(object sender, GH_CanvasDocumentChangedEventArgs e)
        {
            Lost();
            foreach (var owner in _registered) { owner.Draft.Cancel(); owner.ExitDrawing(); }
        }
        private void SyncPaint(GH_Canvas canvas) { SyncFollow(); } // Guarded absolute translation, before BOTH render channels; covers GH auto-pan.
        internal void Clear() { foreach (var owner in Owners) owner.ClearSelection(); _canvas.Invalidate(); }
        internal void Select(HopperDraw owner, int shape, bool shift, bool control)
        {
            AnnotationSelection.Press(owner.SelectedShapeIndices.Contains(shape),shift,control,
                () => { Clear(); bool native = _canvas.Document.SelectedCount > 0; _canvas.Document.DeselectAll(); owner.SelectedShapeIndices.Add(shape); if (native) Instances.RedrawAll(); },
                () => { if (!owner.SelectedShapeIndices.Contains(shape)) owner.SelectedShapeIndices.Add(shape); },
                () => owner.SelectedShapeIndices.Remove(shape));
        }
        private bool Command(Keys data)
        {
            Keys key = data & Keys.KeyCode, mods = data & Keys.Modifiers;
            if (!Owners.Any() || _canvas.Document == null || _canvas.ActiveWidget != null || _canvas.ActiveObject != null) return false;
            // GH clears its preview boundary before dispatching Escape to any interaction.
            if (key == Keys.Escape && _canvas.Document.PreviewBoundary != null) return false;
            if (_canvas.ActiveInteraction is HopperDrawInteraction active)
            { if (key == Keys.Escape) { active.Cancel(true); _canvas.ActiveInteraction = null; return true; } return false; }
            if (_canvas.ActiveInteraction != null) return false;
            var drawing = DrawingOwner;
            if (key == Keys.Escape)
            {
                switch (AnnotationEscape.Layer(drawing?.Draft.Active == true,drawing != null,Owners.Any(o=>o.HasSelection)))
                {
                    case EscapeLayer.Gesture: drawing.Draft.Cancel(); break;
                    case EscapeLayer.Drawing: drawing.ExitDrawing(); break;
                    case EscapeLayer.Selection: Clear(); break;
                    default: return false;
                }
                _canvas.Invalidate(); return true;
            }
            if (drawing?.Draft.Active == true && (key == Keys.Enter || key == Keys.Back))
            {
                if (key == Keys.Back) drawing.Draft.Backspace();
                else if (drawing.DrawMode == 1 || drawing.DrawMode == 3) drawing.CompleteDraft(drawing.Draft.Finish(drawing.DrawMode,false));
                _canvas.Invalidate(); return true;
            }
            // Select All / Deselect All: extend GH's own commands to annotations, then let GH run them (return false).
            if (key == Keys.A && mods == Keys.Control)
            {
                foreach (var owner in Owners) { owner.ClearSelection(); for(int i=0;i<owner.Shapes.Count;i++) if (!(owner.Shapes[i] is OpaqueShape)) owner.SelectedShapeIndices.Add(i); }
                _canvas.Invalidate(); return false;
            }
            if (key == Keys.D && mods == Keys.Control) { Clear(); return false; }
            if ((key == Keys.Delete || key == Keys.Back) && Owners.Any(o=>o.HasSelection))
            { AnnotationCommands.Delete(Owners,_canvas.Document,true); return true; }
            return false;
        }
        private void Hover(object sender, MouseEventArgs e)
        {
            // Observe GH's actual last selection rectangle, including synthetic auto-pan moves.
            if (_window != null && ReferenceEquals(_canvas.ActiveInteraction,_window) && _window.IsActive)
            {
                var point=_canvas.Viewport.UnprojectPoint(e.Location); var start=_window.CanvasPointDown;
                _windowRect=Rectangle.Union(new Rectangle(GH_Convert.ToPoint(start),Size.Empty),new Rectangle(GH_Convert.ToPoint(point),Size.Empty));
                _windowCrossing=point.X<start.X;
            }
            if (_canvas.ActiveInteraction != null || _canvas.ActiveObject != null || _canvas.ActiveWidget != null) return;
            var previous = Owners.ToDictionary(o=>o,o=>(o.HoveredShapeIndex,o.HoveredPointIndex));
            foreach (var owner in previous.Keys) { owner.HoveredShapeIndex=-1; owner.HoveredPointIndex=-1; }
            string cursor = null;
            var drawing = DrawingOwner;
            if (Eligible(e.Location))
            {
                var p = _canvas.Viewport.UnprojectPoint(e.Location);
                if (drawing != null) { if (!OverNative(_canvas.Document,p)) cursor = "GH_Pencil"; } // A press always draws.
                else
                {
                    var hit = Hit(p,true);
                    if (hit.owner == null && !OverNative(_canvas.Document,p)) hit = Hit(p,false);
                    if (hit.owner != null)
                    {
                        hit.owner.HoveredShapeIndex=hit.shape; hit.owner.HoveredPointIndex=hit.handle;
                        cursor = hit.handle >= 0 ? "" : "GH_HandOpen";
                    }
                }
            }
            // Only touch the cursor for HopperDraw targets; GH's own wire/slider/selection cursors stay intact.
            // GH's MouseMove runs first and may already have assigned its own cursor; never reset that one.
            if (cursor != null) { _ownedCursor = cursor == "" ? Cursors.SizeAll : Instances.CursorServer.Cursor(cursor); _canvas.Cursor = _ownedCursor; }
            else if (_ownedCursor != null) { if (ReferenceEquals(_canvas.Cursor,_ownedCursor)) Instances.CursorServer.ResetCursor(_canvas); _ownedCursor = null; }
            if (drawing != null || previous.Any(pair=>pair.Value != (pair.Key.HoveredShapeIndex,pair.Key.HoveredPointIndex))) _canvas.Invalidate();
        }
        private void PaintHud(GH_Canvas canvas) { AnnotationFeedback.Hud(canvas,DrawingOwner); }
    }
}
