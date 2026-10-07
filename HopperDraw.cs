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
        private System.Windows.Forms.Timer _ghDragSyncTimer;
        private Dictionary<IGH_DocumentObject, PointF> _ghObjectInitialPositions = new Dictionary<IGH_DocumentObject, PointF>();

        private static int _shapeIdCounter = 1;

        public List<DrawShape> Shapes { get; set; } = new List<DrawShape>();
        public Color BorderColor { get; set; } = Color.Black;
        public float LineThickness { get; set; } = 8f;
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

            int drawOrder = 0;
            DA.GetData(6, ref drawOrder);

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

            ExpirePreview(false);
        }

        private void MarkDocumentModified()
        {
            ExpirePreview(false);
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
                _canvas.DocumentChanged += Canvas_DocumentChanged;
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    _drawingShortcut = CanvasDrawingShortcut.Register(_canvas, CanActivateDrawingShortcut, EnterDrawingMode);
                _eventsRegistered = true;

                _ghDragSyncTimer = new System.Windows.Forms.Timer();
                _ghDragSyncTimer.Interval = 16;
                _ghDragSyncTimer.Tick += GhDragSyncTimer_Tick;
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
                _canvas.DocumentChanged -= Canvas_DocumentChanged;
            }
            _eventsRegistered = false;
            _canvas = null;

            if (_ghDragSyncTimer != null)
            {
                _ghDragSyncTimer.Stop();
                _ghDragSyncTimer.Tick -= GhDragSyncTimer_Tick;
                _ghDragSyncTimer.Dispose();
                _ghDragSyncTimer = null;
            }
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
            return _isActivated && !Locked && Visible &&
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
            _ghDragSyncTimer?.Stop();
            _ghObjectInitialPositions.Clear();
            ClearCapturedGHObjects();
        }

        internal void EnterDrawingMode()
        {
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
            _ghDragSyncTimer?.Stop();
            _ghObjectInitialPositions.Clear();
            ClearCapturedGHObjects();
            _lastClickTime = DateTime.MinValue;
            _lastClickPosition = Point3d.Unset;
            _drawingModeEnabled = true;
            _capturingOwner = this;
            _canvas.Focus();
            _canvas.Invalidate();
        }

        private void Canvas_LostFocus(object sender, EventArgs e)
        {
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
                    Shapes.Add(lineShape);
                    MarkDocumentModified();
                    _isDrawing = false;
                    ResetNextShapeOverrides();
                    ExpireDisplay();
                    _canvas?.Invalidate();
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
                    ExpireDisplay();
                    _canvas?.Invalidate();
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
                        ExpireDisplay();
                        _canvas?.Invalidate();
                        return;
                    }
                }
                else if (_isDrawing && _currentPoints.Count >= 2 && isDoubleClick)
                {
                    var polyShape = new PolylineShape(new List<Point3d>(_currentPoints), true);
                    polyShape.ThicknessMultiplier = _currentThicknessMultiplier;
                    polyShape.OverrideColor = _pendingColorOverride;
                    polyShape.LineType = _currentLineType;
                    Shapes.Add(polyShape);
                    MarkDocumentModified();
                    _currentPoints.Clear();
                    _isDrawing = false;
                    ResetNextShapeOverrides();
                    _lastClickTime = DateTime.Now;
                    _lastClickPosition = pt;
                    ExpireDisplay();
                    _canvas?.Invalidate();
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
                        ExpireDisplay();
                        _canvas?.Invalidate();
                        return;
                    }
                }
                else if (_isDrawing && _currentPoints.Count >= 2 && isDoubleClick)
                {
                    var curveShape = new CurveShape(new List<Point3d>(_currentPoints), true);
                    curveShape.ThicknessMultiplier = _currentThicknessMultiplier;
                    curveShape.OverrideColor = _pendingColorOverride;
                    curveShape.LineType = _currentLineType;
                    Shapes.Add(curveShape);
                    MarkDocumentModified();
                    _currentPoints.Clear();
                    _isDrawing = false;
                    ResetNextShapeOverrides();
                    _lastClickTime = DateTime.Now;
                    _lastClickPosition = pt;
                    ExpireDisplay();
                    _canvas?.Invalidate();
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
                    ExpireDisplay();
                    _canvas?.Invalidate();
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
                    ExpireDisplay();
                    _canvas?.Invalidate();
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
                if (HasSelection)
                {
                    CaptureGHObjectsAndStartSync(pt);
                }
                _clickStartPt = pt;
                _additiveSelection = shiftPressed;
            }

            ExpireDisplay();
            _canvas?.Invalidate();
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
            Shapes.Add(frameShape);
            MarkDocumentModified();
            ResetNextShapeOverrides();
            ExpireDisplay();
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
                var shape = Shapes[_dragShapeIndex];

                if (_isFrameDragging && shape is FrameShape frame)
                {
                    if (_dragPointIndex >= 0)
                    {
                        Point3d newPos = pt;
                        var fixedCorner = GetFrameOppositeCorner(frame, _dragPointIndex);

                        bool shiftPressed = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
                        if (shiftPressed && _dragAspectRatio > 0)
                        {
                            newPos = GeometryUtilities.SnapToAspectRatio(fixedCorner, pt, _dragAspectRatio);
                        }

                        double fw = newPos.X - fixedCorner.X;
                        double fh = newPos.Y - fixedCorner.Y;

                        switch (_dragPointIndex)
                        {
                            case 0:
                                frame.TopLeft = newPos;
                                frame.TopRight = new Point3d(fixedCorner.X, newPos.Y, 0);
                                frame.BottomLeft = new Point3d(newPos.X, fixedCorner.Y, 0);
                                frame.BottomRight = fixedCorner;
                                break;
                            case 1:
                                frame.TopRight = newPos;
                                frame.TopLeft = new Point3d(fixedCorner.X, newPos.Y, 0);
                                frame.BottomRight = new Point3d(newPos.X, fixedCorner.Y, 0);
                                frame.BottomLeft = fixedCorner;
                                break;
                            case 2:
                                frame.BottomRight = newPos;
                                frame.BottomLeft = new Point3d(fixedCorner.X, newPos.Y, 0);
                                frame.TopRight = new Point3d(newPos.X, fixedCorner.Y, 0);
                                frame.TopLeft = fixedCorner;
                                break;
                            case 3:
                                frame.BottomLeft = newPos;
                                frame.BottomRight = new Point3d(fixedCorner.X, newPos.Y, 0);
                                frame.TopLeft = new Point3d(newPos.X, fixedCorner.Y, 0);
                                frame.TopRight = fixedCorner;
                                break;
                        }
                    }
                    else
                    {
                        var deltaX = pt.X - _dragFrameOffset.X;
                        var deltaY = pt.Y - _dragFrameOffset.Y;
                        frame.TopLeft = new Point3d(frame.TopLeft.X + deltaX, frame.TopLeft.Y + deltaY, 0);
                        frame.TopRight = new Point3d(frame.TopRight.X + deltaX, frame.TopRight.Y + deltaY, 0);
                        frame.BottomRight = new Point3d(frame.BottomRight.X + deltaX, frame.BottomRight.Y + deltaY, 0);
                        frame.BottomLeft = new Point3d(frame.BottomLeft.X + deltaX, frame.BottomLeft.Y + deltaY, 0);
                        _dragFrameOffset = new Point3d(pt.X, pt.Y, 0);
                    }
                }
                else
                {
                    if (_dragPointIndex >= 0)
                    {
                        Point3d newPos = pt;
                        if (shape is LineShape line && _dragPointIndex >= 0)
                        {
                            var otherPt = _dragPointIndex == 0 ? line.End : line.Start;
                            bool shiftPressed = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
                            if (shiftPressed)
                            {
                                newPos = GeometryUtilities.SnapAngle(otherPt, pt);
                            }
                        }
                        shape.MovePoint(_dragPointIndex, newPos);
                    }
                    else
                    {
                        var deltaX = pt.X - _dragOffset.X;
                        var deltaY = pt.Y - _dragOffset.Y;
                        shape.Move(deltaX, deltaY);
                        _dragOffset = new Point3d(pt.X, pt.Y, 0);
                    }
                }

                ExpireDisplay();
                _canvas?.Invalidate();
            }
            else if (_isMultiDragging && HasSelection)
            {
                var deltaX = pt.X - _multiDragStart.X;
                var deltaY = pt.Y - _multiDragStart.Y;
                foreach (var idx in SelectedShapeIndices)
                {
                    Shapes[idx].Move(deltaX, deltaY);
                }
                foreach (var obj in _selectedGHObjects)
                {
                    var pivot = obj.Attributes.Pivot;
                    obj.Attributes.Pivot = new PointF(
                        pivot.X + (float)deltaX,
                        pivot.Y + (float)deltaY);
                }
                _multiDragStart = pt;
                ExpireDisplay();
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
                HoveredShapeIndex = shapeIdx >= 0 ? shapeIdx : -1;
                HoveredPointIndex = pointIdx >= 0 ? pointIdx : -1;
                _canvas?.Invalidate();
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
                    ExpireDisplay();
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
                ExpireDisplay();
                _canvas?.Invalidate();
            }
            else if (_clickStartPt.IsValid)
            {
                if (!_additiveSelection && _ghObjectInitialPositions.Count == 0 && !_isWindowSelecting)
                {
                    ClearSelection();
                }
                ExpireDisplay();
                _canvas?.Invalidate();
            }

            _acceptDrawingMouseUp = false;
            bool wasDragging = _isDragging || _isMultiDragging;
            _isDragging = false;
            _isMultiDragging = false;
            _dragShapeIndex = -1;
            _dragPointIndex = -1;
            _clickStartPt = Point3d.Unset;
            ClearCapturedGHObjects();
            _ghDragSyncTimer?.Stop();
            _ghObjectInitialPositions.Clear();

            if (wasDragging)
            {
                MarkDocumentModified();
            }

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

            if (e.KeyCode == Keys.Escape && _drawingModeEnabled)
            {
                CancelDrawing();
                ExpireDisplay();
                _canvas?.Invalidate();
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
                    ExpireDisplay();
                    e.SuppressKeyPress = true;
                }
            }
            else if (e.KeyCode == Keys.Escape)
            {
                if (HasSelection && !HasSelectedGHObjects())
                {
                    ClearSelection();
                    ExpireDisplay();
                    _canvas?.Invalidate();
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
                var indicesToRemove = SelectedShapeIndices.OrderByDescending(i => i).ToList();
                foreach (var idx in indicesToRemove)
                {
                    Shapes.RemoveAt(idx);
                }
                MarkDocumentModified();
                ClearSelection();
                ExpireDisplay();
                _canvas?.Invalidate();
            }
        }

        public void ClearAll()
        {
            Shapes.Clear();
            MarkDocumentModified();
            CancelDrawing();
            ClearSelection();
            ExpireDisplay();
            _canvas?.Invalidate();
        }

        public override bool AppendMenuItems(ToolStripDropDown menu)
        {
            base.AppendMenuItems(menu);
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
                for (int i = 0; i < Shapes.Count; i++) SelectedShapeIndices.Add(i);
                ExpireDisplay();
                _canvas?.Invalidate();
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
                MarkDocumentModified();
                ExpireDisplay();
                _canvas?.Invalidate();
            }) { Enabled = SelectedShapeIndices.Count > 1 });
            menu.Items.Add(new ToolStripMenuItem("Ungroup selected shapes", null, (s, e) =>
            {
                UngroupSelectedGroups();
                MarkDocumentModified();
                ExpireDisplay();
                _canvas?.Invalidate();
            }) { Enabled = SelectedShapeIndices.Any(idx => idx >= 0 && idx < Shapes.Count && Shapes[idx] is DrawShapeGroup) });
            menu.Items.Add(new ToolStripMenuItem("Clear all HopperDraw shapes", null, (s, e) => ClearAll()) { Enabled = Shapes.Count > 0 });
            return true;
        }

        private void SetThicknessMultiplier(float multiplier)
        {
            if (HasSelection)
            {
                foreach (var idx in SelectedShapeIndices) Shapes[idx].ThicknessMultiplier = multiplier;
                MarkDocumentModified();
            }
            else
            {
                _currentThicknessMultiplier = multiplier;
            }
            ExpireDisplay();
            _canvas?.Invalidate();
        }

        private void CycleLineType()
        {
            if (HasSelection)
            {
                foreach (var idx in SelectedShapeIndices)
                    Shapes[idx].LineType = (DashStyle)(((int)Shapes[idx].LineType + 1) % 5);
                MarkDocumentModified();
            }
            else
            {
                _currentLineType = (DashStyle)(((int)_currentLineType + 1) % 5);
                _hasPendingLineTypeOverride = true;
            }
            ExpireDisplay();
            _canvas?.Invalidate();
        }

        private void DuplicateSelectedShapes()
        {
            if (!HasSelection) return;
            var newIndices = new List<int>();
            foreach (var idx in SelectedShapeIndices)
            {
                var clone = Shapes[idx].Clone();
                clone.Move(30, 30);
                Shapes.Add(clone);
                newIndices.Add(Shapes.Count - 1);
            }
            SelectedShapeIndices.Clear();
            SelectedShapeIndices.AddRange(newIndices);
            MarkDocumentModified();
            ExpireDisplay();
            _canvas?.Invalidate();
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
                    foreach (var idx in SelectedShapeIndices)
                    {
                        Shapes[idx].OverrideColor = dialog.Color;
                    }
                    MarkDocumentModified();
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

        private void CaptureGHObjectsAndStartSync(Point3d pt)
        {
            _ghObjectInitialPositions.Clear();
            var canvas = Grasshopper.Instances.ActiveCanvas;
            if (canvas?.Document != null)
            {
                foreach (var obj in canvas.Document.Objects)
                {
                    if (obj?.Attributes?.Selected == true)
                    {
                        _ghObjectInitialPositions[obj] = obj.Attributes.Pivot;
                    }
                }
            }
            if (_ghObjectInitialPositions.Count > 0 && _ghDragSyncTimer != null)
            {
                _ghDragSyncTimer.Start();
            }
        }

        private void GhDragSyncTimer_Tick(object sender, EventArgs e)
        {
            if (!HasSelection || _ghObjectInitialPositions.Count == 0)
            {
                _ghDragSyncTimer?.Stop();
                return;
            }

            var canvas = Grasshopper.Instances.ActiveCanvas;
            if (canvas?.Document == null)
            {
                _ghDragSyncTimer?.Stop();
                return;
            }

            bool anyMoved = false;
            float totalDeltaX = 0;
            float totalDeltaY = 0;
            int movedCount = 0;

            foreach (var kvp in _ghObjectInitialPositions)
            {
                var obj = kvp.Key;
                if (obj?.Attributes == null) continue;

                var currentPos = obj.Attributes.Pivot;
                var initialPos = kvp.Value;
                var deltaX = currentPos.X - initialPos.X;
                var deltaY = currentPos.Y - initialPos.Y;

                if (Math.Abs(deltaX) > 0.1f || Math.Abs(deltaY) > 0.1f)
                {
                    anyMoved = true;
                    totalDeltaX += deltaX;
                    totalDeltaY += deltaY;
                    movedCount++;
                }
            }

            if (anyMoved && movedCount > 0)
            {
                float avgDeltaX = totalDeltaX / movedCount;
                float avgDeltaY = totalDeltaY / movedCount;

                foreach (var idx in SelectedShapeIndices)
                {
                    Shapes[idx].Move(avgDeltaX, avgDeltaY);
                }

                var updatedPositions = new Dictionary<IGH_DocumentObject, PointF>();
                foreach (var kvp in _ghObjectInitialPositions)
                {
                    updatedPositions[kvp.Key] = kvp.Key.Attributes.Pivot;
                }
                _ghObjectInitialPositions = updatedPositions;

                ExpireDisplay();
                _canvas?.Invalidate();
            }
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

        public override bool Write(GH_IWriter writer)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[HopperDraw Write] Called. Shapes.Count={Shapes.Count}, Color={BorderColor}, Thickness={LineThickness}, DrawOrder={DrawOrder}");
#endif
            bool result = base.Write(writer);
            if (!result) return false;

            writer.SetInt32("ShapeCount", Shapes.Count);
            writer.SetInt32("Color", BorderColor.ToArgb());
            writer.SetDouble("Thickness", LineThickness);
            writer.SetInt32("DrawOrder", DrawOrder);
            writer.SetInt32("Visible", Visible ? 1 : 0);
            for (int i = 0; i < Shapes.Count; i++)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[HopperDraw Write] Writing shape {i} of type {Shapes[i].GetType().Name}");
#endif
                Shapes[i].Write(writer, i);
            }
            return true;
        }

        public override bool Read(GH_IReader reader)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[HopperDraw Read] Called. reader={reader == null}");
