using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
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
        private bool _shiftPressed;

        private Point3d _drawStart;
        private Point3d _drawEnd;
        private bool _isDrawing;
        private bool _isDragging;
        private int _dragBorderIndex = -1;
        private int _dragHandleIndex = -1;
        private Point3d _dragOffset;

        private GH_Canvas _canvas;
        private bool _eventsRegistered;

        private BorderAnnotation _annotation;
        private bool _annotationCreated;

        private float _lineThickness = 8f;
        private Color _borderColor = Color.Black;
        private const double RectangleThreshold = 10.0;

        public BorderController()
            : base("Border Controller", "Border", "Draw and manage canvas borders", "Draw", "Primitive")
        {
            CreateAttributes();
        }

        public override Guid ComponentGuid => new Guid("C1D2E3F4-A5B6-7890-1234-567890ABCDEF");

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddBooleanParameter("Enable", "E", "Enable border drawing", GH_ParamAccess.item, false);
            pManager.AddColourParameter("Color", "C", "Border color", GH_ParamAccess.item, Color.Black);
            pManager.AddNumberParameter("Thickness", "T", "Line thickness", GH_ParamAccess.item, 8.0);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            bool enable = false;
            if (!DA.GetData(0, ref enable)) return;

            Color color = Color.Black;
            DA.GetData(1, ref color);

            double thickness = 3.0;
            DA.GetData(2, ref thickness);

            _borderColor = color;
            _lineThickness = (float)thickness;

            bool wasActivated = _isActivated;
            _isActivated = enable;

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
                _annotation.BorderColor = _borderColor;
                _annotation.LineThickness = _lineThickness;
            }

            ExpirePreview(false);
        }

        private void EnsureAnnotation()
        {
            var doc = OnPingDocument();
            if (doc == null) return;

            if (_annotation != null && _annotationCreated)
                return;

            var borderGuid = new Guid("B1C2D3E4-F5A6-7890-1234-567890ABCDEF");

            foreach (var obj in doc.Objects)
            {
                if (obj is BorderAnnotation existing)
                {
                    _annotation = existing;
                    _annotationCreated = true;
                    return;
                }
            }

            _annotation = new BorderAnnotation();
            _annotation.BorderColor = _borderColor;
            _annotation.LineThickness = _lineThickness;

            doc.AddObject(_annotation, false, -1);
            _annotationCreated = true;

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
                _canvas.KeyUp += Canvas_KeyUp;
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
                _canvas.KeyUp -= Canvas_KeyUp;
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
            if (!_isActivated || _annotation == null || _annotation.IsLocked)
                return;

            if (e.Button != MouseButtons.Left)
                return;

            var pt = ScreenToCanvas(e.Location);

            var (borderIdx, handleIdx) = _annotation.HitTest(new PointF((float)pt.X, (float)pt.Y));

            if (borderIdx >= 0)
            {
                _annotation.SelectedBorderIndex = borderIdx;
                _dragBorderIndex = borderIdx;
                _dragHandleIndex = handleIdx;

                if (handleIdx >= 0)
                {
                    var border = _annotation.Borders[borderIdx];
                    _dragOffset = pt;
                }
                else
                {
                    var border = _annotation.Borders[borderIdx];
                    _dragOffset = pt;
                }

                _isDragging = true;
            }
            else
            {
                _annotation.ClearSelection();
                _drawStart = pt;
                _drawEnd = pt;
                _isDrawing = true;
            }

            _annotation.ExpireDisplay();
            _canvas?.Invalidate();
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
            else if (_isDragging)
            {
                if (_dragBorderIndex >= 0 && _dragBorderIndex < _annotation.Borders.Count)
                {
                    var border = _annotation.Borders[_dragBorderIndex];

                    if (_dragHandleIndex >= 0)
                    {
                        if (_dragHandleIndex == 0)
                            border.Start = pt;
                        else if (_dragHandleIndex == 1)
                            border.End = pt;
                        else if (_dragHandleIndex == 2)
                            border.End = new Point3d(border.Start.X, pt.Y, 0);
                        else if (_dragHandleIndex == 3)
                            border.End = new Point3d(pt.X, border.Start.Y, 0);
                    }
                    else
                    {
                        var deltaX = pt.X - _dragOffset.X;
                        var deltaY = pt.Y - _dragOffset.Y;
                        border.Start = new Point3d(border.Start.X + deltaX, border.Start.Y + deltaY, 0);
                        border.End = new Point3d(border.End.X + deltaX, border.End.Y + deltaY, 0);
                        _dragOffset = new Point3d(pt.X, pt.Y, 0);
                    }

                    _annotation.ExpireDisplay();
                }

                _canvas?.Invalidate();
            }
            else
            {
                var (borderIdx, handleIdx) = _annotation.HitTest(new PointF((float)pt.X, (float)pt.Y));
                _annotation.HoveredBorderIndex = borderIdx >= 0 ? borderIdx : -1;
                _annotation.HoveredHandleIndex = handleIdx >= 0 ? handleIdx : -1;
                _canvas?.Invalidate();
            }
        }

        private void Canvas_MouseUp(object sender, MouseEventArgs e)
        {
            if (!_isActivated || _annotation == null)
                return;

            if (_isDrawing)
            {
                double dx = Math.Abs(_drawEnd.X - _drawStart.X);
                double dy = Math.Abs(_drawEnd.Y - _drawStart.Y);

                if (dx > 5 || dy > 5)
                {
                    Point3d start = _drawStart;
                    Point3d end = _drawEnd;

                    bool isRect = (dx > RectangleThreshold && dy > 1) || (dy > RectangleThreshold && dx > 1);

                    if (_shiftPressed && isRect)
                    {
                        double size = Math.Max(dx, dy);
                        end = new Point3d(start.X + Math.Sign(end.X - start.X) * size, start.Y + Math.Sign(end.Y - start.Y) * size, 0);
                    }

                    _annotation.Borders.Add(new BorderLine(start, end));
                    _annotation.ExpireDisplay();
                }
            }

            if (_isDragging)
            {
                _annotation.ExpireDisplay();
            }

            _isDrawing = false;
            _isDragging = false;
            _dragBorderIndex = -1;
            _dragHandleIndex = -1;

            _canvas?.Invalidate();
        }

        private void Canvas_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.LShiftKey || e.KeyCode == Keys.RShiftKey)
            {
                _shiftPressed = true;
                _canvas?.Invalidate();
            }

            if (!_isActivated || _annotation == null)
                return;

            if (e.KeyCode == Keys.Delete)
            {
                if (_annotation.SelectedBorderIndex >= 0)
                {
                    DeleteSelectedBorder();
                    e.SuppressKeyPress = true;
                }
            }
        }

        private void Canvas_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.LShiftKey || e.KeyCode == Keys.RShiftKey)
            {
                _shiftPressed = false;
                _canvas?.Invalidate();
            }
        }

        private void DeleteSelectedBorder()
        {
            if (_annotation.SelectedBorderIndex < 0)
                return;

            _annotation.Borders.RemoveAt(_annotation.SelectedBorderIndex);
            _annotation.SelectedBorderIndex = -1;
            _annotation.ExpireDisplay();
            _canvas?.Invalidate();
        }

        public void ClearAll()
        {
            if (_annotation == null) return;

            _annotation.Borders.Clear();
            _annotation.ExpireDisplay();
            _canvas?.Invalidate();
        }

        public void ToggleLock()
        {
            if (_annotation == null) return;

            _annotation.IsLocked = !_annotation.IsLocked;
            _annotation.ExpireDisplay();
            _canvas?.Invalidate();
        }

        public void ShowColorPicker()
        {
            using (var dialog = new ColorDialog())
            {
                dialog.Color = _annotation?.BorderColor ?? Color.Black;
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    _borderColor = dialog.Color;
                    if (_annotation != null)
                    {
                        _annotation.BorderColor = _borderColor;
                        _annotation.ExpireDisplay();
                    }
                    ExpireSolution(true);
                }
            }
        }

        public override bool AppendMenuItems(ToolStripDropDown menu)
        {
            Menu_AppendItem(menu, "Clear All", (s, e) => ClearAll());
            Menu_AppendItem(menu, "Lock/Unlock", (s, e) => ToggleLock());
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Color...", (s, e) => ShowColorPicker());

            return base.AppendMenuItems(menu);
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            UnregisterCanvasEvents();
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
        public bool ShiftPressed => _shiftPressed;
        public double RectThreshold => RectangleThreshold;
    }
}
