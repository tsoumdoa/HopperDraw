using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using hopperdraw;
using Rhino.Geometry;

internal static class TextTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr w,IntPtr l);
    private static TextShape Text(string value = "Hello world") => new TextShape { Anchor = new Point3d(20,20,0), Text = value };
    private static GH_LooseChunk Save(TextShape text)
    { var chunk = new GH_LooseChunk("Root"); ShapeSerializer.WriteShapeList(chunk,"Shapes",new DrawShape[]{text}); return chunk; }
    private static DrawShape Read(GH_LooseChunk chunk) => ShapeSerializer.ReadShapeList(chunk,"Shapes",out _)[0];
    private static GH_Chunk Record(GH_LooseChunk chunk) => (GH_Chunk)chunk.FindChunk("Shapes").FindChunk("Shape",0);
    private static ToolStripMenuItem Find(ToolStripItemCollection items,string name) => items.OfType<ToolStripMenuItem>().Single(i=>i.Text==name);
    internal static void Run()
    {
        Program.Test("review fixes: editor CRLF, narrow boxes, box width bounds and shared layout",()=>
        {
            // Existing multi-line text must open as separate editor lines (Win32 EDIT breaks only on CRLF).
            Program.Check(CanvasAnnotationController.EditorText("a\nb\r\nc")=="a\r\nb\r\nc","editor text not CRLF");
            using(var editor=new TextEditorControl{Text=CanvasAnnotationController.EditorText("alpha\nbeta\ngamma")})
            {
                var handle=editor.Handle; int lines=(int)SendMessage(handle,0xBA,IntPtr.Zero,IntPtr.Zero);
                Program.Check(lines==3,"editor shows "+lines+" lines for three-line text");
            }
            // A jittered click (drag of a few units) becomes point text at the press, never a 1-unit sliver.
            var draft=new AnnotationDraft(); var press=new Point3d(100,100,0);
            draft.Down(5,press,new System.Drawing.Point(100,100),1,false,1); draft.Up(5,new Point3d(101,103,0),new System.Drawing.Point(101,103),false,new Size(4,4));
            var narrow=new TextShape(); draft.TakePlacement().Fit(narrow);
            Program.Check(narrow.BoxWidth==null && narrow.Anchor==press,"jittered click created a box: "+narrow.BoxWidth);
            draft.Down(5,press,new System.Drawing.Point(100,100),1,false,1); draft.Up(5,new Point3d(300,140,0),new System.Drawing.Point(300,140),false,new Size(4,4));
            var wide=new TextShape(); draft.TakePlacement().Fit(wide);
            Program.Check(wide.BoxWidth==200 && wide.Anchor==press,"real drag lost its box");
            // The width handle clamps to the minimum box and the coordinate budget.
            var text=Text(); text.BoxWidth=200; text.MovePoint(0,new Point3d(text.Anchor.X-50,0,0));
            Program.Check(text.BoxWidth==text.MinBoxWidth && text.MinBoxWidth>2*text.Layout.Padding,"width handle made a zero-content box");
            text.MovePoint(0,new Point3d(9999999,0,0));
            Program.Check(text.BoxWidth<=TextShape.MaxBoxWidth && ShapeSerializer.ValidCoordinate(text.Anchor.X+text.BoxWidth.Value),"width handle exceeded bounds");
            // Oversized persisted widths stay opaque instead of loading an unpaintable live record.
            foreach(double bad in new[]{1e30,TextShape.MaxBoxWidth*2})
            {
                var boxed=Text(); boxed.BoxWidth=100; var chunk=Save(boxed); var r=Record(chunk); r.RemoveItem("BoxWidth"); r.SetDouble("BoxWidth",bad);
                Program.Check(Read(chunk) is OpaqueShape,"oversized BoxWidth "+bad+" loaded live");
            }
            var edge=Text(); edge.Anchor=new Point3d(9999990,0,0); edge.BoxWidth=50; var edgeChunk=Save(edge);
            Program.Check(Read(edgeChunk) is OpaqueShape,"box right edge past the coordinate budget loaded live");
            // Line layout is shared by content: a moved clone reuses it and is only re-positioned.
            var source=Text("Shared layout\nacross clones"); source.BoxWidth=180; var first=source.Layout;
            var clone=(TextShape)source.Clone(); clone.Move(37,11);
            Program.Check(ReferenceEquals(clone.Layout.Lines,first.Lines) && Math.Abs(clone.Layout.Bounds.X-first.Bounds.X-37)<.001 && Math.Abs(clone.Layout.Bounds.Y-first.Bounds.Y-11)<.001,"clone re-measured or mispositioned");
            Program.Check(ReferenceEquals(TextLayoutService.PaintFont(source),TextLayoutService.PaintFont(clone)),"paint font not cached");
            // Deleting the last text of a group removes the empty group in the same transaction.
            var owner=new HopperDraw(); var only=Text(); var g=new DrawShapeGroup(); g.Children.Add(only); var keep=new DrawShapeGroup(); keep.Children.Add(Text("keep")); keep.Children.Add(only.Clone());
            owner.Shapes.Add(g); owner.Shapes.Add(keep); var doc=new object(); int records=0;
            var s=new TextEditSession(owner,doc,only,false); s.Buffer=""; Program.Check(s.Commit(doc,true,n=>{records++;return true;}) && records==1 && owner.Shapes.Count==1 && owner.Shapes[0]==keep,"empty group survived");
        });        Program.Test("text v1 roundtrip includes independent glyph and box styles",()=>
        {
            var owner = new HopperDraw(); var text=Text("αβ\nTitle   "); text.SetPreset(TextStylePreset.Title);
            text.SizeMultiplier=1.5; text.BoxWidth=230; text.Italic=true; text.TextColorOverride=Color.Blue;
            text.OverrideColor=Color.Red; text.StrokeVisible=true; text.FillColor=Color.Yellow; text.Hatch=HatchPattern.Grid;
            owner.Shapes.Add(text);
            foreach(bool binary in new[]{false,true})
                Program.Check(Program.Fingerprint(Program.RoundTrip(owner,binary).Shapes[0])==Program.Fingerprint(text),"text fields changed");
            Program.Check(Record(Save(text)).GetInt32("Kind")==5,"text is not Kind 5");
        });
        Program.Test("missing additive text keys default silently",()=>
        {
            var chunk=Save(Text()); var record=Record(chunk);
            foreach(var name in new[]{"HasBoxWidth","BoxWidth","StylePreset","SizeMultiplier","Bold","Italic","HasTextColor","TextColor","StrokeVisible",
                "ThickMult","LineType","HasColor","Color","HasFillColor","FillColor","FillOpacity","Hatch","HatchScale","HasHatchBackground","HatchBackground"}) record.RemoveItem(name);
            var loaded=ShapeSerializer.ReadShapeList(chunk,"Shapes",out var diagnostics)[0] as TextShape;
            Program.Check(loaded!=null && loaded.StylePreset==TextStylePreset.Body && loaded.SizeMultiplier==1 && !loaded.Bold && !loaded.Italic &&
                loaded.BoxWidth==null && loaded.TextColorOverride==null && !loaded.StrokeVisible && diagnostics.Count==0,"text defaults/diagnostics wrong");
        });
        Program.Test("invalid text fields remain opaque without losing raw data",()=>
        {
            Action<GH_Chunk>[] damage = {
                r=>r.RemoveItem("Text"), r=>{r.RemoveItem("Text");r.SetInt32("Text",42);}, r=>{r.RemoveItem("Text");r.SetString("Text",new string('x',100001));},
                r=>{r.RemoveItem("X");r.SetDoubleArray("X",new[]{1d,2d});}, r=>{r.RemoveItem("Y");r.SetDoubleArray("Y",new[]{double.NaN});},
                r=>{r.RemoveItem("SizeMultiplier");r.SetDouble("SizeMultiplier",.1);},r=>{r.RemoveItem("SizeMultiplier");r.SetDouble("SizeMultiplier",double.PositiveInfinity);},r=>{r.RemoveItem("SizeMultiplier");r.SetDouble("SizeMultiplier",20.01);},
                r=>{r.RemoveItem("StylePreset");r.SetInt32("StylePreset",99);},r=>{r.RemoveItem("Bold");r.SetString("Bold","yes");},r=>{r.RemoveItem("Italic");r.SetString("Italic","yes");},
                r=>{r.RemoveItem("HasBoxWidth");r.SetBoolean("HasBoxWidth",true);r.SetDouble("BoxWidth",0);},
                r=>{r.RemoveItem("HasBoxWidth");r.SetBoolean("HasBoxWidth",true);r.RemoveItem("BoxWidth");},
                r=>r.SetDouble("BoxWidth",double.NaN),r=>{r.RemoveItem("HasTextColor");r.SetString("HasTextColor","yes");},
                r=>{r.RemoveItem("HasTextColor");r.SetBoolean("HasTextColor",true);r.RemoveItem("TextColor");},r=>r.SetString("TextColor","red")
            };
            foreach(var change in damage)
            {
                var chunk=Save(Text()); change(Record(chunk)); var before=ShapeSerializer.Capture(Record(chunk)).Serialize_Xml();
                var shape=Read(chunk); Program.Check(shape is OpaqueShape,"invalid record interpreted");
                var saved=new GH_LooseChunk("Root");ShapeSerializer.WriteShapeList(saved,"Shapes",new[]{shape});
                Program.Check(ShapeSerializer.Capture(Record(saved)).Serialize_Xml()==before,"opaque record changed");
            }
            foreach(double value in new[]{.10001,20}) { var t=Text();t.SizeMultiplier=value;Program.Check(Read(Save(t)) is TextShape,"valid finite size rejected"); }
        });
        Program.Test("text unknown indexed keys and chunks survive edits and clones",()=>
        {
            var chunk=Save(Text()); var record=Record(chunk);record.SetString("Future",2,"extension");record.SetString("Text",3,"indexed text");record.CreateChunk("FutureChild").SetInt32("Data",7);
            var text=(TextShape)Read(chunk); text.Text="Edited"; var copy=(TextShape)text.Clone();var saved=Save(copy);var result=Record(saved);
            Program.Check(result.GetString("Future",2)=="extension" && result.GetString("Text",3)=="indexed text" && result.FindChunk("FutureChild").GetInt32("Data")==7 && result.GetString("Text")=="Edited","residual lost");
        });
        Program.Test("text layout wraps words and long words, preserves explicit newlines and spaces",()=>
        {
            var text=Text("one two three\n\nlongwordlongwordlongword\r\nend   ");text.BoxWidth=85;
            var layout=text.Layout;
            Program.Check(layout.Lines.Length>4 && layout.Lines.Contains("") && string.Join("",layout.Lines)=="one two threelongwordlongwordlongwordend   ","wrap lost text/newlines/spaces");
            Program.Check(layout.Bounds.Width==85 && layout.LineHeight==layout.Em*1.3f,"canvas metrics incorrect");
            var plain=Text("word");var spaces=Text("word      ");Program.Check(plain.Layout.Bounds==spaces.Layout.Bounds,"trailing spaces widened point text");
            plain.BoxWidth=plain.Layout.Bounds.Width+1; plain.Text="word      "; Program.Check(plain.Layout.Lines.Length==1,"trailing spaces forced a wrap");
            using(var font=TextLayoutService.Font(text)) Program.Check(font.Unit==GraphicsUnit.Pixel && font.Size==16,"font uses points");
        });
        Program.Test("text layout cache invalidates for every geometry and typography field",()=>
        {
            var text=Text();var layout=text.Layout;Program.Check(ReferenceEquals(layout,text.Layout),"layout not cached");
            Action[] changes={ ()=>text.Text+="!",()=>text.Anchor=new Point3d(50,60,0),()=>text.BoxWidth=50,()=>text.StylePreset=TextStylePreset.Subtitle,
                ()=>text.SizeMultiplier=2,()=>text.Bold=true,()=>text.Italic=true };
            foreach(var change in changes) {change();var next=text.Layout;Program.Check(!ReferenceEquals(layout,next),"layout key missing field");layout=next;}
            text.OverrideColor=Color.Red;text.FillColor=Color.Blue;Program.Check(ReferenceEquals(layout,text.Layout),"box paint invalidated text layout");
            Program.Check(TextLayoutService.Greek(16,.18f) && !TextLayoutService.Greek(16,.2f),"greek threshold wrong");
        });
        Program.Test("text layout work is bounded and greeking keeps measured bounds",()=>
        {
            var text=Text(new string('x',TextLayoutService.MaxTextLength)); text.BoxWidth=15;
            Program.Check(text.Layout.Lines.Length==TextLayoutService.MaxLines,"layout line budget not enforced");
            text=Text("Wide text");var bounds=text.LayoutBounds;
            using(var bitmap=new Bitmap(100,100))using(var g=Graphics.FromImage(bitmap))
            {
                g.Clear(Color.White);g.ScaleTransform(.15f,.15f);ShapeRenderer.Render(g,text,Color.Blue,1);
                int grey=0,blue=0;
                for(int y=0;y<100;y++)for(int x=0;x<100;x++){var c=bitmap.GetPixel(x,y);if(c.R<200 && c.R==c.G && c.G==c.B)grey++;if(c.B>150 && c.R<80)blue++;}
                Program.Check(grey>0 && blue==0 && bounds==text.LayoutBounds,"greeking changed colour or bounds");
            }
        });
        Program.Test("text pick, window and group bounds use measured layout with backdrop edge rule",()=>
        {
            var text=Text("Wide title\nTwo lines");var b=text.LayoutBounds;var centre=new PointF(b.Left+b.Width/2,b.Top+b.Height/2);
            Program.Check(AnnotationGeometry.Hit(text,centre,0,1) && !AnnotationGeometry.Hit(text,centre,0,1,false),"interior/backdrop rule wrong");
            Program.Check(AnnotationGeometry.Hit(text,new PointF(b.Left,b.Top+5),1,1,false),"backdrop edge not pickable");
            Program.Check(AnnotationGeometry.InRectangle(text,b,false) && !AnnotationGeometry.InRectangle(text,new RectangleF(b.Left,b.Top,b.Width-1,b.Height),false),"window ignores layout bounds");
            Program.Check(AnnotationGeometry.InRectangle(text,new RectangleF(centre,new SizeF(1,1)),true,0,true),"crossing inside overlay text missed");
            Program.Check(!AnnotationGeometry.InRectangle(text,new RectangleF(centre,new SizeF(1,1)),true,0,false),"crossing wholly inside backdrop text selected it");
            Program.Check(AnnotationGeometry.InRectangle(text,new RectangleF(b.Left-5,centre.Y,10,2),true,0,false),"crossing over backdrop text edge missed");
            var inner=new DrawShapeGroup();inner.Children.Add(text);var outer=new DrawShapeGroup();outer.Children.Add(inner);
            Program.Check(Math.Abs(outer.GetBoundingBox().Width-b.Width)<.001 && Math.Abs(outer.GetBoundingBox().Height-b.Height)<.001 && text.EditHandles.Length==1,"group bounds or width handle wrong: " + outer.GetBoundingBox() + " => " + b);
            text.Text+=" long extension";Program.Check(outer.GetBoundingBox().Width>b.Width,"text change did not invalidate bounds");
        });
        Program.Test("text width edit retains anchor, bypasses snap and detects point-to-box change",()=>
        {
            var owner=new HopperDraw();var text=Text();owner.Shapes.Add(text);owner.SelectedShapeIndices.Add(0);
            var move=new AnnotationMove(new[]{owner},new IGH_DocumentObject[0],null);
            move.Edit(owner,0,0,new Point3d(120,800,0),true);var edited=(TextShape)owner.Shapes[0];
            Program.Check(edited.Anchor==text.Anchor && edited.BoxWidth==100 && move.Changed,"width edit changed anchor/snapped/lost no-op detection");
            move.Revert(false);Program.Check(((TextShape)owner.Shapes[0]).BoxWidth==null,"rollback lost point text");
            text=(TextShape)owner.Shapes[0];text.BoxWidth=100;move=new AnnotationMove(new[]{owner},new IGH_DocumentObject[0],null);
            move.Edit(owner,0,0,new Point3d(120,-999,0),true);Program.Check(!move.Changed,"same width creates undo");
        });
        Program.Test("nested text duplicate, move and undo snapshots retain all fields",()=>
        {
            var owner=new HopperDraw();var text=Text();text.BoxWidth=120;text.Bold=true;text.Italic=true;text.SizeMultiplier=3;text.TextColorOverride=Color.Blue;
            var inner=new DrawShapeGroup();inner.Children.Add(text);var outer=new DrawShapeGroup();outer.Children.Add(inner);owner.Shapes.Add(outer);owner.SelectedShapeIndices.Add(0);
            var before=Program.Fingerprint(outer);var undo=new ShapeEditUndoAction(owner,new IGH_DocumentObject[0]);
            AnnotationCommands.Duplicate(new[]{owner});var copy=(DrawShapeGroup)owner.Shapes[1];var leaf=(TextShape)((DrawShapeGroup)copy.Children[0]).Children[0];
            Program.Check(leaf.PersistentId!=text.PersistentId && leaf.Anchor==new Point3d(50,50,0) && leaf.SameTextStyle(text) && leaf.BoxWidth==120,"duplicate lost fields/IDs");
            undo.Swap(null,owner);Program.Check(owner.Shapes.Count==1 && Program.Fingerprint(owner.Shapes[0])==before,"undo lost text snapshot");
            undo.Swap(null,owner);Program.Check(owner.Shapes.Count==2,"redo lost nested text");
        });
        Program.Test("text editor commits only changes and resolves snapshot GUIDs inside groups",()=>
        {
            var owner=new HopperDraw();var text=Text();var group=new DrawShapeGroup();group.Children.Add(text);owner.Shapes.Add(group);var doc=new object();int records=0;
            Func<string,bool> record=n=>{records++;return true;};
            var session=new TextEditSession(owner,doc,text,false);Program.Check(!session.Commit(doc,true,record) && records==0,"unchanged edit recorded");
            session=new TextEditSession(owner,doc,text,false);session.Buffer="Hello world".Replace("\n","\r\n");
            Program.Check(!session.Commit(doc,true,record) && records==0,"equivalent editor newline recorded");
            session=new TextEditSession(owner,doc,text,false);session.Buffer="new text";
            owner.Shapes=ShapeEditUndoAction.Snapshot(owner.Shapes);owner.Shapes[0].Move(50,40);
            Program.Check(session.Commit(doc,true,record) && records==1 && !session.Commit(doc,true,record),"changed edit not exactly once");
            var resolved=TextEditSession.Find(owner.Shapes,text.PersistentId);Program.Check(resolved.Text=="new text" && text.Text=="Hello world" && resolved.Anchor==new Point3d(70,60,0),"stale object written or geometry overwritten");
            session=new TextEditSession(owner,doc,resolved,false);session.Buffer="  \r\n";Program.Check(session.Commit(doc,true,record) && records==2 && group.Children.Count==1 && owner.Shapes.Count==0,"clear did not delete exactly once or left an empty group");
        });
        Program.Test("blank new text and aborted undo/document/owner edits record nothing",()=>
        {
            var owner=new HopperDraw();var doc=new object();int records=0;Func<string,bool> record=n=>{records++;return true;};
            var session=new TextEditSession(owner,doc,Text(" \n"),true);Program.Check(!session.Commit(doc,true,record) && records==0 && owner.Shapes.Count==0,"blank creation recorded");
            session=new TextEditSession(owner,doc,Text(),true);session.Abort();Program.Check(!session.Commit(doc,true,record),"abort wrote text");
            session=new TextEditSession(owner,doc,Text(),true);Program.Check(!session.Commit(new object(),true,record),"document switch wrote text");
            session=new TextEditSession(owner,doc,Text(),true);Program.Check(!session.Commit(doc,false,record) && records==0,"ineligible owner wrote text");
            session=new TextEditSession(owner,doc,Text(),true);Program.Check(session.Commit(doc,true,record) && records==1 && owner.Shapes.Count==1,"new text not committed once");
            var target=(TextShape)owner.Shapes[0];session=new TextEditSession(owner,doc,target,false);session.Buffer="stale";
            owner.RestoreShapes(ShapeEditUndoAction.Snapshot(owner.Shapes));session.Abort();session.Commit(doc,true,record);
            Program.Check(((TextShape)owner.Shapes[0]).Text=="Hello world" && records==1,"undo restore overwritten");
        });
        Program.Test("text menus apply only text leaves and keep glyph and border colours independent",()=>
        {
            var owner=new HopperDraw();var text=Text();var line=new LineShape(new Point3d(0,0,0),new Point3d(10,10,0));
            owner.Shapes.Add(text);owner.Shapes.Add(line);owner.SelectedShapeIndices.AddRange(new[]{0,1});
            using(var menu=new ContextMenuStrip())
            {
                AnnotationMenuBuilder.Selection(menu.Items,new[]{owner});var textMenu=Find(menu.Items,"Text");
                Find(Find(textMenu.DropDownItems,"Style").DropDownItems,"Title").PerformClick();
                Find(Find(textMenu.DropDownItems,"Color").DropDownItems,"Blue").PerformClick();
                Program.Check(text.StylePreset==TextStylePreset.Title && text.Bold && text.TextColorOverride==Color.Blue && text.OverrideColor==null && line.OverrideColor==null,"text menu affected geometry/border");
                Find(Find(Find(menu.Items,"Stroke").DropDownItems,"Color").DropDownItems,"Red").PerformClick();
                Program.Check(text.TextColorOverride==Color.Blue && text.OverrideColor==Color.Red,"stroke menu changed glyph colour");
            }
            text.SetPreset(TextStylePreset.Body);text.SizeMultiplier=3;text.Italic=true;text.FillColor=Color.Yellow;text.SetPreset(TextStylePreset.Subtitle);
            Program.Check(!text.Bold && !text.Italic && text.SizeMultiplier==1 && text.FillColor==Color.Yellow && text.TextColorOverride==Color.Blue,"preset reset box/colour or kept typography");
            owner.TextPen.TextColorOverride=Color.Green;owner.PenFrom(text);Program.Check(owner.TextPen.TextColorOverride==Color.Blue && owner.PenStyle.StrokeVisible,"text pen contaminated geometry pen");
            using(var menu=new ContextMenuStrip())
            {
                AnnotationMenuBuilder.Drawing(menu.Items,owner);var tools=Find(menu.Items,"Tool");
                Program.Check(Find(tools.DropDownItems,"Text").ShortcutKeyDisplayString=="T" && Find(tools.DropDownItems,"Frame").ShortcutKeyDisplayString=="R" && Find(tools.DropDownItems,"Freehand").ShortcutKeys==Keys.None,"tool key displays are real shortcuts or incorrect");
            }
        });
        Program.Test("mixed text menu values are unchecked and text transaction spans owners once",()=>
        {
            var a=new HopperDraw();var b=new HopperDraw();var t=Text();var u=Text();u.Bold=true;u.TextColorOverride=Color.Blue;
            a.Shapes.Add(t);a.SelectedShapeIndices.Add(0);b.Shapes.Add(u);b.SelectedShapeIndices.Add(0);
            using(var menu=new ContextMenuStrip())
            {
                AnnotationMenuBuilder.Selection(menu.Items,new[]{a,b});var text=Find(menu.Items,"Text");
                Program.Check(!Find(text.DropDownItems,"Bold").Checked && !Find(text.DropDownItems,"Edit text…").Enabled && !Find(Find(text.DropDownItems,"Color").DropDownItems,"Use component color").Checked,"mixed text has checks/editor");
                Program.Check(!Find(menu.Items,"Use selection style for drawing").Enabled,"mixed text copied");
            }
            int records=0;AnnotationCommands.Apply(new[]{a,b},"Text bold",l=>l is TextShape v && !v.Bold,l=>((TextShape)l).Bold=true,(owners,name)=>{records++;return owners.Length==1;});
            Program.Check(records==1 && t.Bold && u.Bold,"text transaction/no-op wrong");
        });
        Program.Test("bitmap draws blue glyphs after red box border with stroke hidden support",()=>
        {
            var text=Text("MMMM");text.TextColorOverride=Color.Blue;text.OverrideColor=Color.Red;text.StrokeVisible=true;text.FillColor=Color.Yellow;
            using(var bitmap=new Bitmap(250,150)) using(var graphics=Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);ShapeRenderer.Render(graphics,text,Color.Black,2);
                int blue=0,red=0;
                for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++) {var c=bitmap.GetPixel(x,y);if(c.B>150 && c.R<80)blue++;if(c.R>150 && c.B<80 && c.G<80)red++;}
                Program.Check(blue>20 && red>20,"glyph/border colours not rendered independently");
                text.StrokeVisible=false;graphics.Clear(Color.White);ShapeRenderer.Render(graphics,text,Color.Black,2);blue=0;red=0;
                for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++) {var c=bitmap.GetPixel(x,y);if(c.B>150 && c.R<80)blue++;if(c.R>150 && c.B<80 && c.G<80)red++;}
                Program.Check(blue>20 && red==0,"hidden stroke suppressed glyphs");
            }
        });
    }
}
