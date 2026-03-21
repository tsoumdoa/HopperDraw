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

        private DateTime _lastClickTime = DateTime.MinValue;
        private Point3d _lastClickPosition = Point3d.Unset;

        private GH_Canvas _canvas;
        private bool _eventsRegistered;

        private BorderAnnotation _annotation;

        private float _lineThickness = 8f;
        private Color _borderColor = Color.Black;
        private int _drawMode = 0;
        private int _frameCornerCount = 0;
        private Point3d _frameFirstCorner;

        private List<Point3d> _currentPoints = new List<Point3d>();

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
            pManager.AddIntegerParameter("DrawMode", "M", "Drawing mode", GH_ParamAccess.item, 0);
            pManager.AddColourParameter("Color", "C", "Border color", GH_ParamAccess.item, Color.Black);
            pManager.AddNumberParameter("Thickness", "T", "Line thickness", GH_ParamAccess.item, 8.0);
        }

        protected override void AfterSolveInstance()
        {
            var drawModeParam = Params.Input[2] as Param_Integer;
            if (drawModeParam != null)
            {
                drawModeParam.AddNamedValue("Line", 0);
                drawModeParam.AddNamedValue("Polyline", 1);
                drawModeParam.AddNamedValue("Frame", 2);
            }
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
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
                EnsureAnnotation();
                RegisterCanvasEvents();
            }
            else if (!_isActivated && wasActivated)
            {
                UnregisterCanvasEvents();
            }

            if (_annotation != null)
            {
                _annotation.Visible = show && !this.Locked;
                _annotation.BorderColor = _borderColor;
                _annotation.LineThickness = _lineThickness;
            }

            ExpirePreview(false);
        }

        private void EnsureAnnotation()
        {
            var doc = OnPingDocument();
            if (doc == null) return;

            if (_annotation != null)
                return;

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

            if (e.Button != MouseButtons.Left)
                return;

            var pt = ScreenToCanvas(e.Location);

            var timeSinceLastClick = DateTime.Now - _lastClickTime;
            bool isDoubleClick = (timeSinceLastClick.TotalMilliseconds < 500) &&
                                (_lastClickPosition.DistanceTo(pt) < 10);

            bool ctrlPressed = (Control.ModifierKeys & Keys.Control) == Keys.Control;

            if (_drawMode == 0 && ctrlPressed)
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
                    _annotation.Shapes.Add(new PolylineShape(new List<Point3d>(_currentPoints), true));
                    _currentPoints.Clear();
                    _isDrawing = false;
                    _lastClickTime = DateTime.Now;
                    _lastClickPosition = pt;
                    _annotation.ExpireDisplay();
                    _canvas?.Invalidate();
                    return;
                }
            }
            else if (_drawMode == 2 && ctrlPressed)
            {
                if (_frameCornerCount == 0)
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
                else if (_frameCornerCount == 1)
                {
                    _frameCornerCount = 0;
                    _isDrawing = false;
                    CreateFrame(_frameFirstCorner, pt);
                    _lastClickTime = DateTime.Now;
                    _lastClickPosition = pt;
                    _annotation.ExpireDisplay();
                    _canvas?.Invalidate();
                    return;
                }
            }

            if (_drawMode == 1 && _isDrawing)
            {
                _lastClickTime = DateTime.Now;
                _lastClickPosition = pt;
                return;
            }

            var (shapeIdx, pointIdx) = _annotation.HitTest(new PointF((float)pt.X, (float)pt.Y));

            if (shapeIdx >= 0)
            {
                _annotation.SelectedShapeIndex = shapeIdx;
                _annotation.SelectedPointIndex = pointIdx;
                _dragShapeIndex = shapeIdx;
                _dragPointIndex = pointIdx;

                var shape = _annotation.Shapes[shapeIdx];
                if (shape is FrameShape)
                {
                    _isFrameDragging = true;
                    _dragFrameOffset = pt;
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
                _annotation.ClearSelection();
                _isDragging = false;
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

            _annotation.Shapes.Add(new FrameShape(topLeft, topRight, bottomRight, bottomLeft));
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

                        double w = newPos.X - fixedCorner.X;
                        double h = newPos.Y - fixedCorner.Y;

                        switch (_dragPointIndex)
                        {
                            case 0:
                                frame.TopLeft = newPos;
                                frame.TopRight = new Point3d(fixedCorner.X + w, newPos.Y, 0);
                                frame.BottomLeft = new Point3d(newPos.X, fixedCorner.Y + h, 0);
                                frame.BottomRight = fixedCorner;
                                break;
                            case 1:
                                frame.TopRight = newPos;
                                frame.TopLeft = new Point3d(newPos.X - w, fixedCorner.Y + h, 0);
                                frame.BottomRight = new Point3d(newPos.X, fixedCorner.Y, 0);
                                frame.BottomLeft = fixedCorner;
                                break;
                            case 2:
                                frame.BottomRight = newPos;
                                frame.BottomLeft = new Point3d(fixedCorner.X, newPos.Y, 0);
                                frame.TopRight = new Point3d(newPos.X, fixedCorner.Y + h, 0);
                                frame.TopLeft = fixedCorner;
                                break;
                            case 3:
                                frame.BottomLeft = newPos;
                                frame.BottomRight = new Point3d(fixedCorner.X + w, newPos.Y, 0);
                                frame.TopLeft = new Point3d(newPos.X, fixedCorner.Y + h, 0);
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
                        shape.MovePoint(_dragPointIndex, pt);
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

            if (_isDrawing && _drawMode == 0)
            {
                double dx = _drawEnd.X - _drawStart.X;
                double dy = _drawEnd.Y - _drawStart.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);

                if (length > 5)
                {
                    Point3d end = _drawEnd;

                    if (shiftPressed)
                    {
                        double angle = Math.Atan2(dy, dx);
                        double snapAngle = Math.Round(angle / (Math.PI / 4.0)) * (Math.PI / 4.0);
                        end = new Point3d(
                            _drawStart.X + length * Math.Cos(snapAngle),
                            _drawStart.Y + length * Math.Sin(snapAngle),
                            0);
                    }

                    _annotation.Shapes.Add(new LineShape(_drawStart, end));
                    _annotation.ExpireDisplay();
                }
                _isDrawing = false;
            }
            else if (_isDrawing && _drawMode == 1)
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

            _isDragging = false;
            _dragShapeIndex = -1;
            _dragPointIndex = -1;

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

            if (e.KeyCode == Keys.Enter)
            {
                if (_currentPoints.Count >= 2)
                {
                    if (_drawMode == 1)
                    {
                        _annotation.Shapes.Add(new PolylineShape(new List<Point3d>(_currentPoints), false));
                    }
                    _currentPoints.Clear();
                    _isDrawing = false;
                    _annotation.ExpireDisplay();
                    e.SuppressKeyPress = true;
                }
            }
            else if (e.KeyCode == Keys.Escape)
            {
                if (_drawMode == 1 && _currentPoints.Count >= 2)
                {
                    _annotation.Shapes.Add(new PolylineShape(new List<Point3d>(_currentPoints), false));
                    _currentPoints.Clear();
                    _isDrawing = false;
                    _annotation.ExpireDisplay();
                    e.SuppressKeyPress = true;
                }
                else if (_currentPoints.Count > 0 || _isDrawing)
                {
                    _currentPoints.Clear();
                    _isDrawing = false;
                    _annotation.ExpireDisplay();
                    e.SuppressKeyPress = true;
                }
            }

            if (e.KeyCode == Keys.Delete)
            {
                if (_annotation.SelectedShapeIndex >= 0)
                {
                    DeleteSelectedShape();
                    e.SuppressKeyPress = true;
                }
            }

            _canvas?.Invalidate();
        }

        private void DeleteSelectedShape()
        {
            if (_annotation.SelectedShapeIndex >= 0)
            {
                _annotation.Shapes.RemoveAt(_annotation.SelectedShapeIndex);
                _annotation.SelectedShapeIndex = -1;
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
            menu.Items.Insert(0, modeMenu);

            return base.AppendMenuItems(menu);
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
    }
}
