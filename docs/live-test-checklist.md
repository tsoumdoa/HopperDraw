# Reliability release: live Rhino 8 checks / PR notes

These steps require Rhino and are not recorded as executed by the headless verifier. Record Rhino version, runtime, Windows scaling, plugin SHA, and pass/fail for each section. Start with one installed HopperDraw build and a new unsaved Grasshopper definition.

## Save and reopen

1. Place HopperDraw and a slider connected to a panel. Draw a red line, blue dashed polyline, frame, open curve, and closed curve. Change leaf widths and group the first two shapes (group at index zero), then group that group with the frame.
2. Save as `reliability.gh`. Close the definition, reopen it, and compare geometry, order, group nesting, colors, dashes, and widths. Repeat as `reliability.ghx`. Pan until the HopperDraw component is offscreen while its distant annotations remain onscreen; annotations must still paint.
3. Re-save and reopen both files a second time. Confirm no HopperDraw warning for valid data. Test a definition with more than 1,000 shapes if available.
4. In a copy of the GHX, change a shape Kind to 99, keeping all other data. Reopen: unaffected annotations stay visible; a persistent warning names the unreadable record. Trigger several solutions; warning must remain. Save/reopen again and inspect that record's values/sub-chunks are unchanged and its list slot retained.
5. Repeat with a missing X coordinate array. Repeat on another copy with FormatVersion=99: annotation editing is disabled, the warning persists, and the annotation chunk survives re-save unchanged. Keep an untouched copy for comparison.
6. Return to a valid definition. Copy/paste the HopperDraw component. Inspect shape GUIDs in a GHX saved copy: supported shapes and nested children in the pasted component must have fresh IDs; the original IDs must stay unchanged. Undo/redo a shape edit and confirm its IDs remain unchanged. Shape IDs in opaque future data cannot be interpreted by this version.

## Undo/redo script (short smoke sequence for PR review)

1. Save the definition. Create one line, then Ctrl+Z: only the line disappears. Ctrl+Y: it returns. Repeat for frame, open/closed polyline, and open/closed curve.
2. Select a line; set 3x thickness, cycle line type, and set color, separately. Undo/redo each action and verify exactly one style change each time. Repeat on a nested group with differently styled children.
3. Shift-select two separated shapes with an unselected shape between them. Group; undo; redo. Confirm highest-slot placement and original child order. Select two groups and a plain shape; ungroup and check the resulting selection references the correct children and plain shape. Undo; redo: geometry, order and IDs are restored, and the annotation selection is cleared (by design, undo/redo never restores transient selection).
4. Duplicate selected shapes; undo/redo. Delete selected shapes; undo/redo. Clear all; undo/redo. Include an opaque record in the Clear all check: undo restores its warning and raw data; redo clears them.
5. Drag a body through many mouse moves; one undo restores the starting geometry, one redo restores the final position. Repeat for every frame corner, a line endpoint with Shift, and polyline/curve vertices. Save, click without moving, and send a same-position move: no new undo record or modified asterisk. Hold Shift and make moves that snap to the same geometry: no new record until geometry changes.
6. Start a real drag, then Escape. Undo must restore its start. Repeat with focus moving to Rhino, capture loss (switch applications), document switch, and component removal. Return and perform a fresh drag: it must still get one fresh record.
7. Select annotations plus native GH components, and drag an annotation body. Undo/redo must restore both annotation geometry and native pivots, update component bounds/wires, and leave native selection/interaction usable. Drag a native component by itself: annotations must not follow via a timer. Compare undo menu entries for unexpected extra native records.
8. Attach the counter below. Repeat shape editing plus undo/redo. The solution counter must not increase for canvas-only actions; input changes may solve normally. Reopening a file starts fresh undo history; do not expect undo-stack persistence.

## Render order, picking, and DPI

1. Draw thick lines across wires and across two components created before/after HopperDraw. O=0 must put all strokes below wires/components; O=1 above. Change GH object ordering and repeat. Both selections and in-progress previews must always remain on top.
2. Overlap annotations with different multipliers. Click their bodies and verify the topmost matching shape wins, including when they are group children. Group/duplicate/ungroup must not flatten child style.
3. Draw horizontal, vertical, and coincident lines; group and nest them. Check bounds and window selection away from the origin. Empty groups must not invent origin geometry.
4. Repeat selection and handle drags at 25%, 100%, and 400% zoom and at Windows 100%, 150%, and 200% DPI (restart Rhino after scaling if needed). Handles and the extra hit tolerance should remain similar in screen pixels; actual stroke width scales with zoom. Test near wide strokes, not just vertices.
5. Feed M=-1/99, L=-1/5/99, O=-1/2, T=0/-1/huge/non-finite (use a script for NaN/infinity). Expect warnings, safe values, and continuing canvas paint. Open a styled definition with the solver disabled and confirm it paints safely before solving.

