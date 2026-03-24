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

        private float _lineThickness = 8f;
        private Color _borderColor = Color.Black;
        private int _drawMode = 0;
        private int _frameCornerCount = 0;
        private Point3d _frameFirstCorner;
        private float _currentThicknessMultiplier = 1.0f;
        private Color? _pendingColorOverride;
        private System.Drawing.Drawing2D.DashStyle _currentLineType = System.Drawing.Drawing2D.DashStyle.Solid;

        public System.Drawing.Drawing2D.DashStyle CurrentLineType => _currentLineType;

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
            : base("Hopper Draw", "HDraw", "Draw and manage canvas borders", "Draw", "Primitive")
        {
            CreateAttributes();
        }

        public override Guid ComponentGuid => new Guid("D2E3F4A5-B6C7-8901-2345-67890ABCDEF0");

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddBooleanParameter("Active", "A", "Enable border drawing", GH_ParamAccess.item, true);
            pManager.AddBooleanParameter("Show", "S", "Display borders on canvas", GH_ParamAccess.item, true);
            pManager.AddIntegerParameter("DrawMode", "M", "Drawing mode: 0=Line, 1=Polyline, 2=Frame, 3=Curve", GH_ParamAccess.item, 0);
            pManager.AddColourParameter("Color", "C", "Default border color. Right-click during drawing to set per-shape color override", GH_ParamAccess.item, Color.Black);
            pManager.AddNumberParameter("Thickness", "T", "Base thickness. Use Ctrl+1 (0.5x), Ctrl+2 (2x), Ctrl+3 (1x default) to modify", GH_ParamAccess.item, 8.0);
            pManager.AddIntegerParameter("LineType", "L", "Line type: 0=Solid, 1=Dash, 2=Dot, 3=DashDot. Use Ctrl+B to cycle on selected shapes", GH_ParamAccess.item, 0);
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
            _drawMode = drawMode;
            _currentLineType = (System.Drawing.Drawing2D.DashStyle)lineType;

            if (_drawMode != 1 && _drawMode != 3)
            {
                _currentPoints.Clear();
            }

            bool wasActivated = _isActivated;
            _isActivated = active;

            if (_isActivated && !wasActivated)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[HopperDraw SolveInstance] Activating!");
#endif
                RegisterCanvasEvents();
            }
            else if (!_isActivated && wasActivated)
            {
                UnregisterCanvasEvents();
            }

