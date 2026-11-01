using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Undo;

namespace hopperdraw
{
    // In-memory undo only. GH clears history when loading a document; no persistence is claimed.
    internal sealed class ShapeEditUndoAction : GH_ObjectUndoAction
    {
        private List<DrawShape> _shapes;
        private Dictionary<Guid, PointF> _pivots;
        internal ShapeEditUndoAction(HopperDraw owner, IEnumerable<IGH_DocumentObject> movedObjects) : base(owner.InstanceGuid)
        {
            _shapes = Snapshot(owner.Shapes);
            _pivots = movedObjects.Where(o => o?.Attributes != null).Distinct()
                .ToDictionary(o => o.InstanceGuid, o => o.Attributes.Pivot);
        }
        public override bool ExpiresSolution => false;
        protected override void Object_Undo(GH_Document doc, IGH_DocumentObject obj) => Swap(doc, obj);
        protected override void Object_Redo(GH_Document doc, IGH_DocumentObject obj) => Swap(doc, obj);

        internal void Swap(GH_Document doc, IGH_DocumentObject obj)
        {
            if (!(obj is HopperDraw owner)) throw new InvalidOperationException("HopperDraw undo target is unavailable.");
            var current = Snapshot(owner.Shapes);
            // Prepare everything before changing the live state. No tolerant archive reader is involved.
            var restored = Snapshot(_shapes);
            var objects = new Dictionary<Guid, IGH_DocumentObject>();
            var currentPivots = new Dictionary<Guid, PointF>();
            foreach (var pair in _pivots)
            {
                var target = doc?.FindObject(pair.Key, true);
                if (target?.Attributes == null) throw new InvalidOperationException("Moved object is unavailable for undo.");
                objects.Add(pair.Key, target);
                currentPivots.Add(pair.Key, target.Attributes.Pivot);
            }
            owner.RestoreShapes(restored);
            foreach (var pair in _pivots)
            {
                var attributes = objects[pair.Key].Attributes;
                attributes.Pivot = pair.Value;
                attributes.ExpireLayout();
                attributes.PerformLayout();
            }
            _shapes = current;
            _pivots = currentPivots;
        }

        internal static List<DrawShape> Snapshot(IEnumerable<DrawShape> shapes) => shapes.Select(CloneForSnapshot).ToList();
        internal static DrawShape CloneForSnapshot(DrawShape shape)
        {
            DrawShape result;
            if (shape is DrawShapeGroup group)
            {
                var copy = new DrawShapeGroup();
                copy.Children.AddRange(Snapshot(group.Children));
                result = copy;
            }
            else result = shape.Clone();
            result.Id = shape.Id;
            result.PersistentId = shape.PersistentId;
            result.ThicknessMultiplier = shape.ThicknessMultiplier;
            result.OverrideColor = shape.OverrideColor;
            result.LineType = shape.LineType;
            return result;
        }
    }
}
