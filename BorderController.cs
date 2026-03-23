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
using System.Linq;
using System.Windows.Forms;

namespace hopperborder
{
    public class BorderController : GH_Component
    {
        private bool _isActivated;

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

        internal BorderAnnotation _annotation;

        private float _lineThickness = 8f;
        private Color _borderColor = Color.Black;
        private int _drawMode = 0;
        private int _frameCornerCount = 0;
        private Point3d _frameFirstCorner;
        private float _currentThicknessMultiplier = 1.0f;
        private Color? _pendingColorOverride;

        private List<Point3d> _currentPoints = new List<Point3d>();

        private Point3d _windowSelectStart;
        private bool _isWindowSelecting = false;
        private bool _additiveSelection = false;
        private Point3d _multiDragStart;
        private bool _isMultiDragging = false;
        private Point3d _clickStartPt;
        private List<IGH_DocumentObject> _selectedGHObjects = new List<IGH_DocumentObject>();
        private System.Windows.Forms.Timer _ghDragSyncTimer;
        private Dictionary<IGH_DocumentObject, PointF> _ghObjectInitialPositions = new Dictionary<IGH_DocumentObject, PointF>();

        public BorderController()
            : base("Border Controller", "Border", "Draw and manage canvas borders", "Draw", "Primitive")
        {
            CreateAttributes();
        }

        public override Guid ComponentGuid => new Guid("C1D2E3F4-A5B6-7890-1234-567890ABCDEF");

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddBooleanParameter("Active", "A", "Enable border drawing", GH_ParamAccess.item, true);
            pManager.AddBooleanParameter("Show", "S", "Display borders on canvas", GH_ParamAccess.item, true);
            pManager.AddIntegerParameter("DrawMode", "M", "Drawing mode: 0=Line, 1=Polyline, 2=Frame, 3=Curve", GH_ParamAccess.item, 0);
            pManager.AddColourParameter("Color", "C", "Default border color. Right-click during drawing to set per-shape color override", GH_ParamAccess.item, Color.Black);
            pManager.AddNumberParameter("Thickness", "T", "Base thickness. Use Ctrl+1 (0.5x), Ctrl+2 (2x), Ctrl+3 (3x) to modify", GH_ParamAccess.item, 8.0);
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

            var drawOrderParam = Params.Input[5] as Param_Integer;
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
            System.Diagnostics.Debug.WriteLine($"[BorderController SolveInstance] Called. _isActivated={_isActivated}");
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

            int drawOrder = 0;
            DA.GetData(5, ref drawOrder);

            _borderColor = color;
            _lineThickness = (float)thickness;
            _drawMode = drawMode;

            if (_drawMode != 1 && _drawMode != 3)
            {
                _currentPoints.Clear();
            }

            bool wasActivated = _isActivated;
            _isActivated = active;

            if (_isActivated && !wasActivated)
            {
                System.Diagnostics.Debug.WriteLine($"[BorderController SolveInstance] Activating!");
                EnsureAnnotation();
                RegisterCanvasEvents();
            }
            else if (!_isActivated && wasActivated)
            {
                UnregisterCanvasEvents();
            }

            if (_annotation != null)
            {
                System.Diagnostics.Debug.WriteLine($"[BorderController SolveInstance] Updating annotation. Shapes count = {_annotation.Shapes.Count}");
                _annotation.Visible = show && !this.Locked;
                _annotation.BorderColor = _borderColor;
                _annotation.LineThickness = _lineThickness;
                _annotation.DrawOrder = drawOrder;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[BorderController SolveInstance] _annotation is null!");
            }

            ExpirePreview(false);
        }

