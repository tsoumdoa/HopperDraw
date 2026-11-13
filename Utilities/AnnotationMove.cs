using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Undo;
using Rhino.Geometry;

namespace hopperdraw
{
    // Absolute proposals from press snapshots. No undo or dirty state until a successful commit.
    internal sealed class AnnotationMove
    {
        private readonly Dictionary<HopperDraw, Dictionary<int, DrawShape>> _before;
        private readonly Dictionary<HopperDraw, int[]> _indices;
        internal readonly Dictionary<HopperDraw, ShapeEditUndoAction> Actions;
        private readonly Dictionary<IGH_DocumentObject, PointF> _pivots;
        internal readonly GH_UndoRecord Record;
        private PointF _delta;
        private bool _edited;
        internal bool Changed => _edited || _delta != PointF.Empty;
        internal bool Allowed { get; }
        internal AnnotationMove(IEnumerable<HopperDraw> owners, IEnumerable<IGH_DocumentObject> natives, GH_Document document)
            : this(owners.Where(o => o.HasSelection).ToDictionary(o => o, o => o.SelectedShapeIndices.ToArray()), natives, document) { }
        internal AnnotationMove(IDictionary<HopperDraw, int[]> indices, IEnumerable<IGH_DocumentObject> natives, GH_Document document)
        {
            _indices = indices.ToDictionary(p => p.Key, p => p.Value.Distinct().Where(i => i >= 0 && i < p.Key.Shapes.Count).ToArray());
            var selected = _indices.Keys.ToArray();
            _before = selected.ToDictionary(o => o, o => _indices[o].ToDictionary(i => i, i => ShapeEditUndoAction.CloneForSnapshot(o.Shapes[i])));
            Actions = selected.ToDictionary(o => o, o => new ShapeEditUndoAction(o, Enumerable.Empty<IGH_DocumentObject>()));
            _pivots = natives.Where(o => o.Attributes != null).Distinct().ToDictionary(o => o, o => o.Attributes.Pivot);
            Allowed = selected.All(o => _indices[o].All(i => o.Shapes[i].CanTransformGeometry));
            if (!Allowed) foreach (var o in selected) o.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Geometry edit blocked: selection contains unreadable annotation records.");
            Record = document?.UndoUtil.CreatePivotEvent("HopperDraw: Move selection", _pivots.Keys) ?? new GH_UndoRecord("HopperDraw: Move selection");
            foreach (var action in Actions.Values) Record.AddAction(action);
        }
        // Window containment against GH's rectangle, including blob/hull groups' bounding rectangle.
        // Only top-level shapes move: annotation groups stay whole. Unreadable contained shapes stay put;
        // selected unreadable shapes remain in the union so they block the transaction with the usual warning.
        internal static Dictionary<HopperDraw, int[]> MovingSet(IEnumerable<HopperDraw> owners, GH_Document document)
            => MovingSet(owners, document?.Objects.OfType<GH_Group>() ?? Enumerable.Empty<GH_Group>());
        internal static Dictionary<HopperDraw, int[]> MovingSet(IEnumerable<HopperDraw> owners, IEnumerable<GH_Group> groups)
        {
            var bounds = groups.Where(g => g.Attributes?.Selected == true).Select(g => g.Attributes.Bounds).ToArray();
            var result = new Dictionary<HopperDraw, int[]>();
            foreach (var owner in owners.Where(o => o.Eligible))
            {
                var indices = new HashSet<int>(owner.SelectedShapeIndices.Where(i => i >= 0 && i < owner.Shapes.Count));
                for (int i = 0; i < owner.Shapes.Count; i++)
                    if (!indices.Contains(i) && owner.Shapes[i].CanTransformGeometry && bounds.Any(b =>
                        AnnotationGeometry.InRectangle(owner.Shapes[i], b, false, owner.LineThickness, owner.DrawOrder != 0))) indices.Add(i);
                if (indices.Count != 0) result.Add(owner, indices.OrderBy(i => i).ToArray());
            }
            return result;
        }
        internal void Translate(PointF delta, bool native)
        {
            if (!Allowed || delta == _delta) return;
            // Clone and move only the moving set; untouched shapes stay as they are (O(moving set) per move).
            var proposals = _before.ToDictionary(p => p.Key, p => _indices[p.Key].Select(i =>
            { var copy = ShapeEditUndoAction.CloneForSnapshot(p.Value[i]); copy.Move(delta.X, delta.Y); return copy; }).ToArray());
            if (proposals.Values.SelectMany(s => s).Any(s => !ValidGeometry(s))) return;
            foreach (var pair in proposals)
                for (int k = 0; k < pair.Value.Length; k++) pair.Key.Shapes[_indices[pair.Key][k]] = pair.Value[k];
            if (native) foreach (var pair in _pivots) SetPivot(pair.Key, new PointF(pair.Value.X + delta.X, pair.Value.Y + delta.Y));
            _delta = delta;
        }
        internal void Edit(HopperDraw owner, int shape, int handle, Point3d point, bool shift)
        {
            if (!Allowed) return;
            var proposal = ShapeEditUndoAction.CloneForSnapshot(_before[owner][shape]);
            var points = proposal.GetPoints();
            if (proposal is TextShape text) text.MovePoint(handle, point);
            else if (proposal is FrameShape frame)
            {
                var fixedPoint = points[(handle + 2) % 4];
                if (shift) point = GeometryUtilities.SnapToAspectRatio(new Point3d(fixedPoint.X, fixedPoint.Y, 0), point,
                    Math.Abs((points[1].X-points[0].X) / Math.Max(.001, Math.Abs(points[3].Y-points[0].Y))));
                var a = new Point3d(fixedPoint.X, fixedPoint.Y, 0);
                var b = point;
                frame.TopLeft = new Point3d(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),0);
                frame.TopRight = new Point3d(Math.Max(a.X,b.X),Math.Min(a.Y,b.Y),0);
                frame.BottomRight = new Point3d(Math.Max(a.X,b.X),Math.Max(a.Y,b.Y),0);
                frame.BottomLeft = new Point3d(Math.Min(a.X,b.X),Math.Max(a.Y,b.Y),0);
            }
            else
            {
                if (shift && points.Length > 1)
                {
                    bool closed = proposal is PolylineShape poly && poly.Closed || proposal is CurveShape curve && curve.Closed;
                    int anchor = proposal is LineShape ? 1-handle : handle > 0 ? handle-1 : closed ? points.Length-1 : 1;
                    point = GeometryUtilities.SnapAngle(new Point3d(points[anchor].X,points[anchor].Y,0), point);
                }
                proposal.MovePoint(handle, point);
            }
            if (!ValidGeometry(proposal)) return;
            owner.Shapes[shape] = proposal;
            _edited = proposal is TextShape editedText ? !editedText.SameText((TextShape)_before[owner][shape]) : !proposal.GetPoints().SequenceEqual(_before[owner][shape].GetPoints());
        }
        internal void Revert(bool native)
        {
            foreach (var pair in _before)
                foreach (var shape in pair.Value) pair.Key.Shapes[shape.Key] = ShapeEditUndoAction.CloneForSnapshot(shape.Value);
            if (native) foreach (var pair in _pivots) SetPivot(pair.Key,pair.Value);
            _delta = PointF.Empty; _edited = false;
        }
        internal void Commit(GH_Document document)
        {
            if (!Changed) return;
            if (document != null && document.UndoUtil.RecordEvent(Record) == Guid.Empty) { Revert(true); return; }
            foreach (var owner in _before.Keys) owner.Changed();
        }
        internal static bool ValidGeometry(DrawShape shape) => shape is DrawShapeGroup group
            ? group.Children.All(ValidGeometry) : shape is OpaqueShape || ShapeRenderer.ValidPoints(shape.GetPoints()) &&
                (!(shape is TextShape text) || !text.BoxWidth.HasValue || TextShape.ValidBox(text.Anchor.X, text.BoxWidth.Value));
        private static void SetPivot(IGH_DocumentObject obj, PointF point)
        { obj.Attributes.Pivot = point; obj.Attributes.ExpireLayout(); obj.Attributes.PerformLayout(); }
    }
}
