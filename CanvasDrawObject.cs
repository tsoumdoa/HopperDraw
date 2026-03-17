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
using System.Windows.Forms;

namespace hopperborder
{
    public class CanvasDrawObject : GH_Component
    {
        private bool _isActivated;
        
        private List<Point3d> _existingStarts;
        private List<Point3d> _existingEnds;
        private bool _hasExisting;

        private Point3d _drawStart;
        private Point3d _drawEnd;
        private bool _isDrawing;
        private bool _isDragging;
        private int _dragHandle;

        private GH_Canvas _canvas;
        private bool _registered;

        public CanvasDrawObject()
            : base("Canvas Draw", "CanvasDraw",
                "Draw orthogonal lines anywhere on canvas",
                "Draw", "Primitive")
        {
            _isActivated = false;
            _hasExisting = false;
            _isDrawing = false;
            _isDragging = false;
            _dragHandle = -1;
            _registered = false;
            _existingStarts = new List<Point3d>();
            _existingEnds = new List<Point3d>();
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddBooleanParameter("Enable", "E", "Enable drawing - drag anywhere on canvas", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("Curves", "C", "Drawn curves", GH_ParamAccess.list);
            pManager.AddPointParameter("Points", "P", "Corner points", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            bool enable = false;
            if (!DA.GetData(0, ref enable)) return;

            bool wasActivated = _isActivated;
            _isActivated = enable;

            if (_isActivated && !wasActivated)
            {
                RegisterCanvasEvents();
            }
            else if (!_isActivated && wasActivated)
            {
                UnregisterCanvasEvents();
            }

            var curves = new List<Curve>();
            var points = new List<Point3d>();

            if (_hasExisting)
            {
                for (int i = 0; i < _existingStarts.Count; i++)
                {
                    var start = _existingStarts[i];
                    var end = _existingEnds[i];
                    if (!start.IsValid || !end.IsValid) continue;

                    double dx = Math.Abs(end.X - start.X);
                    double dy = Math.Abs(end.Y - start.Y);

                    Point3d constrainedEnd = dx >= dy 
                        ? new Point3d(end.X, start.Y, 0)
                        : new Point3d(start.X, end.Y, 0);

                    var plane = new Plane(start, Vector3d.ZAxis);
                    var rect = new Rectangle3d(plane, start, constrainedEnd);

                    if (dx > 1 && dy > 1)
                    {
                        curves.Add(rect.ToNurbsCurve());
                        for (int j = 0; j < 4; j++)
                            points.Add(rect.Corner(j));
                    }
                    else
                    {
                        curves.Add(new Line(start, constrainedEnd).ToNurbsCurve());
                        points.Add(start);
                        points.Add(constrainedEnd);
                    }
                }
            }

            if (_isDrawing && _drawStart.IsValid && _drawEnd.IsValid)
            {
                double dx = Math.Abs(_drawEnd.X - _drawStart.X);
                double dy = Math.Abs(_drawEnd.Y - _drawStart.Y);

                Point3d constrainedEnd = dx >= dy 
                    ? new Point3d(_drawEnd.X, _drawStart.Y, 0)
                    : new Point3d(_drawStart.X, _drawEnd.Y, 0);

                var plane = new Plane(_drawStart, Vector3d.ZAxis);
                var rect = new Rectangle3d(plane, _drawStart, constrainedEnd);

                if (dx > 1 && dy > 1)
                {
                    curves.Add(rect.ToNurbsCurve());
                    for (int j = 0; j < 4; j++)
                        points.Add(rect.Corner(j));
                }
                else
                {
                    curves.Add(new Line(_drawStart, constrainedEnd).ToNurbsCurve());
                    points.Add(_drawStart);
                    points.Add(constrainedEnd);
                }
            }

            DA.SetDataList(0, curves);
            DA.SetDataList(1, points);
        }

        private void RegisterCanvasEvents()
        {
            if (_registered) return;
            
            var doc = OnPingDocument();
            if (doc != null)
            {
                _canvas = Grasshopper.Instances.ActiveCanvas;
                if (_canvas != null)
                {
                    _canvas.MouseDown += Canvas_MouseDown;
                    _canvas.MouseMove += Canvas_MouseMove;
                    _canvas.MouseUp += Canvas_MouseUp;
                    _registered = true;
                }
            }
        }

        private void UnregisterCanvasEvents()
        {
            if (!_registered) return;

            if (_canvas != null)
            {
                _canvas.MouseDown -= Canvas_MouseDown;
                _canvas.MouseMove -= Canvas_MouseMove;
                _canvas.MouseUp -= Canvas_MouseUp;
            }
            _registered = false;
        }

        private void Canvas_MouseDown(object sender, MouseEventArgs e)
        {
            if (!_isActivated || e.Button != MouseButtons.Left) return;

            var pt = new Point3d(e.Location.X, e.Location.Y, 0);

            if (_hasExisting && HitTestHandles(pt, out int handleIdx))
            {
                _isDragging = true;
                _dragHandle = handleIdx;
                return;
            }

            _drawStart = pt;
            _drawEnd = pt;
            _isDrawing = true;
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isActivated) return;

            var pt = new Point3d(e.Location.X, e.Location.Y, 0);

            if (_isDrawing)
            {
                _drawEnd = pt;
                ExpireSolution(false);
                _canvas?.Invalidate();
            }
            else if (_isDragging && _dragHandle >= 0)
            {
                int lineIdx = _dragHandle / 2;
                int ptIdx = _dragHandle % 2;

                if (ptIdx == 0 && lineIdx < _existingStarts.Count)
                    _existingStarts[lineIdx] = pt;
                else if (ptIdx == 1 && lineIdx < _existingEnds.Count)
                    _existingEnds[lineIdx] = pt;

                ExpireSolution(false);
                _canvas?.Invalidate();
            }
        }

        private void Canvas_MouseUp(object sender, MouseEventArgs e)
        {
            if (!_isActivated) return;

            if (_isDrawing && _drawStart.IsValid && _drawEnd.IsValid)
            {
                double dx = Math.Abs(_drawEnd.X - _drawStart.X);
                double dy = Math.Abs(_drawEnd.Y - _drawStart.Y);

                if (dx > 5 || dy > 5)
                {
                    _hasExisting = true;
                    _existingStarts.Add(_drawStart);
                    _existingEnds.Add(_drawEnd);
                }
            }

            _isDrawing = false;
            _isDragging = false;
            _dragHandle = -1;
            ExpireSolution(true);
            _canvas?.Invalidate();
        }

        private bool HitTestHandles(Point3d pt, out int handleIdx)
        {
            handleIdx = -1;
            float hitDist = 15f;

            for (int i = 0; i < _existingStarts.Count; i++)
            {
                var start = _existingStarts[i];
                var end = _existingEnds[i];

                double dx = Math.Abs(end.X - start.X);
                double dy = Math.Abs(end.Y - start.Y);

                Point3d constrainedEnd = dx >= dy 
                    ? new Point3d(end.X, start.Y, 0)
                    : new Point3d(start.X, end.Y, 0);

                if (Distance(pt, start) < hitDist) { handleIdx = i * 2; return true; }
                if (Distance(pt, constrainedEnd) < hitDist) { handleIdx = i * 2 + 1; return true; }
            }
            return false;
        }

        private double Distance(Point3d a, Point3d b)
        {
            return Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            UnregisterCanvasEvents();
            _canvas = null;
            base.RemovedFromDocument(document);
        }

        public override void CreateAttributes()
        {
            m_attributes = new CanvasDrawAttributes(this);
        }

        public bool IsActivated => _isActivated;
        public bool IsDrawing => _isDrawing;
        public bool HasExisting => _hasExisting;
        public List<Point3d> ExistingStarts => _existingStarts;
        public List<Point3d> ExistingEnds => _existingEnds;
        public Point3d DrawStart => _drawStart;
        public Point3d DrawEnd => _drawEnd;

        public void Clear()
        {
            _hasExisting = false;
            _existingStarts.Clear();
            _existingEnds.Clear();
            ExpireSolution(true);
        }

        public override bool AppendMenuItems(ToolStripDropDown menu)
        {
            Menu_AppendItem(menu, "Clear All", (s, e) => Clear());
            return base.AppendMenuItems(menu);
        }

        public override Guid ComponentGuid => new Guid("A0B1C2D3-E4F5-6789-0123-4DEF56789012");
    }