#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[HopperDraw SolveInstance] Updating. Shapes count = {Shapes.Count}");
#endif
            Visible = show && !this.Locked;
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
            if (!_isActivated)
                return;

            if (e.Button == MouseButtons.Right)
            {
                if (_isDrawing)
                {
                    ShowColorPickerForDrawing();
                }
                else if (HasSelection)
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
                    Shapes.Add(lineShape);
                    MarkDocumentModified();
                    _isDrawing = false;
                    _pendingColorOverride = null;
                    _currentThicknessMultiplier = 1.0f;
                    ExpireDisplay();
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
                    ExpireDisplay();
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
                    Shapes.Add(polyShape);
                    MarkDocumentModified();
                    _currentPoints.Clear();
                    _isDrawing = false;
                    _pendingColorOverride = null;
                    _currentThicknessMultiplier = 1.0f;
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
                    if (ctrlPressed)
                    {
                        _currentPoints.Add(pt);
                        _isDrawing = true;
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
                    Shapes.Add(curveShape);
                    MarkDocumentModified();
                    _currentPoints.Clear();
                    _isDrawing = false;
                    _pendingColorOverride = null;
                    _currentThicknessMultiplier = 1.0f;
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
                else if (ctrlPressed)
                {
                    _frameFirstCorner = pt;
                    _frameCornerCount = 1;
                    _isDrawing = true;
                    _lastClickTime = DateTime.Now;
                    _lastClickPosition = pt;
                    ExpireDisplay();
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

            var (shapeIdx, pointIdx) = HitTest(new PointF((float)pt.X, (float)pt.Y));

            if (shapeIdx >= 0)
            {
                bool isAlreadySelected = SelectedShapeIndices.Contains(shapeIdx);
                bool hasSelectedGH = HasSelectedGHObjects();
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
                _additiveSelection = ctrlPressed;
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
            Shapes.Add(frameShape);
            MarkDocumentModified();
            _pendingColorOverride = null;
            _currentThicknessMultiplier = 1.0f;
            ExpireDisplay();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isActivated)
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
            if (!_isActivated)
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
            if (!_isActivated)
                return;

            bool ctrlPressed = (Control.ModifierKeys & Keys.Control) == Keys.Control;

            if (ctrlPressed)
            {
                if (e.KeyCode == Keys.A)
                {
                    SelectedShapeIndices.Clear();
                    for (int i = 0; i < Shapes.Count; i++)
                    {
                        SelectedShapeIndices.Add(i);
                    }
                    ExpireDisplay();
                    _canvas?.Invalidate();
                    e.SuppressKeyPress = true;
                    return;
                }
                else if (e.KeyCode == Keys.D1 || e.KeyCode == Keys.NumPad1)
                {
                    if (HasSelection)
                    {
                        foreach (var idx in SelectedShapeIndices)
                        {
                            Shapes[idx].ThicknessMultiplier = 0.5f;
                        }
                        MarkDocumentModified();
                        ExpireDisplay();
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
                    if (HasSelection)
                    {
                        foreach (var idx in SelectedShapeIndices)
                        {
                            Shapes[idx].ThicknessMultiplier = 2.0f;
                        }
                        MarkDocumentModified();
                        ExpireDisplay();
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
                    if (HasSelection)
                    {
                        foreach (var idx in SelectedShapeIndices)
                        {
                            Shapes[idx].ThicknessMultiplier = 1.0f;
                        }
                        MarkDocumentModified();
                        ExpireDisplay();
                        _canvas?.Invalidate();
                    }
                    else
                    {
                        _currentThicknessMultiplier = 1.0f;
                    }
                    e.SuppressKeyPress = true;
                    return;
                }
                else if (e.KeyCode == Keys.D)
                {
                    if (HasSelection && !_isDrawing)
                    {
                        var newIndices = new List<int>();
                        foreach (var idx in SelectedShapeIndices)
                        {
                            var clone = Shapes[idx].Clone();
                            clone.Move(30, 30);
                            Shapes.Add(clone);
                            newIndices.Add(Shapes.Count - 1);
                        }
                        MarkDocumentModified();
                        SelectedShapeIndices.Clear();
                        SelectedShapeIndices.AddRange(newIndices);
                        ExpireDisplay();
                        _canvas?.Invalidate();
                        e.SuppressKeyPress = true;
                        return;
                    }
                }
                else if (e.KeyCode == Keys.B)
                {
                    if (HasSelection)
                    {
                        foreach (var idx in SelectedShapeIndices)
                        {
                            int currentType = (int)Shapes[idx].LineType;
                            int nextType = (currentType + 1) % 5;
                            Shapes[idx].LineType = (System.Drawing.Drawing2D.DashStyle)nextType;
                        }
                        MarkDocumentModified();
                        ExpireDisplay();
                        _canvas?.Invalidate();
                    }
                    else if (!_isDrawing)
                    {
                        int currentType = (int)_currentLineType;
                        int nextType = (currentType + 1) % 5;
                        _currentLineType = (System.Drawing.Drawing2D.DashStyle)nextType;
                    }
                    e.SuppressKeyPress = true;
                    return;
                }
                else if (e.KeyCode == Keys.G)
                {
                    bool shiftPressed = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
                    if (ctrlPressed && shiftPressed)
                    {
                        if (!_isDrawing && HasSelection)
                        {
                            UngroupSelectedGroups();
                            MarkDocumentModified();
                            ExpireDisplay();
                            _canvas?.Invalidate();
                        }
                    }
                    else if (ctrlPressed)
                    {
                        if (!_isDrawing && SelectedShapeIndices.Count > 0)
                        {
                            GroupSelectedShapes();
                            MarkDocumentModified();
                            ExpireDisplay();
                            _canvas?.Invalidate();
                        }
                    }
                    e.SuppressKeyPress = true;
                    return;
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
                        Shapes.Add(polyShape);
                    }
                    else if (_drawMode == 3)
                    {
                        var curveShape = new CurveShape(new List<Point3d>(_currentPoints), false);
                        curveShape.ThicknessMultiplier = _currentThicknessMultiplier;
                        curveShape.OverrideColor = _pendingColorOverride;
                        Shapes.Add(curveShape);
                    }
                    MarkDocumentModified();
                    _currentPoints.Clear();
                    _isDrawing = false;
                    _pendingColorOverride = null;
                    _currentThicknessMultiplier = 1.0f;
                    ExpireDisplay();
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
                    Shapes.Add(polyShape);
                    MarkDocumentModified();
                    _currentPoints.Clear();
                    _isDrawing = false;
                    _pendingColorOverride = null;
                    _currentThicknessMultiplier = 1.0f;
                    ExpireDisplay();
                    e.SuppressKeyPress = true;
                }
                else if (_drawMode == 3 && _currentPoints.Count >= 2)
                {
                    var curveShape = new CurveShape(new List<Point3d>(_currentPoints), false);
                    curveShape.ThicknessMultiplier = _currentThicknessMultiplier;
                    curveShape.OverrideColor = _pendingColorOverride;
                    Shapes.Add(curveShape);
                    MarkDocumentModified();
                    _currentPoints.Clear();
                    _isDrawing = false;
                    _pendingColorOverride = null;
                    _currentThicknessMultiplier = 1.0f;
                    ExpireDisplay();
                    e.SuppressKeyPress = true;
                }
                else if (_currentPoints.Count > 0 || _isDrawing)
                {
                    _currentPoints.Clear();
                    _isDrawing = false;
                    _pendingColorOverride = null;
                    ExpireDisplay();
                    e.SuppressKeyPress = true;
                }
                else if (HasSelection)
                {
                    ClearSelection();
                    ExpireDisplay();
                    _canvas?.Invalidate();
                    e.SuppressKeyPress = true;
                }
            }

            if (e.KeyCode == Keys.Delete)
            {
                if (HasSelection)
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
            _currentPoints.Clear();
            ExpireDisplay();
            _canvas?.Invalidate();
        }

        public override bool AppendMenuItems(ToolStripDropDown menu)
        {
            Menu_AppendItem(menu, "Clear All", (s, e) => ClearAll());

            return base.AppendMenuItems(menu);
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

    public abstract class DrawShape
    {
        public int Id { get; set; }
        public float ThicknessMultiplier { get; set; } = 1.0f;
        public Color? OverrideColor { get; set; }
        public DashStyle LineType { get; set; } = DashStyle.Solid;

        public abstract void Render(Graphics g, Pen pen, float thickness);
        public abstract (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness);
        public abstract PointF[] GetPoints();
        public abstract void Move(double dx, double dy);
        public abstract void MovePoint(int pointIndex, Point3d newPos);
        public abstract void Write(GH_IWriter writer, int index);
        public abstract void Read(GH_IReader reader, int index);
        public abstract DrawShape Clone();

        protected void WriteCommonProperties(GH_IWriter writer, int index, int type)
        {
            writer.SetInt32($"Type{index}", type);
            writer.SetDouble($"ThickMult{index}", ThicknessMultiplier);
            writer.SetInt32($"Color{index}", OverrideColor.HasValue ? OverrideColor.Value.ToArgb() : 0);
            writer.SetInt32($"LineType{index}", (int)LineType);
        }

        protected void ReadCommonProperties(GH_IReader reader, int index)
        {
            ThicknessMultiplier = (float)reader.GetDouble($"ThickMult{index}");
            int colorArgb = reader.GetInt32($"Color{index}");
            if (colorArgb != 0)
            {
                OverrideColor = Color.FromArgb(colorArgb);
            }
            if (reader.GetInt32($"LineType{index}") is int lineType && lineType >= 0 && lineType <= 3)
            {
                LineType = (DashStyle)lineType;
            }
        }
    }

    public class LineShape : DrawShape
    {
        public Point3d Start { get; set; }
        public Point3d End { get; set; }

        public LineShape() { Start = Point3d.Unset; End = Point3d.Unset; }
        public LineShape(Point3d start, Point3d end) { Start = start; End = end; }

        public override void Render(Graphics g, Pen pen, float thickness)
        {
            if (!Start.IsValid || !End.IsValid) return;
            g.DrawLine(pen, (float)Start.X, (float)Start.Y, (float)End.X, (float)End.Y);
        }

        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness)
        {
            if (GeometryUtilities.Distance(pt, new PointF((float)Start.X, (float)Start.Y)) <= tolerance)
                return (true, 0);
            if (GeometryUtilities.Distance(pt, new PointF((float)End.X, (float)End.Y)) <= tolerance)
                return (true, 1);
            if (GeometryUtilities.DistanceToSegment(pt, new PointF((float)Start.X, (float)Start.Y), new PointF((float)End.X, (float)End.Y)) <= thickness / 2 + tolerance)
                return (true, -1);
            return (false, -1);
        }

        public override PointF[] GetPoints() => new PointF[] { new PointF((float)Start.X, (float)Start.Y), new PointF((float)End.X, (float)End.Y) };

        public override void Move(double dx, double dy)
        {
            Start = new Point3d(Start.X + dx, Start.Y + dy, 0);
            End = new Point3d(End.X + dx, End.Y + dy, 0);
        }

        public override void MovePoint(int pointIndex, Point3d newPos)
        {
            if (pointIndex == 0) Start = newPos;
            else if (pointIndex == 1) End = newPos;
        }

        public override void Write(GH_IWriter writer, int index)
        {
            writer.SetDouble($"StartX{index}", Start.X);
            writer.SetDouble($"StartY{index}", Start.Y);
            writer.SetDouble($"StartZ{index}", Start.Z);
            writer.SetDouble($"EndX{index}", End.X);
            writer.SetDouble($"EndY{index}", End.Y);
            writer.SetDouble($"EndZ{index}", End.Z);
            WriteCommonProperties(writer, index, 0);
        }

        public override void Read(GH_IReader reader, int index)
        {
            double sx = reader.GetDouble($"StartX{index}");
            double sy = reader.GetDouble($"StartY{index}");
            double sz = reader.GetDouble($"StartZ{index}");
            double ex = reader.GetDouble($"EndX{index}");
            double ey = reader.GetDouble($"EndY{index}");
            double ez = reader.GetDouble($"EndZ{index}");
            Start = new Point3d(sx, sy, sz);
            End = new Point3d(ex, ey, ez);
            ReadCommonProperties(reader, index);
        }

        public override DrawShape Clone()
        {
            return new LineShape(Start, End)
            {
                ThicknessMultiplier = ThicknessMultiplier,
                OverrideColor = OverrideColor,
                LineType = LineType,
                Id = HopperDraw.GetNextId()
            };
        }
    }

    public class PolylineShape : DrawShape
    {
        public List<Point3d> Points { get; set; } = new List<Point3d>();
        public bool Closed { get; set; } = false;

        public PolylineShape() { }
        public PolylineShape(List<Point3d> points, bool closed = false) { Points = points; Closed = closed; }

        public override void Render(Graphics g, Pen pen, float thickness)
        {
            if (Points.Count < 2) return;
            var pts = Points.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();
            if (Closed && pts.Length > 2)
            {
                g.DrawPolygon(pen, pts);
            }
            else
            {
                g.DrawLines(pen, pts);
            }
        }

        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness)
        {
            for (int i = 0; i < Points.Count; i++)
            {
                if (GeometryUtilities.Distance(pt, new PointF((float)Points[i].X, (float)Points[i].Y)) <= tolerance)
                    return (true, i);
            }
            for (int i = 0; i < Points.Count - 1; i++)
            {
                if (GeometryUtilities.DistanceToSegment(pt, new PointF((float)Points[i].X, (float)Points[i].Y), new PointF((float)Points[i + 1].X, (float)Points[i + 1].Y)) <= thickness / 2 + tolerance)
                    return (true, -1);
            }
            if (Closed && Points.Count > 2)
            {
                if (GeometryUtilities.DistanceToSegment(pt, new PointF((float)Points[Points.Count - 1].X, (float)Points[Points.Count - 1].Y), new PointF((float)Points[0].X, (float)Points[0].Y)) <= thickness / 2 + tolerance)
                    return (true, -1);
            }
            return (false, -1);
        }

        public override PointF[] GetPoints() => Points.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();

        public override void Move(double dx, double dy)
        {
            for (int i = 0; i < Points.Count; i++)
                Points[i] = new Point3d(Points[i].X + dx, Points[i].Y + dy, 0);
        }

        public override void MovePoint(int pointIndex, Point3d newPos)
        {
            if (pointIndex >= 0 && pointIndex < Points.Count)
                Points[pointIndex] = newPos;
        }

        public override void Write(GH_IWriter writer, int index)
        {
            writer.SetInt32($"PointCount{index}", Points.Count);
            writer.SetBoolean($"Closed{index}", Closed);
            for (int i = 0; i < Points.Count; i++)
            {
                writer.SetDouble($"Pt{i}X{index}", Points[i].X);
                writer.SetDouble($"Pt{i}Y{index}", Points[i].Y);
                writer.SetDouble($"Pt{i}Z{index}", Points[i].Z);
            }
            WriteCommonProperties(writer, index, 1);
        }

        public override void Read(GH_IReader reader, int index)
        {
            int count = reader.GetInt32($"PointCount{index}");
            Closed = reader.GetBoolean($"Closed{index}");
            Points.Clear();
            for (int i = 0; i < count; i++)
            {
                double px = reader.GetDouble($"Pt{i}X{index}");
                double py = reader.GetDouble($"Pt{i}Y{index}");
                double pz = reader.GetDouble($"Pt{i}Z{index}");
                Points.Add(new Point3d(px, py, pz));
            }
            ReadCommonProperties(reader, index);
        }

        public override DrawShape Clone()
        {
            return new PolylineShape(new List<Point3d>(Points), Closed)
            {
                ThicknessMultiplier = ThicknessMultiplier,
                OverrideColor = OverrideColor,
                LineType = LineType,
                Id = HopperDraw.GetNextId()
            };
        }
    }

    public class CurveShape : DrawShape
    {
        public List<Point3d> Points { get; set; } = new List<Point3d>();
        public bool Closed { get; set; } = false;
        private const float Tension = 0.5f;

        public CurveShape() { }
        public CurveShape(List<Point3d> points, bool closed = false) { Points = points; Closed = closed; }

        public override void Render(Graphics g, Pen pen, float thickness)
        {
            if (Points.Count < 2) return;
            var pts = Points.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();
            if (Closed && pts.Length > 2)
            {
                g.DrawClosedCurve(pen, pts, Tension, System.Drawing.Drawing2D.FillMode.Winding);
            }
            else
            {
                g.DrawCurve(pen, pts, Tension);
            }
        }

        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness)
        {
            for (int i = 0; i < Points.Count; i++)
            {
                if (GeometryUtilities.Distance(pt, new PointF((float)Points[i].X, (float)Points[i].Y)) <= tolerance)
                    return (true, i);
            }

            var curvePoints = SampleCurve(20);
            for (int i = 0; i < curvePoints.Count - 1; i++)
            {
                if (GeometryUtilities.DistanceToSegment(pt, curvePoints[i], curvePoints[i + 1]) <= thickness / 2 + tolerance)
                    return (true, -1);
            }
            return (false, -1);
        }

        public override PointF[] GetPoints() => Points.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();

        public override void Move(double dx, double dy)
        {
            for (int i = 0; i < Points.Count; i++)
                Points[i] = new Point3d(Points[i].X + dx, Points[i].Y + dy, 0);
        }

        public override void MovePoint(int pointIndex, Point3d newPos)
        {
            if (pointIndex >= 0 && pointIndex < Points.Count)
                Points[pointIndex] = newPos;
        }

        public override void Write(GH_IWriter writer, int index)
        {
            writer.SetInt32($"PointCount{index}", Points.Count);
            writer.SetBoolean($"Closed{index}", Closed);
            for (int i = 0; i < Points.Count; i++)
            {
                writer.SetDouble($"Pt{i}X{index}", Points[i].X);
                writer.SetDouble($"Pt{i}Y{index}", Points[i].Y);
                writer.SetDouble($"Pt{i}Z{index}", Points[i].Z);
            }
            WriteCommonProperties(writer, index, 3);
        }

        public override void Read(GH_IReader reader, int index)
        {
            int count = reader.GetInt32($"PointCount{index}");
            Closed = reader.GetBoolean($"Closed{index}");
            Points.Clear();
            for (int i = 0; i < count; i++)
            {
                double px = reader.GetDouble($"Pt{i}X{index}");
                double py = reader.GetDouble($"Pt{i}Y{index}");
                double pz = reader.GetDouble($"Pt{i}Z{index}");
                Points.Add(new Point3d(px, py, pz));
            }
            ReadCommonProperties(reader, index);
        }

        private List<PointF> SampleCurve(int segmentsPerSpan)
        {
            var result = new List<PointF>();
            if (Points.Count < 2) return result;

            var pts = Points.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();

            if (Closed && pts.Length > 2)
            {
                int totalSegments = pts.Length * segmentsPerSpan;
                for (int i = 0; i < totalSegments; i++)
                {
                    float t = (float)i / totalSegments;
                    int idx = (int)(t * pts.Length);
                    float localT = (t * pts.Length) - idx;
                    int i0 = idx;
                    int i1 = (idx + 1) % pts.Length;
                    int i2 = (idx + 2) % pts.Length;
                    int i3 = (idx + 3) % pts.Length;

                    if (i1 >= pts.Length) i1 -= pts.Length;
                    if (i2 >= pts.Length) i2 -= pts.Length;
                    if (i3 >= pts.Length) i3 -= pts.Length;

                    var p = CatmullRom(pts[i0], pts[i1], pts[i2], pts[i3], localT);
                    result.Add(p);
                }
            }
            else
            {
                int totalSegments = (pts.Length - 1) * segmentsPerSpan;
                for (int i = 0; i < totalSegments; i++)
                {
                    float t = (float)i / totalSegments;
                    int span = (int)(t * (pts.Length - 1));
                    if (span >= pts.Length - 1) span = pts.Length - 2;
                    float localT = (t * (pts.Length - 1)) - span;

                    int i0 = Math.Max(0, span - 1);
                    int i1 = span;
                    int i2 = Math.Min(pts.Length - 1, span + 1);
                    int i3 = Math.Min(pts.Length - 1, span + 2);

                    var p = CatmullRom(pts[i0], pts[i1], pts[i2], pts[i3], localT);
                    result.Add(p);
                }
                result.Add(pts[pts.Length - 1]);
            }

            return result;
        }

        private PointF CatmullRom(PointF p0, PointF p1, PointF p2, PointF p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return new PointF(
                0.5f * ((2 * p1.X) + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3),
                0.5f * ((2 * p1.Y) + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3)
            );
        }

        public override DrawShape Clone()
        {
            return new CurveShape(new List<Point3d>(Points), Closed)
            {
                ThicknessMultiplier = ThicknessMultiplier,
                OverrideColor = OverrideColor,
                LineType = LineType,
                Id = HopperDraw.GetNextId()
            };
        }
    }

    public class FrameShape : DrawShape
    {
        public Point3d TopLeft { get; set; }
        public Point3d TopRight { get; set; }
        public Point3d BottomRight { get; set; }
        public Point3d BottomLeft { get; set; }

        public FrameShape() { }
        public FrameShape(Point3d topLeft, Point3d topRight, Point3d bottomRight, Point3d bottomLeft)
        {
            TopLeft = topLeft; TopRight = topRight; BottomRight = bottomRight; BottomLeft = bottomLeft;
        }

        public override void Render(Graphics g, Pen pen, float thickness)
        {
            var points = new PointF[]
            {
                new PointF((float)TopLeft.X, (float)TopLeft.Y),
                new PointF((float)TopRight.X, (float)TopRight.Y),
                new PointF((float)BottomRight.X, (float)BottomRight.Y),
                new PointF((float)BottomLeft.X, (float)BottomLeft.Y),
                new PointF((float)TopLeft.X, (float)TopLeft.Y)
            };
            g.DrawPolygon(pen, points);
        }

        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness)
        {
            var corners = new PointF[] { new PointF((float)TopLeft.X, (float)TopLeft.Y), new PointF((float)TopRight.X, (float)TopRight.Y), new PointF((float)BottomRight.X, (float)BottomRight.Y), new PointF((float)BottomLeft.X, (float)BottomLeft.Y) };
            for (int i = 0; i < 4; i++)
                if (GeometryUtilities.Distance(pt, corners[i]) <= tolerance) return (true, i);
            if (GeometryUtilities.DistanceToSegment(pt, corners[0], corners[1]) <= thickness / 2 + tolerance) return (true, -1);
            if (GeometryUtilities.DistanceToSegment(pt, corners[1], corners[2]) <= thickness / 2 + tolerance) return (true, -1);
            if (GeometryUtilities.DistanceToSegment(pt, corners[2], corners[3]) <= thickness / 2 + tolerance) return (true, -1);
            if (GeometryUtilities.DistanceToSegment(pt, corners[3], corners[0]) <= thickness / 2 + tolerance) return (true, -1);
            return (false, -1);
        }

        public override PointF[] GetPoints() => new PointF[] { new PointF((float)TopLeft.X, (float)TopLeft.Y), new PointF((float)TopRight.X, (float)TopRight.Y), new PointF((float)BottomRight.X, (float)BottomRight.Y), new PointF((float)BottomLeft.X, (float)BottomLeft.Y) };

        public override void Move(double dx, double dy)
        {
            TopLeft = new Point3d(TopLeft.X + dx, TopLeft.Y + dy, 0);
            TopRight = new Point3d(TopRight.X + dx, TopRight.Y + dy, 0);
            BottomRight = new Point3d(BottomRight.X + dx, BottomRight.Y + dy, 0);
            BottomLeft = new Point3d(BottomLeft.X + dx, BottomLeft.Y + dy, 0);
        }

        public override void MovePoint(int pointIndex, Point3d newPos)
        {
            switch (pointIndex)
            {
                case 0: TopLeft = newPos; break;
                case 1: TopRight = newPos; break;
                case 2: BottomRight = newPos; break;
                case 3: BottomLeft = newPos; break;
            }
        }

        public override void Write(GH_IWriter writer, int index)
        {
            writer.SetDouble($"TLX{index}", TopLeft.X);
            writer.SetDouble($"TLY{index}", TopLeft.Y);
            writer.SetDouble($"TLZ{index}", TopLeft.Z);
            writer.SetDouble($"TRX{index}", TopRight.X);
            writer.SetDouble($"TRY{index}", TopRight.Y);
            writer.SetDouble($"TRZ{index}", TopRight.Z);
            writer.SetDouble($"BRX{index}", BottomRight.X);
            writer.SetDouble($"BRY{index}", BottomRight.Y);
            writer.SetDouble($"BRZ{index}", BottomRight.Z);
            writer.SetDouble($"BLX{index}", BottomLeft.X);
            writer.SetDouble($"BLY{index}", BottomLeft.Y);
            writer.SetDouble($"BLZ{index}", BottomLeft.Z);
            WriteCommonProperties(writer, index, 2);
        }

        public override void Read(GH_IReader reader, int index)
        {
            TopLeft = new Point3d(reader.GetDouble($"TLX{index}"), reader.GetDouble($"TLY{index}"), reader.GetDouble($"TLZ{index}"));
            TopRight = new Point3d(reader.GetDouble($"TRX{index}"), reader.GetDouble($"TRY{index}"), reader.GetDouble($"TRZ{index}"));
            BottomRight = new Point3d(reader.GetDouble($"BRX{index}"), reader.GetDouble($"BRY{index}"), reader.GetDouble($"BRZ{index}"));
            BottomLeft = new Point3d(reader.GetDouble($"BLX{index}"), reader.GetDouble($"BLY{index}"), reader.GetDouble($"BLZ{index}"));
            ReadCommonProperties(reader, index);
        }

        public override DrawShape Clone()
        {
            return new FrameShape(TopLeft, TopRight, BottomRight, BottomLeft)
            {
                ThicknessMultiplier = ThicknessMultiplier,
                OverrideColor = OverrideColor,
                LineType = LineType,
                Id = HopperDraw.GetNextId()
            };
        }
    }

    public class DrawShapeGroup : DrawShape
    {
        public List<DrawShape> Children { get; set; } = new List<DrawShape>();

        public override void Render(Graphics g, Pen pen, float thickness)
        {
            foreach (var child in Children)
            {
                child.Render(g, pen, thickness);
            }
        }

        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness)
        {
            foreach (var child in Children)
            {
                var (hit, ptIdx) = child.HitTest(pt, tolerance, thickness);
                if (hit) return (true, -1);
            }
            return (false, -1);
        }

        public override PointF[] GetPoints()
        {
            if (Children.Count == 0) return new PointF[0];

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var child in Children)
            {
                foreach (var p in child.GetPoints())
                {
                    minX = Math.Min(minX, p.X);
                    minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X);
                    maxY = Math.Max(maxY, p.Y);
                }
            }
            return new PointF[]
            {
                new PointF((float)minX, (float)minY),
                new PointF((float)maxX, (float)minY),
                new PointF((float)maxX, (float)maxY),
                new PointF((float)minX, (float)maxY)
            };
        }

        internal RectangleF GetBoundingBox()
        {
            if (Children.Count == 0) return RectangleF.Empty;

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var child in Children)
            {
                foreach (var p in child.GetPoints())
                {
                    minX = Math.Min(minX, p.X);
                    minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X);
                    maxY = Math.Max(maxY, p.Y);
                }
            }
            return new RectangleF((float)minX, (float)minY, (float)(maxX - minX), (float)(maxY - minY));
        }

        public override void Move(double dx, double dy)
        {
            foreach (var child in Children)
            {
                child.Move(dx, dy);
            }
        }

        public override void MovePoint(int pointIndex, Point3d newPos)
        {
        }

        public override void Write(GH_IWriter writer, int index)
        {
            writer.SetInt32($"GroupChildCount{index}", Children.Count);
            for (int i = 0; i < Children.Count; i++)
            {
                Children[i].Write(writer, 1000 * index + i);
            }
            WriteCommonProperties(writer, index, 4);
        }

        public override void Read(GH_IReader reader, int index)
        {
            int childCount = reader.GetInt32($"GroupChildCount{index}");
            Children.Clear();
            for (int i = 0; i < childCount; i++)
            {
                int childIndex = 1000 * index + i;
                int type = reader.GetInt32($"Type{childIndex}");
                DrawShape child;
                switch (type)
                {
                    case 0: child = new LineShape(); break;
                    case 1: child = new PolylineShape(); break;
                    case 2: child = new FrameShape(); break;
                    case 3: child = new CurveShape(); break;
                    default: child = new LineShape(); break;
                }
                child.Read(reader, childIndex);
                Children.Add(child);
            }
            ReadCommonProperties(reader, index);
        }

        public override DrawShape Clone()
        {
            var group = new DrawShapeGroup();
            foreach (var child in Children)
            {
                var clonedChild = child.Clone();
                clonedChild.ThicknessMultiplier = ThicknessMultiplier;
                clonedChild.OverrideColor = OverrideColor;
                clonedChild.LineType = LineType;
                group.Children.Add(clonedChild);
            }
            group.Id = HopperDraw.GetNextId();
            return group;
        }
    }
}
