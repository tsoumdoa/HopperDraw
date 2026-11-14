using System;
using System.Collections.Generic;
using System.Linq;

namespace hopperdraw
{
    // Buffer-only transaction: no live mutation before Commit. Resolve by persistent GUID on
    // every operation because move/undo replace the entire shape tree with snapshots.
    internal sealed class TextEditSession
    {
        internal readonly HopperDraw Owner;
        internal readonly object Document;
        internal readonly Guid TargetId;
        internal readonly TextShape Draft;
        internal string Buffer;
        // Set when the style bar or a format key restyled Draft; an existing text then takes Draft's style on commit.
        internal bool StyleEdited;
        internal bool Ended { get; private set; }
        internal bool IsNew { get; }
        internal TextEditSession(HopperDraw owner, object document, TextShape shape, bool isNew)
        {
            Owner = owner; Document = document; TargetId = shape.PersistentId; IsNew = isNew;
            Draft = (TextShape)ShapeEditUndoAction.CloneForSnapshot(shape); Buffer = shape.Text;
        }
        internal static TextShape Find(IEnumerable<DrawShape> shapes, Guid id) =>
            shapes.Select(s => s is DrawShapeGroup g ? Find(g.Children, id) : s.PersistentId == id ? s as TextShape : null).FirstOrDefault(s => s != null);
        private static bool Remove(IList<DrawShape> shapes, Guid id)
        {
            for (int i = 0; i < shapes.Count; i++)
            {
                if (shapes[i].PersistentId == id) { shapes.RemoveAt(i); return true; }
                if (shapes[i] is DrawShapeGroup g && Remove(g.Children, id))
                {
                    // A group emptied by deleting its last text would be invisible and unpickable but persist forever.
                    if (g.Children.Count == 0) shapes.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }
        internal void Abort() { Ended = true; }
        internal bool Commit(object currentDocument, bool eligible, Func<string, bool> record = null)
        {
            if (Ended) return false;
            Ended = true;
            if (!eligible || !ReferenceEquals(Document, currentDocument)) return false;
            var target = IsNew ? null : Find(Owner.Shapes, TargetId);
            if (!IsNew && target == null) return false;
            string text = TextShape.NormalizeNewlines(Buffer);
            if (text.Length > TextLayoutService.MaxTextLength) text = text.Substring(0, TextLayoutService.MaxTextLength);
            bool blank = string.IsNullOrWhiteSpace(text);
            bool restyled = !IsNew && StyleEdited && !(Draft.SameStyle(target) && Draft.SameTextStyle(target));
            if (IsNew && blank || !IsNew && text == target.Text && !restyled) return false;
            string name = IsNew ? "Create text" : blank ? "Delete text" : "Edit text";
            if (!(record ?? (n => AnnotationCommands.Record(new[] { Owner }, n)))(name)) return false;
            if (IsNew) { Draft.Text = text; Owner.Shapes.Add(Draft); }
            else if (blank) { Remove(Owner.Shapes, TargetId); Owner.ClearSelection(); }
            else
            {
                target.Text = text;
                if (restyled)
                {
                    // Style only: the anchor and width belong to the live shape, which may have moved since.
                    Draft.CopyStyleTo(target); Draft.CopyTextStyleTo(target);
                    if (target.BoxWidth.HasValue) target.BoxWidth = target.ClampBoxWidth(target.BoxWidth.Value);
                }
            }
            Owner.Changed(); return true;
        }
    }
}
