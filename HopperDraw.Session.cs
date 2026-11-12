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
        internal void ResetPen() { ResetNextShapeOverrides(); new LineShape().CopyStyleTo(PenStyle); TextPen = new TextShape(); Smoothing=SmoothingLevel.Medium; RequestCanvasRedraw(); }
        internal void Pen(Color? color, float width, DashStyle dash)
        {
            _pendingColorOverride = color; _currentThicknessMultiplier = width;
            _currentLineType = dash; _hasPendingLineTypeOverride = true; RequestCanvasRedraw();
        }
        internal TextShape TextPen = new TextShape();
        internal readonly DrawShape PenStyle = new LineShape();
        internal SmoothingLevel Smoothing = SmoothingLevel.Medium;
        internal string PenNotice;
        internal void ShowNotice(string text) { PenNotice=text; _annotationController?.Notice(text); Redraw(); }
        internal void ApplyPen(DrawShape shape)
        {
            if (shape is TextShape text) { TextPen.CopyStyleTo(text); TextPen.CopyTextStyleTo(text); return; }
            PenStyle.CopyStyleTo(shape);
            shape.OverrideColor=PendingColorOverride; shape.ThicknessMultiplier=CurrentThicknessMultiplier; shape.LineType=CurrentLineType;
            if (!shape.SupportsFill) { shape.FillColor=null; shape.HatchBackground=null; }
        }
        internal void PenFrom(DrawShape shape)
        {
            if (shape is TextShape text) { text.CopyStyleTo(TextPen); text.CopyTextStyleTo(TextPen); Redraw(); return; }
            shape.CopyStyleTo(PenStyle); Pen(shape.OverrideColor,shape.ThicknessMultiplier,shape.LineType);
        }
        internal void CompleteDraft(DrawShape shape)
        {
            if (shape == null) return;
            ApplyPen(shape);
            if (!shape.HasPaint(BorderColor))
            {
                ShowNotice("Shape has no visible paint"); return;
            }
            if (!RecordShapeUndo("Create annotation")) return;
            Shapes.Add(shape); Changed();
        }
        internal void CancelGesture(bool commitText = false)
        {
            _annotationController?.CancelOwner(this, commitText); Draft.Cancel();
            EndShapeGesture(); _isDrawing = false; _currentPoints.Clear();
            _frameCornerCount = 0; _acceptDrawingMouseUp = false; _clickStartPt = Point3d.Unset;
            _isWindowSelecting = _isDragging = _isMultiDragging = _isFrameDragging = false;
            _dragShapeIndex = _dragPointIndex = -1; ClearCapturedGHObjects();
        }
        internal void ResetTextKeys() { _drawingKeyGesture.Reset(); _drawingShortcut?.Reset(); }
        internal void EditText(TextShape text) => _annotationController?.BeginText(this, text, false);
        internal void SetTool(int mode)
        {
            if (!DrawingTool.Valid(mode)) return;
            if (Params.Input[2].SourceCount != 0 || mode == _drawMode) return;
            var param = (Param_Integer)Params.Input[2];
            param.RecordUndoEvent("HopperDraw: Change drawing tool");
            param.PersistentData.Clear(); param.PersistentData.Append(new GH_Integer(mode));
            CancelGesture(true); _drawMode = mode; param.ExpireSolution(false); ExpireSolution(true); Redraw();
        }
    }
}
