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
            ":" + shape.StrokeVisible + ":" + shape.CloudSize + ":" + shape.FillColor?.ToArgb() + ":" + shape.FillOpacity + ":" + shape.Hatch + ":" + shape.HatchScale + ":" + shape.HatchBackground?.ToArgb() +
            (shape is DrawShapeGroup g ? string.Join(";", g.Children.Select(Fingerprint)) : string.Join(";", shape.GetPoints().Select(p => p.ToString()))) +
            (shape is TextShape t ? t.Text+":"+t.BoxWidth+":"+t.StylePreset+":"+t.SizeMultiplier+":"+t.Bold+":"+t.Italic+":"+t.TextColorOverride?.ToArgb() : "") +
            (shape is CurveShape c ? c.Closed.ToString() : shape is PolylineShape l ? l.Closed.ToString() : "");
    }
    private static DrawShape RandomShape(Random random, int depth)
    {
        Point3d P() => new Point3d(random.NextDouble() * 2000, random.NextDouble() * 2000, 0);
        DrawShape result;
        switch (random.Next(depth > 4 ? 5 : 6))
        {
            case 0: result = new LineShape(P(), P()); break;
            case 1: result = new PolylineShape(Enumerable.Range(0, random.Next(2, 15)).Select(_ => P()).ToList(), random.Next(2) == 0); break;
            case 2: result = new FrameShape(P(), P(), P(), P()); break;
            case 3: result = new CurveShape(Enumerable.Range(0, random.Next(2, 15)).Select(_ => P()).ToList(), random.Next(2) == 0); break;
            case 4: result = new TextShape { Text = "Random text "+random.Next()+"\nSecond line   ", Anchor=P(),
                BoxWidth=random.Next(2)==0 ? (double?)null : random.Next(20,300), StylePreset=(TextStylePreset)random.Next(3),
                SizeMultiplier=new[]{.5,.75,1,1.5,2,3,4}[random.Next(7)], Bold=random.Next(2)==0, Italic=random.Next(2)==0,
                TextColorOverride=random.Next(2)==0 ? (Color?)null : Color.FromArgb(random.Next()) }; break;
            default:
                var group = new DrawShapeGroup();
                group.Children.AddRange(Enumerable.Range(0, random.Next(1, 5)).Select(_ => RandomShape(random, depth + 1)));
                result = group; break;
        }
        result.ThicknessMultiplier = new[] { .5f, 1, 2, 3 }[random.Next(4)];
        result.LineType = (DashStyle)random.Next(5);
        result.OverrideColor = random.Next(2) == 0 ? (Color?)null : Color.FromArgb(random.Next());
        result.StrokeVisible=random.Next(2)==0;
        result.FillColor=random.Next(2)==0 ? (Color?)null : Color.FromArgb(random.Next());
        result.FillOpacity=new[]{.1,.25,.5,.75,1}[random.Next(5)];
        result.Hatch=(HatchPattern)random.Next(11); result.HatchScale=new[]{.5,1,2,4}[random.Next(4)];
        result.HatchBackground=random.Next(2)==0 ? (Color?)null : Color.FromArgb(random.Next());
        result.CloudSize=StyleTarget.Clouds[random.Next(4)];
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
        Test("missing required coordinates remain opaque", () => { var root = Save(Pair()); FirstRecord(root).RemoveItem("X"); AssertOpaquePreserved(root); });
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
        GuardrailTests.Run();
        return Finish();
    }
    private static int Finish()
    {
        Console.WriteLine($"Serialization/model: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }
}