#endif
            try
            {
                bool result = base.Read(reader);
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[HopperDraw Read] base.Read returned {result}");
#endif
                if (!result) return false;

                CancelDrawing();
                Shapes.Clear();
                int count = reader.GetInt32("ShapeCount");
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[HopperDraw Read] ShapeCount={count}");
#endif
                BorderColor = Color.FromArgb(reader.GetInt32("Color"));
                LineThickness = (float)reader.GetDouble("Thickness");
                DrawOrder = reader.GetInt32("DrawOrder");
                Visible = reader.GetInt32("Visible") == 1;
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[HopperDraw Read] Color={BorderColor}, Thickness={LineThickness}, DrawOrder={DrawOrder}");
#endif
                for (int i = 0; i < count; i++)
                {
#if DEBUG
                    System.Diagnostics.Debug.WriteLine($"[HopperDraw Read] About to read Type{i}");
#endif
                    int type = reader.GetInt32($"Type{i}");
#if DEBUG
                    System.Diagnostics.Debug.WriteLine($"[HopperDraw Read] Reading shape {i} of type {type}");
#endif
                    DrawShape shape;
                    switch (type)
                    {
                        case 0:
                            shape = new LineShape();
                            break;
                        case 1:
                            shape = new PolylineShape();
                            break;
                        case 2:
                            shape = new FrameShape();
                            break;
                        case 3:
                            shape = new CurveShape();
                            break;
                        case 4:
                            shape = new DrawShapeGroup();
                            break;
                        default:
                            shape = new LineShape();
                            break;
                    }
#if DEBUG
                    System.Diagnostics.Debug.WriteLine($"[HopperDraw Read] Calling shape.Read for shape {i}");
#endif
                    shape.Read(reader, i);
#if DEBUG
                    System.Diagnostics.Debug.WriteLine($"[HopperDraw Read] Adding shape {i} to list");
#endif
                    Shapes.Add(shape);
                }
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[HopperDraw Read] Done. Shapes.Count={Shapes.Count}");
#endif
                return true;
            }
            catch (Exception ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[HopperDraw Read] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[HopperDraw Read] StackTrace: {ex.StackTrace}");
#endif
                return false;
            }
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

        public bool HasSelection => SelectedShapeIndices.Count > 0;

        internal static int GetNextId() => _shapeIdCounter++;

        public (int shapeIndex, int pointIndex) HitTest(PointF point)
        {
            for (int i = 0; i < Shapes.Count; i++)
            {
                var (hit, ptIdx) = Shapes[i].HitTest(point, HitTolerance, LineThickness);
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
            if (SelectedShapeIndices.Count < 1) return null;

            var sortedIndices = SelectedShapeIndices.OrderBy(i => i).ToList();
            var group = new DrawShapeGroup();

            for (int i = sortedIndices.Count - 1; i >= 0; i--)
            {
                var shape = Shapes[sortedIndices[i]];
                Shapes.RemoveAt(sortedIndices[i]);
                group.Children.Add(shape);
            }

            Shapes.Add(group);
            int groupIndex = Shapes.Count - 1;

            SelectedShapeIndices.Clear();
            SelectedShapeIndices.Add(groupIndex);

            return group;
        }

        public void UngroupSelectedGroups()
        {
            var indicesToRemove = SelectedShapeIndices
                .Where(i => i >= 0 && i < Shapes.Count && Shapes[i] is DrawShapeGroup)
                .OrderByDescending(i => i)
                .ToList();

            if (indicesToRemove.Count == 0) return;

            var newSelections = new List<int>();

            foreach (var idx in indicesToRemove)
            {
                if (Shapes[idx] is DrawShapeGroup group)
                {
                    int insertPos = idx;
                    for (int i = 0; i < group.Children.Count; i++)
                    {
                        Shapes.Insert(insertPos + i, group.Children[i]);
                        newSelections.Add(insertPos + i);
                    }
                    Shapes.RemoveAt(insertPos + group.Children.Count);
                }
            }

            SelectedShapeIndices.Clear();
            SelectedShapeIndices.AddRange(newSelections);
        }

        public void ExpireDisplay()
        {
            var doc = OnPingDocument();
            if (doc != null)
                doc.ScheduleSolution(5, d => this.ExpirePreview(false));
        }
    }
}
