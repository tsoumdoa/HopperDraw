using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.IO;
using System.Text;
using GH_IO.Serialization;
using Rhino.Geometry;

namespace hopperdraw
{
    /// <summary>Version 1: dedicated chunks, strict geometry, optional styles, lossless opaque recovery.</summary>
    internal static class ShapeSerializer
    {
        // Major version only: breaking changes bump this. Additive unknown keys are retained.
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
                // Items is a detached list, not a mutable view into GH_Chunk. Round-trip one
                // node's items through GH_IO itself to retain every supported item type and
                // indexed key, without recursively copying its children or renaming the target.
                var items = pair.Item1.Items;
                using (var stream = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                    {
                        writer.Write(pair.Item2.Name);
                        writer.Write(pair.Item2.Index);
                        writer.Write(items.Count);
                        writer.Write(0); // Children are copied iteratively below.
                        foreach (var item in items) item.Write(writer);
                    }
                    stream.Position = 0;
                    using (var reader = new BinaryReader(stream)) pair.Item2.Read(reader);
                }
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

        internal static GH_LooseChunk Residual(GH_IReader source, IEnumerable<string> knownItems, params string[] knownChunks)
        {
            var residual = Capture(source);
            foreach (string name in knownItems) residual.RemoveItem(name);
            foreach (string name in knownChunks) residual.RemoveChunk(name);
            return residual;
        }

        internal static void RegenerateIds(IEnumerable<DrawShape> shapes)
        {
            foreach (var shape in shapes)
            {
                shape.PersistentId = Guid.NewGuid();
                if (shape is OpaqueShape opaque) opaque.UpdateArchiveIdentity();
                if (shape is DrawShapeGroup group) RegenerateIds(group.Children);
            }
        }

        internal static void WriteShapeList(GH_IWriter parent, string name, IList<DrawShape> shapes, GH_IReader extensions = null)
        {
            var list = parent.CreateChunk(name);
            if (extensions != null) CopyChunk(extensions, list);
            list.SetInt32("Count", shapes.Count);
            for (int i = 0; i < shapes.Count; i++) WriteShape(list.CreateChunk("Shape", i), shapes[i]);
        }

