# Canvas UX implementation report

Scope: `ux/canvas-interaction`, based on main `80fed18`, implemented only in the assigned worktree. No push or PR. Component/assembly GUIDs, seven inputs, version 0.1.7.0 and format v1 remain unchanged. No persisted fields or compatibility readers were added. Local hooks are installed; commits run the staged-snapshot Quick verifier.

## Architecture by file

| File | Responsibility |
| --- | --- |
| `HopperDraw.cs` | Existing component/serialization, eligible-owner state and native object lookup; delegates Windows input and menus; preserves the unsupported non-Windows event path. Cancels controller gestures on disabling/hiding/removal and respects consumed Escape. |
| `HopperDraw.Session.cs` | Transient draft/pen bridge, explicit pen reset, tool persistent-data API and owner-only expiration; transaction guard for shared ungroup. |
| `HopperDrawUIAttributes.cs` | Draft preview, close-start ring, 45-degree preview snapping, selected/hover geometry rendering. |
| `Utilities/CanvasDrawingShortcut.cs` | Shared per-HWND hook, signed mouse coordinates, pre/post dispatch routing and command message filter before WinForms menu preprocessing. Retains double-D routing and handles recreation/focus/capture cleanup. |
| `Utilities/CanvasAnnotationController.cs` | Per-canvas eligible owners, pre-press native selection decisions, annotation claiming, actual native window-session observation, native drag following, verified drag-record attachment, keyboard commands and lifecycle/cursor coordination. |
| `Utilities/HopperDrawInteraction.cs` | Active click/drag/context GH interaction; selection vs handle editing, screen thresholds, native right-pan/zoom handoff with the crossing move, trailing-up consumption, balanced auto-pan and cancellation. |
| `Utilities/AnnotationMove.cs` | Press snapshots, absolute translation/geometry proposals, all-owner opaque guard, no-op detection, rollback and one-record commits; native pivot actions remain separate from annotation snapshot actions. |
| `Utilities/AnnotationGeometry.cs` | Shared flattened GDI+ stroke paths (including curves and closing segments), stroke hit/window/crossing tests, modifier transitions, Escape priority and stable channel/owner/shape picking. |
| `Utilities/AnnotationDraft.cs` | Pure click/drag drawing state machine, native click counts, open completion, close-on-start, Backspace and Shift constraints. |
| `Utilities/AnnotationCommands.cs` | Shared selected-leaf style transactions, surviving-owner mixed removal snapshots, native removal events, stable arrange and atomic duplicate. |
| `Utilities/AnnotationMenuBuilder.cs` | Reused drawing/selection/component menus; swatches/recent/custom/default color, width/dash previews and mixed/current checks, enabled states and tooltips. |
| `Utilities/AnnotationFeedback.cs` | Overlay screen-space action hint above the style bar. The pen-preview HUD chip it used to paint was replaced by the interactive `Utilities/StyleBar.cs` (style targets in `Utilities/StyleTarget.cs`). |
| `Utilities/StyleKeys.cs` | The key table for style shortcuts. On the canvas three rows are read by position (digits: colour; Q row: thickness or text size; A row: line type or text style; Shift: the fill's colour, opacity, hatch); in the text editor only Ctrl chords exist (digits, B, I, comma/period). Routed by the controller's command handler and the editor's format hook; `StyleTarget.Run` decides what a position means for its target, and the flyouts in `StyleBar` print the same rows as key caps. The tool row (Z X C V B N) is `DrawingTool.Key` via `ToolShortcut`. |
| `Utilities/DelayedAction.cs` | One cancellable UI-thread timer. The controller uses it for D with a selection outside drawing mode: the press is left to the D D gesture and the style change runs after the double-press window unless a key, a mouse button, focus loss or a different selection intervened. |
| `Utilities/GeometryUtilities.cs` | Aspect constraint also handles axis-aligned frame edits. |
| `Utilities/ShapeRenderer.cs` | Actual-stroke selection halo/dashed green outline and subtle hover; stored style remains unchanged. |
| `Tests/Serialization/Program.cs`, `Tests/Serialization/GuardrailTests.cs` | Nine GH-backed guardrails for archive preservation, nested snapshot undo, bitmap rendering and real interaction teardown. |
| `Tests/Serialization/Serialization.csproj` | Compiles the transient component partial alongside the real plugin sources and GH assemblies. |
| `Tests/ShortcutRouting/Program.cs` | Five real WinForms keyboard guardrails: native Escape, queued double-D, pre-menu Delete/Ctrl+A, inline editing and final-owner cleanup. |
| `README.md` | Rewritten drawing/editing/menu behavior, transactions and integration limitations. |
| `docs/live-test-checklist.md` | Corrected obsolete behaviors and added twelve Canvas UX live groups, including every host-only requirement. |

## Spec traceability

“Done” means implemented with the verification scope below; it does not mean live Rhino integration has been observed.

| Spec item | Status and evidence |
| --- | --- |
| A1 pre-WinForms HWND input; retain double-D | Done. Shared hook and real message-loop regressions. |
| A2 per-canvas controller, per-owner shapes/selection | Done. Controller registry with lifecycle cleanup. |
| A3 claimed GH interaction, no replacement, native tests, active clicks, auto-pan, double-click ordering | Done with conservative icon guard deviation below. Actual GH dispatch remains live-only. |
| A4 handles > native > Overlay > Groups; owner/shape ordering; actual strokes | Done. Stable picking and path helpers. |
| B unselected replace / selected mixed preserve | Done. |
| B Shift add, Ctrl remove, Shift+Ctrl toggle; handles edit alone | Done. Selection helper and snapshot geometry edit. |
| B native pre-press replacement | Done. Hook reads the native top-level selection before GH changes it. Live-only. |
| B actual native window/crossing session, release modifiers, empty click, no Escape/replacement completion | Done. Tracks the actual interaction and its last integer-rounded rectangle, including synthetic auto-pan moves. Session behavior live-only. |
| B Ctrl+A / Ctrl+D, idle Escape | Done (revised after review). Ctrl+A/Ctrl+D extend GH's Select All/Deselect All to annotations and then fall through to GH's own command (no menu lookups). Ctrl+Shift+A is GH Grow Selection and is not intercepted. Escape yields to GH's preview-boundary clearing. |
| B opaque all-or-nothing geometry block | Done across every selected annotation owner and annotation-led native pivots. Native-led GH movement remains native; annotations do not partially transform. |
| C annotation-led mixed movement, expanded groups, H/V, absolute snapshots | Done. Native layout/group expansion live-only. |
| C one native-pivot + per-owner annotation record; true Escape/focus/capture/document cancellation | Done. Commit-on-release and rollback; native undo dispatch live-only. |
| C native follow, group anchor, Alt toggle/copy, key/move/release/auto-pan sync | Done. Uses a non-group anchor. Auto-pan sync is guarded translation before Groups paint, not a timer. Live-only. |
| C verified RecordAdded attachment; safe no-record fallback | Done. Checks document, active interaction identity, release dispatch, creation time, unseen record GUID, Drag name, pivot-action count/types; adds prepared annotation-only actions once. Missing record reverts annotations. No MergeRecords. Live-only. |
| D focused commands before menus; mixed Delete/Backspace | Done. Native removal snapshot plus surviving-owner shape actions; owners removed natively retain shapes in their native snapshot. Host removal/undo remains live-only. |
| D layered physical Escape; Enter / drafting Backspace | Done. Real-loop Escape guardrail. |
| E right-press deferred selection, contextual release, native pan/Ctrl zoom handoff | Done; host handoff/menu suppression live-only.   |
| E exact GH_CanvasMenuStrip class | Partially: impossible through the pinned public API (internal type). Uses its public ContextMenuStrip base with the same standard WinForms appearance. No reflection/private API dependency. |
| E all selection style menus, swatches/recent/custom/default, current/mixed states, previews | Done. Custom OS dialog is live-only. |
| E uniform-style-to-pen; owner-local stable arrange; group/ungroup; duplicate/delete | Done. Cross-owner grouping disabled with explanation; shared transactions across owners. |
| E drawing-mode Tool/Pen/Reset/Exit; wired M disable, persistent API undo | Done. GH undo/expiration live-only. |
| E component Drawing/Selection/housekeeping; remove Cycle line type menu | Done. Shared builders. |
| E session sticky pen independent of selection styling | Done. |
| F line/frame drag and click-click | Done. |
| F polyline/curve open double-click/Enter; close-on-start >=3 distinct; Backspace/ring | Done. Ring/real host sequence live-only. |
| F Shift 45-degree drawing/vertex edits; square/aspect frames; H/V whole move | Done. Live visual checks listed. |
| F no GH window/deselect/search during drawing | Done via deliberate active interactions and validator; real GH ordering live-only. |
| G cursors and clean reset | Done; actual GH cursor/DPI behavior live-only. |
| G screen-space DPI HUD | Done; live appearance/placement remains unverified. |
| G selected halo/dashed outline, subtle hover, unchanged stored color | Done. Bitmap render guardrail; live visibility checks listed. |
| G annotation double-click blocks search without geometry edits | Done. Validator and active double-click handling. Live GH check required. |
| H reduced automated guardrails | Nine Serialization cases and five ShortcutRouting cases; broader canvas UX behavior is covered by the live checklist. |
| H README and comprehensive Canvas UX live section | Done. No live verification is claimed. |
| Deferred arrowheads/cross-owner grouping/clipboard/text/fills/tool keys/Mac/mixed Alt-copy | Deferred as specified; no format additions or release bump. |

## Deviations and verification limits

- `GH_CanvasMenuStrip` is internal in the pinned Grasshopper reference assembly and the installed Rhino assembly. Constructing it fails at compile time. Context menus therefore use its public WinForms base. The internal class's extra special-control Enter behavior is unnecessary for these ordinary menu items.
- Public pinned GH APIs expose native relevant-object and widget hit tests, but not abort/cluster icon hit tests. The controller yields in aborted/processing documents and reserves the top-left 100 DPI-scaled pixels inside cluster documents. This conservative area is broader than the icon. MRU panels require no loaded document, so they cannot coexist with eligible owners. Ctrl+Alt yields through the Alt/info guard.
- Native auto-pan sync uses `CanvasPrePaintGroups`, before either annotation channel, for translation only when the native anchor delta changes. Other follow sync runs after mouse/key handling and before commit. No timers or blind undo merging were added.
- The existing fallback event path is retained for unsupported non-Windows builds; the new UX contract targets Windows. Four-target compilation does not establish Mac support.
- Escape cancels an unfinished shape before exiting mode and respects an Escape consumed by another handler. The reduced native keyboard suite retains the layered cancellation guardrail.
- Full verification builds all four TFMs and runs both executable test projects. Tests use real plugin sources/reference assemblies, without constructing a headless GH_Document. Live undo server dispatch, GH group/Alt/auto-pan movement, native deletion/wires, contextual pan/zoom, dialogs, focus/lifecycle, search suppression, cursors, HUD and DPI must still be checked in Rhino using the Canvas UX section.

Current automated guardrails: Serialization/model 9 and ShortcutRouting 5 (14 cases total). Quick verification builds the Windows Debug target and runs both projects.
