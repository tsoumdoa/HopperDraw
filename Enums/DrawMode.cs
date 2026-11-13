using System.Linq;
namespace hopperdraw
{
    public enum HatchPattern { None = 0, Diagonal = 1, CrossHatch = 2, Concrete = 3, Earth = 4, Insulation = 5, Brick = 6, Grid = 7, Dots = 8, Horizontal = 9, Vertical = 10 }
    internal enum SmoothingLevel { Low, Medium, High }
    internal sealed class DrawingTool
    {
        internal readonly string Name, Hint; internal readonly int Index; internal readonly char Key;
        private DrawingTool(string name, int index, string hint, char key) { Name=name; Index=index; Hint=hint; Key=key; }
        internal static readonly DrawingTool[] All = {
            new DrawingTool("Line",0,"Click or drag · Shift snaps · Double-click exits",'L'),
            new DrawingTool("Polyline",1,"Click to add · Double-click finishes and exits · Enter finishes",'P'),
            new DrawingTool("Frame",2,"Click or drag · Shift squares · Double-click exits",'R'),
            new DrawingTool("Curve",3,"Click to add · Double-click finishes and exits · Enter finishes",'C'),
            new DrawingTool("Freehand",4,"Hold and draw · release finishes · Double-click exits",'F'),
            new DrawingTool("Text",5,"Click for point text · drag for box text · Double-click exits",'T') };
        internal static int Maximum => All.Length-1;
        internal static DrawingTool Get(int index) => All[System.Math.Max(0,System.Math.Min(Maximum,index))];
        internal static string Description => "Drawing mode: " + string.Join(", ",All.Select(t=>t.Index+"="+t.Name));
        internal static bool Valid(int mode) => mode >= 0 && mode <= Maximum;
    }
}
