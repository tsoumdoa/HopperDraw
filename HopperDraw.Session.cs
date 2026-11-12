using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace hopperdraw
{
    public partial class HopperDraw
    {
        internal bool MenuTransaction;
        internal void Redraw() => RequestCanvasRedraw();
        private CanvasAnnotationController _annotationController;
        internal readonly AnnotationDraft Draft = new AnnotationDraft();
        internal bool Eligible => CanEditShapes && IsActivated && Visible && !Locked;
        internal IEnumerable<DrawShape> Selection => SelectedShapeIndices.Distinct().Where(i => i >= 0 && i < Shapes.Count).Select(i => Shapes[i]);
        internal IEnumerable<DrawShape> SelectionLeaves => SelectedLeaves();
        internal void Changed() => MarkDocumentModified();
        internal void ExitDrawing() { Draft.Cancel(); CancelDrawing(); RequestCanvasRedraw(); }
        internal void ResetPen() { ResetNextShapeOverrides(); new LineShape().CopyStyleTo(PenStyle); Smoothing=SmoothingLevel.Medium; RequestCanvasRedraw(); }
        internal void Pen(Color? color, float width, DashStyle dash)
        {
            _pendingColorOverride = color; _currentThicknessMultiplier = width;
            _currentLineType = dash; _hasPendingLineTypeOverride = true; RequestCanvasRedraw();
        }
        internal readonly DrawShape PenStyle = new LineShape();
        internal SmoothingLevel Smoothing = SmoothingLevel.Medium;
        internal string PenNotice;
        internal DateTime PenNoticeUntil;
        internal void ApplyPen(DrawShape shape)
        {
            PenStyle.CopyStyleTo(shape);
            shape.OverrideColor=PendingColorOverride; shape.ThicknessMultiplier=CurrentThicknessMultiplier; shape.LineType=CurrentLineType;
            if (!shape.SupportsFill) { shape.FillColor=null; shape.HatchBackground=null; }
        }
        internal void PenFrom(DrawShape shape)
        {
            shape.CopyStyleTo(PenStyle); Pen(shape.OverrideColor,shape.ThicknessMultiplier,shape.LineType);
        }
        internal void CompleteDraft(DrawShape shape)
        {
            if (shape == null) return;
            ApplyPen(shape);
            if (!shape.HasPaint(BorderColor))
            {
                PenNotice="Shape has no visible paint"; PenNoticeUntil=DateTime.UtcNow.AddSeconds(3); Redraw(); return;
            }
            if (!RecordShapeUndo("Create annotation")) return;
            Shapes.Add(shape); Changed();
        }
        internal void SetTool(int mode)
        {
            if (!DrawingTool.Valid(mode)) return;
            if (Params.Input[2].SourceCount != 0 || mode == _drawMode) return;
            var param = (Param_Integer)Params.Input[2];
            param.RecordUndoEvent("HopperDraw: Change drawing tool");
            param.PersistentData.Clear(); param.PersistentData.Append(new GH_Integer(mode));
            _annotationController?.CancelOwner(this); Draft.Cancel(); _drawMode = mode; param.ExpireSolution(false); ExpireSolution(true);
        }
    }
}
