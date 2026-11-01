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
        }
        protected override int ShapeType => -1;
        public override void Render(Graphics g, Pen pen, float thickness) { }
        public override (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness) => (false, -1);
        public override PointF[] GetPoints() => new PointF[0];
        public override void Move(double dx, double dy) { }
        public override void MovePoint(int pointIndex, Point3d newPos) { }
        // Opaque payloads cannot be meaningfully edited or duplicated by this version.
        public override DrawShape Clone() => new OpaqueShape(Payload, Diagnostic);
    }
}