        private void EnsureAnnotation()
        {
            var doc = OnPingDocument();
            if (doc == null) return;

            System.Diagnostics.Debug.WriteLine($"[EnsureAnnotation] Called. _annotation is null? {_annotation == null}. Doc has {doc.Objects.Count} objects.");

            if (_annotation == null)
            {
                foreach (var obj in doc.Objects)
                {
                    System.Diagnostics.Debug.WriteLine($"[EnsureAnnotation] Checking object: {obj?.GetType().Name} - {obj?.ComponentGuid}");
                    if (obj is BorderAnnotation existing)
                    {
                        System.Diagnostics.Debug.WriteLine($"[EnsureAnnotation] Found existing BorderAnnotation!");
                        _annotation = existing;
                        _annotation.Controller = this;
                        System.Diagnostics.Debug.WriteLine($"[EnsureAnnotation] Reconnected. Shapes count: {_annotation.Shapes.Count}");
                        break;
                    }
                }
            }

            if (_annotation != null)
            {
                System.Diagnostics.Debug.WriteLine($"[EnsureAnnotation] Using existing annotation with {_annotation.Shapes.Count} shapes.");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[EnsureAnnotation] Creating NEW annotation.");
            _annotation = new BorderAnnotation();
            _annotation.Controller = this;
            _annotation.BorderColor = _borderColor;
            _annotation.LineThickness = _lineThickness;

            doc.AddObject(_annotation, false, -1);

            _annotation.Attributes.Pivot = new PointF(-10000, -10000);
        }

        private void RegisterCanvasEvents()
        {
            if (_eventsRegistered) return;

            _canvas = Grasshopper.Instances.ActiveCanvas;
            if (_canvas != null)
            {
                _canvas.MouseDown += Canvas_MouseDown;
                _canvas.MouseMove += Canvas_MouseMove;
                _canvas.MouseUp += Canvas_MouseUp;
                _canvas.KeyDown += Canvas_KeyDown;
                _eventsRegistered = true;
            }

            _ghDragSyncTimer = new System.Windows.Forms.Timer();
            _ghDragSyncTimer.Interval = 16;
            _ghDragSyncTimer.Tick += GhDragSyncTimer_Tick;
        }

        private void UnregisterCanvasEvents()
        {
            if (!_eventsRegistered) return;

            if (_canvas != null)
            {
                _canvas.MouseDown -= Canvas_MouseDown;
                _canvas.MouseMove -= Canvas_MouseMove;
                _canvas.MouseUp -= Canvas_MouseUp;
                _canvas.KeyDown -= Canvas_KeyDown;
            }
            _eventsRegistered = false;

            if (_ghDragSyncTimer != null)
            {
                _ghDragSyncTimer.Stop();
                _ghDragSyncTimer.Tick -= GhDragSyncTimer_Tick;
                _ghDragSyncTimer.Dispose();
                _ghDragSyncTimer = null;
            }
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
            if (!_isActivated || _annotation == null)
                return;

            if (e.Button == MouseButtons.Right)
            {
                if (_isDrawing)
                {
                    ShowColorPickerForDrawing();
                }
                else if (_annotation.HasSelection)
                {
                    ShowColorPickerForSelected();
                }
                return;
            }

            if (e.Button != MouseButtons.Left)
                return;

            var pt = ScreenToCanvas(e.Location);

            var timeSinceLastClick = DateTime.Now - _lastClickTime;
            bool isDoubleClick = (timeSinceLastClick.TotalMilliseconds < 500) &&
                                (_lastClickPosition.DistanceTo(pt) < 10);

            bool ctrlPressed = (Control.ModifierKeys & Keys.Control) == Keys.Control;
            bool shiftPressed = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;

            if (_drawMode == 0)
            {
                if (_isDrawing)
                {
                    Point3d endPt = pt;
                    if (shiftPressed)
                    {
                        double dx = pt.X - _drawStart.X;
                        double dy = pt.Y - _drawStart.Y;
                        double length = Math.Sqrt(dx * dx + dy * dy);
                        if (length > 0)
                        {
                            double angle = Math.Atan2(dy, dx);
                            double snapAngle = Math.Round(angle / (Math.PI / 4.0)) * (Math.PI / 4.0);
                            endPt = new Point3d(
                                _drawStart.X + length * Math.Cos(snapAngle),
                                _drawStart.Y + length * Math.Sin(snapAngle),
                                0);
                        }
                    }
                    var lineShape = new LineShape(_drawStart, endPt);
                    lineShape.ThicknessMultiplier = _currentThicknessMultiplier;
                    lineShape.OverrideColor = _pendingColorOverride;
                    _annotation.Shapes.Add(lineShape);
                    _isDrawing = false;
                    _pendingColorOverride = null;
                    _currentThicknessMultiplier = 1.0f;
                    _annotation.ExpireDisplay();
                    _canvas?.Invalidate();
                    return;
                }
                else if (ctrlPressed)
                {
                    _drawStart = pt;
                    _drawEnd = pt;
                    _isDrawing = true;
                    _lastClickTime = DateTime.Now;
                    _lastClickPosition = pt;
                    _annotation.ExpireDisplay();
                    _canvas?.Invalidate();
                    return;
                }
            }
            else if (_drawMode == 1)
            {
                if (!_isDrawing || _currentPoints.Count == 0)
                {
                    if (ctrlPressed)
                    {
                        _currentPoints.Add(pt);
                        _isDrawing = true;
                        _lastClickTime = DateTime.Now;
                        _lastClickPosition = pt;
                        _annotation.ExpireDisplay();
                        _canvas?.Invalidate();
                        return;
                    }
                }
                else if (_isDrawing && _currentPoints.Count >= 2 && isDoubleClick)
                {
                    var polyShape = new PolylineShape(new List<Point3d>(_currentPoints), true);
                    polyShape.ThicknessMultiplier = _currentThicknessMultiplier;
                    polyShape.OverrideColor = _pendingColorOverride;
                    _annotation.Shapes.Add(polyShape);
                    _currentPoints.Clear();
                    _isDrawing = false;
                    _pendingColorOverride = null;
                    _currentThicknessMultiplier = 1.0f;
                    _lastClickTime = DateTime.Now;
                    _lastClickPosition = pt;
                    _annotation.ExpireDisplay();
                    _canvas?.Invalidate();
                    return;
                }
            }
            else if (_drawMode == 3)
            {
                if (!_isDrawing || _currentPoints.Count == 0)
                {
                    if (ctrlPressed)
                    {
                        _currentPoints.Add(pt);
                        _isDrawing = true;
                        _lastClickTime = DateTime.Now;
                        _lastClickPosition = pt;
                        _annotation.ExpireDisplay();
                        _canvas?.Invalidate();
                        return;
                    }
                }
                else if (_isDrawing && _currentPoints.Count >= 2 && isDoubleClick)
                {
                    var curveShape = new CurveShape(new List<Point3d>(_currentPoints), true);
                    curveShape.ThicknessMultiplier = _currentThicknessMultiplier;
                    curveShape.OverrideColor = _pendingColorOverride;
                    _annotation.Shapes.Add(curveShape);
                    _currentPoints.Clear();
                    _isDrawing = false;
                    _pendingColorOverride = null;
                    _currentThicknessMultiplier = 1.0f;
                    _lastClickTime = DateTime.Now;
                    _lastClickPosition = pt;
                    _annotation.ExpireDisplay();
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
                    _annotation.ExpireDisplay();
                    _canvas?.Invalidate();
                    return;
                }
                else if (ctrlPressed)
                {
                    _frameFirstCorner = pt;
                    _frameCornerCount = 1;
                    _isDrawing = true;
                    _lastClickTime = DateTime.Now;
                    _lastClickPosition = pt;
                    _annotation.ExpireDisplay();
                    _canvas?.Invalidate();
                    return;
                }
            }

            if ((_drawMode == 1 || _drawMode == 3) && _isDrawing)
            {
                _lastClickTime = DateTime.Now;
                _lastClickPosition = pt;
                return;
            }

            var (shapeIdx, pointIdx) = _annotation.HitTest(new PointF((float)pt.X, (float)pt.Y));

            if (shapeIdx >= 0)
            {
                bool isAlreadySelected = _annotation.SelectedShapeIndices.Contains(shapeIdx);
                bool hasSelectedGH = HasSelectedGHObjects();
                bool multipleShapesSelected = _annotation.SelectedShapeIndices.Count > 1;
                
                if (shiftPressed)
                {
                    if (isAlreadySelected)
                    {
                        _annotation.SelectedShapeIndices.Remove(shapeIdx);
                    }
                    else
                    {
                        _annotation.SelectedShapeIndices.Add(shapeIdx);
                    }
                    _annotation.SelectedPointIndex = pointIdx;
                }
                else
                {
                    if (isAlreadySelected && pointIdx < 0 && (hasSelectedGH || multipleShapesSelected))
                    {
                        _annotation.SelectedPointIndex = pointIdx;
                        _multiDragStart = pt;
                        _isMultiDragging = true;
                        CaptureSelectedGHObjects();
                    }
                    else if (isAlreadySelected && pointIdx < 0)
                    {
                        _annotation.SelectedPointIndex = pointIdx;
                        _dragShapeIndex = shapeIdx;
                        _dragPointIndex = pointIdx;

                        var shape = _annotation.Shapes[shapeIdx];
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
                        _annotation.SelectedShapeIndices.Clear();
                        _annotation.SelectedShapeIndices.Add(shapeIdx);
                        _annotation.SelectedPointIndex = pointIdx;
                        _dragShapeIndex = shapeIdx;
                        _dragPointIndex = pointIdx;

                        var shape = _annotation.Shapes[shapeIdx];
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
            else if (_annotation.HasSelection && HasSelectedGHObjects())
            {
                _multiDragStart = pt;
                _isMultiDragging = true;
                CaptureSelectedGHObjects();
            }
            else if (_annotation.Shapes.Count > 0)
            {
                if (_annotation.HasSelection)
                {
                    CaptureGHObjectsAndStartSync(pt);
                }
                _clickStartPt = pt;
                _additiveSelection = ctrlPressed;
            }

            _annotation.ExpireDisplay();
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
            _annotation.Shapes.Add(frameShape);
            _pendingColorOverride = null;
            _currentThicknessMultiplier = 1.0f;
            _annotation.ExpireDisplay();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isActivated || _annotation == null)
                return;

            var pt = ScreenToCanvas(e.Location);

            if (_isDrawing)
            {
                _drawEnd = pt;
                _canvas?.Invalidate();
            }
            else if (_isDragging && _dragShapeIndex >= 0)
            {
                var shape = _annotation.Shapes[_dragShapeIndex];

                if (_isFrameDragging && shape is FrameShape frame)
                {
                    if (_dragPointIndex >= 0)
                    {
                        Point3d newPos = pt;
                        var fixedCorner = GetFrameOppositeCorner(frame, _dragPointIndex);

                        bool shiftPressed = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
                        if (shiftPressed && _dragAspectRatio > 0)
                        {
                            double w = pt.X - fixedCorner.X;
                            double h = pt.Y - fixedCorner.Y;
                            double absW = Math.Abs(w);
                            double absH = Math.Abs(h);
                            double signW = Math.Sign(w);
                            double signH = Math.Sign(h);

                            if (absW > 2 && absH > 2)
                            {
                                if (absW > absH * _dragAspectRatio)
                                {
                                    absH = absW / _dragAspectRatio;
                                }
                                else
                                {
                                    absW = absH * _dragAspectRatio;
                                }
                                newPos = new Point3d(fixedCorner.X + signW * absW, fixedCorner.Y + signH * absH, 0);
                            }
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
                                double dx = pt.X - otherPt.X;
                                double dy = pt.Y - otherPt.Y;
                                double length = Math.Sqrt(dx * dx + dy * dy);
                                if (length > 0)
                                {
                                    double angle = Math.Atan2(dy, dx);
                                    double snapAngle = Math.Round(angle / (Math.PI / 4.0)) * (Math.PI / 4.0);
                                    newPos = new Point3d(
                                        otherPt.X + length * Math.Cos(snapAngle),
                                        otherPt.Y + length * Math.Sin(snapAngle),
                                        0);
                                }
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

                _annotation.ExpireDisplay();
                _canvas?.Invalidate();
            }
            else if (_isMultiDragging && _annotation.HasSelection)
            {
                var deltaX = pt.X - _multiDragStart.X;
                var deltaY = pt.Y - _multiDragStart.Y;
                foreach (var idx in _annotation.SelectedShapeIndices)
                {
                    _annotation.Shapes[idx].Move(deltaX, deltaY);
                }
                foreach (var obj in _selectedGHObjects)
                {
                    var pivot = obj.Attributes.Pivot;
                    obj.Attributes.Pivot = new PointF(
                        pivot.X + (float)deltaX,
                        pivot.Y + (float)deltaY);
                }
                _multiDragStart = pt;
                _annotation.ExpireDisplay();
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
                        _annotation.ClearSelection();
                    }
                }
                else
                {
                    var (shapeIdx, pointIdx) = _annotation.HitTest(new PointF((float)pt.X, (float)pt.Y));
                    _annotation.HoveredShapeIndex = shapeIdx >= 0 ? shapeIdx : -1;
                    _annotation.HoveredPointIndex = pointIdx >= 0 ? pointIdx : -1;
                }
                _canvas?.Invalidate();
            }
            else
            {
                var (shapeIdx, pointIdx) = _annotation.HitTest(new PointF((float)pt.X, (float)pt.Y));
                _annotation.HoveredShapeIndex = shapeIdx >= 0 ? shapeIdx : -1;
                _annotation.HoveredPointIndex = pointIdx >= 0 ? pointIdx : -1;
                _canvas?.Invalidate();
            }
        }

        private void Canvas_MouseUp(object sender, MouseEventArgs e)
        {
            if (!_isActivated || _annotation == null)
                return;

            bool shiftPressed = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;

            if (_isDrawing && (_drawMode == 1 || _drawMode == 3))
            {
                var pt = ScreenToCanvas(e.Location);
                Point3d endPt = pt;

                if (shiftPressed && _currentPoints.Count > 0)
                {
                    var lastPt = _currentPoints[_currentPoints.Count - 1];
                    double dx = endPt.X - lastPt.X;
                    double dy = endPt.Y - lastPt.Y;
                    double length = Math.Sqrt(dx * dx + dy * dy);

                    if (length > 0)
                    {
                        double angle = Math.Atan2(dy, dx);
                        double snapAngle = Math.Round(angle / (Math.PI / 4.0)) * (Math.PI / 4.0);
                        endPt = new Point3d(
                            lastPt.X + length * Math.Cos(snapAngle),
                            lastPt.Y + length * Math.Sin(snapAngle),
                            0);
                    }
                }

                double dist = endPt.DistanceTo(_currentPoints[_currentPoints.Count - 1]);
                if (dist > 5)
                {
                    _currentPoints.Add(endPt);
                    _annotation.ExpireDisplay();
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
                _annotation.SelectShapesInRectangle(rect, _additiveSelection);
                _isWindowSelecting = false;
                _annotation.ExpireDisplay();
                _canvas?.Invalidate();
            }
            else if (_clickStartPt.IsValid)
            {
                if (!_additiveSelection && _ghObjectInitialPositions.Count == 0 && !_isWindowSelecting)
                {
                    _annotation.ClearSelection();
                }
                _annotation.ExpireDisplay();
                _canvas?.Invalidate();
            }

            _isDragging = false;
            _isMultiDragging = false;
            _dragShapeIndex = -1;
            _dragPointIndex = -1;
            _clickStartPt = Point3d.Unset;
            ClearCapturedGHObjects();
            _ghDragSyncTimer?.Stop();
            _ghObjectInitialPositions.Clear();

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
            if (!_isActivated || _annotation == null)
                return;

            bool ctrlPressed = (Control.ModifierKeys & Keys.Control) == Keys.Control;

            if (ctrlPressed)
            {
                if (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0)
                {
                    if (_annotation.HasSelection)
                    {
                        foreach (var idx in _annotation.SelectedShapeIndices)
                        {
                            _annotation.Shapes[idx].ThicknessMultiplier = 1.0f;
                        }
                        _annotation.ExpireDisplay();
                        _canvas?.Invalidate();
                    }
                    else if (!_isDrawing)
                    {
                        _currentThicknessMultiplier = 1.0f;
                    }
                    e.SuppressKeyPress = true;
                    return;
                }
                else if (e.KeyCode == Keys.A)
                {
                    _annotation.SelectedShapeIndices.Clear();
                    for (int i = 0; i < _annotation.Shapes.Count; i++)
                    {
                        _annotation.SelectedShapeIndices.Add(i);
                    }
                    _annotation.ExpireDisplay();
                    _canvas?.Invalidate();
                    e.SuppressKeyPress = true;
                    return;
                }
                else if (e.KeyCode == Keys.D1 || e.KeyCode == Keys.NumPad1)
                {
                    if (_annotation.HasSelection)
                    {
                        foreach (var idx in _annotation.SelectedShapeIndices)
                        {
                            _annotation.Shapes[idx].ThicknessMultiplier = 0.5f;
                        }
                        _annotation.ExpireDisplay();
                        _canvas?.Invalidate();
                    }
                    else if (!_isDrawing)
                    {
                        _currentThicknessMultiplier = 0.5f;
                    }
                    e.SuppressKeyPress = true;
                    return;
                }
                else if (e.KeyCode == Keys.D2 || e.KeyCode == Keys.NumPad2)
                {
                    if (_annotation.HasSelection)
                    {
                        foreach (var idx in _annotation.SelectedShapeIndices)
                        {
                            _annotation.Shapes[idx].ThicknessMultiplier = 2.0f;
                        }
                        _annotation.ExpireDisplay();
                        _canvas?.Invalidate();
                    }
                    else if (!_isDrawing)
                    {
                        _currentThicknessMultiplier = 2.0f;
                    }
                    e.SuppressKeyPress = true;
                    return;
                }
                else if (e.KeyCode == Keys.D3 || e.KeyCode == Keys.NumPad3)
                {
                    if (_annotation.HasSelection)
                    {
                        foreach (var idx in _annotation.SelectedShapeIndices)
                        {
                            _annotation.Shapes[idx].ThicknessMultiplier = 3.0f;
                        }
                        _annotation.ExpireDisplay();
                        _canvas?.Invalidate();
                    }
                    else if (!_isDrawing)
                    {
                        _currentThicknessMultiplier = 3.0f;
                    }
                    e.SuppressKeyPress = true;
                    return;
                }
                else if (e.KeyCode == Keys.D)
                {
                    if (_annotation.HasSelection && !_isDrawing)
                    {
                        var newIndices = new List<int>();
                        foreach (var idx in _annotation.SelectedShapeIndices)
                        {
                            var clone = _annotation.Shapes[idx].Clone();
                            clone.Move(15, 15);
                            _annotation.Shapes.Add(clone);
                            newIndices.Add(_annotation.Shapes.Count - 1);
                        }
                        _annotation.SelectedShapeIndices.Clear();
                        _annotation.SelectedShapeIndices.AddRange(newIndices);
                        _annotation.ExpireDisplay();
                        _canvas?.Invalidate();
                        e.SuppressKeyPress = true;
                        return;
                    }
                }
            }

            if (e.KeyCode == Keys.Enter)
            {
                if (_currentPoints.Count >= 2)
                {
                    if (_drawMode == 1)
                    {
                        var polyShape = new PolylineShape(new List<Point3d>(_currentPoints), false);
                        polyShape.ThicknessMultiplier = _currentThicknessMultiplier;
                        polyShape.OverrideColor = _pendingColorOverride;
                        _annotation.Shapes.Add(polyShape);
                    }
                    else if (_drawMode == 3)
                    {
                        var curveShape = new CurveShape(new List<Point3d>(_currentPoints), false);
                        curveShape.ThicknessMultiplier = _currentThicknessMultiplier;
                        curveShape.OverrideColor = _pendingColorOverride;
                        _annotation.Shapes.Add(curveShape);
                    }
                    _currentPoints.Clear();
                    _isDrawing = false;
                    _pendingColorOverride = null;
                    _currentThicknessMultiplier = 1.0f;
                    _annotation.ExpireDisplay();
                    e.SuppressKeyPress = true;
                }
            }
            else if (e.KeyCode == Keys.Escape)
            {
                if (_drawMode == 1 && _currentPoints.Count >= 2)
                {
                    var polyShape = new PolylineShape(new List<Point3d>(_currentPoints), false);
                    polyShape.ThicknessMultiplier = _currentThicknessMultiplier;
                    polyShape.OverrideColor = _pendingColorOverride;
                    _annotation.Shapes.Add(polyShape);
                    _currentPoints.Clear();
                    _isDrawing = false;
                    _pendingColorOverride = null;
                    _currentThicknessMultiplier = 1.0f;
                    _annotation.ExpireDisplay();
                    e.SuppressKeyPress = true;
                }
                else if (_drawMode == 3 && _currentPoints.Count >= 2)
                {
                    var curveShape = new CurveShape(new List<Point3d>(_currentPoints), false);
                    curveShape.ThicknessMultiplier = _currentThicknessMultiplier;
                    curveShape.OverrideColor = _pendingColorOverride;
                    _annotation.Shapes.Add(curveShape);
                    _currentPoints.Clear();
                    _isDrawing = false;
                    _pendingColorOverride = null;
                    _currentThicknessMultiplier = 1.0f;
                    _annotation.ExpireDisplay();
                    e.SuppressKeyPress = true;
                }
                else if (_currentPoints.Count > 0 || _isDrawing)
                {
                    _currentPoints.Clear();
                    _isDrawing = false;
                    _pendingColorOverride = null;
                    _annotation.ExpireDisplay();
                    e.SuppressKeyPress = true;
                }
                else if (_annotation.HasSelection)
                {
                    _annotation.ClearSelection();
                    _annotation.ExpireDisplay();
                    _canvas?.Invalidate();
                    e.SuppressKeyPress = true;
                }
            }

            if (e.KeyCode == Keys.Delete)
            {
                if (_annotation.HasSelection)
                {
                    DeleteSelectedShapes();
                    e.SuppressKeyPress = true;
                }
            }

            _canvas?.Invalidate();
        }

        private void DeleteSelectedShapes()
        {
            if (_annotation.HasSelection)
            {
                var indicesToRemove = _annotation.SelectedShapeIndices.OrderByDescending(i => i).ToList();
                foreach (var idx in indicesToRemove)
                {
                    _annotation.Shapes.RemoveAt(idx);
                }
                _annotation.ClearSelection();
                _annotation.ExpireDisplay();
                _canvas?.Invalidate();
            }
        }

        public void ClearAll()
        {
            if (_annotation == null) return;

            _annotation.Shapes.Clear();
            _currentPoints.Clear();
            _annotation.ExpireDisplay();
            _canvas?.Invalidate();
        }

        public override bool AppendMenuItems(ToolStripDropDown menu)
        {
            Menu_AppendItem(menu, "Clear All", (s, e) => ClearAll());

            var modeMenu = new ToolStripMenuItem("Draw Mode");
            modeMenu.DropDownItems.Add(new ToolStripMenuItem("Line", null, (s, e) => { _drawMode = 0; _canvas?.Invalidate(); }));
            modeMenu.DropDownItems.Add(new ToolStripMenuItem("Polyline", null, (s, e) => { _drawMode = 1; _canvas?.Invalidate(); }));
            modeMenu.DropDownItems.Add(new ToolStripMenuItem("Frame", null, (s, e) => { _drawMode = 2; _canvas?.Invalidate(); }));
            modeMenu.DropDownItems.Add(new ToolStripMenuItem("Curve", null, (s, e) => { _drawMode = 3; _canvas?.Invalidate(); }));
            menu.Items.Insert(0, modeMenu);

            return base.AppendMenuItems(menu);
        }

        private void ShowColorPickerForSelected()
        {
            if (_annotation == null || !_annotation.HasSelection)
                return;

            var firstShape = _annotation.Shapes[_annotation.SelectedShapeIndices[0]];
            using (var dialog = new ColorDialog())
            {
                dialog.Color = firstShape.OverrideColor ?? _borderColor;
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    foreach (var idx in _annotation.SelectedShapeIndices)
                    {
                        _annotation.Shapes[idx].OverrideColor = dialog.Color;
                    }
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

        private ToolStripMenuItem CreateModeMenuItem(string label, int mode)
        {
            var item = new ToolStripMenuItem(label);
            item.Checked = (_drawMode == mode);
            item.Click += (s, e) =>
            {
                _drawMode = mode;
                _canvas?.Invalidate();
            };
            return item;
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
            if (!_annotation.HasSelection || _ghObjectInitialPositions.Count == 0)
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

                foreach (var idx in _annotation.SelectedShapeIndices)
                {
                    _annotation.Shapes[idx].Move(avgDeltaX, avgDeltaY);
                }

                var updatedPositions = new Dictionary<IGH_DocumentObject, PointF>();
                foreach (var kvp in _ghObjectInitialPositions)
                {
                    updatedPositions[kvp.Key] = kvp.Key.Attributes.Pivot;
                }
                _ghObjectInitialPositions = updatedPositions;

                _annotation.ExpireDisplay();
                _canvas?.Invalidate();
            }
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            UnregisterCanvasEvents();

            if (_annotation != null && document != null && _annotation is IGH_DocumentObject docObj)
            {
                document.RemoveObject(docObj, false);
                _annotation = null;
            }

            base.RemovedFromDocument(document);
        }

        public override void CreateAttributes()
        {
            m_attributes = new BorderModeUIAttributes(this);
        }

        internal BorderAnnotation Annotation => _annotation;
        public bool IsActivated => _isActivated;
        public Point3d DrawStart => _drawStart;
        public Point3d DrawEnd => _drawEnd;
        public bool IsDrawing => _isDrawing;
        public bool ShiftPressed => (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
        public bool CtrlPressed => (Control.ModifierKeys & Keys.Control) == Keys.Control;
        public int DrawMode => _drawMode;
        public List<Point3d> CurrentPoints => _currentPoints;
        public Point3d FrameFirstCorner => _frameFirstCorner;
        public float CurrentThicknessMultiplier => _currentThicknessMultiplier;
        public Color? PendingColorOverride => _pendingColorOverride;
    }
}
