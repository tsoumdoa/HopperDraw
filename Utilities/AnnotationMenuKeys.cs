using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace hopperdraw
{
    // All state is scoped to our menus; neither the system cue preference nor GH's renderer is changed.
    internal static class AnnotationMenuKeys
    {
        private sealed class CueRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextFormat &= ~TextFormatFlags.HidePrefix;
                base.OnRenderItemText(e);
            }
        }
        private static readonly ToolStripRenderer Renderer = new CueRenderer();
        private static readonly HashSet<ToolStrip> Open = new HashSet<ToolStrip>();
        internal static bool Active => Open.Count != 0;
        internal static char Mnemonic(string text)
        {
            for (int i=0;i<text.Length-1;i++) if (text[i]=='&')
            { if (text[i+1]=='&') { i++; continue; } return char.ToUpperInvariant(text[i+1]); }
            return '\0';
        }
        private static readonly Dictionary<string,string> Labels = new Dictionary<string,string>
        {
            {"Drawing","&Drawing"},{"Selection","&Selection"},{"Select all annotations","Select &all annotations"},
            {"Select unreadable annotation records","Select &unreadable annotation records"},{"Clear all annotations","&Clear all annotations"},
            {"Enter drawing mode","&Enter drawing mode"},{"Tool","&Tool"},{"Pen","&Pen"},
            {"Reset pen to component defaults","&Reset pen to component defaults"},{"Exit drawing mode","E&xit drawing mode"},
            {"Stroke","&Stroke"},{"Fill","&Fill"},{"Text","&Text"},{"Use selection style for drawing","&Use selection style for drawing"},
            {"Bring to front","&Bring to front"},{"Send to back","Send to bac&k"},{"Group","&Group"},{"Ungroup","U&ngroup"},
            {"Duplicate","&Duplicate"},{"Delete","D&elete"},{"Line","&Line"},{"Polyline","&Polyline"},{"Frame","F&rame"},
            {"Curve","&Curve"},{"Freehand","&Freehand"},{"Smoothing","S&moothing"},
            {"Color","&Color"},{"Thickness","&Thickness"},{"Line type","&Line type"},{"None","&None"},
            {"Opacity","Op&acity"},{"Hatch","&Hatch"},{"Hatch scale","Hatch &scale"},{"Hatch background","Hatch bac&kground"},
            {"Black","&Black"},{"Dark grey","&Dark grey"},{"White","&White"},{"Red","&Red"},{"Orange","&Orange"},
            {"Yellow","&Yellow"},{"Green","&Green"},{"Teal","&Teal"},{"Blue","B&lue"},{"Purple","&Purple"},{"Pink","P&ink"},
            {"Recent colors","R&ecent colors"},{"Custom…","&Custom…"},{"Use component color","&Use component color"},
            {"Solid","&Solid"},{"Dash","&Dash"},{"Dot","D&ot"},{"Dash-dot","Dash-dot (&A)"},{"Dash-dot-dot","Dash-dot-dot (&T)"},
            {"Diagonal","&Diagonal"},{"CrossHatch","&CrossHatch"},{"Concrete","C&oncrete"},{"Earth","&Earth"},
            {"Insulation","&Insulation"},{"Brick","&Brick"},{"Grid","&Grid"},{"Dots","Do&ts"},{"Horizontal","&Horizontal"},{"Vertical","&Vertical"},
            {"Low","&Low"},{"Medium","&Medium"},{"High","&High"},
            {"10%","&10%"},{"25%","&25%"},{"50%","&50%"},{"75%","&75%"},{"100%","1&00%"},
            {"0.5x","&0.5x"},{"1x","&1x"},{"2x","&2x"},{"3x","&3x"},{"4x","&4x"},{"5x","&5x"},
            {"Style","&Style"},{"Size","Si&ze"},{"Bold","&Bold"},{"Italic","&Italic"},{"Edit text…","&Edit text…"},
            {"Title","&Title"},{"Subtitle","&Subtitle"},{"Body","&Body"}
        };
        internal static string Label(string name, ISet<char> used)
        {
            string preferred=Labels.TryGetValue(name,out var label) ? label : "&"+name;
            if (used.Add(Mnemonic(preferred))) return preferred;
            for(int i=0;i<name.Length;i++) if(char.IsLetterOrDigit(name[i]) && used.Add(char.ToUpperInvariant(name[i])))
                return name.Insert(i,"&");
            foreach(char c in "1234567890ABCDEFGHIJKLMNOPQRSTUVWXYZ") if(used.Add(c)) return name+" (&"+c+")";
            throw new InvalidOperationException("No mnemonic available for "+name);
        }
        private static readonly HashSet<ToolStrip> Tracked = new HashSet<ToolStrip>();
        // Visibility tracking only: any menu that hosts HopperDraw items (including GH's own component menu,
        // whose renderer we never touch) counts as open, so menu keys win over canvas commands while it shows.
        internal static void Track(ToolStrip menu)
        {
            if(menu==null || !Tracked.Add(menu)) return;
            menu.VisibleChanged+=(s,e)=>{ if(menu.Visible) Open.Add(menu); else Open.Remove(menu); };
            menu.Disposed+=(s,e)=>{ Open.Remove(menu); Tracked.Remove(menu); };
            if(menu.Visible) Open.Add(menu);
        }
        internal static void Configure(ToolStrip menu)
        {
            Track(menu);
            if(!ReferenceEquals(menu.Renderer,Renderer)) menu.Renderer=Renderer;
        }
        internal static void Apply(ToolStripItemCollection items)
        {
            var used=new HashSet<char>();
            // Reserve structural commands first, then resolve the embedded palette around them.
            var entries=items.OfType<ToolStripMenuItem>().ToArray();
            foreach(var item in entries.OrderBy(i=>IsColor(i.Name) ? 1 : 0)) item.Text=Label(item.Name,used);
            foreach(var item in entries)
                if(item.HasDropDownItems) { Configure(item.DropDown); Apply(item.DropDownItems); }
            if(items.Count>0 && items[0].Owner is ContextMenuStrip context) Configure(context);
        }
        private static bool IsColor(string name) => new[]{"Black","Dark grey","White","Red","Orange","Yellow","Green","Teal","Blue","Purple","Pink","Recent colors","Custom…","Light grey"}.Contains(name);
    }
}
