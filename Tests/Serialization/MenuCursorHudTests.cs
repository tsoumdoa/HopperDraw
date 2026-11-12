using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Grasshopper.GUI.Canvas;
using hopperdraw;
using Rhino.Geometry;

internal static class MenuCursorHudTests
{
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon,out DrawingCursorCache.IconInfo info);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap { internal int Type,Width,Height,Stride; internal ushort Planes,Bits; internal IntPtr Pixels; }
    [DllImport("gdi32.dll",EntryPoint="GetObjectW")] private static extern int GetObject(IntPtr value,int size,out NativeBitmap bitmap);
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process,int flag);
    private static void Walk(ToolStripItemCollection items)
    {
        var used=new HashSet<char>();
        foreach(var item in items.OfType<ToolStripMenuItem>())
        {
            char c=AnnotationMenuKeys.Mnemonic(item.Text);
            Program.Check(c!='\0' && used.Add(c),"Missing/duplicate mnemonic at "+item.Name+": "+item.Text);
            Program.Check(item.ShortcutKeys==Keys.None,"Global shortcut installed: "+item.Name);
            Program.Check(!string.IsNullOrEmpty(item.Name),"Command has no stable name");
            if(item.HasDropDownItems) Walk(item.DropDownItems);
        }
    }
    private static HopperDraw Owner(params DrawShape[] shapes)
    {
        var owner=new HopperDraw(); owner.Shapes.AddRange(shapes);
        for(int i=0;i<shapes.Length;i++) owner.SelectedShapeIndices.Add(i);
        return owner;
    }
    internal static void Run()
    {
        Program.Test("every menu level has unique mnemonics including disabled mixed/text/closed/wired commands",()=>
        {
            var text=new TextShape();
            var frame=new FrameShape(new Point3d(0,0,0),new Point3d(50,0,0),new Point3d(50,40,0),new Point3d(0,40,0));
            foreach(var owner in new[]{Owner(),Owner(new LineShape()),Owner(text),Owner(frame),Owner(text,frame,new LineShape())})
            {
                using(var menu=new ContextMenuStrip())
                {
                    AnnotationMenuBuilder.Selection(menu.Items,new[]{owner}); Walk(menu.Items);
                    // Populate the recent palette, then walk again with its numbered children.
                    var stroke=menu.Items.OfType<ToolStripMenuItem>().Single(i=>i.Name=="Stroke");
                    var colors=stroke.DropDownItems.OfType<ToolStripMenuItem>().Single(i=>i.Name=="Color");
                    if(owner.HasSelection) foreach(var color in colors.DropDownItems.OfType<ToolStripMenuItem>().Take(5)) color.PerformClick();
                }
                foreach(bool wired in new[]{false,true})
                {
                    if(wired) owner.Params.Input[2].AddSource(new Grasshopper.Kernel.Parameters.Param_Integer());
                    using(var menu=new ContextMenuStrip())
                    {
                        AnnotationMenuBuilder.Drawing(menu.Items,owner); Walk(menu.Items);
                        var tools=menu.Items.OfType<ToolStripMenuItem>().Single(i=>i.Name=="Tool");
                        foreach(var tool in tools.DropDownItems.OfType<ToolStripMenuItem>())
                            Program.Check(AnnotationMenuKeys.Mnemonic(tool.Text)==DrawingTool.All.Single(t=>t.Name==tool.Name).Key,"Tool/menu letters differ");
                    }
                }
                using(var menu=new ContextMenuStrip())
                {
                    // The wrapper must avoid every native sibling, without modifying their text or renderer.
                    foreach(string label in new[]{"&Host","H&ost","Ho&st","Hos&t","Ho&p","Hopp&er"}) menu.Items.Add(new ToolStripMenuItem(label));
                    var renderer=menu.Renderer; var host=menu.Items.Cast<ToolStripItem>().Select(i=>i.Text).ToArray();
                    AnnotationMenuBuilder.Component(menu,owner);
                    var root=menu.Items.OfType<ToolStripMenuItem>().Single(i=>i.Name=="HopperDraw");
                    Program.Check(!host.Select(AnnotationMenuKeys.Mnemonic).Contains(AnnotationMenuKeys.Mnemonic(root.Text)),"Wrapper collides with host");
                    Program.Check(ReferenceEquals(renderer,menu.Renderer) && host.SequenceEqual(menu.Items.Cast<ToolStripItem>().Take(host.Length).Select(i=>i.Text)),"Host menu modified");
                    Walk(root.DropDownItems);
                }
            }
        });
        Program.Test("shared tool menu glyphs survive Release and glyph paths render at three DPIs",()=>
        {
            var owner=Owner(); Image cached;
            using(var menu=new ContextMenuStrip())
            {
                AnnotationMenuBuilder.Drawing(menu.Items,owner); cached=((ToolStripMenuItem)menu.Items[0]).DropDownItems[0].Image;
                AnnotationMenuBuilder.Release(menu);
            }
            Program.Check(cached.Width>0,"Shared menu glyph disposed");
            var fingerprints=new HashSet<string>();
            foreach(int tool in Enumerable.Range(0,6)) foreach(int size in new[]{16,24,32})
            using(var bitmap=ToolGlyph.Bitmap(tool,size))
            {
                int pixels=0; var data=new List<int>();
                for(int y=0;y<size;y++) for(int x=0;x<size;x++) { int argb=bitmap.GetPixel(x,y).ToArgb(); if(bitmap.GetPixel(x,y).A>0) pixels++; if(size==16) data.Add(argb); }
                Program.Check(pixels>size && pixels<size*size/2,"Blank or flooded glyph "+tool+" @ "+size);
                if(size==16) Program.Check(fingerprints.Add(string.Join(",",data)),"Tools share a glyph");
            }
        });
        Program.Test("native cursors have explicit centre hotspots sizes and destroyed handles without resource growth",()=>
        {
            using(var warm=DrawingCursorCache.Create(0,96)) {}
            var process=Process.GetCurrentProcess().Handle;
            int user=GetGuiResources(process,1),gdi=GetGuiResources(process,0),live=DrawingCursorCache.LiveHandles;
            for(int cycle=0;cycle<12;cycle++)
            using(var cache=new DrawingCursorCache())
            {
                foreach(int dpi in new[]{96,144,192}) foreach(int tool in Enumerable.Range(0,6))
                {
                    var cursor=cache.Get(tool,dpi); int size=DrawingCursorCache.SizeForDpi(dpi);
                    Program.Check(ReferenceEquals(cursor,cache.Get(tool,dpi)),"Cursor not cached");
                    Program.Check(cursor.HotSpot==new System.Drawing.Point(size/2,size/2),"Wrong hotspot: dpi="+dpi+" HotSpot="+cursor.HotSpot);
                    Program.Check(GetIconInfo(cursor.Handle,out var info),"Invalid native cursor");
                    try
                    {
                        // Cursor.Size reports the system metric (32 even for a 48px native bitmap).
                        Program.Check(GetObject(info.Color,Marshal.SizeOf(typeof(NativeBitmap)),out var color)>0 && color.Width==size && color.Height==size && color.Bits==32,"Wrong colour bitmap dimensions");
                        Program.Check(GetObject(info.Mask,Marshal.SizeOf(typeof(NativeBitmap)),out var mask)>0 && mask.Width==size && mask.Height==size && mask.Bits==1,"Wrong monochrome mask");
                        using(var maskImage=Image.FromHbitmap(info.Mask))
                        {
                            Program.Check(maskImage.GetPixel(size/2,size/2).R==255,"Cursor centre gap missing");
                            Program.Check(maskImage.GetPixel(size/2-size/8,size/2).R==0,"Cursor targeting arm missing");
                            int lower=0,upper=0;
                            for(int y=0;y<size/2;y++) for(int x=size/2;x<size;x++)
                            { if(maskImage.GetPixel(x,y).R==0) upper++; if(maskImage.GetPixel(x,y+size/2).R==0) lower++; }
                            Program.Check(lower>upper+5,"Tool glyph mask is not bottom-right");
                        }
                        Program.Check(!info.Icon && info.XHotspot==size/2 && info.YHotspot==size/2 && info.Mask!=IntPtr.Zero && info.Color!=IntPtr.Zero,"Invalid ICONINFO"); }
                    finally { DeleteObject(info.Mask); DeleteObject(info.Color); }
                }
                var handle=cache.Get(0,96).Handle; cache.Dispose();
                bool valid=GetIconInfo(handle,out var dead);
                if(valid) { DeleteObject(dead.Mask); DeleteObject(dead.Color); }
                Program.Check(!valid,"Disposed cursor handle is still valid");
            }
            Program.Check(DrawingCursorCache.LiveHandles==live && GetGuiResources(process,1)<=user+2 && GetGuiResources(process,0)<=gdi+2,"Native cursor/mask/color leak");
        });
        Program.Test("HUD layout scales widths clamps narrow canvases and retains explicit swatch states",()=>
        {
            foreach(int dpi in new[]{96,144,192})
            {
                float s=dpi/96f;
                var normal=HudLayout.Measure(new Size(1200,600),dpi,50*s,10*s,true,false,true);
                Program.Check(Math.Abs(normal.Chip.Width-160*s)<.01 && normal.Fill && normal.Hatched && !normal.HiddenStroke,"DPI layout/swatch");
                foreach(bool fill in new[]{false,true}) foreach(bool hidden in new[]{false,true}) foreach(bool hatch in new[]{false,true})
                {
                    var state=HudLayout.Measure(new Size(800,600),dpi,50*s,10*s,fill,hidden,hatch);
                    Program.Check(state.Fill==fill && state.HiddenStroke==hidden && state.Hatched==(fill && hatch),"Dormant swatch state displayed");
                }
                foreach(int width in new[]{0,1,16,48,100,200})
                {
                    var narrow=HudLayout.Measure(new Size(width,200),dpi,50*s,10*s,false,true,false);
                    foreach(var box in new[]{narrow.Chip,narrow.Glyph,narrow.Swatch,narrow.Name,narrow.Key,narrow.Hint})
                        Program.Check(box.Width>=0 && box.Left>=0 && box.Right<=width+.01,"Layout exceeds narrow canvas: "+width+" "+box);
                    Program.Check(!narrow.Fill && narrow.HiddenStroke && !narrow.Hatched,"None swatch state lost");
                }
            }
        });
        Program.Test("GH's own component menu is tracked as open without changing its renderer; root shows its letter",()=>
        {
            var owner=new HopperDraw();
            using(var host=new ContextMenuStrip())
            {
                var renderer=host.Renderer;
                AnnotationMenuBuilder.Component(host,owner);
                var root=host.Items.OfType<ToolStripMenuItem>().Single(i=>i.Name=="HopperDraw");
                Program.Check(ReferenceEquals(host.Renderer,renderer),"host renderer replaced");
                Program.Check(root.ShortcutKeyDisplayString==AnnotationMenuKeys.Mnemonic(root.Text).ToString() && root.ShortcutKeys==Keys.None,"root letter not displayed or bound as a shortcut");
                host.Show(new System.Drawing.Point(0,0)); Application.DoEvents();
                Program.Check(AnnotationMenuKeys.Active && CanvasDrawingShortcut.MenuActive,"open host menu not detected");
                host.Close(); Application.DoEvents();
                Program.Check(!AnnotationMenuKeys.Active,"closed host menu still active");
            }
        });
        Program.Test("hint clock starts on notice holds draft fades stops and disposes with last controller",()=>
        {
            var now=DateTime.UtcNow; int invalidated=0; var token=new object();
            using(var clock=new AnnotationHintClock(()=>invalidated++,()=>now))
            {
                clock.Update(token,0,false); Program.Check(clock.Running && clock.Alpha==1 && clock.Interval>=2900,"Entry hint not armed once for the fade start: "+clock.Interval);
                int armed=clock.Interval; now=now.AddSeconds(1); clock.Update(token,0,false); clock.Update(token,0,false);
                Program.Check(clock.Running && clock.Interval==armed,"hover updates re-armed (postponed) the steady timer: "+clock.Interval);
                now=now.AddSeconds(2.25); clock.Tick(null,EventArgs.Empty); Program.Check(Math.Abs(clock.Alpha-.5)<.01 && clock.Running && clock.Interval==40,"No 40 ms fade ticks");
                now=now.AddSeconds(1); clock.Tick(null,EventArgs.Empty); Program.Check(!clock.Running && clock.Alpha==0,"Timer kept running");
                clock.Notice("notice"); Program.Check(clock.Running && clock.Text("hint")=="notice","Notice did not restart");
                now=now.AddSeconds(4); clock.Update(token,0,true); Program.Check(!clock.Running && clock.Alpha==1 && clock.Text("hint")=="hint","Held hint must stay visible with the timer stopped");
                clock.Notice("held notice"); Program.Check(clock.Running && clock.Interval>=2900,"Notice during hold not scheduled");
                now=now.AddSeconds(4); clock.Tick(null,EventArgs.Empty); Program.Check(!clock.Running && clock.Alpha==1,"Faded notice left the held timer running");
                clock.Update(token,1,false); Program.Check(clock.Running && clock.Alpha==1 && clock.Interval>=2900,"Tool hint did not restart");
                clock.Update(null,-1,false); Program.Check(!clock.Running,"Timer runs outside drawing mode");
                clock.Dispose(); clock.Notice("after dispose"); Program.Check(clock.IsDisposed && !clock.Running && invalidated>0,"Clock disposal");
            }
            using(var canvas=new GH_Canvas())
            {
                var owner=Owner(); var controller=CanvasAnnotationController.Register(canvas,owner);
                var clock=(AnnotationHintClock)typeof(CanvasAnnotationController).GetField("_hints",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(controller);
                var cache=(DrawingCursorCache)typeof(CanvasAnnotationController).GetField("_cursors",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(controller);
                controller.AssignCursor(cache.Get(0,96)); clock.Update(token,0,false); controller.Remove(owner);
                Program.Check(clock.IsDisposed && !clock.Running && cache.IsDisposed && canvas.Cursor==Cursors.Default,"Controller did not reset/dispose owned resources");
            }
        });
    }
}