## Repaint and ownership

Use a temporary C# script component with the following subscriptions (attach once, keep delegates to unsubscribe afterward):

```csharp
// Run in a live GH scripting component, not the headless test harness.
int paints = 0, solutions = 0;
var canvas = Grasshopper.Instances.ActiveCanvas;
var doc = canvas.Document;
System.Windows.Forms.PaintEventHandler onPaint = (s, e) => paints++;
Grasshopper.Kernel.GH_Document.SolutionStartEventHandler onSolve = (s, e) => solutions++;
canvas.Paint += onPaint;
doc.SolutionStart += onSolve;
// Inspect counters with a debugger or a separate non-solving UI callback.
// Cleanup: canvas.Paint -= onPaint; doc.SolutionStart -= onSolve;
```

1. With a large definition, move the pointer through empty canvas for 10 seconds, without drawing/dragging. HopperDraw should request no paints when hover is unchanged. Enter/leave a shape handle: a repaint is expected when feedback changes. Native GH hover/UI can paint independently; compare with HopperDraw inactive.
2. Draw and drag continuously: previews update while the solution counter stays unchanged. Repeat Undo/Redo. Monitor resource use during repeated previews; there should be no progressive accumulation of temporary pens/brushes.
3. Add a second HopperDraw, each with differently colored annotations. Select A, enter drawing with double-D, finish a shape: only A changes. Escape, select B, and repeat. Shift selection/drag/delete must affect the owner only.
4. Switch documents while drawing and dragging; return and repeat double-D/drag. Only the current document's owner handles input. Disable Active, hide Show, lock/unlock, remove, undo removal, and close documents; verify no retained input capture or extra reactions.
5. Verify Ctrl/Alt native gestures, text editing, menu shortcuts, held/repeated D, and two distinct D presses. Double-click closes polyline/curve while Enter leaves them open; GH search should stay suppressed during drawing on empty canvas and work again after Escape.

## Known integration limits

Headless tests exercise archive serialization, actual shape code, bitmap render channels, snapshot swapping, shortcut routing, and gesture resets. They do not dispatch undo through a live GH_Document, validate GH's native mouse-interaction ordering, or demonstrate DPI/OS integration. Mixed native-object movement and multi-component ownership require particular attention. Unknown payload preservation consumes memory proportional to that payload; parser budgets bound interpretation rather than archive loading performed by GH itself.

## Independent review follow-up checks

1. Put a future-format/read-only HopperDraw before an editable one in document order. Select the read-only component and leave it first. The editable component must still draw and edit when it is the eligible owner; the read-only component must never capture double-D or mouse input.
2. Group a readable line with an opaque child, including one nested group. Attempt body/handle/multi-selection drags: no selected geometry or native pivot may move, no undo record should be added, and a blocking warning should appear. Ungroup to expose the opaque child, use Select unreadable annotation records and Delete, then Undo/Redo its removal and confirm original raw data and order.
3. Remove only the HopperDraw annotation chunk from a saved GHX while leaving valid GH metadata. Reopen: expect an empty editable annotation component and a one-time pre-v1 notice; draw/save/reopen normally. No GH metadata should appear inside the new annotation chunk.
4. Remove a shape style field or a component style field from a GHX copy. Defaults should be safe and warnings should name the missing fields. Make another edit and trigger solutions: component diagnostics must remain. Clear all, Undo, Redo, then read a clean file to check diagnostic lifecycle. Selecting an already-set color/thickness, or styling an all-opaque group, must not dirty the file or add undo history.
5. Add optional unknown items/sub-chunks to a v1 component, shape and child-list chunk. Reopen, edit, save as both GHX and GH, and reopen: values/sub-chunks must survive while edited known values update. A changed major FormatVersion remains opaque/read-only instead. During polyline/curve drafting, vertex squares should appear without a dashed selection path over the preview stroke.

The automated hook suite additionally stages a failing marker while its working file passes, stages a passing snapshot while its working files/verifier fail, checks pushed refs different from HEAD, multiple refs, duplicate tips and deletions, and compares index/working/untracked contents before and after.
