using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Attributes;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Rhino;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using GH_IO.Serialization;

namespace hopperborder
{
    internal class BorderLine
    {
        public Point3d Start { get; set; }
        public Point3d End { get; set; }

        public BorderLine()
        {
            Start = Point3d.Unset;
            End = Point3d.Unset;
        }

        public BorderLine(Point3d start, Point3d end)
        {
            Start = start;
            End = end;
        }

    }

    internal class BorderAnnotation : GH_Component
    {
        public List<BorderLine> Borders { get; set; } = new List<BorderLine>();
        public Color BorderColor { get; set; } = Color.Black;
        public float LineThickness { get; set; } = 8f;
        public bool IsLocked { get; set; } = false;

        public int SelectedBorderIndex { get; set; } = -1;
        public int HoveredBorderIndex { get; set; } = -1;
        public int HoveredHandleIndex { get; set; } = -1;

        private const float HandleSize = 8f;
        private const float HitTolerance = 15f;

        public BorderAnnotation() : base("Canvas Border", "Border", "Canvas border annotation", "Draw", "Annotation")
        {
            CreateAttributes();
        }

        public override Guid ComponentGuid => new Guid("B1C2D3E4-F5A6-7890-1234-567890ABCDEF");

        public override void CreateAttributes()
        {
            m_attributes = new BorderAnnotationAttributes(this);
        }

        public RectangleF Bounds => CalculateUnionBounds();

        private RectangleF CalculateUnionBounds()
        {
            if (Borders.Count == 0)
                return new RectangleF(0, 0, 1, 1);

            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;

            foreach (var border in Borders)
            {
                minX = Math.Min(minX, Math.Min(border.Start.X, border.End.X));
                minY = Math.Min(minY, Math.Min(border.Start.Y, border.End.Y));
                maxX = Math.Max(maxX, Math.Max(border.Start.X, border.End.X));
                maxY = Math.Max(maxY, Math.Max(border.Start.Y, border.End.Y));
            }

            return new RectangleF(
                (float)(minX - HandleSize),
                (float)(minY - HandleSize),
                (float)(maxX - minX + HandleSize * 2),
                (float)(maxY - minY + HandleSize * 2)
            );
        }

        public (int borderIndex, int handleIndex) HitTest(PointF point)
        {
            if (IsLocked)
                return (-1, -1);

            for (int i = 0; i < Borders.Count; i++)
            {
                var border = Borders[i];
                var handles = GetHandlePoints(border);

                for (int j = 0; j < handles.Count; j++)
                {
                    if (Distance(point, handles[j]) <= HitTolerance)
                        return (i, j);
                }

                if (HitTestLine(point, border))
                    return (i, -1);
            }

            return (-1, -1);
        }

        public int HitTestBorder(PointF point)
        {
            if (IsLocked)
                return -1;

            for (int i = 0; i < Borders.Count; i++)
            {
                if (HitTestLine(point, Borders[i]))
                    return i;
            }

            return -1;
        }

        private bool HitTestLine(PointF point, BorderLine border)
        {
            return DistanceToSegment(point, new PointF((float)border.Start.X, (float)border.Start.Y), new PointF((float)border.End.X, (float)border.End.Y)) <= LineThickness / 2 + 3;
        }

        private float Distance(PointF a, PointF b)
        {
            return (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        }

        private float DistanceToSegment(PointF pt, PointF a, PointF b)
        {
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            float lengthSq = dx * dx + dy * dy;

            if (lengthSq < 0.0001f)
                return Distance(pt, a);

            float t = Math.Max(0, Math.Min(1, ((pt.X - a.X) * dx + (pt.Y - a.Y) * dy) / lengthSq));
            float projX = a.X + t * dx;
            float projY = a.Y + t * dy;

            return Distance(pt, new PointF(projX, projY));
        }

        private List<PointF> GetHandlePoints(BorderLine border)
        {
            return new List<PointF>
            {
                new PointF((float)border.Start.X, (float)border.Start.Y),
                new PointF((float)border.End.X, (float)border.End.Y)
            };
        }

        private RectangleF GetBoundingRect(BorderLine border)
        {
            float x = (float)Math.Min(border.Start.X, border.End.X);
            float y = (float)Math.Min(border.Start.Y, border.End.Y);
            float w = (float)Math.Abs(border.End.X - border.Start.X);
            float h = (float)Math.Abs(border.End.Y - border.Start.Y);
            return new RectangleF(x, y, w, h);
        }

        public void ClearSelection()
        {
            SelectedBorderIndex = -1;
            HoveredBorderIndex = -1;
            HoveredHandleIndex = -1;
        }

        public void SelectBorder(int index)
        {
            SelectedBorderIndex = index;
        }

        public void ExpireDisplay()
        {
            var doc = OnPingDocument();
            if (doc != null)
            {
                doc.ScheduleSolution(5, d => this.ExpirePreview(false));
            }
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
        }

        public override bool Write(GH_IWriter writer)
        {
            writer.SetInt32("Count", Borders.Count);
            for (int i = 0; i < Borders.Count; i++)
            {
                writer.SetDouble($"StartX{i}", Borders[i].Start.X);
                writer.SetDouble($"StartY{i}", Borders[i].Start.Y);
                writer.SetDouble($"EndX{i}", Borders[i].End.X);
                writer.SetDouble($"EndY{i}", Borders[i].End.Y);
            }
            writer.SetInt32("Color", BorderColor.ToArgb());
            writer.SetDouble("Thickness", LineThickness);
            writer.SetBoolean("Locked", IsLocked);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            Borders.Clear();
            int count = reader.GetInt32("Count");
            for (int i = 0; i < count; i++)
            {
                double sx = reader.GetDouble($"StartX{i}");
                double sy = reader.GetDouble($"StartY{i}");
                double ex = reader.GetDouble($"EndX{i}");
                double ey = reader.GetDouble($"EndY{i}");
                Borders.Add(new BorderLine(new Point3d(sx, sy, 0), new Point3d(ex, ey, 0)));
            }
            int colorArgb = reader.GetInt32("Color");
            BorderColor = Color.FromArgb(colorArgb);
            LineThickness = (float)reader.GetDouble("Thickness");
            if (LineThickness <= 0)
                LineThickness = 8f;
            IsLocked = reader.GetBoolean("Locked");
            return base.Read(reader);
        }
    }

