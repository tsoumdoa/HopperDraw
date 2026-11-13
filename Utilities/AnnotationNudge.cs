using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;

namespace hopperdraw
{
    // GH records BEFORE translating, and coalesces consecutive same-direction Alt+arrow nudges.
    // Keep absolute snapshots for that record; add each owner's action once, even across key repeats.
    internal sealed class AnnotationNudge
    {
        private readonly GH_Document _document;
        private readonly GH_UndoRecord _record;
        private readonly IGH_DocumentObject[] _selected;
        private Dictionary<IGH_DocumentObject, PointF> _pivots;
        private AnnotationMove _move;
        private readonly Dictionary<HopperDraw,ShapeEditUndoAction> _actions = new Dictionary<HopperDraw,ShapeEditUndoAction>();
        private PointF _delta;
        internal static bool IsNudge(GH_UndoRecord record) => record != null &&
            new[] { "Nudge Up", "Nudge Down", "Nudge Left", "Nudge Right" }.Contains(record.Name);
        internal AnnotationNudge(IDictionary<HopperDraw,int[]> moving, IEnumerable<IGH_DocumentObject> selected, GH_Document document, GH_UndoRecord record)
        {
            _document = document; _record = record;
            _selected = selected.ToArray();
            Prepare(moving,_selected);
        }
        internal void Prepare(IDictionary<HopperDraw,int[]> moving, IEnumerable<IGH_DocumentObject> selected)
        {
            // GH itself reuses the previous native pivot record without checking selection identity. Fail
            // closed if its targets changed: annotations must never join a record for another native set.
            _move = null;
            if (!new HashSet<IGH_DocumentObject>(selected).SetEquals(_selected)) return;
            // Groups have a constant (0,0) pivot. Their selected descendants carry the real translation.
            _pivots = _selected.Where(o => !(o is GH_Group) && o.Attributes != null).ToDictionary(o => o, o => o.Attributes.Pivot);
            _move = new AnnotationMove(moving, Enumerable.Empty<IGH_DocumentObject>(), null, _actions);
            _delta = PointF.Empty;
        }
        internal bool Matches(string direction) => _record.Name == "Nudge " + direction;
        internal static bool UniformDelta(GH_Document expected, GH_Document actual, GH_UndoRecord record, int count,
            IDictionary<IGH_DocumentObject, PointF> before, out PointF delta)
        {
            delta = PointF.Empty;
            if (!ReferenceEquals(expected, actual) || actual == null || !IsNudge(record) || record.ActionCount != count ||
                !record.Actions.All(a => a is GH_PivotAction) || before.Count == 0) return false;
            bool first = true;
            foreach (var pair in before)
            {
                if (pair.Key.Attributes == null) return false;
                var p = pair.Key.Attributes.Pivot;
                var d = new PointF(p.X - pair.Value.X, p.Y - pair.Value.Y);
                if (float.IsNaN(d.X) || float.IsInfinity(d.X) || float.IsNaN(d.Y) || float.IsInfinity(d.Y)) return false;
                if (!first && !Near(d, delta)) return false;
                if (first) delta = d; // One representative delta for every annotation owner.
                first = false;
            }
            return !Near(delta, PointF.Empty);
        }
        private static bool Near(PointF a, PointF b) => Math.Abs(a.X-b.X) <= .001f && Math.Abs(a.Y-b.Y) <= .001f;
        internal bool Sync(GH_Document document)
            => document != null && ReferenceEquals(document,_document) &&
                Sync(document,document.SelectedObjects(),document.UndoServer.UndoGuids.FirstOrDefault());
        internal bool Sync(GH_Document document, IEnumerable<IGH_DocumentObject> selected, Guid topRecord)
        {
            if (_move == null || !_move.Allowed || _move.Actions.Count == 0 || !ReferenceEquals(document, _document) ||
                !_move.Actions.Keys.All(o => o.Eligible) || !new HashSet<IGH_DocumentObject>(selected).SetEquals(_selected) || topRecord != _record.Guid) return false;
            // Our own actions are the only permitted non-pivot actions after the first successful sample.
            var pivots = new GH_UndoRecord(_record.Name, _record.Actions.Where(a => !_actions.Values.Any(own => ReferenceEquals(own,a))));
            if (!UniformDelta(_document, document, pivots, _selected.Length, _pivots, out var delta) ||
                _record.ActionCount != _selected.Length + _actions.Count) return false;
            if (Near(delta, _delta)) return false;
            _move.Translate(delta, false);
            if (!_move.Changed) return false;
            foreach (var pair in _move.Actions)
                if (!_actions.ContainsKey(pair.Key)) { _record.AddAction(pair.Value); _actions.Add(pair.Key,pair.Value); }
            foreach (var owner in _move.Actions.Keys) owner.Changed();
            _delta = delta;
            return true;
        }
    }
}
