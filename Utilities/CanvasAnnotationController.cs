using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
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
    internal sealed class CanvasAnnotationController : IMessageFilter
    {
        private static readonly Dictionary<GH_Canvas, CanvasAnnotationController> Controllers = new Dictionary<GH_Canvas, CanvasAnnotationController>();
        private readonly GH_Canvas _canvas;
        private readonly HashSet<HopperDraw> _registered = new HashSet<HopperDraw>();
        private readonly CanvasDrawingShortcut.Registration _hook;
        private TextEditSession _text;
        private TextEditorControl _editor;
        private bool _endingText;
        private int _textEpoch;
        private sealed class PlacementEpoch { internal int Value; }
        private readonly ConditionalWeakTable<HopperDraw, PlacementEpoch> _ownerTextEpoch = new ConditionalWeakTable<HopperDraw, PlacementEpoch>();
        private PlacementEpoch OwnerPlacement(HopperDraw owner) => _ownerTextEpoch.GetValue(owner, _ => new PlacementEpoch());
        private int OwnerTextEpoch(HopperDraw owner) => OwnerPlacement(owner).Value;
        private GH_Document _textDocument;
        private AnnotationNudge _nudge;
        private bool _nudgePending;
        private void ObserveTextDocument()
        {
            if (_textDocument != null) { _textDocument.UndoStateChanged -= TextUndoChanged; _textDocument.UndoStateChanged -= NudgeUndoChanged; }
            _textDocument = _canvas.Document;
            if (_textDocument != null) { _textDocument.UndoStateChanged += TextUndoChanged; _textDocument.UndoStateChanged += NudgeUndoChanged; }
            _nudge = null;
        }
        internal void PlaceTextAfterRelease(HopperDraw owner, TextShape shape)
        {
            int epoch = _textEpoch, ownerEpoch = OwnerTextEpoch(owner); var document = _canvas.Document;
            _canvas.BeginInvoke(new Action(() =>
            {
                if (!_canvas.IsDisposed && epoch == _textEpoch && ownerEpoch == OwnerTextEpoch(owner) && ReferenceEquals(document,_canvas.Document)) BeginText(owner,shape,true);
            }));
        }
        internal void BeginText(HopperDraw owner, TextShape shape, bool isNew)
        {
            EndText(true);
            if (!owner.Eligible || !ReferenceEquals(owner.OnPingDocument(), _canvas.Document)) return;
            if (!isNew) { shape = TextEditSession.Find(owner.Shapes,shape.PersistentId); if (shape == null) return; }
            DrawingOwner?.Draft.Cancel();
            _hook.Reset(); owner.ResetTextKeys();
            var session = new TextEditSession(owner, _canvas.Document, shape, isNew);
            var font = TextLayoutService.Font(shape, (float)_canvas.Viewport.Zoom);
            TextEditorControl editor = null;
            try
            {
                editor = new TextEditorControl();
                editor.Font = font;
                // The model stores LF; a Win32 multiline EDIT control only breaks lines on CRLF.
                editor.Text = EditorText(shape.Text);
                editor.WordWrap = shape.BoxWidth.HasValue;
                _editor = editor;
                _text = session;
                if (!isNew) ShapeRenderer.EditingText.Add(session.TargetId);
                editor.Commit = () => EndText(true, true);
                editor.Dismiss = DismissFlyout;
                editor.Format = FormatKey;
                editor.Updating = EditorUpdating;
                editor.LostFocus += EditorLostFocus;
                editor.TextChanged += EditorTextChanged;
                LayoutEditor();
                _canvas.Controls.Add(editor);
                // The handle now exists: line count and wrapping are the control's own.
                LayoutEditor();
                editor.BringToFront(); _bar.Raise(); editor.Focus(); editor.SelectAll();
                _canvas.BeginInvoke(new Action(() => { if (ReferenceEquals(_editor, editor) && !editor.IsDisposed && !editor.ContainsFocus) editor.Focus(); }));
                Application.AddMessageFilter(this);
                FeedbackChanged(); _canvas.Invalidate();
            }
            catch
            {
                if (_text == session) EndText(false);
                else { editor?.Dispose(); font.Dispose(); }
                throw;
            }
        }
        private static Color Over(Color top, Color under)
        {
            float a = top.A / 255f;
            return Color.FromArgb((int)Math.Round(top.R * a + under.R * (1 - a)), (int)Math.Round(top.G * a + under.G * (1 - a)), (int)Math.Round(top.B * a + under.B * (1 - a)));
        }
        // The skin is only loaded inside Grasshopper; its default background stands in elsewhere.
        internal static Color CanvasBack()
        {
            var skin = GH_Skin.canvas_back;
            return skin.A == 0 ? Color.FromArgb(212, 208, 200) : Color.FromArgb(255, skin);
        }
        // A native text box cannot be transparent, so it takes the colour the canvas shows behind the text:
        // the canvas background, under the text box's own fill when it has one.
        internal static Color EditorBack(TextShape draft, Color canvas)
        {
            var back = Color.FromArgb(255, canvas);
            if (!draft.FillColor.HasValue || !(draft.FillOpacity > 0)) return back;
            var layer = draft.Hatch == HatchPattern.None ? draft.FillColor : draft.HatchBackground;
            return layer.HasValue ? Over(HatchRenderer.Ink(layer.Value, draft.FillOpacity), back) : back;
        }
        // Places the editor exactly over the text it stands in for: the content box of the draft's layout, grown
        // to whatever the control itself needs so that nothing scrolls. Runs on every text and style change.
        private void LayoutEditor()
        {
            if (_text == null || _editor == null) return;
            var draft = _text.Draft; var layout = draft.Layout;
            float zoom = (float)_canvas.Viewport.Zoom, em = layout.Em * zoom;
            var origin = _canvas.Viewport.ProjectPoint(new PointF(layout.Bounds.Left + layout.Padding, layout.Bounds.Top + layout.Padding));
            var back = EditorBack(draft, CanvasBack());
            if (_editor.BackColor != back) _editor.BackColor = back;
            var ink = Over(draft.TextColorOverride ?? _text.Owner.BorderColor, back);
            if (_editor.ForeColor != ink) _editor.ForeColor = ink;
            int Clamp(float value, int min) => float.IsNaN(value) ? min : (int)Math.Max(min, Math.Min(10000, Math.Ceiling(value)));
            int x = (int)Math.Round(origin.X), y = (int)Math.Round(origin.Y);
            float width = (layout.Bounds.Width - 2 * layout.Padding) * zoom;
            // Unwrapped text needs room for the caret after the last character, or the control scrolls sideways.
            if (!draft.BoxWidth.HasValue) width = Math.Max(width, _editor.ContentSize.Width) + Math.Max(4, .3f * em);
            _editor.SetBounds(x, y, Clamp(width, 4), _editor.Height);
            // Wrapping follows the width just set.
            _editor.Height = Clamp(Math.Max((layout.Bounds.Height - 2 * layout.Padding) * zoom, _editor.ContentSize.Height), 4);
            _editor.ResetScroll();
        }
        // Device-pixel frame of the text being edited: its layout box, grown to enclose the editor.
        private RectangleF EditorFrame()
        {
            var layout = _text.Draft.Layout; float zoom = (float)_canvas.Viewport.Zoom;
            var corner = _canvas.Viewport.ProjectPoint(layout.Bounds.Location);
            var box = new RectangleF(corner.X, corner.Y, layout.Bounds.Width * zoom, layout.Bounds.Height * zoom);
            return RectangleF.Union(box, RectangleF.Inflate(_editor.Bounds, 1, 1));
        }
        private readonly FrameShape _frameSample = new FrameShape();
        // The editor hides the stored text, so the canvas paints the edited text's box (fill and border, live as
        // the style changes) and a thin editing frame around it.
        internal void PaintEditorFrame(Graphics g)
        {
            if (_text == null || _editor == null || g == null) return;
            var state = g.Save();
            try
            {
                g.ResetTransform(); g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.None;
                var draft = _text.Draft; var frame = EditorFrame();
                float zoom = (float)_canvas.Viewport.Zoom, s = Math.Max(1, g.DpiX / 96f), stroke = 0;
                var sample = _frameSample;
                sample.TopLeft = new Rhino.Geometry.Point3d(frame.Left, frame.Top, 0); sample.TopRight = new Rhino.Geometry.Point3d(frame.Right, frame.Top, 0);
                sample.BottomRight = new Rhino.Geometry.Point3d(frame.Right, frame.Bottom, 0); sample.BottomLeft = new Rhino.Geometry.Point3d(frame.Left, frame.Bottom, 0);
                draft.CopyStyleTo(sample); sample.HatchScale = draft.HatchScale * zoom;
                using (var path = AnnotationGeometry.Boundary(sample))
                {
                    HatchRenderer.Paint(g, sample, path);
                    if (draft.StrokeVisible)
                    {
                        stroke = StyleValidation.EffectiveWidth(_text.Owner.LineThickness, draft.ThicknessMultiplier) * zoom;
                        using (var pen = new Pen(draft.OverrideColor ?? _text.Owner.BorderColor, stroke) { DashStyle = StyleValidation.SafeDash(draft.LineType) })
                            if (path.PointCount > 0) g.DrawPath(pen, path);
                    }
                }
                // Whole-pixel line on whole pixels (pixel centres are integers with the offset mode set above): a
                // hairline frame must not smear across two rows.
                float outset = stroke / 2 + 3 * s, line = (float)Math.Max(1, Math.Round(1.25f * s)), half = line % 2 == 1 ? 0 : .5f;
                var around = RectangleF.Inflate(frame, outset, outset);
                around = new RectangleF((float)Math.Floor(around.X) + half, (float)Math.Floor(around.Y) + half, (float)Math.Ceiling(around.Width), (float)Math.Ceiling(around.Height));
                using (var pen = new Pen(Color.FromArgb(0, 120, 215), line))
                using (var outline = StyleSurface.Rounded(around, 3 * s))
                    g.DrawPath(pen, outline);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("HopperDraw editor frame skipped: " + ex.Message); }
            finally { g.Restore(state); }
        }
        private void SyncEditor()
        {
            if (_text == null || _editor == null) return;
            _text.Buffer = _editor.Text; _text.Draft.Text = _editor.Text;
            LayoutEditor();
        }
        // Before the new text is drawn: grow first so a new line or character never shows scrolled.
        private void EditorUpdating() { SyncEditor(); }
        private void EditorTextChanged(object sender, EventArgs e)
        {
            SyncEditor();
            // Typing dismisses an open style flyout; the frame follows the text.
            _bar.CloseFlyout(); _canvas.Invalidate();
        }
        // The style bar or a format key restyled the draft: the editor shows it immediately.
        private void TextStyleChanged()
        {
            if (_text == null || _editor == null) return;
            var old = _editor.Font;
            _editor.Font = TextLayoutService.Font(_text.Draft, (float)_canvas.Viewport.Zoom);
            old.Dispose();
            LayoutEditor(); _canvas.Invalidate();
        }
        private StyleTarget TextTarget()
        {
            var drawing = DrawingOwner;
            bool pen = _text.IsNew && ReferenceEquals(drawing, _text.Owner) && drawing.DrawMode == 5;
            return new TextEditTarget(_text, pen ? drawing.TextPen : null, TextStyleChanged);
        }
        private bool FormatKey(Keys data)
        {
            if (_text == null || !StyleKeys.Match(data, true, out var key)) return false;
            // Always consumed while typing: an unused chord must not reach the text box as a control character.
            string result = TextTarget().Run(key);
            if (result != null) Notice(result);
            SyncBar(); return true;
        }
        // The key rows of the canvas, for the pen while drawing and for selected annotations. Keys with nothing
        // to act on (a position past the end of its row, fill on a line) are left to Grasshopper.
        private bool RunStyleKey(Keys data)
        {
            if (!StyleKeys.Match(data, false, out var key)) return false;
            string result = BarState()?.Target.Run(key);
            if (result == null) return false;
            Notice(result); SyncBar(); _canvas.Invalidate();
            return true;
        }
        // D is on the line-type row and is also the first half of D D. While drawing D D has nothing left to
        // do and D acts at once. With annotations selected outside drawing mode, the press is left to the
        // gesture and D acts once the double-press window has passed without a second D, provided nothing
        // else has happened in between: no other key, no mouse button, the same selection.
        private readonly DelayedAction _deferredKey = new DelayedAction();
        private void DeferStyleKey(Keys data)
        {
            if (BarState() == null) return;
            int signature = BarSignature();
            _deferredKey.Arm(SystemInformation.DoubleClickTime + 30, () =>
            {
                if (_canvas.IsDisposed || !_canvas.Focused || _text != null || DrawingOwner != null || Control.MouseButtons != MouseButtons.None ||
                    Control.ModifierKeys != Keys.None || CanvasDrawingShortcut.MenuActive || _canvas.ActiveInteraction != null || BarSignature() != signature) return;
                RunStyleKey(data);
            });
        }
        // Escape closes an open flyout first. The same press also arrives through the host's Escape route.
        private bool DismissFlyout()
        {
            if (!_bar.CloseFlyout()) return false;
            FreshEscape(); return true;
        }
        private void EditorLostFocus(object sender, EventArgs e) { if (!_styleModal) EndText(true, true); }
        private readonly StyleBar _bar;
        private bool _barPending, _styleModal;
        private int _barSignature;
        internal StyleBar Bar => _bar;
        private StyleBarState BarState()
        {
            var drawing = DrawingOwner;
            StyleTarget target = null;
            if (_text != null) target = TextTarget();
            else if (drawing != null) target = new PenTarget(drawing);
            else if (Owners.Any(o => o.HasSelection)) target = new SelectionTarget(Owners);
            if (target == null || target.Shapes.Length == 0) return null;
            return new StyleBarState
            {
                Target = target, Tool = drawing?.DrawMode ?? -1, SetTool = drawing == null ? (Action<int>)null : drawing.SetTool,
                ToolsLocked = drawing != null && drawing.Params.Input[2].SourceCount > 0, Typing = _text != null
            };
        }
        // Everything that decides which bar is shown. Style values are not included: every style change goes
        // through FeedbackChanged or the bar itself.
        private int BarSignature()
        {
            unchecked
            {
                var drawing = DrawingOwner;
                int hash = _text == null ? 17 : RuntimeHelpers.GetHashCode(_text);
                hash = hash * 31 + (drawing == null ? 0 : RuntimeHelpers.GetHashCode(drawing));
                hash = hash * 31 + (drawing?.DrawMode ?? -1);
                hash = hash * 31 + (drawing != null && drawing.Params.Input[2].SourceCount > 0 ? 1 : 0);
                hash = hash * 31 + _canvas.ClientSize.Width; hash = hash * 31 + _canvas.ClientSize.Height; hash = hash * 31 + _canvas.DeviceDpi;
                if (_text == null && drawing == null)
                    foreach (var owner in Owners)
                    {
                        hash = hash * 31 + RuntimeHelpers.GetHashCode(owner);
                        foreach (int index in owner.SelectedShapeIndices) hash = hash * 31 + index;
                    }
                return hash;
            }
        }
        private void QueueBar()
        {
            if (_barPending || _canvas.IsDisposed || !_canvas.IsHandleCreated) return;
            _barPending = true;
            _canvas.BeginInvoke(new Action(() => { _barPending = false; SyncBar(); }));
        }
        internal void SyncBar()
        {
            if (_canvas.IsDisposed || _bar.IsDisposed) return;
            try
            {
                // Recorded first: a bar that fails must not be retried on every paint.
                _barSignature = BarSignature();
                _bar.Backdrop = CanvasBack();
                _bar.Sync();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("HopperDraw style bar skipped: " + ex); }
        }
        private void CanvasResized(object sender, EventArgs e) => QueueBar();
        // The custom colour dialog takes focus and clicks; neither may end the text being styled.
        private void StyleModal(bool open)
        {
            _styleModal = open;
            if (open || _canvas.IsDisposed) return;
            if (_editor != null && !_editor.IsDisposed) _editor.Focus(); else RestoreCanvasFocus();
        }
        private void TextUndoChanged(object sender, GH_DocUndoEventArgs e)
        { if (e.Operation == GH_UndoOperation.Undo || e.Operation == GH_UndoOperation.Redo) { _textEpoch++; EndText(false); } }
        internal void AbortText(HopperDraw owner)
        {
            OwnerPlacement(owner).Value++;
            if (_text?.Owner == owner) EndText(false);
        }
        // finished: the user ended this text (Esc, Ctrl+Enter, left click outside, focus left). Like the text tool in
        // design apps, that also leaves the Text tool, so the next click selects/drags instead of placing more text.
        // Viewport changes, right-click, M changes, aborts and starting another edit keep drawing mode.
        private void EndText(bool commit, bool finished = false)
        {
            if (_text == null || _endingText) return;
            _endingText = true;
            var session = _text; var editor = _editor;
            _text = null; _editor = null; _textEndedTick = Environment.TickCount;
            try
            {
                Application.RemoveMessageFilter(this);
                ShapeRenderer.EditingText.Remove(session.TargetId); _bar.CloseFlyout();
                try
                {
                    session.Buffer = editor.Text;
                    bool eligible = session.Owner.Eligible && Owners.Contains(session.Owner);
                    if (commit) session.Commit(_canvas.Document, eligible); else session.Abort();
                }
                finally
                {
                    editor.LostFocus -= EditorLostFocus; editor.TextChanged -= EditorTextChanged;
                    // Esc / Ctrl+Enter / a canvas click end the edit while the editor still has focus. Removing a
                    // focused child lets Windows move focus to an arbitrary sibling (live Rhino: not the canvas), so
                    // drawing-mode keys (Escape, tool keys) stopped working and a click would place new text.
                    bool hadFocus = editor.ContainsFocus;
                    // Remove immediately; defer disposal until after this message/filter has returned.
                    _canvas.Controls.Remove(editor);
                    if (hadFocus) RestoreCanvasFocus();
                    if (!_canvas.IsDisposed && _canvas.IsHandleCreated) _canvas.BeginInvoke(new Action(() => { var font = editor.Font; editor.Dispose(); font.Dispose(); }));
                    else { var font = editor.Font; editor.Dispose(); font.Dispose(); }
                    _hook.Reset(); session.Owner.ResetTextKeys(); FeedbackChanged(); _canvas.Invalidate();
                }
            }
            finally { _endingText = false; }
            if (commit && finished && ExitsAfterText(DrawingOwner)) { DrawingOwner.ExitDrawing(); FeedbackChanged(); }
        }
        internal static bool ExitsAfterText(HopperDraw drawing) => drawing != null && drawing.DrawingModeEnabled && drawing.DrawMode == 5;
        internal static string EditorText(string model) => TextShape.NormalizeNewlines(model).Replace("\n", "\r\n");
        public bool PreFilterMessage(ref Message m)
        {
            if (_text == null) return false;
            // Cheap identity checks on every message; the document scan (owner still present) only on presses/wheel,
            // the only messages that can commit. Owner removal is also handled by CancelOwner/DocumentChanged.
            if (!_text.Owner.Eligible || !ReferenceEquals(_text.Document,_canvas.Document)) { EndText(false); return false; }
            // Styling is part of the edit: presses on the style bar, its flyout or its colour dialog never end it.
            if (_styleModal || _bar.Owns(m.HWnd)) return false;
            bool press = m.Msg == 0x201 || m.Msg == 0x204 || m.Msg == 0x207 || m.Msg == 0x20b, wheel = m.Msg == 0x20a;
            if (!press && !wheel) return false;
            if (!Owners.Contains(_text.Owner)) EndText(false);
            else if (press && m.HWnd != _editor.Handle || wheel && m.HWnd == _canvas.Handle) EndText(true, m.Msg == 0x201);
            return false;
        }
        private void ViewportChanged(object sender, GH_CanvasViewportChangedEventArgs e) => EndText(true);
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
        private bool _refreshingCursor, _cursorRefreshPending;
        private readonly DrawingCursorCache _cursors = new DrawingCursorCache();
        private readonly AnnotationHintClock _hints;
        internal void Notice(string text) { _hints.Update(DrawingOwner,DrawingOwner?.DrawMode??-1,DrawingOwner?.Draft.Active==true || _text!=null); _hints.Notice(text); }
        // Explicit state changes (tool, mode, DPI, editor) refresh hints and the stationary-pointer cursor.
        internal void FeedbackChanged()
        {
            UpdateHints(); QueueBar();
            if (_cursorRefreshPending || _canvas.IsDisposed || !_canvas.IsHandleCreated) return;
            _cursorRefreshPending = true;
            _canvas.BeginInvoke(new Action(() =>
            {
                _cursorRefreshPending = false;
                RefreshCursor();
            }));
        }
        // Paint and fade ticks only update hint state: they must never drive cursor or hover changes.
        private void UpdateHints()
        {
            var owner=DrawingOwner;
            _hints.Update(owner,owner?.DrawMode??-1,owner?.Draft.Active==true || _text!=null);
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        private void DpiChanged(object sender,EventArgs e) { FeedbackChanged(); _canvas.Invalidate(); }
        internal void AssignCursor(Cursor cursor) { _ownedCursor=cursor; _canvas.Cursor=cursor; }
        internal void ResetCursor()
        {
            if(_ownedCursor==null) return;
            if(ReferenceEquals(_canvas.Cursor,_ownedCursor))
            {
                // A disposed canvas can still hold the Cursor object; detach before freeing its native handle.
                if(_canvas.IsDisposed) _canvas.Cursor=Cursors.Default;
                else { Instances.CursorServer.ResetCursor(_canvas); if(ReferenceEquals(_canvas.Cursor,_ownedCursor)) _canvas.Cursor=Cursors.Default; }
            }
            _ownedCursor=null;
        }
        internal void RefreshCursor()
        {
            if(_refreshingCursor || _cursors.IsDisposed || _canvas.IsDisposed || !_canvas.IsHandleCreated) return;
            // Only when the canvas window itself is under the pointer: never over the inline editor (a child),
            // a context menu, a colour dialog or any popup overlapping the canvas rectangle.
            if(WindowFromPoint(Cursor.Position)!=_canvas.Handle) return;
            _refreshingCursor=true;
            try { var p=_canvas.PointToClient(Control.MousePosition); Hover(_canvas,new MouseEventArgs(MouseButtons.None,0,p.X,p.Y,0)); }
            finally { _refreshingCursor=false; }
        }
        private const GH_RelevantObjectFilter NativeFilter = GH_RelevantObjectFilter.Attributes | GH_RelevantObjectFilter.Grips;
        internal IEnumerable<HopperDraw> Owners => _canvas.Document?.Objects.OfType<HopperDraw>().Where(o => o.Eligible) ?? Enumerable.Empty<HopperDraw>();
        internal HopperDraw DrawingOwner => Owners.FirstOrDefault(o => o.DrawingModeEnabled);
        internal static CanvasAnnotationController Register(GH_Canvas canvas, HopperDraw owner)
        {
            if (!Controllers.TryGetValue(canvas, out var controller))
            { controller = new CanvasAnnotationController(canvas); Controllers.Add(canvas,controller); }
            controller._registered.Add(owner); return controller;
        }
        // One physical Escape can arrive through both the HWND/editor route and Rhino's EscapeKeyPressed.
        private int _lastEscapeTick = Environment.TickCount - 10000, _textEndedTick = Environment.TickCount - 10000;
        private bool FreshEscape()
        {
            int now = Environment.TickCount;
            if (unchecked((uint)(now - _lastEscapeTick)) < 250) return false;
            _lastEscapeTick = now; return true;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
        private void RestoreCanvasFocus()
        {
            if (_canvas.IsDisposed || !_canvas.IsHandleCreated) return;
            _canvas.Focus();
            // Host focus handling can run after this message; re-assert once if focus landed nowhere useful.
            _canvas.BeginInvoke(new Action(() => { if (!_canvas.IsDisposed && _text == null && !_canvas.Focused && CanvasOwnsKeyboard()) _canvas.Focus(); }));
        }
        // Keyboard input belongs to this canvas when it, or a container of it (the editor form/panels), holds focus,
        // or nothing does while its form is active. A focused text field elsewhere (GH search, panel editor) does not.
        private bool CanvasOwnsKeyboard()
        {
            if (_canvas.IsDisposed || !_canvas.IsHandleCreated) return false;
            var focus = GetFocus();
            if (focus == _canvas.Handle) return true;
            if (focus == IntPtr.Zero) return Form.ActiveForm != null && ReferenceEquals(Form.ActiveForm, _canvas.FindForm());
            return IsChild(focus, _canvas.Handle);
        }
        private bool _rhinoEscape;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void ObserveRhinoEscape(bool on)
        {
            try
            {
                if (on == _rhinoEscape || !Rhino.Runtime.HostUtils.RunningInRhino) return;
                if (on) Rhino.RhinoApp.EscapeKeyPressed += RhinoEscape; else Rhino.RhinoApp.EscapeKeyPressed -= RhinoEscape;
                _rhinoEscape = on;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("HopperDraw Escape hook unavailable: " + ex.Message); }
        }
        private void RhinoEscape(object sender, EventArgs e)
        {
            if (_canvas.IsDisposed || !_canvas.IsHandleCreated) return;
            _canvas.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_canvas.IsDisposed || CanvasDrawingShortcut.MenuActive) return;
                    if (_bar.FlyoutOpen) { if (FreshEscape()) _bar.CloseFlyout(); return; }
                    if (_text != null)
                    {
                        // Only the editor this canvas owns, and only while it has focus.
                        if (_editor != null && _editor.ContainsFocus && FreshEscape()) EndText(true, true);
                        return;
                    }
                    if (!CanvasOwnsKeyboard() || unchecked((uint)(Environment.TickCount - _textEndedTick)) < 250) return;
                    if (!_canvas.Focused) _canvas.Focus(); // later keys (tool keys) route through the canvas again
                    if (Command(Keys.Escape)) _canvas.Invalidate();
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("HopperDraw Escape skipped: " + ex); }
            }));
        }
        private CanvasAnnotationController(GH_Canvas canvas)
        {
            _canvas = canvas;
            ObserveRhinoEscape(true);
            _hints = new AnnotationHintClock(() => { if(!_canvas.IsDisposed) { UpdateHints(); _canvas.Invalidate(); } });
            _hook = CanvasDrawingShortcut.Register(canvas, () => false, () => {});
            _hook.Mouse = Message; _hook.Command = Command; _hook.Lost = Lost; _hook.BeforeKey = BeforeNativeKey; _hook.KeyFailed = ClearNudge;
            canvas.DocumentChanged += DocumentChanged;
            canvas.ViewportChanged += ViewportChanged;
            ObserveTextDocument();
            canvas.Disposed += Disposed;
            canvas.MouseMove += Hover;
            canvas.DpiChangedAfterParent += DpiChanged;
            canvas.KeyDown += NativeKey; canvas.KeyUp += NativeKey;
            canvas.CanvasPrePaintGroups += SyncPaint;
            canvas.CanvasPostPaintOverlay += PaintHud;
            _bar = new StyleBar { Source = BarState, Modal = StyleModal, PointerPressed = _deferredKey.Cancel };
            _bar.Attach(canvas);
            canvas.Resize += CanvasResized;
        }
        internal void Remove(HopperDraw owner)
        {
            AbortText(owner); Lost(); _registered.Remove(owner);
            if (_registered.Count != 0) return;
            if (_textDocument != null) { _textDocument.UndoStateChanged -= TextUndoChanged; _textDocument.UndoStateChanged -= NudgeUndoChanged; }
            _textDocument = null;
            _hook.Dispose(); Controllers.Remove(_canvas);
            _canvas.Disposed -= Disposed;
            _canvas.ViewportChanged -= ViewportChanged; _canvas.DocumentChanged -= DocumentChanged; _canvas.MouseMove -= Hover;
            _canvas.KeyDown -= NativeKey; _canvas.KeyUp -= NativeKey;
            _canvas.CanvasPrePaintGroups -= SyncPaint; _canvas.CanvasPostPaintOverlay -= PaintHud;
            _canvas.DpiChangedAfterParent -= DpiChanged; _canvas.Resize -= CanvasResized;
            _bar.Dispose(); // also leaves the canvas' child controls
            _deferredKey.Dispose();
            ObserveRhinoEscape(false);
            ResetCursor(); _cursors.Dispose(); _hints.Dispose();
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
            try { RouteMessage(message, e, after); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("HopperDraw input skipped: " + ex);
                _releasing = false; _window = null; _prePress = null;
                EndFollow(true); ResetCursor();
            }
        }
        private void RouteMessage(int message, MouseEventArgs e, bool after)
        {
            // Cancel on the press itself: a quick click or wheel can leave the selection and focus unchanged.
            if (!after && (message == 0x201 || message == 0x203 || message == 0x204 || message == 0x206 ||
                message == 0x207 || message == 0x209 || message == 0x20b || message == 0x20d || message == 0x20a || message == 0x20e))
                _deferredKey.Cancel();
            if (!after && (message == 0x201 || message == 0x203 || message == 0x204 || message == 0x207 || message == 0x20a)) _bar.CloseFlyout();
            if (!after && (message == 0x201 || message == 0x203 || message == 0x204 || message == 0x20a)) EndText(true, message == 0x201 || message == 0x203);
            if (!after && (message == 0x201 || message == 0x203 || message == 0x204))
            {
                SyncNudge();
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
                var moving = drag == null ? null : AnnotationMove.MovingSet(Owners,_canvas.Document);
                if (moving?.Count > 0)
                {
                    var natives = NativeSelection(_canvas.Document).ToArray();
                    _anchor = natives.FirstOrDefault();
                    if (_anchor == null) return;
                    _native = drag; _anchorPoint = _anchor.Attributes.Pivot; _document = _canvas.Document;
                    _follow = new AnnotationMove(moving,Enumerable.Empty<IGH_DocumentObject>(),_document);
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
                            for (int i=0;i<owner.Shapes.Count;i++) if (AnnotationGeometry.InRectangle(owner.Shapes[i],rect,_windowCrossing,owner.LineThickness,owner.DrawOrder!=0))
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
            SyncNudge();
            SyncFollow();
            if (_native != null && !ReferenceEquals(_canvas.ActiveInteraction,_native)) EndFollow(true);
            if (_window != null && !ReferenceEquals(_canvas.ActiveInteraction,_window)) _window = null;
        }
        private void BeforeNativeKey(Keys data)
        {
            try
            {
                SyncNudge();
                var key = data & Keys.KeyCode;
                if ((data & Keys.Modifiers) == Keys.Alt && key >= Keys.Left && key <= Keys.Down)
                {
                    if (_nudge?.Matches(key.ToString()) == true && CanNudge())
                        _nudge.Prepare(AnnotationMove.MovingSet(Owners,_canvas.Document,true),_canvas.Document.SelectedObjects());
                    QueueNudge();
                }
            }
            catch (Exception ex) { ClearNudge(); System.Diagnostics.Debug.WriteLine("HopperDraw nudge key skipped: " + ex); }
        }
        private void NudgeUndoChanged(object sender, GH_DocUndoEventArgs e)
        {
            try
            {
                // Align/Distribute (even an accidentally uniform align) and every unrelated record end this session.
                _nudge = null;
                if (e.Operation != GH_UndoOperation.RecordAdded || !ReferenceEquals(e.Document,_canvas.Document) ||
                    !AnnotationNudge.IsNudge(e.Record) || e.Record.ActionCount == 0 || !e.Record.Actions.All(a => a is GH_PivotAction) ||
                    !CanNudge()) return;
                _nudge = new AnnotationNudge(AnnotationMove.MovingSet(Owners,e.Document,true),e.Document.SelectedObjects(),e.Document,e.Record);
                QueueNudge(); // GH raises RecordAdded before translating the selected objects.
            }
            catch (Exception ex) { ClearNudge(); System.Diagnostics.Debug.WriteLine("HopperDraw nudge undo skipped: " + ex); }
        }
        private void ClearNudge() { _nudge = null; _nudgePending = false; }
        private void QueueNudge()
        {
            if (_nudgePending || _canvas.IsDisposed || !_canvas.IsHandleCreated) return;
            _nudgePending = true;
            _canvas.BeginInvoke(new Action(QueuedNudge));
        }
        private void QueuedNudge()
        {
            try { _nudgePending = false; if (!_canvas.IsDisposed) SyncNudge(); }
            catch (Exception ex) { ClearNudge(); System.Diagnostics.Debug.WriteLine("HopperDraw queued nudge skipped: " + ex); }
        }
        private void SyncNudge()
        { if (CanNudge() && _nudge?.Sync(_canvas.Document) == true) _canvas.Invalidate(); }
        private bool CanNudge() => _canvas.Document != null && _text == null && _canvas.ActiveInteraction == null &&
            _canvas.ActiveWidget == null && _canvas.ActiveObject == null;
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
        internal void CancelOwner(HopperDraw owner, bool commitText = false)
        {
            if (commitText && _text?.Owner == owner) EndText(true);
            AbortText(owner);
            if (_canvas.ActiveInteraction is HopperDrawInteraction interaction && interaction.Owns(owner) || _follow?.Actions.ContainsKey(owner)==true) Lost();
            owner.Draft.Cancel();
        }
        // Focus/capture loss ends only gestures HopperDraw is part of. Multi-click drafts (polyline/curve) survive
        // between clicks; an unrelated native drag is left to GH.
        private void Lost()
        {
            // Not for the bar's own colour dialog, whose choice is applied when it closes.
            if (!_styleModal) _bar?.CloseFlyout();
            _deferredKey.Cancel();
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
            _textEpoch++; EndText(false); ObserveTextDocument(); Lost();
            foreach (var owner in _registered) { owner.Draft.Cancel(); owner.ExitDrawing(); }
        }
        private void SyncPaint(GH_Canvas canvas) { if (_text != null && (!_text.Owner.Eligible || !Owners.Contains(_text.Owner))) EndText(false); SyncFollow(); } // Guarded absolute translation, before BOTH render channels; covers GH auto-pan.
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
            // Any other key ends the wait of a deferred D; a second D re-arms it below or enters drawing mode.
            _deferredKey.Cancel();
            // An open style flyout is the innermost Escape layer, ahead of the text, the draft and drawing mode.
            if (key == Keys.Escape && _bar.FlyoutOpen) { if (FreshEscape()) _bar.CloseFlyout(); return true; }
            if (_text != null)
            {
                if (key != Keys.Escape && !(key == Keys.Enter && mods == Keys.Control)) return false;
                if (key == Keys.Escape && !FreshEscape()) return true;
                EndText(true, true); return true;
            }
            if (!Owners.Any() || _canvas.Document == null || _canvas.ActiveWidget != null || _canvas.ActiveObject != null) return false;
            // GH clears its preview boundary before dispatching Escape to any interaction.
            if (key == Keys.Escape && _canvas.Document.PreviewBoundary != null) return false;
            var toolOwner = DrawingOwner;
            if (toolOwner != null && Control.MouseButtons == MouseButtons.None && _canvas.Focused && !CanvasDrawingShortcut.MenuActive &&
                (_canvas.ActiveInteraction == null || _canvas.ActiveInteraction is HopperDrawInteraction) &&
                ToolShortcut.Route(data, toolOwner.DrawMode, toolOwner.Params.Input[2].SourceCount > 0,
                    toolOwner.SetTool, () => Notice("Disconnect M to switch tools"))) return true;
            // Style keys under the same conditions as the tool keys, for the pen or the selection. A held
            // mouse button (a drag, a freehand stroke) keeps its keys.
            if (Control.MouseButtons == MouseButtons.None && _canvas.Focused && !CanvasDrawingShortcut.MenuActive &&
                (_canvas.ActiveInteraction == null || _canvas.ActiveInteraction is HopperDrawInteraction))
            {
                if (toolOwner == null && data == Keys.D) DeferStyleKey(data);
                else if (RunStyleKey(data)) return true;
            }
            if (_canvas.ActiveInteraction is HopperDrawInteraction active)
            { if (key == Keys.Escape) { active.Cancel(true); _canvas.ActiveInteraction = null; return true; } return false; }
            if (_canvas.ActiveInteraction != null) return false;
            var drawing = DrawingOwner;
            if (key == Keys.Escape)
            {
                if (!FreshEscape()) return true; // the same physical press already arrived through the host route
                switch (AnnotationEscape.Layer(drawing?.Draft.Active == true,drawing != null,Owners.Any(o=>o.HasSelection)))
                {
                    case EscapeLayer.Gesture: drawing.Draft.Cancel(); break;
                    case EscapeLayer.Drawing: drawing.ExitDrawing(); break;
                    case EscapeLayer.Selection: Clear(); break;
                    default: return false;
                }
                _canvas.Invalidate(); return true;
            }
            if (drawing?.Draft.Active == true && drawing.DrawMode != 4 && (key == Keys.Enter || key == Keys.Back))
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
            try { UpdateHover(sender, e); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("HopperDraw hover skipped: " + ex); ResetCursor(); }
        }
        private void UpdateHover(object sender, MouseEventArgs e)
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
            Cursor cursor = null;
            var drawing = DrawingOwner;
            if (Eligible(e.Location))
            {
                var p = _canvas.Viewport.UnprojectPoint(e.Location);
                if (drawing != null)
                {
                    if (!OverNative(_canvas.Document,p))
                    {
                        var textHit=drawing.DrawMode==5 ? Hit(p,false) : default;
                        bool text=textHit.owner!=null && ContainsTextAt(textHit.owner.Shapes[textHit.shape],p);
                        cursor=text ? Cursors.IBeam : _cursors.Get(drawing.DrawMode,_canvas.DeviceDpi);
                    }
                } // A press always draws.
                else
                {
                    var hit = Hit(p,true);
                    if (hit.owner == null && !OverNative(_canvas.Document,p)) hit = Hit(p,false);
                    if (hit.owner != null)
                    {
                        hit.owner.HoveredShapeIndex=hit.shape; hit.owner.HoveredPointIndex=hit.handle;
                        cursor = hit.handle >= 0 ? Cursors.SizeAll : Instances.CursorServer.Cursor("GH_HandOpen");
                    }
                }
            }
            // Only touch the cursor for HopperDraw targets; GH's own wire/slider/selection cursors stay intact.
            // GH's MouseMove runs first and may already have assigned its own cursor; never reset that one.
            if (cursor != null) AssignCursor(cursor);
            else ResetCursor();
            if (!_refreshingCursor && (drawing != null || previous.Any(pair=>pair.Value != (pair.Key.HoveredShapeIndex,pair.Key.HoveredPointIndex)))) _canvas.Invalidate();
        }
        private static bool ContainsTextAt(DrawShape shape,PointF p) => shape is TextShape text && AnnotationGeometry.Hit(text,p,0,0) || shape is DrawShapeGroup group && group.Children.Any(c=>ContainsTextAt(c,p));
        private void PaintHud(GH_Canvas canvas)
        {
            UpdateHints();
            // Selection changes and resizes reach the bar from here; it is never synchronised inside a paint.
            if (BarSignature() != _barSignature) QueueBar();
            PaintEditorFrame(canvas.Graphics);
            float clearance = _bar.Visible ? canvas.ClientSize.Height - _bar.Top : 8 * canvas.DeviceDpi / 96f;
            AnnotationFeedback.Hint(canvas,DrawingOwner,_hints,_text!=null,clearance);
        }
    }
}