    internal class BorderAnnotationAttributes : GH_ComponentAttributes
    {
        public BorderAnnotationAttributes(BorderAnnotation owner) : base(owner)
        {
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            if (channel != GH_CanvasChannel.Objects)
            {
                base.Render(canvas, graphics, channel);
                return;
            }

            var annotation = (BorderAnnotation)Owner;

            using (var pen = new Pen(annotation.BorderColor, annotation.LineThickness))
            {
                foreach (var border in annotation.Borders)
                {
                    RenderBorder(graphics, pen, border);
                }
            }

            if (annotation.SelectedBorderIndex >= 0 && annotation.SelectedBorderIndex < annotation.Borders.Count)
            {
                RenderSelectionHandles(graphics, annotation.Borders[annotation.SelectedBorderIndex]);
            }

            if (annotation.HoveredBorderIndex >= 0 && annotation.HoveredBorderIndex < annotation.Borders.Count)
            {
                RenderHoverHandles(graphics, annotation.Borders[annotation.HoveredBorderIndex]);
            }
        }

        private void RenderBorder(Graphics g, Pen pen, BorderLine border)
        {
            if (!border.Start.IsValid || !border.End.IsValid)
                return;

            g.DrawLine(pen,
                (float)border.Start.X, (float)border.Start.Y,
                (float)border.End.X, (float)border.End.Y);
        }

        private void RenderSelectionHandles(Graphics g, BorderLine border)
        {
            var handles = GetHandlePoints(border);

            using (var handleBrush = new SolidBrush(Color.White))
            using (var handlePen = new Pen(Color.FromArgb(200, 0, 120, 215), 2))
            {
                foreach (var pt in handles)
                {
                    g.FillRectangle(handleBrush, pt.X - 4, pt.Y - 4, 8, 8);
                    g.DrawRectangle(handlePen, (int)pt.X - 4, (int)pt.Y - 4, 8, 8);
                }
            }

            using (var selPen = new Pen(Color.FromArgb(200, 0, 120, 215), 1))
            {
                selPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                g.DrawLine(selPen, (float)border.Start.X, (float)border.Start.Y, (float)border.End.X, (float)border.End.Y);
            }
        }

        private void RenderHoverHandles(Graphics g, BorderLine border)
        {
            var handles = GetHandlePoints(border);

            using (var hoverBrush = new SolidBrush(Color.FromArgb(100, 0, 120, 215)))
            {
                foreach (var pt in handles)
                {
                    g.FillRectangle(hoverBrush, pt.X - 4, pt.Y - 4, 8, 8);
                }
            }
        }

        private List<PointF> GetHandlePoints(BorderLine border)
        {
            return new List<PointF>
            {
                new PointF((float)border.Start.X, (float)border.Start.Y),
                new PointF((float)border.End.X, (float)border.End.Y)
            };
        }

        private RectangleF GetBoundingRect(BorderLine border)
        {
            float x = (float)Math.Min(border.Start.X, border.End.X);
            float y = (float)Math.Min(border.Start.Y, border.End.Y);
            float w = (float)Math.Abs(border.End.X - border.Start.X);
            float h = (float)Math.Abs(border.End.Y - border.Start.Y);
            return new RectangleF(x, y, w, h);
        }
    }
}
