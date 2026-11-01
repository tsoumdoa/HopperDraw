# HopperDraw

Persistent annotation lines, polylines, frames, curves, and groups on the Grasshopper canvas in Rhino 8. Annotations belong to a **Hopper Draw** component and use absolute canvas coordinates. They stay in place when the component moves.

## Start drawing

1. Build the plugin for your Rhino runtime (see Development), then load the resulting `hopperborder.gha` through Grasshopper's component libraries folder and restart Rhino. Keep one copy installed.
2. Place **Hopper Draw** from **Params > Util**. Leave Active and Show enabled. Choose DrawMode using the M input.
3. Focus the canvas, press and release **D twice**, then click empty canvas to draw. The component footer shows the mode and `ON`. **Escape** exits drawing mode and discards unfinished geometry; completed annotations remain.

Line and frame use two clicks. Polyline and curve add vertices on clicks; press **Enter** to finish **open**, or **double-click** near the last click to finish **closed**. Drawing mode stays on after completion. There are no outputs and no Rhino model geometry is created.

## Inputs (stable order)

| Input | Default | Meaning |
| --- | --- | --- |
| Active (A) | true | Enable annotation editing and double-D activation. Inactive annotations still display. |
| Show (S) | true | Display annotations. Hiding cancels drawing. Locking the component also hides its annotations. |
| DrawMode (M) | 0 | 0 Line, 1 Polyline, 2 Frame, 3 Curve. Changing mode cancels an unfinished shape. |
| Color (C) | Black | Default annotation color; shapes can override it. |
| Thickness (T) | 8 | Base width in canvas units, clamped to 0.1-1000. Effective base times multiplier is capped at 1000. Non-finite input uses 8. |
| LineType (L) | 0 | 0 Solid, 1 Dash, 2 Dot, 3 DashDot, 4 DashDotDot. |
| DrawOrder (O) | 1 | 0 below wires/components (Groups channel); 1 above (Overlay). Previews and handles always use Overlay. |

Out-of-range mode, order, dash, or thickness inputs produce runtime warnings and safe values. Stroke widths scale with canvas zoom; handle sizes and picking tolerance stay approximately constant in screen pixels.

## Editing and menus

Click a shape outside drawing mode to select it. **Shift-click** toggles selection. Drag a body to move it, or a vertex handle to edit it. Drag empty canvas to select shapes with a vertex inside the rectangle. At overlaps, the topmost annotation is picked. **Delete** removes selected annotations when native GH objects are not also selected. **Escape** clears an annotation selection; during a drag it ends the gesture, retaining changes already applied and their undo record.

**Shift** while drawing lines/polyline segments/curves constrains the next segment to horizontal or vertical. With a frame it makes a square. When editing, Shift snaps a line endpoint to 45-degree increments and preserves a frame's starting aspect ratio. Polyline/curve handle drags have no Shift constraint.

The component menu provides Exit drawing mode, Select all shapes, Select unreadable annotation records, Shape thickness (0.5x/1x/2x/3x), Cycle line type, next/selected shape color, Duplicate, Group, Ungroup, and Clear all. Thickness and line type affect selected leaves when there is a selection, otherwise the next shape. Next-shape overrides reset after completing or cancelling a shape. Color selection uses the system color dialog. Style commands that change no stored leaf values do not add undo records or mark the document modified.

Groups are pure containers: every child retains its color, thickness, and dash. Group style commands visit all descendant leaves. Grouping keeps selected children's original order and replaces the **highest selected slot** after removing those children. For `[A, B, C, D]`, grouping A+C produces `[B, Group(A,C), D]`. Ungroup restores children at the group's slot and selects them alongside any other selected shapes. Duplicate offsets copies by 30 canvas units and assigns new shape GUIDs.

Creation, deletion, clear, selected style changes, group/ungroup, duplication, and completed drags are undoable through Grasshopper Undo/Redo. A drag records once, before its first real geometry change. A click without movement creates no undo entry. Escape, focus/capture loss, or document switching ends a drag; applied movement remains undoable. Shape undo refreshes the canvas without requesting a solution. Undo history is **in memory only** and does not survive reopening a file. Selecting a style for an uncreated shape changes temporary drawing state. Undo while drafting discards the unfinished preview and undoes the previous committed action.

Multiple HopperDraw components keep separate annotation lists. Read-only recovery components are excluded from input ownership. While drawing, that editable component owns input. Otherwise the selected eligible HopperDraw wins, then one with an annotation selection, then the first eligible component in document order. Ctrl/Alt mouse gestures and clicks on native GH objects retain native handling. Double-D reserves plain D while shortcut activation is eligible; text controls, native shortcuts, focus changes, and document switches interrupt activation. Moving GH objects alone does not make annotations follow them. Mixed annotation drags include selected GH pivots in the same annotation undo action; verify host interaction using the live checklist.

## File format and recovery

