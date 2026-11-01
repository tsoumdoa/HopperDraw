using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Rhino;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.Kernel.Undo;

namespace hopperdraw
{
    public class HopperDraw : GH_Component
    {
        private bool _isActivated;
        private bool _drawingModeEnabled;
        private readonly DrawingKeyGesture _drawingKeyGesture = new DrawingKeyGesture();
        private bool _acceptDrawingMouseUp;
        private static HopperDraw _capturingOwner;

        private Point3d _drawStart;
        private Point3d _drawEnd;
        private bool _isDrawing;
        private bool _isDragging;
        private int _dragShapeIndex = -1;
        private int _dragPointIndex = -1;
        private Point3d _dragOffset;
        private bool _isFrameDragging = false;
        private Point3d _dragFrameOffset;
        private double _dragAspectRatio = 1.0;

        private DateTime _lastClickTime = DateTime.MinValue;
        private Point3d _lastClickPosition = Point3d.Unset;

        private GH_Canvas _canvas;
        private bool _eventsRegistered;
        private DrawingCanvasValidator _drawingCanvasValidator;
        private CanvasDrawingShortcut.Registration _drawingShortcut;

        private float _lineThickness = 8f;
        private Color _borderColor = Color.Black;
        private int _drawMode = 0;
        private int _frameCornerCount = 0;
        private Point3d _frameFirstCorner;
        private float _currentThicknessMultiplier = 1.0f;
        private Color? _pendingColorOverride;
        private System.Drawing.Drawing2D.DashStyle _currentLineType = System.Drawing.Drawing2D.DashStyle.Solid;
        private System.Drawing.Drawing2D.DashStyle _baseLineType = System.Drawing.Drawing2D.DashStyle.Solid;
        private bool _hasPendingLineTypeOverride;

        public System.Drawing.Drawing2D.DashStyle CurrentLineType => _currentLineType;

        private List<Point3d> _currentPoints = new List<Point3d>();

        private Point3d _windowSelectStart;
        private bool _isWindowSelecting = false;
        private bool _additiveSelection = false;
        private Point3d _multiDragStart;
        private bool _isMultiDragging = false;
        private Point3d _clickStartPt = Point3d.Unset;
        private List<IGH_DocumentObject> _selectedGHObjects = new List<IGH_DocumentObject>();

        private static int _shapeIdCounter = 1;

        public List<DrawShape> Shapes { get; set; } = new List<DrawShape>();
        public Color BorderColor { get; set; } = Color.Black;
        private float _validatedThickness = 8f;
        public float LineThickness { get => _validatedThickness; set => _validatedThickness = StyleValidation.Width(value); }
        public int DrawOrder { get; set; } = 1;
        public bool Visible { get; set; } = true;

        public List<int> SelectedShapeIndices { get; set; } = new List<int>();
        public int SelectedPointIndex { get; set; } = -1;
        public int HoveredShapeIndex { get; set; } = -1;
        public int HoveredPointIndex { get; set; } = -1;

        private const float HandleSize = 8f;
        private const float HitTolerance = 15f;

        public HopperDraw()
            : base("Hopper Draw", "HopperDraw", "Press D twice to enter drawing mode, then click empty canvas. Hold Shift for orthogonal snapping. Press Escape to exit.", "Params", "Util")
        {
            CreateAttributes();
        }

