using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using GH_IO.Serialization;
using hopperdraw;
using Rhino.Geometry;

internal static class Program
{
    private static int _passed, _failed;
    internal static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Test(string name, Action test)
    {
        try { test(); _passed++; Console.WriteLine("PASS: " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL: " + name + ": " + ex); }
    }
    private static LineShape Line(double y = 10) => new LineShape(new Point3d(10, y, 0), new Point3d(100, y, 0));
    internal static GH_LooseChunk Save(HopperDraw component)
    {
        var chunk = new GH_LooseChunk("Container");
        Check(component.Write(chunk), "write failed");
        return chunk;
    }
    internal static HopperDraw Load(GH_IReader reader)
    {
        var component = new HopperDraw();
        Check(component.Read(reader), "component read failed");
        return component;
    }
    internal static HopperDraw RoundTrip(HopperDraw component, bool binary)
    {
        // Exercise actual GH_Archive, including its XML and binary envelopes.
        var archive = new GH_Archive();
        Check(archive.AppendObject(component, "Annotation"), "append failed");
        var copy = new GH_Archive();
        Check(binary ? copy.Deserialize_Binary(archive.Serialize_Binary()) : copy.Deserialize_Xml(archive.Serialize_Xml()), "archive parse failed");
        var result = new HopperDraw();
        Check(copy.ExtractObject(result, "Annotation"), "extract failed");
        return result;
    }
    internal static string Fingerprint(DrawShape shape)
    {
        if (shape is OpaqueShape o) return o.Payload.Serialize_Xml();
        return shape.GetType().Name + shape.PersistentId + ":" + shape.ThicknessMultiplier + ":" + shape.LineType + ":" + shape.OverrideColor?.ToArgb() +
            (shape is DrawShapeGroup g ? string.Join(";", g.Children.Select(Fingerprint)) : string.Join(";", shape.GetPoints().Select(p => p.ToString()))) +
            (shape is CurveShape c ? c.Closed.ToString() : shape is PolylineShape l ? l.Closed.ToString() : "");
    }
    private static DrawShape RandomShape(Random random, int depth)
    {
        Point3d P() => new Point3d(random.NextDouble() * 2000, random.NextDouble() * 2000, 0);
        DrawShape result;
        switch (random.Next(depth > 4 ? 4 : 5))
        {
            case 0: result = new LineShape(P(), P()); break;
            case 1: result = new PolylineShape(Enumerable.Range(0, random.Next(2, 15)).Select(_ => P()).ToList(), random.Next(2) == 0); break;
            case 2: result = new FrameShape(P(), P(), P(), P()); break;
            case 3: result = new CurveShape(Enumerable.Range(0, random.Next(2, 15)).Select(_ => P()).ToList(), random.Next(2) == 0); break;
            default:
                var group = new DrawShapeGroup();
                group.Children.AddRange(Enumerable.Range(0, random.Next(1, 5)).Select(_ => RandomShape(random, depth + 1)));
                result = group; break;
        }
        result.ThicknessMultiplier = new[] { .5f, 1, 2, 3 }[random.Next(4)];
        result.LineType = (DashStyle)random.Next(5);
        result.OverrideColor = random.Next(2) == 0 ? (Color?)null : Color.FromArgb(random.Next());
        return result;
    }
    private static GH_IWriter FirstRecord(GH_LooseChunk root) => (GH_IWriter)root.FindChunk("HopperDraw").FindChunk("Shapes").FindChunk("Shape", 0);
    private static string RawFingerprint(GH_IReader reader)
    {
        // Independent SDK copy, deliberately not ShapeSerializer.Capture/CopyChunk.
        var copy = new GH_LooseChunk("Independent");
        copy.CopyValuesFromChunk((GH_Chunk)reader);
        return copy.Serialize_Xml();
    }
    private static void AssertOpaquePreserved(GH_LooseChunk root)
    {
        var original = RawFingerprint((GH_IReader)FirstRecord(root));
        var loaded = Load(root);
        Check(loaded.Shapes[0] is OpaqueShape && loaded.Shapes.Count == 2, "opaque position/neighbor lost");
        Check(!string.IsNullOrEmpty(loaded.LoadWarning), "missing warning");
        var saved = Save(loaded);
        Check(RawFingerprint((GH_IReader)FirstRecord(saved)) == original, "opaque values changed");
        Check(!string.IsNullOrEmpty(Load(saved).LoadWarning), "warning lost on resave");
        foreach (bool binary in new[] { false, true })
        {
            var copy = RoundTrip(loaded, binary);
            Check(RawFingerprint((GH_IReader)FirstRecord(Save(copy))) == original, "opaque archive values changed");
            if (((GH_IReader)FirstRecord(root)).ItemExists("Id"))
                Check(copy.Shapes[0].PersistentId == loaded.Shapes[0].PersistentId, "opaque identity changed on reload");
        }
    }
    private static HopperDraw Pair() => new HopperDraw { Shapes = new List<DrawShape> { Line(), Line(20) } };

    [STAThread]
    private static int Main()
    {
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--failure-exit-probe") >= 0)
        {
            Test("intentional failure exit probe", () => Check(false, "Intentional failure to verify process exit status."));
            return Finish();
        }
        Test("group at index zero and nested groups round trip XML/binary", () =>
        {
            var nested = new DrawShapeGroup(); nested.Children.Add(Line());
            var group = new DrawShapeGroup(); group.Children.Add(nested); group.Children.Add(new FrameShape(new Point3d(1, 1, 0), new Point3d(3, 1, 0), new Point3d(3, 3, 0), new Point3d(1, 3, 0)));
            var component = new HopperDraw(); component.Shapes.Add(group);
            foreach (bool binary in new[] { false, true }) Check(Fingerprint(component.Shapes[0]) == Fingerprint(RoundTrip(component, binary).Shapes[0]), "tree changed");
        });
        Test("1205 shapes have collision-free keys", () =>
        {
            var c = new HopperDraw(); c.Shapes.AddRange(Enumerable.Range(0, 1205).Select(i => Line(i)));
            var group = new DrawShapeGroup(); group.Children.Add(Line(9999)); c.Shapes.Insert(0, group);
            foreach (bool binary in new[] { false, true }) Check(string.Join("|", c.Shapes.Select(Fingerprint)) == string.Join("|", RoundTrip(c, binary).Shapes.Select(Fingerprint)), "large list changed");
        });
        Test("400 random trees round trip through XML and binary GH_Archive", () =>
        {
            var random = new Random(7342);
            for (int i = 0; i < 400; i++)
            {
                var c = new HopperDraw(); c.Shapes.Add(RandomShape(random, 0));
                foreach (bool binary in new[] { false, true }) Check(Fingerprint(c.Shapes[0]) == Fingerprint(RoundTrip(c, binary).Shapes[0]), "random tree " + i);
            }
        });
        Test("unknown kinds preserve opaque records and z-order", () =>
        {
            var root = Save(Pair()); var record = FirstRecord(root); record.RemoveItem("Kind"); record.SetInt32("Kind", 99); record.SetString("FutureData", "keep me");
            record.CreateChunk("Extension", 42).SetDoubleArray("Payload", new[] { 1.2, 3.4 });
            AssertOpaquePreserved(root);
        });
        Test("future format payload preserved verbatim", () =>
        {
            var root = Save(Pair()); var payload = (GH_IWriter)root.FindChunk("HopperDraw"); payload.RemoveItem("FormatVersion"); payload.SetInt32("FormatVersion", 99); payload.SetString("Future", "verbatim");
            var original = RawFingerprint((GH_IReader)payload);
            var loaded = Load(root); Check(!loaded.CanEditShapes && loaded.LoadWarning != null, "future data editable/unwarned");
            var saved = Save(loaded); Check(RawFingerprint(saved.FindChunk("HopperDraw")) == original, "future version rewritten");
            Check(!Load(saved).CanEditShapes, "future state lost");
            foreach (bool binary in new[] { false, true })
                Check(RawFingerprint(Save(RoundTrip(loaded, binary)).FindChunk("HopperDraw")) == original, "future archive rewritten");
        });
        Test("optional style defaults and transparent color", () =>
        {
            var root = Save(Pair()); var record = FirstRecord(root);
            foreach (var key in new[] { "ThickMult", "LineType", "HasColor" }) record.RemoveItem(key);
            var loaded = Load(root); Check(loaded.LoadWarning != null && loaded.Shapes[0].ThicknessMultiplier == 1 && loaded.Shapes[0].LineType == DashStyle.Solid, "optional defaults wrong");
            loaded.Shapes[0].OverrideColor = Color.FromArgb(0);
            Check(RoundTrip(loaded, true).Shapes[0].OverrideColor.Value.ToArgb() == 0, "transparent color lost");
        });
        Test("present mismatched optional style types remain recoverable", () =>
        {
            foreach (string key in new[] { "ThickMult", "LineType", "HasColor" })
            {
                var root = Save(Pair()); var record = FirstRecord(root); record.RemoveItem(key); record.SetString(key, "wrong type");
                AssertOpaquePreserved(root);
            }
            var componentRoot = Save(Pair()); var payload = (GH_IWriter)componentRoot.FindChunk("HopperDraw"); payload.RemoveItem("Thickness"); payload.SetString("Thickness", "wrong type");
            var c = Load(componentRoot); Check(!c.CanEditShapes && c.LoadWarning != null, "component style corruption went unreported");
            Check(RawFingerprint(Save(c).FindChunk("HopperDraw")) == RawFingerprint((GH_IReader)payload), "component style corruption lost data");
        });
        Test("missing required coordinates remain opaque", () => { var root = Save(Pair()); FirstRecord(root).RemoveItem("X"); AssertOpaquePreserved(root); });
        Test("required kind, GUID, closed flag and frame cardinality are never defaulted", () =>
        {
            foreach (string key in new[] { "Kind", "Id" })
            {
                var root = Save(Pair()); FirstRecord(root).RemoveItem(key); AssertOpaquePreserved(root);
            }
            foreach (int kind in new[] { 1, 2, 3 })
            {
                var root = Save(Pair()); var record = FirstRecord(root); record.RemoveItem("Kind"); record.SetInt32("Kind", kind);
                // A two-point frame is invalid; a polyline/curve requires an explicit closed flag.
                AssertOpaquePreserved(root);
            }
        });
        Test("unreadable group children preserve their positions and valid siblings", () =>
        {
            var c = Pair(); var group = new DrawShapeGroup(); group.Children.AddRange(c.Shapes); c.Shapes = new List<DrawShape> { group };
            var root = Save(c); var child = (GH_IWriter)((GH_IReader)FirstRecord(root)).FindChunk("Children").FindChunk("Shape", 0); child.RemoveItem("X");
            var loaded = Load(root); var children = ((DrawShapeGroup)loaded.Shapes[0]).Children;
            Check(children.Count == 2 && children[0] is OpaqueShape && children[1] is LineShape && loaded.LoadWarning != null, "child recovery lost sibling");
            Check(((DrawShapeGroup)RoundTrip(loaded, true).Shapes[0]).Children[0] is OpaqueShape, "opaque child lost on save");
        });
        Test("mismatched required coordinate arrays remain opaque", () =>
        {
            var root = Save(Pair()); var r = FirstRecord(root); r.RemoveItem("Y"); r.SetDoubleArray("Y", new[] { 1.0 }); AssertOpaquePreserved(root);
        });
        Test("valid empty list differs from missing list/count", () =>
        {
            Check(Load(Save(new HopperDraw())).LoadWarning == null, "empty list warned");
            foreach (bool list in new[] { false, true })
            {
                var root = Save(Pair()); var payload = (GH_IWriter)root.FindChunk("HopperDraw");
                if (list) payload.RemoveChunk("Shapes"); else ((GH_IWriter)((GH_IReader)payload).FindChunk("Shapes")).RemoveItem("Count");
                var loaded = Load(root); Check(loaded.LoadWarning != null && !loaded.CanEditShapes, "damage mistaken for empty");
                Check(Load(Save(loaded)).LoadWarning != null, "damage warning lost on save");
            }
        });
        Test("depth budget preserves offending subtree", () =>
        {
            var root = Save(Pair()); var record = FirstRecord(root);
            for (int i = 0; i < 45; i++)
            {
                record.RemoveItem("Kind"); record.SetInt32("Kind", 4);
                var children = record.CreateChunk("Children"); children.SetInt32("Count", 1);
                record = children.CreateChunk("Shape", 0); record.SetInt32("Kind", 0); record.SetGuid("Id", Guid.NewGuid());
            }
            var loaded = Load(root); Check(loaded.LoadWarning != null && loaded.Shapes.Count == 2, "depth damage lost neighbor");
            Check(Load(Save(loaded)).LoadWarning != null, "depth warning lost");
        });
        Test("count and point budgets prevent oversized interpretation", () =>
        {
            var root = Save(Pair()); var list = (GH_IWriter)root.FindChunk("HopperDraw").FindChunk("Shapes"); list.RemoveItem("Count"); list.SetInt32("Count", int.MaxValue);
            Check(!Load(root).CanEditShapes, "count budget ignored");
            root = Save(Pair()); var r = FirstRecord(root); r.RemoveItem("Kind"); r.SetInt32("Kind", 1); r.SetBoolean("Closed", false); r.RemoveItem("X"); r.RemoveItem("Y");
            r.SetDoubleArray("X", new double[ShapeSerializer.MaxPoints + 1]); r.SetDoubleArray("Y", new double[ShapeSerializer.MaxPoints + 1]);
            Check(Load(root).Shapes[0] is OpaqueShape, "point budget ignored");
        });
        Test("successful read clears stale warning and restored selection", () =>
        {
            var root = Save(Pair()); FirstRecord(root).RemoveItem("X"); var loaded = Load(root); loaded.SelectedShapeIndices.Add(1);
            Check(loaded.Read(Save(Pair())) && loaded.LoadWarning == null && !loaded.HasSelection && loaded.RuntimeMessages(Grasshopper.Kernel.GH_RuntimeMessageLevel.Warning).Count == 0, "stale warning/selection");
        });
        Test("base Grasshopper read exceptions propagate without annotation recovery", () =>
        {
            var component = new HopperDraw(); bool threw = false;
            try { component.Read(null); } catch (Exception) { threw = true; }
            Check(threw && component.LoadWarning == null, "base metadata exception became annotation recovery");
        });
        Test("known duplicates and pasted component IDs are fresh", () =>
        {
            var original = Pair(); var copy = original.Shapes[0].Clone(); Check(copy.PersistentId != original.Shapes[0].PersistentId, "duplicate reused ID");
            var pasted = Load(Save(original)); var id = pasted.Shapes[0].PersistentId;
            pasted.NewInstanceGuid(); ((Grasshopper.Kernel.IGH_InstanceGuidDependent)pasted).InstanceGuidsChanged(new SortedDictionary<Guid, Guid>()); Check(pasted.Shapes[0].PersistentId != id, "paste reused ID");
            var root = Save(original); FirstRecord(root).RemoveItem("X");
            pasted = Load(root); id = pasted.Shapes[0].PersistentId;
            pasted.NewInstanceGuid(); ((Grasshopper.Kernel.IGH_InstanceGuidDependent)pasted).InstanceGuidsChanged(new SortedDictionary<Guid, Guid>());
            Check(pasted.Shapes[0].PersistentId != id && RoundTrip(pasted, true).Shapes[0].PersistentId == pasted.Shapes[0].PersistentId, "opaque v1 paste identity not rekeyed");
        });
        ReliabilityTests.Run();
        Review2Tests.Run();
        CanvasMenuTests.Run();
        return Finish();
    }
    private static int Finish()
    {
        Console.WriteLine($"Serialization/model: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }
}
