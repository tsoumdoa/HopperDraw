using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using GH_IO.Serialization;
using Rhino.Geometry;

namespace hopperdraw
{
    /// <summary>Version 1: dedicated chunks, strict geometry, optional styles, lossless opaque recovery.</summary>
    internal static class ShapeSerializer
    {
        internal const int CurrentFormat = 1;
        internal const int MaxDepth = 32, MaxShapes = 20000, MaxPoints = 200000;

        // Use an explicit stack even for opaque trees: corrupt nesting must not overflow our stack.
        internal static void CopyChunk(GH_IReader source, GH_IWriter target)
        {
            var pending = new Stack<Tuple<GH_Chunk, GH_Chunk>>();
            pending.Push(Tuple.Create((GH_Chunk)source, (GH_Chunk)target));
            while (pending.Count > 0)
            {
                var pair = pending.Pop();
                pair.Item2.Items.AddRange(pair.Item1.Items);
                foreach (GH_Chunk child in pair.Item1.Chunks)
                {
                    var copy = child.HasIndex ? pair.Item2.CreateChunk(child.Name, child.Index) : pair.Item2.CreateChunk(child.Name);
                    pending.Push(Tuple.Create(child, (GH_Chunk)copy));
                }
            }
        }

        internal static GH_LooseChunk Capture(GH_IReader reader)
        {
            var copy = new GH_LooseChunk("Preserved");
            if (reader != null) CopyChunk(reader, copy);
            return copy;
        }

        internal static void RegenerateIds(IEnumerable<DrawShape> shapes)
        {
            foreach (var shape in shapes)
            {
                shape.PersistentId = Guid.NewGuid();
                if (shape is DrawShapeGroup group) RegenerateIds(group.Children);
            }
        }

        internal static void WriteShapeList(GH_IWriter parent, string name, IList<DrawShape> shapes)
        {
            var list = parent.CreateChunk(name);
            list.SetInt32("Count", shapes.Count);
            for (int i = 0; i < shapes.Count; i++) WriteShape(list.CreateChunk("Shape", i), shapes[i]);
        }

        internal static void WriteShape(GH_IWriter writer, DrawShape shape)
        {
            if (shape is OpaqueShape opaque) { CopyChunk(opaque.Payload, writer); return; }
            int kind = shape is LineShape ? 0 : shape is PolylineShape ? 1 : shape is FrameShape ? 2 : shape is CurveShape ? 3 : 4;
            writer.SetInt32("Kind", kind);
            writer.SetGuid("Id", shape.PersistentId);
            writer.SetDouble("ThickMult", shape.ThicknessMultiplier);
            writer.SetInt32("LineType", (int)shape.LineType);
            writer.SetBoolean("HasColor", shape.OverrideColor.HasValue);
            if (shape.OverrideColor.HasValue) writer.SetInt32("Color", shape.OverrideColor.Value.ToArgb());
            if (shape is DrawShapeGroup group) { WriteShapeList(writer, "Children", group.Children); return; }
            IEnumerable<Point3d> points;
            if (shape is LineShape line) points = new[] { line.Start, line.End };
            else if (shape is FrameShape frame) points = new[] { frame.TopLeft, frame.TopRight, frame.BottomRight, frame.BottomLeft };
            else if (shape is PolylineShape poly) { points = poly.Points; writer.SetBoolean("Closed", poly.Closed); }
            else { var curve = (CurveShape)shape; points = curve.Points; writer.SetBoolean("Closed", curve.Closed); }
            var array = points.ToArray();
            writer.SetDoubleArray("X", array.Select(p => p.X).ToArray());
            writer.SetDoubleArray("Y", array.Select(p => p.Y).ToArray());
        }

        private sealed class Budget
        {
            internal int Shapes, Points;
            internal readonly HashSet<Guid> Ids = new HashSet<Guid>();
            internal readonly List<string> Diagnostics = new List<string>();
        }

        internal static List<DrawShape> ReadShapeList(GH_IReader parent, string name, out List<string> diagnostics)
        {
            var budget = new Budget();
            var result = ReadList(parent, name, budget, 0);
            diagnostics = budget.Diagnostics;
            return result;
        }

