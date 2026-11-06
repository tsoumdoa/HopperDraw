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
        internal void ResetPen() { ResetNextShapeOverrides(); RequestCanvasRedraw(); }
        internal void Pen(Color? color, float width, DashStyle dash)
        {
            _pendingColorOverride = color; _currentThicknessMultiplier = width;
            _currentLineType = dash; _hasPendingLineTypeOverride = true; RequestCanvasRedraw();
        }
        internal void CompleteDraft(DrawShape shape)
        {
            if (shape == null || !RecordShapeUndo("Create annotation")) return;
            shape.OverrideColor = PendingColorOverride; shape.ThicknessMultiplier = CurrentThicknessMultiplier; shape.LineType = CurrentLineType;
            Shapes.Add(shape); Changed();
        }
        internal void SetTool(int mode)
        {
            if (Params.Input[2].SourceCount != 0 || mode == _drawMode) return;
            var param = (Param_Integer)Params.Input[2];
            param.RecordUndoEvent("HopperDraw: Change drawing tool");
            param.PersistentData.Clear(); param.PersistentData.Append(new GH_Integer(mode));
            Draft.Cancel(); _drawMode = mode; param.ExpireSolution(false); ExpireSolution(true);
        }
    }
}