    public class CanvasDrawAttributes : GH_ComponentAttributes
    {
        private CanvasDrawObject OwnerComp => (CanvasDrawObject)Owner;

        public CanvasDrawAttributes(CanvasDrawObject owner) : base(owner)
        {
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);
            
            if (channel == GH_CanvasChannel.Wires && OwnerComp.IsActivated)
            {
                RenderAllLines(graphics);
            }
        }

        private void RenderAllLines(Graphics g)
        {
            var pen = new Pen(Color.Black, 3);
            var handlePen = new Pen(Color.Black, 1);
            var handleBrush = new SolidBrush(Color.White);

            if (OwnerComp.HasExisting)
            {
                var starts = OwnerComp.ExistingStarts;
                var ends = OwnerComp.ExistingEnds;

                for (int i = 0; i < starts.Count; i++)
                {
                    var start = starts[i];
                    var end = ends[i];
                    if (!start.IsValid || !end.IsValid) continue;

                    double dx = Math.Abs(end.X - start.X);
                    double dy = Math.Abs(end.Y - start.Y);

                    Point3d constrainedEnd = dx >= dy 
                        ? new Point3d(end.X, start.Y, 0)
                        : new Point3d(start.X, end.Y, 0);

                    DrawLineOrRect(g, pen, handlePen, handleBrush, start, constrainedEnd, dx > 1 && dy > 1);
                }
            }

            if (OwnerComp.IsDrawing)
            {
                var start = OwnerComp.DrawStart;
                var end = OwnerComp.DrawEnd;
                if (start.IsValid && end.IsValid)
                {
                    double dx = Math.Abs(end.X - start.X);
                    double dy = Math.Abs(end.Y - start.Y);

                    Point3d constrainedEnd = dx >= dy 
                        ? new Point3d(end.X, start.Y, 0)
                        : new Point3d(start.X, end.Y, 0);

                    DrawLineOrRect(g, pen, handlePen, handleBrush, start, constrainedEnd, dx > 1 && dy > 1);
                }
            }
        }