This release uses annotation **format v1**, in a dedicated `HopperDraw` chunk with a `FormatVersion` value. Each shape has an indexed sub-chunk and a persistent GUID; groups recursively contain child lists. GUIDs survive save/load and undo; copies receive fresh IDs. Copy/paste rekeys the standard v1 ID envelope even for opaque records; future-format payload contents remain untouched. No legacy flat-key reader or downgrade format is provided. Files from 0.1.6 and earlier are unsupported. **Files saved by this version cannot be read by HopperDraw <=0.1.6**: those readers omit the component, so do not re-save these definitions with an older plugin. The owner sets the package version when preparing the release.

`FormatVersion` is a **major format version**. Breaking changes require a new major value and are retained opaque/read-only by this reader. Additive optional fields keep the major value: unknown items (including indexed keys) and sub-chunks in readable components, shapes, and shape lists are preserved verbatim through save/load, duplication, and undo. Known fields reflect edits. Additive data must remain safe for an older reader to ignore; features requiring new geometry semantics need a major version change.

Required kind, GUID, geometry/cardinality, and coordinate checks prevent damaged data becoming guessed geometry at the origin. Optional missing styles use defaults and emit load diagnostics. Component load diagnostics persist through edits and solutions until the next successful read or Clear all; clear/undo restores them. Shape diagnostics remain until the affected records are removed or successfully re-read. Invalid/unknown records remain opaque in their original list positions and produce a persistent runtime warning. They are retained on re-save and are not painted. Any subtree containing opaque records blocks geometry transforms and duplication, with feedback, so readable children cannot drift away from unreadable siblings. Use Select unreadable annotation records, then Delete, to remove top-level opaque records safely; ungroup first to expose nested records. Deletion and Clear all are undoable. An unsupported format or damaged list keeps the entire raw annotation payload read-only on re-save. Open it with a compatible version to resolve it; this release does not interpret or rewrite it as v1. A missing `HopperDraw` annotation chunk loads as an empty, editable component with a one-time notice that pre-v1 formats are unsupported; GH component metadata is never captured as annotation data. Failures reading base Grasshopper metadata and wires retain GH's native failure behavior; only annotation parsing has recovery.

Parsing is limited to depth 32, 20,000 shapes, 200,000 points, and finite coordinates within +/-10,000,000 canvas units. Exceeding a budget keeps the affected record or payload opaque. Raw unknown data is retained, so these are interpretation budgets, not a file-size limit.

## Development

Use Windows, Git, a .NET SDK supporting the declared targets, and the .NET Framework 4.8 runtime for the console tests. NuGet supplies the pinned Rhino/Grasshopper reference assemblies. The assembly/project name remains `hopperborder`; component and assembly GUIDs and all seven parameter slots remain unchanged.

```powershell
pwsh -NoProfile -File scripts/install-hooks.ps1
pwsh -NoProfile -File scripts/verify.ps1 -Quick
pwsh -NoProfile -File scripts/verify.ps1 -Full
```

Quick builds `net8.0-windows` Debug and runs every project under `Tests` by exit code, plus the hook snapshot script suite. Full builds Release for `net8.0-windows`, `net7.0-windows`, `net7.0`, and `net48`, then runs all tests. Both check that `yak/manifest.yml` matches the project version. Test projects are included in `hopperborder.slnx`; tests use the real plugin sources and GH assemblies without constructing a headless GH_Document.

The installer sets repository `core.hooksPath` to `.githooks`. Pre-commit exports the **staged index** into an isolated temporary directory and runs that snapshot's Quick verifier. Unstaged changes cannot hide staged failures or block a passing staged snapshot. Pre-push reads Git's ref updates from stdin and exports each distinct **outgoing commit tip** for Full verification, including branches other than the current checkout; repeated tips run once and deletion-only pushes need no build. Both paths use the normal shared NuGet cache and clean up their temporary directories without stashing or rewriting the working tree/index.

Git for Windows invokes the sh hooks, which prefer `pwsh` and fall back to `powershell`; both use `-NoProfile -ExecutionPolicy Bypass`. Calling `scripts/verify.ps1` directly verifies the current working tree. `Tests/Hooks/snapshot-tests.ps1` checks snapshot isolation and pushed-ref behavior in disposable repositories, and runs in both verification modes. Deliberate bypass: `git commit --no-verify` or `git push --no-verify`. There is no hosted CI.

`HopperDraw.cs` handles component state, interaction, and transactions. `Shapes/ShapeSerializer.cs` owns persistence. `Utilities/ShapeRenderer.cs` guards annotation/preview paint. `Utilities/ShapeEditUndoAction.cs` owns snapshots and canvas-only undo. `HopperDrawUIAttributes.cs` selects render channels and builds previews. Curve hit-test samples are cached until geometry changes.

Run the [live Rhino checklist](docs/live-test-checklist.md) before release. Headless tests cover archive/model/bitmap/input regressions, not host undo-server dispatch, native selection event ordering, DPI behavior, or actual .gh/.ghx document integration. Build success for all targets is not a claim of live Mac support.
