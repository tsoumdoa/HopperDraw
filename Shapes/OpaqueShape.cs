using System;
using System.Drawing;
using GH_IO.Serialization;
using Rhino.Geometry;

namespace hopperdraw
{
    /// <summary>An unreadable record keeps its exact archive values and position in its list.</summary>
    public sealed class OpaqueShape : DrawShape
    {
        internal GH_LooseChunk Payload { get; }
        public string Diagnostic { get; }
        internal OpaqueShape(GH_IReader reader, string diagnostic)
        {
            Payload = ShapeSerializer.Capture(reader);
            Diagnostic = diagnostic;
            Guid id = Guid.Empty;
            try { if (reader != null && reader.TryGetGuid("Id", ref id) && id != Guid.Empty) PersistentId = id; }
            catch (Exception) { } // A malformed ID is itself opaque data, not permission to discard the record.
        }
        internal void UpdateArchiveIdentity()
        {
            Guid id = Guid.Empty;
            // Copy/paste is an explicit new identity. Only the known v1 envelope GUID is changed;
            // unsupported extension contents and future-format payloads remain uninterpreted.
            try
            {
                if (!Payload.TryGetGuid("Id", ref id) || id == Guid.Empty) return;
                Payload.RemoveItem("Id");
                Payload.SetGuid("Id", PersistentId);
            }
            catch (Exception) { }
        }
        protected override int ShapeType => -1;
        internal override bool CanTransformGeometry => false;
        public override void Render(Graphics g, Pen pen, float thickness) { }
        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness) => (false, -1);
        public override PointF[] GetPoints() => new PointF[0];
        public override void Move(double dx, double dy) => throw new InvalidOperationException("Unreadable annotation geometry cannot be transformed.");
        public override void MovePoint(int pointIndex, Point3d newPos) => throw new InvalidOperationException("Unreadable annotation geometry cannot be edited.");
        // Opaque payloads cannot be meaningfully edited or duplicated by this version.
        public override DrawShape Clone() => throw new InvalidOperationException("Unreadable annotations cannot be duplicated.");
    }
}
