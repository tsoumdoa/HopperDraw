using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Undo;

namespace hopperdraw
{
    internal static class AnnotationCommands
    {
        internal static bool Apply(IEnumerable<HopperDraw> owners, string name, Func<DrawShape,bool> differs, Action<DrawShape> edit, Func<HopperDraw[],string,bool> record = null)
        {
            var changed=owners.Where(o=>o.CanEditShapes && o.SelectionLeaves.Any(differs)).ToArray();
            if (changed.Length==0) return false;
            if (!(record ?? Record)(changed,name)) return false;
            foreach(var owner in changed) { foreach(var leaf in owner.SelectionLeaves.Where(differs)) edit(leaf); owner.Changed(); }
            return true;
        }
        internal static bool Record(IEnumerable<HopperDraw> owners,string name)
        {
            var array=owners.ToArray(); var doc=array.FirstOrDefault()?.OnPingDocument();
            var record=new GH_UndoRecord("HopperDraw: "+name);
            foreach(var owner in array) record.AddAction(new ShapeEditUndoAction(owner,Enumerable.Empty<IGH_DocumentObject>()));
            return doc==null || doc.UndoUtil.RecordEvent(record)!=Guid.Empty;
        }
        internal static void Delete(IEnumerable<HopperDraw> owners,GH_Document doc,bool mixed)
        {
            var array=owners.Where(o=>o.HasSelection).ToArray();
            var natives=mixed && doc!=null ? doc.Objects.Where(o=>o.Attributes.Selected).ToArray() : new IGH_DocumentObject[0];
            var surviving=array.Where(o=>!natives.Contains(o)).ToArray();
            if (array.Length==0 && natives.Length==0) return;
            var record=doc?.UndoUtil.CreateRemoveObjectEvent("HopperDraw: Delete selection",natives) ?? new GH_UndoRecord("HopperDraw: Delete selection");
            foreach(var owner in surviving) record.AddAction(new ShapeEditUndoAction(owner,Enumerable.Empty<IGH_DocumentObject>()));
            if(doc!=null && doc.UndoUtil.RecordEvent(record)==Guid.Empty) return;
            foreach(var owner in surviving)
            {
                foreach(int index in owner.SelectedShapeIndices.Distinct().Where(i=>i>=0 && i<owner.Shapes.Count).OrderByDescending(i=>i)) owner.Shapes.RemoveAt(index);
                owner.ClearSelection(); owner.Changed();
            }
            if(natives.Length>0) { doc.RemoveObjects(natives,false); doc.NewSolution(false); }
        }
        internal static void Arrange(HopperDraw[] owners,bool front)
        {
            var changed=owners.Where(o=>o.HasSelection).Where(o=>
            {
                var selected=new HashSet<DrawShape>(o.Selection);
                var ordered=front ? o.Shapes.Where(s=>!selected.Contains(s)).Concat(o.Shapes.Where(selected.Contains)) : o.Shapes.Where(selected.Contains).Concat(o.Shapes.Where(s=>!selected.Contains(s)));
                return !o.Shapes.SequenceEqual(ordered);
            }).ToArray();
            if(changed.Length==0 || !Record(changed,front?"Bring to front":"Send to back")) return;
            foreach(var o in changed)
            {
                var selected=new HashSet<DrawShape>(o.Selection);
                o.Shapes=(front ? o.Shapes.Where(s=>!selected.Contains(s)).Concat(o.Shapes.Where(selected.Contains)) : o.Shapes.Where(selected.Contains).Concat(o.Shapes.Where(s=>!selected.Contains(s)))).ToList();
                o.ClearSelection(); for(int i=0;i<o.Shapes.Count;i++) if(selected.Contains(o.Shapes[i])) o.SelectedShapeIndices.Add(i);
                o.Changed();
            }
        }
        internal static void Duplicate(HopperDraw[] owners)
        {
            var selected=owners.Where(o=>o.HasSelection).ToArray();
            if(selected.Length==0 || selected.Any(o=>o.Selection.Any(s=>!s.CanTransformGeometry))) return;
            var proposals = selected.ToDictionary(o => o, o => o.Selection.Select(s =>
            {
                var copy = s.Clone();
                copy.Move(30, 30);
                return copy;
            }).ToArray());
            if (proposals.Values.SelectMany(copies => copies).Any(copy => !AnnotationMove.ValidGeometry(copy))) return;
            if(!Record(selected,"Duplicate annotations")) return;
            foreach(var o in selected)
            {
                var copies=proposals[o]; o.ClearSelection();
                foreach(var copy in copies) { o.SelectedShapeIndices.Add(o.Shapes.Count); o.Shapes.Add(copy); }
                o.Changed();
            }
        }
    }
}