        public override Guid ComponentGuid => new Guid("D2E3F4A5-B6C7-8901-2345-67890ABCDEF0");

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    var stream = assembly.GetManifestResourceStream("hopperborder.icon.png");
                    if (stream != null)
                    {
                        return new Bitmap(stream);
                    }
                }
                catch
                {
                }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddBooleanParameter("Active", "A", "Enable border drawing", GH_ParamAccess.item, true);
            pManager.AddBooleanParameter("Show", "S", "Display borders on canvas", GH_ParamAccess.item, true);
            pManager.AddIntegerParameter("DrawMode", "M", "Drawing mode: 0=Line, 1=Polyline, 2=Frame, 3=Curve", GH_ParamAccess.item, 0);
            pManager.AddColourParameter("Color", "C", "Default border color. Use the component menu for per-shape color overrides", GH_ParamAccess.item, Color.Black);
            pManager.AddNumberParameter("Thickness", "T", "Base thickness. Use the component menu for per-shape thickness", GH_ParamAccess.item, 8.0);
            pManager.AddIntegerParameter("LineType", "L", "Line type: 0=Solid, 1=Dash, 2=Dot, 3=DashDot. Use the component menu for per-shape line type", GH_ParamAccess.item, 0);
            pManager.AddIntegerParameter("DrawOrder", "O", "0=Below components, 1=Above components", GH_ParamAccess.item, 1);
        }

        protected override void AfterSolveInstance()
        {
            var drawModeParam = Params.Input[2] as Param_Integer;
            if (drawModeParam != null)
            {
                drawModeParam.AddNamedValue("Line", 0);
                drawModeParam.AddNamedValue("Polyline", 1);
                drawModeParam.AddNamedValue("Frame", 2);
                drawModeParam.AddNamedValue("Curve", 3);
            }

            var lineTypeParam = Params.Input[5] as Param_Integer;
            if (lineTypeParam != null)
            {
                lineTypeParam.AddNamedValue("Solid", 0);
                lineTypeParam.AddNamedValue("Dash", 1);
                lineTypeParam.AddNamedValue("Dot", 2);
                lineTypeParam.AddNamedValue("DashDot", 3);
                lineTypeParam.AddNamedValue("DashDotDot", 4);
            }

            var drawOrderParam = Params.Input[6] as Param_Integer;
            if (drawOrderParam != null)
            {
                drawOrderParam.AddNamedValue("Below", 0);
                drawOrderParam.AddNamedValue("Above", 1);
            }
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[HopperDraw SolveInstance] Called. _isActivated={_isActivated}");
#endif
            ReportLoadWarning();
            bool active = true;
            if (!DA.GetData(0, ref active)) return;

            bool show = true;
            DA.GetData(1, ref show);

            int drawMode = 0;
            DA.GetData(2, ref drawMode);

            Color color = Color.Black;
            DA.GetData(3, ref color);

            double thickness = 8.0;
            DA.GetData(4, ref thickness);

            int lineType = 0;
            DA.GetData(5, ref lineType);

            int drawOrder = 1;
            DA.GetData(6, ref drawOrder);

            drawMode = ValidateRange("DrawMode", drawMode, 0, 3);
            lineType = ValidateRange("LineType", lineType, 0, 4);
            drawOrder = ValidateRange("DrawOrder", drawOrder, 0, 1);
            if (!StyleValidation.ValidWidth(thickness))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Thickness must be finite and between 0.1 and 1000 canvas units; clamped/defaulted.");
            thickness = StyleValidation.Width(thickness);
            _borderColor = color;
            _lineThickness = (float)thickness;
            if (_drawMode != drawMode && (_isDrawing || _drawingModeEnabled)) CancelDrawing();
            _drawMode = drawMode;
            _baseLineType = (System.Drawing.Drawing2D.DashStyle)lineType;
            if (!_hasPendingLineTypeOverride) _currentLineType = _baseLineType;

            if (_drawMode != 1 && _drawMode != 3)
            {
                _currentPoints.Clear();
            }

            _isActivated = active;

            if (_isActivated)
            {
                if (_eventsRegistered && !ReferenceEquals(_canvas, Grasshopper.Instances.ActiveCanvas))
                {
                    UnregisterCanvasEvents();
                    CancelDrawing();
                }
                if (!_eventsRegistered) RegisterCanvasEvents();
            }
            else
            {
                UnregisterCanvasEvents();
                CancelDrawing();
                ClearSelection();
            }

#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[HopperDraw SolveInstance] Updating. Shapes count = {Shapes.Count}");
#endif
            Visible = show && !this.Locked;
            if (!Visible && (_isDrawing || _drawingModeEnabled)) CancelDrawing();
            BorderColor = _borderColor;
            LineThickness = _lineThickness;
            DrawOrder = drawOrder;

            RequestCanvasRedraw();
        }

        private int ValidateRange(string name, int value, int minimum, int maximum)
        {
            if (value < minimum || value > maximum)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, name + " must be between " + minimum + " and " + maximum + "; clamped.");
            return StyleValidation.Clamp(value, minimum, maximum);
        }

        private bool _dragUndoRecorded;

        private bool RecordShapeUndo(string name, bool includeMovedObjects = false)
        {
            if (!CanEditShapes) return false;
            var document = OnPingDocument();
            if (document == null) return true; // Detached model has no host undo server (also used by tests).
            try
            {
                var record = new GH_UndoRecord("HopperDraw: " + name);
                record.AddAction(new ShapeEditUndoAction(this,
                    includeMovedObjects ? _selectedGHObjects : Enumerable.Empty<IGH_DocumentObject>()));
                if (document.UndoUtil.RecordEvent(record) == Guid.Empty)
                    throw new InvalidOperationException("Grasshopper rejected the undo record.");
                return true;
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Edit cancelled: could not record undo. " + ex.Message);
                return false;
            }
        }

        private bool EnsureDragUndo()
        {
            if (_dragUndoRecorded) return true;
            if (!RecordShapeUndo("Move annotations", _isMultiDragging)) return false;
            _dragUndoRecorded = true;
            return true;
        }

        private void EndShapeGesture()
        {
            _dragUndoRecorded = false;
            _isDragging = _isMultiDragging = _isFrameDragging = false;
            _dragShapeIndex = _dragPointIndex = -1;
            _dragOffset = _dragFrameOffset = _multiDragStart = Point3d.Unset;
            _dragAspectRatio = 1;
            _clickStartPt = Point3d.Unset;
            _isWindowSelecting = false;
            ClearCapturedGHObjects();
        }

        internal void RestoreShapes(List<DrawShape> shapes)
        {
            CancelDrawing();
            ClearSelection();
            Shapes = shapes;
            RefreshShapeWarnings();
            MarkDocumentModified();
            _canvas?.Invalidate();
            Instances.ActiveCanvas?.Invalidate();
        }

        private static bool ContainsOpaque(DrawShape shape) => shape is OpaqueShape ||
            shape is DrawShapeGroup group && group.Children.Any(ContainsOpaque);

        private void RefreshShapeWarnings()
        {
            if (_opaquePayload != null) return;
            var messages = new List<string>();
            CollectDiagnostics(Shapes, messages);
            string warning = messages.Count == 0 ? null : string.Join(" ", messages.Distinct());
            if (_loadWarning != warning)
            {
                var levels = new[] { GH_RuntimeMessageLevel.Remark, GH_RuntimeMessageLevel.Warning, GH_RuntimeMessageLevel.Error };
                var retained = levels.SelectMany(level => RuntimeMessages(level)
                    .Where(message => level != GH_RuntimeMessageLevel.Warning || message != _loadWarning)
                    .Select(message => new KeyValuePair<GH_RuntimeMessageLevel, string>(level, message))).ToArray();
                ClearRuntimeMessages();
                foreach (var message in retained) AddRuntimeMessage(message.Key, message.Value);
                _loadWarning = warning;
            }
            ReportLoadWarning();
        }

        private static void CollectDiagnostics(IEnumerable<DrawShape> shapes, List<string> messages)
        {
            foreach (var shape in shapes)
                if (shape is OpaqueShape opaque) messages.Add(opaque.Diagnostic);
                else if (shape is DrawShapeGroup group) CollectDiagnostics(group.Children, messages);
        }

        internal void SetSelectedColor(Color color)
        {
            if (!HasSelection || !RecordShapeUndo("Change annotation color")) return;
            foreach (var leaf in SelectedLeaves()) leaf.OverrideColor = color;
            MarkDocumentModified();
        }

        private static IEnumerable<Point3d> GeometryPoints(DrawShape shape)
        {
            if (shape is LineShape line) return new[] { line.Start, line.End };
            if (shape is FrameShape frame) return new[] { frame.TopLeft, frame.TopRight, frame.BottomRight, frame.BottomLeft };
            if (shape is PolylineShape poly) return poly.Points;
            if (shape is CurveShape curve) return curve.Points;
            if (shape is DrawShapeGroup group) return group.Children.SelectMany(GeometryPoints);
            return Enumerable.Empty<Point3d>();
        }

        private void DragShapeTo(Point3d point)
        {
            if (_dragShapeIndex < 0 || _dragShapeIndex >= Shapes.Count) return;
            var shape = Shapes[_dragShapeIndex];
            if (_dragPointIndex < 0)
            {
                var offset = _isFrameDragging ? _dragFrameOffset : _dragOffset;
                double dx = point.X - offset.X, dy = point.Y - offset.Y;
                if ((dx == 0 && dy == 0) || GeometryPoints(shape).Any(p =>
                    !ShapeSerializer.ValidCoordinate(p.X + dx) || !ShapeSerializer.ValidCoordinate(p.Y + dy))) return;
                if (!EnsureDragUndo()) return;
                shape.Move(dx, dy);
                _dragOffset = _dragFrameOffset = point;
                MarkDocumentModified();
                _canvas?.Invalidate();
                return;
            }
            var proposal = ShapeEditUndoAction.CloneForSnapshot(shape);
            ApplyDragGeometry(proposal, point);
            var before = GeometryPoints(shape);
            var after = GeometryPoints(proposal).ToArray();
            if (before.SequenceEqual(after) || after.Any(p => !ShapeSerializer.ValidCoordinate(p.X) || !ShapeSerializer.ValidCoordinate(p.Y))) return;
            if (!EnsureDragUndo()) return;
            ApplyDragGeometry(shape, point);
            _dragOffset = _dragFrameOffset = point;
            MarkDocumentModified();
            _canvas?.Invalidate();
        }

        private void ApplyDragGeometry(DrawShape shape, Point3d point)
        {
            if (_isFrameDragging && shape is FrameShape frame && _dragPointIndex >= 0)
            {
                var fixedCorner = GetFrameOppositeCorner(frame, _dragPointIndex);
                if (ShiftPressed && _dragAspectRatio > 0) point = GeometryUtilities.SnapToAspectRatio(fixedCorner, point, _dragAspectRatio);
                switch (_dragPointIndex)
                {
                    case 0: frame.TopLeft = point; frame.TopRight = new Point3d(fixedCorner.X, point.Y, 0); frame.BottomLeft = new Point3d(point.X, fixedCorner.Y, 0); frame.BottomRight = fixedCorner; break;
                    case 1: frame.TopRight = point; frame.TopLeft = new Point3d(fixedCorner.X, point.Y, 0); frame.BottomRight = new Point3d(point.X, fixedCorner.Y, 0); frame.BottomLeft = fixedCorner; break;
                    case 2: frame.BottomRight = point; frame.BottomLeft = new Point3d(fixedCorner.X, point.Y, 0); frame.TopRight = new Point3d(point.X, fixedCorner.Y, 0); frame.TopLeft = fixedCorner; break;
                    case 3: frame.BottomLeft = point; frame.BottomRight = new Point3d(fixedCorner.X, point.Y, 0); frame.TopLeft = new Point3d(point.X, fixedCorner.Y, 0); frame.TopRight = fixedCorner; break;
                }
            }
            else if (_dragPointIndex >= 0)
            {
                if (shape is LineShape line && ShiftPressed)
                    point = GeometryUtilities.SnapAngle(_dragPointIndex == 0 ? line.End : line.Start, point);
                shape.MovePoint(_dragPointIndex, point);
            }
            else
            {
                var offset = _isFrameDragging ? _dragFrameOffset : _dragOffset;
                shape.Move(point.X - offset.X, point.Y - offset.Y);
            }
        }

        private void MarkDocumentModified()
        {
            RefreshShapeWarnings();
            RequestCanvasRedraw();
            var doc = OnPingDocument();
            if (doc != null)
            {
                doc.IsModified = true;
            }
        }

        private void RegisterCanvasEvents()
        {
            if (_eventsRegistered) return;

            _canvas = Grasshopper.Instances.ActiveCanvas;
            if (_canvas != null)
            {
                if (_drawingCanvasValidator == null)
                    _drawingCanvasValidator = new DrawingCanvasValidator(this);
                _canvas.AddValidator(_drawingCanvasValidator);
                _canvas.MouseDown += Canvas_MouseDown;
                _canvas.MouseMove += Canvas_MouseMove;
                _canvas.MouseUp += Canvas_MouseUp;
                _canvas.KeyDown += Canvas_KeyDown;
                _canvas.KeyUp += Canvas_KeyUp;
                _canvas.KeyPress += Canvas_KeyPress;
                _canvas.LostFocus += Canvas_LostFocus;
                _canvas.MouseCaptureChanged += Canvas_MouseCaptureChanged;
                _canvas.DocumentChanged += Canvas_DocumentChanged;
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    _drawingShortcut = CanvasDrawingShortcut.Register(_canvas, CanActivateDrawingShortcut, EnterDrawingMode);
                _eventsRegistered = true;

            }
        }

        private void UnregisterCanvasEvents()
        {
            if (!_eventsRegistered) return;
            _drawingKeyGesture.Reset(releaseKey: true);
            _drawingShortcut?.Dispose();
            _drawingShortcut = null;

            if (_canvas != null)
            {
                _canvas.RemoveValidator(_drawingCanvasValidator);
                _canvas.MouseDown -= Canvas_MouseDown;
                _canvas.MouseMove -= Canvas_MouseMove;
                _canvas.MouseUp -= Canvas_MouseUp;
                _canvas.KeyDown -= Canvas_KeyDown;
                _canvas.KeyUp -= Canvas_KeyUp;
                _canvas.KeyPress -= Canvas_KeyPress;
                _canvas.LostFocus -= Canvas_LostFocus;
                _canvas.MouseCaptureChanged -= Canvas_MouseCaptureChanged;
                _canvas.DocumentChanged -= Canvas_DocumentChanged;
            }
            _eventsRegistered = false;
            _canvas = null;

        }

        private void Canvas_DocumentChanged(object sender, GH_CanvasDocumentChangedEventArgs e)
        {
            if (!ReferenceEquals(sender, _canvas) ||
                !ReferenceEquals(e.OldDocument, OnPingDocument())) return;

            CancelDrawing();
            _canvas?.Invalidate();
        }

        private bool CanHandleCanvasEvent(object sender)
        {
            return CanEditShapes && _isActivated && !Locked && Visible &&
                   ReferenceEquals(sender, _canvas) &&
                   ReferenceEquals(_canvas?.Document, OnPingDocument()) &&
                   IsInputOwner();
        }

        private bool CanActivateDrawingShortcut()
        {
            if (!ReferenceEquals(_canvas, Instances.ActiveCanvas) || !CanHandleCanvasEvent(_canvas) ||
                !_canvas.ModifiersEnabled || _canvas.ActiveInteraction != null ||
                _canvas.ActiveWidget != null || _canvas.ActiveObject != null ||
                _drawMode < 0 || _drawMode > 3) return false;

            // Respect custom Grasshopper navigation and menu bindings for plain D.
            if (GH_Canvas.NavigationPanLeft == Keys.D || GH_Canvas.NavigationPanRight == Keys.D ||
                GH_Canvas.NavigationPanUp == Keys.D || GH_Canvas.NavigationPanDown == Keys.D ||
                GH_Canvas.NavigationZoomIn == Keys.D || GH_Canvas.NavigationZoomOut == Keys.D) return false;
            var editor = _canvas.FindForm();
            return editor == null || !HasDrawingShortcut(editor.Controls);
        }

        private static bool HasDrawingShortcut(Control.ControlCollection controls)
        {
            foreach (Control control in controls)
            {
                if (control is MenuStrip menu && HasDrawingShortcut(menu.Items)) return true;
                if (control.HasChildren && HasDrawingShortcut(control.Controls)) return true;
            }
            return false;
        }

        private static bool HasDrawingShortcut(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                if (!(item is ToolStripMenuItem menu)) continue;
                if (menu.ShortcutKeys == Keys.D || HasDrawingShortcut(menu.DropDownItems)) return true;
            }
            return false;
        }

        private bool IsInputOwner()
        {
            if (_capturingOwner != null && ReferenceEquals(_capturingOwner.OnPingDocument(), _canvas?.Document))
            {
                if (!_capturingOwner._isActivated || _capturingOwner.Locked ||
                    !_capturingOwner.Visible || !_capturingOwner._drawingModeEnabled)
                    _capturingOwner.CancelDrawing();
                else
                    return ReferenceEquals(_capturingOwner, this);
            }
            var controllers = _canvas?.Document?.Objects.OfType<HopperDraw>()
                .Where(controller => controller._isActivated && !controller.Locked && controller.Visible)
                .ToList();
            if (controllers == null || controllers.Count == 0) return false;
            var selected = controllers.FirstOrDefault(controller => controller.Attributes?.Selected == true);
            var editing = controllers.FirstOrDefault(controller => controller.HasSelection);
            return ReferenceEquals(selected ?? editing ?? controllers[0], this);
        }

        private bool IsOverGrasshopperObject(Point3d point)
        {
            if (_canvas?.Document == null) return false;
            var location = new PointF((float)point.X, (float)point.Y);
            foreach (var obj in _canvas.Document.Objects)
            {
                if (obj?.Attributes == null) continue;
                var bounds = obj.Attributes.Bounds;
                if (obj is Grasshopper.Kernel.Special.GH_Group)
                {
                    if (bounds.Contains(location) &&
                        (location.X - bounds.Left < 12 || bounds.Right - location.X < 12 ||
                         location.Y - bounds.Top < 20 || bounds.Bottom - location.Y < 12))
                        return true;
                    continue;
                }
                bounds.Inflate(12f, 12f);
                if (bounds.Contains(location))
                    return true;
            }
            return false;
        }

        internal sealed class DrawingCanvasValidator : GH_CanvasValidator
        {
            private readonly HopperDraw _owner;

            internal DrawingCanvasValidator(HopperDraw owner) { _owner = owner; }

            public override bool CanShowComponentSearchBox(PointF point)
            {
                return !_owner._drawingModeEnabled || !_owner.CanHandleCanvasEvent(Canvas) ||
                    (Control.ModifierKeys & (Keys.Control | Keys.Alt)) != Keys.None ||
                    _owner.IsOverGrasshopperObject(new Point3d(point.X, point.Y, 0));
            }
        }

        private void CancelDrawing()
        {
            EndShapeGesture();
            if (ReferenceEquals(_capturingOwner, this)) _capturingOwner = null;
            _drawingModeEnabled = false;
            _drawingKeyGesture.Reset();
            _drawingShortcut?.Reset();
            _currentPoints.Clear();
            _isDrawing = false;
            _frameCornerCount = 0;
            _acceptDrawingMouseUp = false;
            ResetNextShapeOverrides();
            _clickStartPt = Point3d.Unset;
            _isWindowSelecting = false;
            _isDragging = false;
            _isMultiDragging = false;
            _isFrameDragging = false;
            _dragShapeIndex = -1;
            _dragPointIndex = -1;
            ClearCapturedGHObjects();
        }

        internal void EnterDrawingMode()
        {
            EndShapeGesture();
            if (!_isActivated || Locked || !Visible || _canvas?.Document == null ||
                !ReferenceEquals(_canvas.Document, OnPingDocument()) ||
                _drawMode < 0 || _drawMode > 3) return;

            if (_capturingOwner != null && !ReferenceEquals(_capturingOwner, this))
                _capturingOwner.CancelDrawing();
            if (_drawingModeEnabled) return;
            ClearSelection();
            _clickStartPt = Point3d.Unset;
            _isDragging = false;
            _isMultiDragging = false;
            _isWindowSelecting = false;
            ClearCapturedGHObjects();
            _lastClickTime = DateTime.MinValue;
            _lastClickPosition = Point3d.Unset;
            _drawingModeEnabled = true;
            _capturingOwner = this;
            _canvas.Focus();
            _canvas.Invalidate();
        }

        private void Canvas_MouseCaptureChanged(object sender, EventArgs e)
        {
            if (_canvas?.Capture != true) EndShapeGesture();
        }

        private void Canvas_LostFocus(object sender, EventArgs e)
        {
            EndShapeGesture();
            _drawingKeyGesture.Reset(releaseKey: true);
            _drawingShortcut?.Reset();
        }

        private void Canvas_KeyUp(object sender, KeyEventArgs e)
        {
            _drawingKeyGesture.KeyUp(e.KeyCode);
            if (CanHandleCanvasEvent(sender) && e.KeyCode == Keys.ShiftKey)
                _canvas?.Invalidate();
        }

        private void Canvas_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (_drawingShortcut != null) return;
            if (CanHandleCanvasEvent(sender) && Control.ModifierKeys == Keys.None &&
                char.ToUpperInvariant(e.KeyChar) == 'D')
                e.Handled = true;
        }

        private void ResetNextShapeOverrides()
        {
            _pendingColorOverride = null;
            _currentThicknessMultiplier = 1.0f;
            _hasPendingLineTypeOverride = false;
            _currentLineType = _baseLineType;
        }

        private Point3d ScreenToCanvas(System.Drawing.Point screenPoint)
        {
            if (_canvas?.Viewport != null)
            {
                var pt = _canvas.Viewport.UnprojectPoint(screenPoint);
                return new Point3d(pt.X, pt.Y, 0);
            }
            return new Point3d(screenPoint.X, screenPoint.Y, 0);
        }

        private void Canvas_MouseDown(object sender, MouseEventArgs e)
        {
            // A mouse gesture between D presses starts a separate activation gesture.
            _drawingKeyGesture.Reset();
            _drawingShortcut?.Reset();
            if (!CanHandleCanvasEvent(sender))
                return;

            if (e.Button != MouseButtons.Left)
                return;

            EndShapeGesture();
            _acceptDrawingMouseUp = false;
            var pt = ScreenToCanvas(e.Location);
            if (IsOverGrasshopperObject(pt)) return;

            var modifiers = Control.ModifierKeys & (Keys.Control | Keys.Alt | Keys.Shift);
            if ((modifiers & (Keys.Control | Keys.Alt)) != Keys.None) return;
            bool startDrawing = _drawingModeEnabled && !_isDrawing;

            var timeSinceLastClick = DateTime.Now - _lastClickTime;
            bool isDoubleClick = (timeSinceLastClick.TotalMilliseconds < 500) &&
                                (_lastClickPosition.DistanceTo(pt) < 10);

            bool shiftPressed = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;

            if (_drawMode == 0)
            {
                if (_isDrawing)
                {
                    Point3d endPt = shiftPressed ? GeometryUtilities.SnapOrthogonal(_drawStart, pt) : pt;
                    var lineShape = new LineShape(_drawStart, endPt);
                    lineShape.ThicknessMultiplier = _currentThicknessMultiplier;
                    lineShape.OverrideColor = _pendingColorOverride;
                    lineShape.LineType = _currentLineType;
                    if (!RecordShapeUndo("Create annotation")) return;
                    Shapes.Add(lineShape);
                    MarkDocumentModified();
                    _isDrawing = false;
                    ResetNextShapeOverrides();
                    RequestCanvasRedraw();
                    return;
                }
                else if (startDrawing)
                {
                    _drawStart = pt;
                    _drawEnd = pt;
                    _isDrawing = true;
                    _capturingOwner = this;
                    _lastClickTime = DateTime.Now;
                    _lastClickPosition = pt;
                    RequestCanvasRedraw();
                    return;
                }
            }
            else if (_drawMode == 1)
            {
                if (!_isDrawing || _currentPoints.Count == 0)
                {
                    if (startDrawing)
                    {
                        _currentPoints.Add(pt);
                        _isDrawing = true;
                        _capturingOwner = this;
                        _acceptDrawingMouseUp = true;
                        _lastClickTime = DateTime.Now;
                        _lastClickPosition = pt;
                        RequestCanvasRedraw();
                        return;
                    }
                }
                else if (_isDrawing && _currentPoints.Count >= 2 && isDoubleClick)
                {
                    var polyShape = new PolylineShape(new List<Point3d>(_currentPoints), true);
                    polyShape.ThicknessMultiplier = _currentThicknessMultiplier;
                    polyShape.OverrideColor = _pendingColorOverride;
                    polyShape.LineType = _currentLineType;
                    if (!RecordShapeUndo("Create annotation")) return;
                    Shapes.Add(polyShape);
                    MarkDocumentModified();
                    _currentPoints.Clear();
                    _isDrawing = false;
                    ResetNextShapeOverrides();
                    _lastClickTime = DateTime.Now;
                    _lastClickPosition = pt;
                    RequestCanvasRedraw();
                    return;
                }
            }
            else if (_drawMode == 3)
            {
                if (!_isDrawing || _currentPoints.Count == 0)
                {
                    if (startDrawing)
                    {
                        _currentPoints.Add(pt);
                        _isDrawing = true;
                        _capturingOwner = this;
                        _acceptDrawingMouseUp = true;
                        _lastClickTime = DateTime.Now;
                        _lastClickPosition = pt;
                        RequestCanvasRedraw();
                        return;
                    }
                }
                else if (_isDrawing && _currentPoints.Count >= 2 && isDoubleClick)
                {
                    var curveShape = new CurveShape(new List<Point3d>(_currentPoints), true);
                    curveShape.ThicknessMultiplier = _currentThicknessMultiplier;
                    curveShape.OverrideColor = _pendingColorOverride;
                    curveShape.LineType = _currentLineType;
                    if (!RecordShapeUndo("Create annotation")) return;
                    Shapes.Add(curveShape);
                    MarkDocumentModified();
                    _currentPoints.Clear();
                    _isDrawing = false;
                    ResetNextShapeOverrides();
                    _lastClickTime = DateTime.Now;
                    _lastClickPosition = pt;
                    RequestCanvasRedraw();
                    return;
                }
            }
            else if (_drawMode == 2)
            {
                if (_frameCornerCount == 1)
                {
                    Point3d finalPt = pt;
                    if (shiftPressed)
                    {
                        double dx = pt.X - _frameFirstCorner.X;
                        double dy = pt.Y - _frameFirstCorner.Y;
                        double size = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        finalPt = new Point3d(
                            _frameFirstCorner.X + size * Math.Sign(dx),
                            _frameFirstCorner.Y + size * Math.Sign(dy),
                            0);
                    }
                    _frameCornerCount = 0;
                    _isDrawing = false;
                    CreateFrame(_frameFirstCorner, finalPt);
                    _lastClickTime = DateTime.Now;
                    _lastClickPosition = pt;
                    RequestCanvasRedraw();
                    return;
                }
                else if (startDrawing)
                {
                    _frameFirstCorner = pt;
                    _frameCornerCount = 1;
                    _isDrawing = true;
                    _capturingOwner = this;
                    _lastClickTime = DateTime.Now;
                    _lastClickPosition = pt;
                    RequestCanvasRedraw();
                    return;
                }
            }

            if ((_drawMode == 1 || _drawMode == 3) && _isDrawing)
            {
                _acceptDrawingMouseUp = true;
                _lastClickTime = DateTime.Now;
                _lastClickPosition = pt;
                return;
            }

            var (shapeIdx, pointIdx) = HitTest(new PointF((float)pt.X, (float)pt.Y));

            if (shapeIdx >= 0)
            {
                bool isAlreadySelected = SelectedShapeIndices.Contains(shapeIdx);
                bool multipleShapesSelected = SelectedShapeIndices.Count > 1;
                
                if (shiftPressed)
                {
                    if (isAlreadySelected)
                    {
                        SelectedShapeIndices.Remove(shapeIdx);
                    }
                    else
                    {
                        SelectedShapeIndices.Add(shapeIdx);
                    }
                    SelectedPointIndex = pointIdx;
                }
                else
                {
                    if (multipleShapesSelected && isAlreadySelected && pointIdx < 0)
                    {
                        SelectedPointIndex = pointIdx;
                        _multiDragStart = pt;
                        _isMultiDragging = true;
                        CaptureSelectedGHObjects();
                    }
                    else if (isAlreadySelected && pointIdx < 0)
                    {
                        SelectedPointIndex = pointIdx;
                        _dragShapeIndex = shapeIdx;
                        _dragPointIndex = pointIdx;

                        var shape = Shapes[shapeIdx];
                        if (shape is FrameShape frame)
                        {
                            _isFrameDragging = true;
                            _dragFrameOffset = pt;
                            double w = Math.Abs(frame.TopRight.X - frame.TopLeft.X);
                            double h = Math.Abs(frame.TopLeft.Y - frame.BottomLeft.Y);
                            _dragAspectRatio = (h > 0) ? w / h : 1.0;
                        }
                        else
                        {
                            _isFrameDragging = false;
                            _dragOffset = pt;
                        }
                        _isDragging = true;
                    }
                    else
                    {
                        SelectedShapeIndices.Clear();
                        SelectedShapeIndices.Add(shapeIdx);
                        SelectedPointIndex = pointIdx;
                        _dragShapeIndex = shapeIdx;
                        _dragPointIndex = pointIdx;

                        var shape = Shapes[shapeIdx];
                        if (shape is FrameShape frame)
                        {
                            _isFrameDragging = true;
                            _dragFrameOffset = pt;
                            double w = Math.Abs(frame.TopRight.X - frame.TopLeft.X);
                            double h = Math.Abs(frame.TopLeft.Y - frame.BottomLeft.Y);
                            _dragAspectRatio = (h > 0) ? w / h : 1.0;
                        }
                        else
                        {
                            _isFrameDragging = false;
                            _dragOffset = pt;
                        }
                        _isDragging = true;
                    }
                }
            }
            else if (HasSelection && HasSelectedGHObjects())
            {
                _multiDragStart = pt;
                _isMultiDragging = true;
                CaptureSelectedGHObjects();
            }
            else if (Shapes.Count > 0)
            {
                _clickStartPt = pt;
                _additiveSelection = shiftPressed;
            }

            RequestCanvasRedraw();
        }

        private void CreateFrame(Point3d corner1, Point3d corner2)
        {
            Point3d topLeft = new Point3d(Math.Min(corner1.X, corner2.X), Math.Max(corner1.Y, corner2.Y), 0);
            Point3d bottomRight = new Point3d(Math.Max(corner1.X, corner2.X), Math.Min(corner1.Y, corner2.Y), 0);
            Point3d topRight = new Point3d(bottomRight.X, topLeft.Y, 0);
            Point3d bottomLeft = new Point3d(topLeft.X, bottomRight.Y, 0);

            var frameShape = new FrameShape(topLeft, topRight, bottomRight, bottomLeft);
            frameShape.ThicknessMultiplier = _currentThicknessMultiplier;
            frameShape.OverrideColor = _pendingColorOverride;
            frameShape.LineType = _currentLineType;
            if (!RecordShapeUndo("Create frame")) return;
            Shapes.Add(frameShape);
            MarkDocumentModified();
            ResetNextShapeOverrides();
            RequestCanvasRedraw();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!CanHandleCanvasEvent(sender))
                return;

            var pt = ScreenToCanvas(e.Location);

            if (_isDrawing)
            {
                _drawEnd = pt;
                _canvas?.Invalidate();
            }
            else if (_isDragging && _dragShapeIndex >= 0)
            {
                DragShapeTo(pt);
            }
            else if (_isMultiDragging && HasSelection)
            {
                double dx = pt.X - _multiDragStart.X, dy = pt.Y - _multiDragStart.Y;
                if ((dx == 0 && dy == 0) || !EnsureDragUndo()) return;
                foreach (int index in SelectedShapeIndices) Shapes[index].Move(dx, dy);
                foreach (var obj in _selectedGHObjects)
                {
                    var attributes = obj.Attributes;
                    var pivot = attributes.Pivot;
                    attributes.Pivot = new PointF(pivot.X + (float)dx, pivot.Y + (float)dy);
                    attributes.ExpireLayout();
                    attributes.PerformLayout();
                }
                _multiDragStart = pt;
                MarkDocumentModified();
                _canvas?.Invalidate();
            }
            else if (_isWindowSelecting)
            {
                _canvas?.Invalidate();
            }
            else if (_clickStartPt.IsValid)
            {
                double dx = Math.Abs(pt.X - _clickStartPt.X);
                double dy = Math.Abs(pt.Y - _clickStartPt.Y);
                if (dx > 5 || dy > 5)
                {
                    _windowSelectStart = _clickStartPt;
                    _isWindowSelecting = true;
                    if (!_additiveSelection)
                    {
                        ClearSelection();
                    }
                }
                else
                {
                    var (shapeIdx, pointIdx) = HitTest(new PointF((float)pt.X, (float)pt.Y));
                    HoveredShapeIndex = shapeIdx >= 0 ? shapeIdx : -1;
                    HoveredPointIndex = pointIdx >= 0 ? pointIdx : -1;
                }
                _canvas?.Invalidate();
            }
            else
            {
                var (shapeIdx, pointIdx) = HitTest(new PointF((float)pt.X, (float)pt.Y));
                int hoveredShape = shapeIdx >= 0 ? shapeIdx : -1;
                int hoveredPoint = pointIdx >= 0 ? pointIdx : -1;
                if (HoveredShapeIndex != hoveredShape || HoveredPointIndex != hoveredPoint)
                {
                    HoveredShapeIndex = hoveredShape;
                    HoveredPointIndex = hoveredPoint;
                    _canvas?.Invalidate();
                }
            }
        }

        private void Canvas_MouseUp(object sender, MouseEventArgs e)
        {
            if (!CanHandleCanvasEvent(sender) || e.Button != MouseButtons.Left)
                return;

            bool shiftPressed = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;

            if (_acceptDrawingMouseUp && _isDrawing && (_drawMode == 1 || _drawMode == 3) &&
                !IsOverGrasshopperObject(ScreenToCanvas(e.Location)) &&
                (Control.ModifierKeys & (Keys.Control | Keys.Alt)) == Keys.None)
            {
                var pt = ScreenToCanvas(e.Location);
                Point3d endPt = pt;

                if (shiftPressed && _currentPoints.Count > 0)
                {
                    var lastPt = _currentPoints[_currentPoints.Count - 1];
                    endPt = GeometryUtilities.SnapOrthogonal(lastPt, endPt);
                }

                double dist = endPt.DistanceTo(_currentPoints[_currentPoints.Count - 1]);
                if (dist > 5)
                {
                    _currentPoints.Add(endPt);
                    RequestCanvasRedraw();
                }
                _canvas?.Invalidate();
            }
            else if (_isWindowSelecting)
            {
                var pt = ScreenToCanvas(e.Location);
                float minX = (float)Math.Min(_windowSelectStart.X, pt.X);
                float maxX = (float)Math.Max(_windowSelectStart.X, pt.X);
                float minY = (float)Math.Min(_windowSelectStart.Y, pt.Y);
                float maxY = (float)Math.Max(_windowSelectStart.Y, pt.Y);
                RectangleF rect = new RectangleF(minX, minY, maxX - minX, maxY - minY);
                SelectShapesInRectangle(rect, _additiveSelection);
                _isWindowSelecting = false;
                RequestCanvasRedraw();
            }
            else if (_clickStartPt.IsValid)
            {
                if (!_additiveSelection && !_isWindowSelecting)
                {
                    ClearSelection();
                }
                RequestCanvasRedraw();
            }

            _acceptDrawingMouseUp = false;
            EndShapeGesture();
            _clickStartPt = Point3d.Unset;

            _canvas?.Invalidate();
        }

        private Point3d GetFrameOppositeCorner(FrameShape frame, int cornerIndex)
        {
            switch (cornerIndex)
            {
                case 0: return frame.BottomRight;
                case 1: return frame.BottomLeft;
                case 2: return frame.TopLeft;
                case 3: return frame.TopRight;
                default: return Point3d.Unset;
            }
        }

        private void Canvas_KeyDown(object sender, KeyEventArgs e)
        {
            if (!ReferenceEquals(sender, _canvas)) return;
            bool canHandle = CanHandleCanvasEvent(sender);
            bool doubleD = _drawingShortcut == null && _drawingKeyGesture.KeyDown(e.KeyCode,
                canHandle && !e.Handled && e.Modifiers == Keys.None,
                Environment.TickCount, SystemInformation.DoubleClickTime);
            if (!canHandle) return;

            if (e.KeyCode == Keys.Escape && (_drawingModeEnabled || _isDragging || _isMultiDragging))
            {
                CancelDrawing();
                RequestCanvasRedraw();
                e.SuppressKeyPress = true;
                return;
            }
            // Ownership can change during the same multicast event (e.g. after Escape).
            if (e.Handled) return;
            if (e.Modifiers != Keys.None)
            {
                if (e.KeyCode == Keys.ShiftKey) _canvas?.Invalidate();
                return;
            }

            if (e.KeyCode == Keys.D)
            {
                // Windows activation is owned by the HWND hook. A passed-through D
                // belongs to native GH input (or is outside shortcut eligibility).
                if (_drawingShortcut != null) return;
                e.Handled = true; // Keep KeyUp so auto-repeat cannot trigger drawing mode.
                if (doubleD) EnterDrawingMode();
                return;
            }

            if (e.KeyCode == Keys.Enter)
            {
                if (_isDrawing && _currentPoints.Count >= 2 && (_drawMode == 1 || _drawMode == 3))
                {
                    if (!RecordShapeUndo("Create annotation")) return;
                    if (_drawMode == 1)
                    {
                        var polyShape = new PolylineShape(new List<Point3d>(_currentPoints), false);
                        polyShape.ThicknessMultiplier = _currentThicknessMultiplier;
                        polyShape.OverrideColor = _pendingColorOverride;
                        polyShape.LineType = _currentLineType;
                        Shapes.Add(polyShape);
                    }
                    else if (_drawMode == 3)
                    {
                        var curveShape = new CurveShape(new List<Point3d>(_currentPoints), false);
                        curveShape.ThicknessMultiplier = _currentThicknessMultiplier;
                        curveShape.OverrideColor = _pendingColorOverride;
                        curveShape.LineType = _currentLineType;
                        Shapes.Add(curveShape);
                    }
                    MarkDocumentModified();
                    _currentPoints.Clear();
                    _isDrawing = false;
                    ResetNextShapeOverrides();
                    RequestCanvasRedraw();
                    e.SuppressKeyPress = true;
                }
            }
            else if (e.KeyCode == Keys.Escape)
            {
                if (HasSelection && !HasSelectedGHObjects())
                {
                    ClearSelection();
                    RequestCanvasRedraw();
                    e.SuppressKeyPress = true;
                }
            }

            if (e.KeyCode == Keys.Delete)
            {
                if (HasSelection && !HasSelectedGHObjects())
                {
                    DeleteSelectedShapes();
                    e.SuppressKeyPress = true;
                }
            }

            _canvas?.Invalidate();
        }

        private void DeleteSelectedShapes()
        {
            if (HasSelection)
            {
                if (!RecordShapeUndo("Delete annotations")) return;
                var indicesToRemove = SelectedShapeIndices.Distinct().Where(i => i >= 0 && i < Shapes.Count).OrderByDescending(i => i).ToList();
                foreach (var idx in indicesToRemove)
                {
                    Shapes.RemoveAt(idx);
                }
                MarkDocumentModified();
                ClearSelection();
                RequestCanvasRedraw();
            }
        }

        public void ClearAll()
        {
            if (!CanEditShapes || Shapes.Count == 0 || !RecordShapeUndo("Clear annotations")) return;
            Shapes.Clear();
            MarkDocumentModified();
            CancelDrawing();
            ClearSelection();
            RequestCanvasRedraw();
        }

        public override bool AppendMenuItems(ToolStripDropDown menu)
        {
            base.AppendMenuItems(menu);
            if (!CanEditShapes) return true;
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit drawing mode", null, (s, e) =>
            {
                CancelDrawing();
                _canvas?.Invalidate();
            }) { Enabled = _drawingModeEnabled });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Select all HopperDraw shapes", null, (s, e) =>
            {
                SelectedShapeIndices.Clear();
                for (int i = 0; i < Shapes.Count; i++) if (!(Shapes[i] is OpaqueShape)) SelectedShapeIndices.Add(i);
                RequestCanvasRedraw();
            }) { Enabled = Shapes.Count > 0 });

            var thicknessMenu = new ToolStripMenuItem("Shape thickness");
            thicknessMenu.DropDownItems.Add("0.5x", null, (s, e) => SetThicknessMultiplier(0.5f));
            thicknessMenu.DropDownItems.Add("1x", null, (s, e) => SetThicknessMultiplier(1.0f));
            thicknessMenu.DropDownItems.Add("2x", null, (s, e) => SetThicknessMultiplier(2.0f));
            thicknessMenu.DropDownItems.Add("3x", null, (s, e) => SetThicknessMultiplier(3.0f));
            menu.Items.Add(thicknessMenu);
            menu.Items.Add(new ToolStripMenuItem("Cycle line type", null, (s, e) => CycleLineType()));
            menu.Items.Add(new ToolStripMenuItem("Set next shape color", null, (s, e) => ShowColorPickerForDrawing()));
            menu.Items.Add(new ToolStripMenuItem("Set selected shape color", null, (s, e) => ShowColorPickerForSelected()) { Enabled = HasSelection });
            menu.Items.Add(new ToolStripMenuItem("Duplicate selected shapes", null, (s, e) => DuplicateSelectedShapes()) { Enabled = HasSelection });
            menu.Items.Add(new ToolStripMenuItem("Group selected shapes", null, (s, e) =>
            {
                GroupSelectedShapes();
                RequestCanvasRedraw();
            }) { Enabled = SelectedShapeIndices.Count > 1 });
            menu.Items.Add(new ToolStripMenuItem("Ungroup selected shapes", null, (s, e) =>
            {
                UngroupSelectedGroups();
                RequestCanvasRedraw();
            }) { Enabled = SelectedShapeIndices.Any(idx => idx >= 0 && idx < Shapes.Count && Shapes[idx] is DrawShapeGroup) });
            menu.Items.Add(new ToolStripMenuItem("Clear all HopperDraw shapes", null, (s, e) => ClearAll()) { Enabled = Shapes.Count > 0 });
            return true;
        }

        private void SetThicknessMultiplier(float multiplier)
        {
            if (HasSelection)
            {
                if (!RecordShapeUndo("Change annotation thickness")) return;
                foreach (var leaf in SelectedLeaves()) leaf.ThicknessMultiplier = multiplier;
                MarkDocumentModified();
            }
            else
            {
                _currentThicknessMultiplier = multiplier;
            }
            RequestCanvasRedraw();
        }

        private void CycleLineType()
        {
            if (HasSelection)
            {
                if (!RecordShapeUndo("Change annotation line type")) return;
                foreach (var leaf in SelectedLeaves())
                    leaf.LineType = (DashStyle)(((int)StyleValidation.SafeDash(leaf.LineType) + 1) % 5);
                MarkDocumentModified();
            }
            else
            {
                _currentLineType = (DashStyle)(((int)_currentLineType + 1) % 5);
                _hasPendingLineTypeOverride = true;
            }
            RequestCanvasRedraw();
        }

        private void DuplicateSelectedShapes()
        {
            if (!HasSelection || !CanEditShapes) return;
            if (SelectedShapeIndices.Any(i => ContainsOpaque(Shapes[i])))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Cannot duplicate unreadable annotation records. Resolve them with a compatible version first.");
                return;
            }
            if (!RecordShapeUndo("Duplicate annotations")) return;
            var newIndices = new List<int>();
            foreach (var idx in SelectedShapeIndices.Distinct().OrderBy(i => i))
            {
                var clone = Shapes[idx].Clone();
                clone.Move(30, 30);
                Shapes.Add(clone);
                newIndices.Add(Shapes.Count - 1);
            }
            SelectedShapeIndices.Clear();
            SelectedShapeIndices.AddRange(newIndices);
            MarkDocumentModified();
            RequestCanvasRedraw();
        }

        private void ShowColorPickerForSelected()
        {
            if (!HasSelection)
                return;

            var firstShape = Shapes[SelectedShapeIndices[0]];
            using (var dialog = new ColorDialog())
            {
                dialog.Color = firstShape.OverrideColor ?? _borderColor;
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    SetSelectedColor(dialog.Color);
                    _canvas?.Invalidate();
                }
            }
        }

        private void ShowColorPickerForDrawing()
        {
            using (var dialog = new ColorDialog())
            {
                dialog.Color = _pendingColorOverride ?? _borderColor;
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    _pendingColorOverride = dialog.Color;
                }
            }
        }

        private void CaptureSelectedGHObjects()
        {
            _selectedGHObjects.Clear();
            var canvas = Grasshopper.Instances.ActiveCanvas;
            if (canvas?.Document != null)
            {
                foreach (var obj in canvas.Document.Objects)
                {
                    if (obj?.Attributes?.Selected == true)
                    {
                        _selectedGHObjects.Add(obj);
                    }
                }
            }
        }

        private void ClearCapturedGHObjects()
        {
            _selectedGHObjects.Clear();
        }

        private bool HasSelectedGHObjects()
        {
            var canvas = Grasshopper.Instances.ActiveCanvas;
            if (canvas?.Document != null)
            {
                foreach (var obj in canvas.Document.Objects)
                {
                    if (obj?.Attributes?.Selected == true)
                        return true;
                }
            }
            return false;
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            UnregisterCanvasEvents();
            CancelDrawing();
            base.RemovedFromDocument(document);
        }

        public override void CreateAttributes()
        {
            m_attributes = new HopperDrawUIAttributes(this);
        }

        // Unknown schema payloads are read-only and copied without interpreting their contents.
        private GH_LooseChunk _opaquePayload;
        private string _loadWarning;
        internal string LoadWarning => _loadWarning;
        internal bool CanEditShapes => _opaquePayload == null;

        private void ReportLoadWarning()
        {
            if (!string.IsNullOrEmpty(_loadWarning))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, _loadWarning);
        }

        public override bool Write(GH_IWriter writer)
        {
            EnsureShapeIdentity();
            if (!base.Write(writer)) return false;
            var payload = writer.CreateChunk("HopperDraw");
            if (_opaquePayload != null) ShapeSerializer.CopyChunk(_opaquePayload, payload);
            else
            {
                payload.SetInt32("FormatVersion", ShapeSerializer.CurrentFormat);
                payload.SetInt32("Color", BorderColor.ToArgb());
                payload.SetDouble("Thickness", LineThickness);
                payload.SetInt32("DrawOrder", DrawOrder);
                payload.SetBoolean("Visible", Visible);
                ShapeSerializer.WriteShapeList(payload, "Shapes", Shapes);
            }
            return true;
        }

        public override bool Read(GH_IReader reader)
        {
            // GH omits a component when Read returns false. Keep recovery data and warn instead.
            ClearRuntimeMessages();
            _loadWarning = null;
            _opaquePayload = null;
            CancelDrawing();
            ClearSelection();
            Shapes = new List<DrawShape>();
            try
            {
                if (!base.Read(reader)) _loadWarning = "Component metadata could not be fully read.";
                _shapeOwnerId = InstanceGuid;
                var payload = reader.FindChunk("HopperDraw");
                if (payload == null) throw new InvalidOperationException("Missing HopperDraw payload (legacy files are unsupported).");
                int version = -1;
                if (!payload.TryGetInt32("FormatVersion", ref version) || version != ShapeSerializer.CurrentFormat)
                {
                    _opaquePayload = ShapeSerializer.Capture(payload);
                    _loadWarning = "Unsupported HopperDraw format " + version + ". Annotation payload preserved; editing disabled.";
                }
                else
                {
                    int color = Color.Black.ToArgb(), order = 1;
                    double width = 8;
                    bool visible = true;
                    payload.TryGetInt32("Color", ref color);
                    payload.TryGetDouble("Thickness", ref width);
                    payload.TryGetInt32("DrawOrder", ref order);
                    payload.TryGetBoolean("Visible", ref visible);
                    BorderColor = Color.FromArgb(color);
                    bool invalidStyle = !StyleValidation.ValidWidth(width) || order < 0 || order > 1;
                    LineThickness = StyleValidation.Width(width);
                    _lineThickness = LineThickness;
                    DrawOrder = StyleValidation.Clamp(order, 0, 1);
                    Visible = visible;
                    Shapes = ShapeSerializer.ReadShapeList(payload, "Shapes", out var diagnostics);
                    if (invalidStyle) diagnostics.Add("Invalid component style clamped/defaulted for safe paint.");
                    if (diagnostics.Count > 0) _loadWarning = string.Join(" ", diagnostics);
                }
            }
            catch (Exception ex)
            {
                // An invalid list is not an empty list. Preserve the entire annotation payload.
                var payload = reader?.FindChunk("HopperDraw");
                _opaquePayload = ShapeSerializer.Capture(payload ?? reader);
                _loadWarning = "Unreadable HopperDraw data preserved: " + ex.Message;
            }
            ReportLoadWarning();
            return true;
        }

        private Guid _shapeOwnerId;
        internal void EnsureShapeIdentity()
        {
            if (_shapeOwnerId != Guid.Empty && _shapeOwnerId != InstanceGuid)
                ShapeSerializer.RegenerateIds(Shapes);
            _shapeOwnerId = InstanceGuid;
        }

        public override void AddedToDocument(GH_Document document)
        {
            EnsureShapeIdentity();
            base.AddedToDocument(document);
        }

        public bool IsActivated => _isActivated;
        public Point3d DrawStart => _drawStart;
        public Point3d DrawEnd => _drawEnd;
        public bool IsDrawing => _isDrawing;
        public bool DrawingModeEnabled => _drawingModeEnabled;
        public bool ShiftPressed => (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
        public bool CtrlPressed => (Control.ModifierKeys & Keys.Control) == Keys.Control;
        public int DrawMode => _drawMode;
        public List<Point3d> CurrentPoints => _currentPoints;
        public Point3d FrameFirstCorner => _frameFirstCorner;
        public float CurrentThicknessMultiplier => _currentThicknessMultiplier;
        public Color? PendingColorOverride => _pendingColorOverride;

        internal float CanvasScale
        {
            get
            {
                double zoom = _canvas?.Viewport?.Zoom ?? 1;
                return StyleValidation.Finite(zoom) && zoom > 0 ? (float)(1 / Math.Max(0.01, Math.Min(100, zoom))) : 1;
            }
        }

        public bool HasSelection => SelectedShapeIndices.Count > 0;

        internal static int GetNextId() => _shapeIdCounter++;

        public (int shapeIndex, int pointIndex) HitTest(PointF point)
        {
            for (int i = Shapes.Count - 1; i >= 0; i--)
            {
                float width = Shapes[i] is DrawShapeGroup ? LineThickness : StyleValidation.EffectiveWidth(LineThickness, Shapes[i].ThicknessMultiplier);
                var (hit, ptIdx) = Shapes[i].HitTest(point, HitTolerance * CanvasScale, width);
                if (hit) return (i, ptIdx);
            }
            return (-1, -1);
        }

        public void SelectShapesInRectangle(RectangleF rect, bool additive = false)
        {
            if (!additive)
            {
                SelectedShapeIndices.Clear();
            }
            for (int i = 0; i < Shapes.Count; i++)
            {
                if (additive && SelectedShapeIndices.Contains(i))
                    continue;
                    
                var pts = Shapes[i].GetPoints();
                bool inside = false;
                foreach (var pt in pts)
                {
                    if (rect.Contains(pt))
                    {
                        inside = true;
                        break;
                    }
                }
                if (inside)
                {
                    SelectedShapeIndices.Add(i);
                }
            }
        }

        public void ClearSelection()
        {
            SelectedShapeIndices.Clear();
            SelectedPointIndex = -1;
            HoveredShapeIndex = -1;
            HoveredPointIndex = -1;
        }

        public DrawShapeGroup GroupSelectedShapes()
        {
            if (!CanEditShapes) return null;
            var indices = SelectedShapeIndices.Where(i => i >= 0 && i < Shapes.Count).Distinct().OrderBy(i => i).ToList();
            if (indices.Count < 2) return null;
            if (!RecordShapeUndo("Group annotations")) return null;
            var group = new DrawShapeGroup();
            foreach (int i in indices) group.Children.Add(Shapes[i]);
            // Keep child order and replace the highest selected slot after removing selected children.
            int slot = indices.Last() - indices.Count + 1;
            for (int i = indices.Count - 1; i >= 0; i--) Shapes.RemoveAt(indices[i]);
            Shapes.Insert(slot, group);
            ClearSelection();
            SelectedShapeIndices.Add(slot);
            MarkDocumentModified();
            return group;
        }

        public void UngroupSelectedGroups()
        {
            if (!CanEditShapes) return;
            var selected = new HashSet<DrawShape>(SelectedShapeIndices.Where(i => i >= 0 && i < Shapes.Count).Select(i => Shapes[i]));
            if (!selected.Any(shape => shape is DrawShapeGroup) || !RecordShapeUndo("Ungroup annotations")) return;
            var result = new List<DrawShape>();
            var newSelection = new HashSet<DrawShape>();
            foreach (var shape in Shapes)
            {
                if (selected.Contains(shape) && shape is DrawShapeGroup group)
                {
                    result.AddRange(group.Children);
                    foreach (var child in group.Children) newSelection.Add(child);
                }
                else
                {
                    result.Add(shape);
                    if (selected.Contains(shape)) newSelection.Add(shape);
                }
            }
            Shapes = result;
            ClearSelection();
            for (int i = 0; i < Shapes.Count; i++) if (newSelection.Contains(Shapes[i])) SelectedShapeIndices.Add(i);
            MarkDocumentModified();
        }

        private IEnumerable<DrawShape> SelectedLeaves()
        {
            foreach (int index in SelectedShapeIndices.Distinct())
                if (index >= 0 && index < Shapes.Count)
                    foreach (var leaf in Leaves(Shapes[index])) yield return leaf;
        }
        private static IEnumerable<DrawShape> Leaves(DrawShape shape)
        {
            if (shape is DrawShapeGroup group)
            {
                foreach (var child in group.Children)
                    foreach (var leaf in Leaves(child)) yield return leaf;
            }
            else if (!(shape is OpaqueShape)) yield return shape;
        }

        private void RequestCanvasRedraw()
        {
            var canvas = _canvas ?? Instances.ActiveCanvas;
            if (canvas != null && ReferenceEquals(canvas.Document, OnPingDocument())) canvas.Invalidate();
        }
    }
}