        private void DrawLineOrRect(Graphics g, Pen pen, Pen handlePen, SolidBrush handleBrush, Point3d start, Point3d constrainedEnd, bool isRect)
        {
            PointF pt1 = new PointF((float)start.X, (float)start.Y);
            PointF pt2 = new PointF((float)constrainedEnd.X, (float)constrainedEnd.Y);

            if (isRect)
            {
                PointF pt3 = new PointF((float)constrainedEnd.X, (float)constrainedEnd.Y);
                PointF pt4 = new PointF((float)start.X, (float)constrainedEnd.Y);
                g.DrawLine(pen, pt1.X, pt1.Y, pt2.X, pt2.Y);
                g.DrawLine(pen, pt2.X, pt2.Y, pt3.X, pt3.Y);
                g.DrawLine(pen, pt3.X, pt3.Y, pt4.X, pt4.Y);
                g.DrawLine(pen, pt4.X, pt4.Y, pt1.X, pt1.Y);

                g.FillRectangle(handleBrush, pt1.X - 5, pt1.Y - 5, 10, 10);
                g.FillRectangle(handleBrush, pt2.X - 5, pt2.Y - 5, 10, 10);
                g.FillRectangle(handleBrush, pt3.X - 5, pt3.Y - 5, 10, 10);
                g.FillRectangle(handleBrush, pt4.X - 5, pt4.Y - 5, 10, 10);
                g.DrawRectangle(handlePen, (int)pt1.X - 5, (int)pt1.Y - 5, 10, 10);
                g.DrawRectangle(handlePen, (int)pt2.X - 5, (int)pt2.Y - 5, 10, 10);
                g.DrawRectangle(handlePen, (int)pt3.X - 5, (int)pt3.Y - 5, 10, 10);
                g.DrawRectangle(handlePen, (int)pt4.X - 5, (int)pt4.Y - 5, 10, 10);
            }
            else
            {
                g.DrawLine(pen, pt1, pt2);
                g.FillRectangle(handleBrush, pt1.X - 5, pt1.Y - 5, 10, 10);
                g.FillRectangle(handleBrush, pt2.X - 5, pt2.Y - 5, 10, 10);
                g.DrawRectangle(handlePen, (int)pt1.X - 5, (int)pt1.Y - 5, 10, 10);
                g.DrawRectangle(handlePen, (int)pt2.X - 5, (int)pt2.Y - 5, 10, 10);
            }
        }
    }
}
