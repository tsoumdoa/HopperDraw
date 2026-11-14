using System;
using System.Linq;
using System.Windows.Forms;

namespace hopperdraw
{
    internal static class ToolShortcut
    {
        // Activation is entirely DD's job. Once drawing, a fresh key of the bottom row (DrawingTool.Key,
        // in the order of the tool buttons) is an ordinary controller command, without a suffix timer
        // or additional gesture state.
        internal static bool Route(Keys data, int current, bool wired, Action<int> change, Action notice)
        {
            if ((data & Keys.Modifiers) != Keys.None) return false;
            var tool = DrawingTool.All.FirstOrDefault(t => (Keys)t.Key == data);
            if (tool == null) return false;
            if (wired) notice();
            else if (current != tool.Index) change(tool.Index);
            return true;
        }
    }
}
