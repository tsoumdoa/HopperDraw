using System;
using System.Reflection;
using System.Windows.Forms;
using hopperdraw;
using Rhino.Geometry;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _failures;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Test(string name, Action action)
    {
        try { action(); Console.WriteLine("PASS: " + name); }
        catch (Exception e) { _failures++; Console.WriteLine("FAIL: " + name + ": " + e.GetBaseException().Message); }
        finally { SetOwner(null); }
    }

    private static void Set(HopperDraw target, string name, object value) =>
        typeof(HopperDraw).GetField(name, PrivateInstance).SetValue(target, value);

    private static void SetOwner(HopperDraw target) =>
        typeof(HopperDraw).GetField("_capturingOwner", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, target);

    private static void Invoke(HopperDraw target, string name, EventArgs args) =>
        typeof(HopperDraw).GetMethod(name, PrivateInstance).Invoke(target, new object[] { null, args });

    private static HopperDraw DrawingComponent(int mode)
    {
        var component = new HopperDraw();
        Set(component, "_isActivated", true);
        Set(component, "_drawingModeEnabled", true);
        Set(component, "_drawMode", mode);
        SetOwner(component);
        return component;
    }

    private static void Click(HopperDraw target, int x, int y)
    {
        var e = new MouseEventArgs(MouseButtons.Left, 1, x, y, 0);
        Invoke(target, "Canvas_MouseDown", e);
        Invoke(target, "Canvas_MouseUp", e);
    }

    [STAThread]
    private static int Main()
    {
        Test("double D requires two distinct presses", () =>
        {
            var gesture = new DrawingKeyGesture();
            Check(!gesture.KeyDown(Keys.D, true, 100, 500), "First press activated");
            Check(!gesture.KeyDown(Keys.D, true, 110, 500), "Auto-repeat activated");
            Check(!gesture.KeyDown(Keys.D, true, 120, 500), "Repeated auto-repeat activated");
            gesture.KeyUp(Keys.D);
            Check(gesture.KeyDown(Keys.D, true, 130, 500), "Distinct second press did not activate");
        });
        Test("activation uses the configured interval and handles tick wraparound", () =>
        {
            foreach (int start in new[] { 100, int.MaxValue - 50 })
            {
                var gesture = new DrawingKeyGesture();
                gesture.KeyDown(Keys.D, true, start, 100);
                gesture.KeyUp(Keys.D);
                Check(!gesture.KeyDown(Keys.D, true, unchecked(start + 101), 100), "Expired press activated");
                gesture.KeyUp(Keys.D);
                Check(gesture.KeyDown(Keys.D, true, unchecked(start + 201), 100), "Boundary second press failed");
            }
        });
        Test("keys handled by another owner break a pending double D", () =>
        {
            var a = new DrawingKeyGesture();
            var b = new DrawingKeyGesture();
            a.KeyDown(Keys.D, true, 100, 500); b.KeyDown(Keys.D, false, 100, 500);
            a.KeyUp(Keys.D); b.KeyUp(Keys.D);
            a.KeyDown(Keys.D, false, 150, 500); b.KeyDown(Keys.D, true, 150, 500);
            a.KeyUp(Keys.D); b.KeyUp(Keys.D);
            Check(!a.KeyDown(Keys.D, true, 200, 500), "A retained a stale first press");
        });
        Test("D held with a modifier must be released before double D", () =>
        {
            var gesture = new DrawingKeyGesture();
            gesture.KeyDown(Keys.D, false, 100, 500);
            gesture.KeyUp(Keys.ControlKey);
            Check(!gesture.KeyDown(Keys.D, true, 120, 500), "Modifier release counted as a fresh press");
            gesture.KeyUp(Keys.D);
            Check(!gesture.KeyDown(Keys.D, true, 130, 500), "Only one fresh D press activated");
            gesture.KeyUp(Keys.D);
            Check(gesture.KeyDown(Keys.D, true, 140, 500), "Two fresh presses failed");
        });
        Test("intervening keys and focus loss break the activation gesture", () =>
        {
            var gesture = new DrawingKeyGesture();
            gesture.KeyDown(Keys.D, true, 100, 500); gesture.KeyUp(Keys.D);
            gesture.KeyDown(Keys.A, true, 110, 500);
            Check(!gesture.KeyDown(Keys.D, true, 120, 500), "D-A-D activated");
            gesture.Reset(releaseKey: true);
            Check(!gesture.KeyDown(Keys.D, true, 130, 500), "D across focus loss activated");
        });
        Test("resetting a gesture preserves the physical held key", () =>
        {
            var gesture = new DrawingKeyGesture();
            gesture.KeyDown(Keys.D, true, 100, 500);
            gesture.Reset();
            gesture.KeyDown(Keys.D, true, 110, 500);
            gesture.KeyUp(Keys.D);
            Check(!gesture.KeyDown(Keys.D, true, 120, 500), "A repeat after reset counted as a fresh press");
        });
        Test("ordinary clicks complete all four shapes and preserve drawing mode", () =>
        {
            for (int mode = 0; mode <= 3; mode++)
            {
                var component = DrawingComponent(mode);
                Click(component, 100, 100);
                Click(component, 200, 150);
                if (mode == 1 || mode == 3)
                    Invoke(component, "Canvas_KeyDown", new KeyEventArgs(Keys.Enter));
                Check(component.Shapes.Count == 1 && !component.IsDrawing, "Shape did not complete for mode " + mode);
                Check(component.DrawingModeEnabled, "Mode exited after completing mode " + mode);
                Click(component, 300, 300);
                Check(component.IsDrawing, "Persistent mode did not start the next shape for mode " + mode);
                SetOwner(null);
            }
        });
        Test("Escape with Shift discards unfinished geometry and preserves completed shapes", () =>
        {
            var component = DrawingComponent(1);
            component.Shapes.Add(new LineShape(new Point3d(0, 0, 0), new Point3d(30, 0, 0)));
            Click(component, 100, 100);
            var e = new KeyEventArgs(Keys.Escape | Keys.Shift);
            Invoke(component, "Canvas_KeyDown", e);
            Check(!component.DrawingModeEnabled && !component.IsDrawing && component.CurrentPoints.Count == 0, "Escape retained drawing state");
            Check(component.Shapes.Count == 1 && e.SuppressKeyPress, "Escape lost completed geometry or was not consumed");
        });
        Test("Escape exits even when a native Grasshopper interaction handled it first", () =>
        {
            var component = DrawingComponent(0);
            var e = new KeyEventArgs(Keys.Escape) { Handled = true };
            Invoke(component, "Canvas_KeyDown", e);
            Check(!component.DrawingModeEnabled, "Native-handled Escape left mode enabled");
        });
        Test("HopperDraw respects keys already handled by another subscriber", () =>
        {
            var component = DrawingComponent(1);
            Click(component, 100, 100); Click(component, 200, 150);
            Invoke(component, "Canvas_KeyDown", new KeyEventArgs(Keys.Enter) { Handled = true });
            Check(component.IsDrawing && component.Shapes.Count == 0, "Handled Enter also completed a HopperDraw shape");
        });
        Test("mouse input resets a pending D gesture", () =>
        {
            var component = DrawingComponent(0);
            var gesture = (DrawingKeyGesture)typeof(HopperDraw).GetField("_drawingKeyGesture", PrivateInstance).GetValue(component);
            gesture.KeyDown(Keys.D, true, 100, 500); gesture.KeyUp(Keys.D);
            Click(component, 100, 100);
            Check(!gesture.KeyDown(Keys.D, true, 120, 500), "D-click-D activated");
        });
        Test("drawing double clicks suppress component search until mode exits", () =>
        {
            var component = DrawingComponent(1);
            var validator = new HopperDraw.DrawingCanvasValidator(component);
            var point = new System.Drawing.PointF(200, 150);
            Check(!validator.CanShowComponentSearchBox(point), "Search can interrupt drawing mode");
            Click(component, 100, 100); Click(component, 200, 150);
            Click(component, 200, 150);
            Check(component.Shapes.Count == 1 && component.DrawingModeEnabled, "Double click did not complete the polyline");
            Check(!validator.CanShowComponentSearchBox(point), "Search reopened as the double click completed");
            Invoke(component, "Canvas_KeyDown", new KeyEventArgs(Keys.Escape));
            Check(validator.CanShowComponentSearchBox(point), "Search stayed blocked after Escape");
        });
        Test("inactive components do not block component search", () =>
        {
            var component = DrawingComponent(0);
            var validator = new HopperDraw.DrawingCanvasValidator(component);
            Set(component, "_isActivated", false);
            Check(validator.CanShowComponentSearchBox(new System.Drawing.PointF(100, 100)), "Inactive component blocked search");
        });
        Test("orthogonal snapping in every direction, zero movement, and diagonal ties", () =>
        {
            var start = new Point3d(10, 20, 0);
            var targets = new[] { new Point3d(35, 25, 0), new Point3d(-15, 25, 0), new Point3d(15, 50, 0), new Point3d(15, -10, 0), start, new Point3d(20, 30, 0) };
            var expected = new[] { new Point3d(35, 20, 0), new Point3d(-15, 20, 0), new Point3d(10, 50, 0), new Point3d(10, -10, 0), start, new Point3d(20, 20, 0) };
            for (int i = 0; i < targets.Length; i++)
                Check(GeometryUtilities.SnapOrthogonal(start, targets[i]) == expected[i], "Snap case " + i);
        });
        Console.WriteLine(_failures + " failure(s)");
        return _failures == 0 ? 0 : 1;
    }
}