        internal static void WriteShape(GH_IWriter writer, DrawShape shape)
        {
            if (shape is OpaqueShape opaque) { CopyChunk(opaque.Payload, writer); return; }
            if (shape.Extensions != null) CopyChunk(shape.Extensions, writer);
            int kind = shape is LineShape ? 0 : shape is PolylineShape ? 1 : shape is FrameShape ? 2 : shape is CurveShape ? 3 : 4;
            writer.SetInt32("Kind", kind);
            writer.SetGuid("Id", shape.PersistentId);
            writer.SetDouble("ThickMult", shape.ThicknessMultiplier);
            writer.SetInt32("LineType", (int)shape.LineType);
            writer.SetBoolean("StrokeVisible", shape.StrokeVisible);
            writer.SetBoolean("HasFillColor", shape.FillColor.HasValue);
            if (shape.FillColor.HasValue) writer.SetInt32("FillColor", shape.FillColor.Value.ToArgb());
            writer.SetDouble("FillOpacity", shape.FillOpacity);
            writer.SetInt32("Hatch", (int)shape.Hatch);
            writer.SetDouble("HatchScale", shape.HatchScale);
            writer.SetBoolean("HasHatchBackground", shape.HatchBackground.HasValue);
            if (shape.HatchBackground.HasValue) writer.SetInt32("HatchBackground", shape.HatchBackground.Value.ToArgb());
            writer.SetBoolean("HasColor", shape.OverrideColor.HasValue);
            if (shape.OverrideColor.HasValue) writer.SetInt32("Color", shape.OverrideColor.Value.ToArgb());
            if (shape is DrawShapeGroup group) { WriteShapeList(writer, "Children", group.Children, group.ChildrenExtensions); return; }
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

        internal static List<DrawShape> ReadShapeList(GH_IReader parent, string name, out List<string> diagnostics) =>
            ReadShapeList(parent, name, out diagnostics, out _);

        internal static List<DrawShape> ReadShapeList(GH_IReader parent, string name, out List<string> diagnostics, out GH_LooseChunk extensions)
        {
            var budget = new Budget();
            var result = ReadList(parent, name, budget, 0, out extensions);
            diagnostics = budget.Diagnostics;
            return result;
        }

        private static List<DrawShape> ReadList(GH_IReader parent, string name, Budget budget, int depth, out GH_LooseChunk extensions)
        {
            var list = parent.FindChunk(name);
            int count = -1;
            if (list == null || !list.TryGetInt32("Count", ref count) || count < 0)
                throw new FormatException("Missing or invalid " + name + " count.");
            if (depth > MaxDepth || count > MaxShapes - budget.Shapes)
                throw new FormatException("Shape depth/count budget exceeded.");
            // Missing or extra records are damage, even when Count claims the list is empty.
            if (((GH_Chunk)list).Chunks.Count(c => string.Equals(c.Name, "Shape", StringComparison.OrdinalIgnoreCase)) != count)
                throw new FormatException("Shape count does not match records.");
            extensions = Residual(list, new[] { "Count" });
            budget.Shapes += count;
            var result = new List<DrawShape>(count);
            for (int i = 0; i < count; i++)
            {
                var record = list.FindChunk("Shape", i);
                if (record == null) throw new FormatException("Missing shape record " + i + ".");
                extensions.RemoveChunk("Shape", i);
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
            int kind = -1;
            if (!reader.TryGetInt32("Kind", ref kind)) throw new FormatException("Missing required shape kind.");
            if (kind < 0 || kind > 4) throw new FormatException("Unknown shape kind " + kind + ".");
            Guid id = reader.GetGuid("Id");
            if (id == Guid.Empty || !budget.Ids.Add(id)) throw new FormatException("Missing/duplicate shape ID.");
            DrawShape shape;
            if (kind == 4)
            {
                var group = new DrawShapeGroup();
                group.Children.AddRange(ReadList(reader, "Children", budget, depth + 1, out var childExtensions));
                group.ChildrenExtensions = childExtensions;
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
            var knownItems = new List<string> { "Kind", "Id", "ThickMult", "LineType", "HasColor", "Color", "StrokeVisible", "HasFillColor", "FillColor", "FillOpacity", "Hatch", "HatchScale", "HasHatchBackground", "HatchBackground" };
            if (kind != 4) knownItems.AddRange(new[] { "X", "Y" });
            if (kind == 1 || kind == 3) knownItems.Add("Closed");
            shape.Extensions = Residual(reader, knownItems, kind == 4 ? new[] { "Children" } : new string[0]);
            foreach (string field in new[] { "ThickMult", "LineType", "HasColor" })
                if (!reader.ItemExists(field))
                {
                    string message = "Annotation " + id + ": missing optional " + field + "; default used.";
                    shape.LoadDiagnostics.Add(message);
                    budget.Diagnostics.Add(message);
                }
            shape.PersistentId = id;
            double multiplier = 1;
            int dash = 0, color = 0;
            bool hasColor = false;
            if (reader.ItemExists("ThickMult") && !reader.TryGetDouble("ThickMult", ref multiplier) ||
                reader.ItemExists("LineType") && !reader.TryGetInt32("LineType", ref dash) ||
                reader.ItemExists("HasColor") && !reader.TryGetBoolean("HasColor", ref hasColor))
                throw new FormatException("Annotation style has a mismatched field type.");
            if (!StyleValidation.ValidMultiplier(multiplier) || dash < 0 || dash > 4)
                throw new FormatException("Invalid annotation style.");
            shape.ThicknessMultiplier = (float)multiplier;
            shape.LineType = (DashStyle)dash;
            if (hasColor)
            {
                if (!reader.TryGetInt32("Color", ref color)) throw new FormatException("Missing override color.");
                shape.OverrideColor = Color.FromArgb(color);
            }
            bool stroke = true, hasFill = false, hasBackground = false;
            double opacity = 1, scale = 1; int hatch = 0, fill = 0, background = 0;
            if (reader.ItemExists("StrokeVisible") && !reader.TryGetBoolean("StrokeVisible", ref stroke) ||
                reader.ItemExists("HasFillColor") && !reader.TryGetBoolean("HasFillColor", ref hasFill) ||
                reader.ItemExists("HasHatchBackground") && !reader.TryGetBoolean("HasHatchBackground", ref hasBackground) ||
                reader.ItemExists("FillOpacity") && !reader.TryGetDouble("FillOpacity", ref opacity) ||
                reader.ItemExists("HatchScale") && !reader.TryGetDouble("HatchScale", ref scale) ||
                reader.ItemExists("Hatch") && !reader.TryGetInt32("Hatch", ref hatch) ||
                reader.ItemExists("FillColor") && !reader.TryGetInt32("FillColor", ref fill) ||
                reader.ItemExists("HatchBackground") && !reader.TryGetInt32("HatchBackground", ref background) ||
                hasFill && !reader.ItemExists("FillColor") || hasBackground && !reader.ItemExists("HatchBackground") ||
                double.IsNaN(opacity) || opacity < .1 || opacity > 1 ||
                double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0 || scale > 1000000 || hatch < 0 || hatch > 10)
                throw new FormatException("Invalid fill/stroke style.");
            shape.StrokeVisible = stroke; shape.FillColor = hasFill ? Color.FromArgb(fill) : (Color?)null;
            shape.FillOpacity = opacity; shape.Hatch = (HatchPattern)hatch; shape.HatchScale = scale;
            shape.HatchBackground = hasBackground ? Color.FromArgb(background) : (Color?)null;
            return shape;
        }

        internal static bool ValidCoordinate(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && Math.Abs(value) <= 10000000;
    }
}