        private static List<DrawShape> ReadList(GH_IReader parent, string name, Budget budget, int depth)
        {
            var list = parent.FindChunk(name);
            int count = -1;
            if (list == null || !list.TryGetInt32("Count", ref count) || count < 0)
                throw new FormatException("Missing or invalid " + name + " count.");
            if (depth > MaxDepth || count > MaxShapes - budget.Shapes)
                throw new FormatException("Shape depth/count budget exceeded.");
            // Missing or extra records are damage, even when Count claims the list is empty.
            if (((GH_Chunk)list).ChunkCount != count) throw new FormatException("Shape count does not match records.");
            budget.Shapes += count;
            var result = new List<DrawShape>(count);
            for (int i = 0; i < count; i++)
            {
                var record = list.FindChunk("Shape", i);
                if (record == null) throw new FormatException("Missing shape record " + i + ".");
                try { result.Add(ReadShape(record, budget, depth)); }
                catch (Exception ex)
                {
                    string message = "Unreadable annotation " + i + ": " + ex.Message;
                    result.Add(new OpaqueShape(record, message));
                    budget.Diagnostics.Add(message);
                }
            }
            return result;
        }

        private static DrawShape ReadShape(GH_IReader reader, Budget budget, int depth)
        {
            int kind = reader.GetInt32("Kind");
            if (kind < 0 || kind > 4) throw new FormatException("Unknown shape kind " + kind + ".");
            Guid id = reader.GetGuid("Id");
            if (id == Guid.Empty || !budget.Ids.Add(id)) throw new FormatException("Missing/duplicate shape ID.");
            DrawShape shape;
            if (kind == 4)
            {
                var group = new DrawShapeGroup();
                group.Children.AddRange(ReadList(reader, "Children", budget, depth + 1));
                shape = group;
            }
            else
            {
                var xs = reader.GetDoubleArray("X");
                var ys = reader.GetDoubleArray("Y");
                if (xs == null || ys == null || xs.Length != ys.Length || xs.Length < 2 ||
                    (kind == 0 && xs.Length != 2) || (kind == 2 && xs.Length != 4))
                    throw new FormatException("Required geometry is missing or mismatched.");
                if (xs.Length > MaxPoints - budget.Points) throw new FormatException("Point budget exceeded.");
                budget.Points += xs.Length;
                var points = new List<Point3d>(xs.Length);
                for (int i = 0; i < xs.Length; i++)
                {
                    if (!ValidCoordinate(xs[i]) || !ValidCoordinate(ys[i])) throw new FormatException("Non-finite/out-of-range coordinate.");
                    points.Add(new Point3d(xs[i], ys[i], 0));
                }
                bool closed = false;
                if ((kind == 1 || kind == 3) && !reader.TryGetBoolean("Closed", ref closed))
                    throw new FormatException("Missing required Closed geometry flag.");
                switch (kind)
                {
                    case 0: shape = new LineShape(points[0], points[1]); break;
                    case 1: shape = new PolylineShape(points, closed); break;
                    case 2: shape = new FrameShape(points[0], points[1], points[2], points[3]); break;
                    default: shape = new CurveShape(points, closed); break;
                }
            }
            shape.PersistentId = id;
            double multiplier = 1;
            int dash = 0, color = 0;
            bool hasColor = false;
            reader.TryGetDouble("ThickMult", ref multiplier);
            reader.TryGetInt32("LineType", ref dash);
            reader.TryGetBoolean("HasColor", ref hasColor);
            if (double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier <= 0 || multiplier > 1000 || dash < 0 || dash > 4)
                throw new FormatException("Invalid annotation style.");
            shape.ThicknessMultiplier = (float)multiplier;
            shape.LineType = (DashStyle)dash;
            if (hasColor)
            {
                if (!reader.TryGetInt32("Color", ref color)) throw new FormatException("Missing override color.");
                shape.OverrideColor = Color.FromArgb(color);
            }
            return shape;
        }

        internal static bool ValidCoordinate(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && Math.Abs(value) <= 10000000;
    }
}
