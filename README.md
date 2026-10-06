# HopperBorder

A Grasshopper plugin for drawing visual border/separator lines on the Grasshopper canvas to help organize definitions.

## Overview

HopperBorder allows users to draw thick visual lines, polylines, curves, and frames on the Grasshopper canvas. These borders are persistent and independent of the source components, helping to visually separate and organize complex definitions.

## Framework targets

The project builds `net8.0-windows` for Rhino 8 running .NET 8, `net7.0-windows` and `net7.0` for earlier Rhino 8 runtimes, and `net48` for Rhino's .NET Framework runtime. The .NET 8 target compiles successfully, but live loading in Rhino has not been verified in this repository.

## How It Works

### Architecture

The plugin uses a hybrid architecture:

1. **BorderController** - The UI component that handles user input and drawing mode
2. **BorderAnnotation** - A persistent annotation object that stores and renders all drawn shapes
3. **BorderAnnotationAttributes** - Custom rendering logic for the annotation
4. **BorderModeUIAttributes** - Preview rendering during drawing

### Canvas Rendering

HopperBorder renders shapes using Grasshopper's custom rendering system:

1. **Render Channel Selection**: Shapes render in either:
   - `GH_CanvasChannel.Objects` - Behind GH components (DrawOrder = 0)
   - `GH_CanvasChannel.Overlay` - Above GH components (DrawOrder = 1)

2. **Rendering Pipeline**:
   - `BorderAnnotationAttributes.Render()` is called by Grasshopper for each canvas channel
   - Only renders when the current channel matches the selected DrawOrder
   - Each shape is rendered with its own effective thickness and color

3. **Preview Rendering**:
   - `BorderModeUIAttributes.Render()` draws the in-progress shape based on cursor position
   - Preview uses the same rendering logic as final shapes
   - Thickness multiplier and color override are reflected in preview

### Drawing Order

```
GH Canvas Channels (render order):
1. Groups (0)
2. Wires (10)
3. Objects (20)     ← GH components render here
4. Overlay (30)      ← Borders with DrawOrder=1 render here

With DrawOrder = 1 (default):
  - Borders render in Overlay channel
  - Borders appear ABOVE all GH components

With DrawOrder = 0:
  - Borders render in Objects channel
  - Borders appear BEHIND GH components
```

## Drawing Modes

With the Grasshopper canvas focused, press the **D key twice quickly** to enter drawing mode, then **left click on empty canvas** to start a shape. The two presses must be within your system's double-click interval; holding D does not activate the mode. The component footer turns blue and shows the shape type followed by **ON**. Drawing mode stays on after each completed shape so you can draw several shapes without reactivating it. Press **Escape** once to exit the mode and discard any unfinished shape; completed shapes remain. Drawing mode always starts off when reopening a file.

**Ctrl + Shift** and other Ctrl/Alt clicks are ignored by HopperDraw so you can grab Grasshopper wires, even while drawing mode is on. HopperDraw also ignores clicks on Grasshopper objects. Double-clicking empty canvas in drawing mode completes polylines/curves without opening Grasshopper's component search; exit drawing mode to use empty-canvas double-click search. If a document contains multiple HopperDraw components, select the component you want to use before pressing D twice; otherwise the component with selected shapes, or the first active component, owns canvas input. That component keeps ownership until you exit drawing mode. Mouse clicks, other keys, and focus changes reset a pending double-D gesture.

### Line Mode (DrawMode = 0)

1. Press **D twice** to enter drawing mode, then **left click** on empty canvas to start drawing
2. Move cursor to desired endpoint
3. **Left Click** to complete the line
4. Line snaps horizontally or vertically when **Shift** is held

### Polyline Mode (DrawMode = 1)

1. Press **D twice** to enter drawing mode, then **left click** on empty canvas to add the first point
2. Continue clicking to add more points
3. **Double Click** or press **Enter** to complete
4. Press **Escape** to cancel and exit drawing mode
5. **Shift** snaps horizontally or vertically while adding points

### Frame Mode (DrawMode = 2)

1. Press **D twice** to enter drawing mode, then **left click** on empty canvas to set the first corner
2. Move cursor to see frame preview
3. **Left Click** to set second corner and complete
4. **Shift** constrains to square frame (equal width/height)

### Curve Mode (DrawMode = 3)

1. Press **D twice** to enter drawing mode, then **left click** on empty canvas to add the first point
2. Continue clicking to add more control points
3. Preview shows the actual curve shape including cursor position
4. **Double Click** or press **Enter** to complete
5. **Shift** snaps horizontally or vertically while adding points

## Thickness Override

Use **Shape thickness** in the HopperDraw component's right-click menu to choose 0.5x, 1x, 2x, or 3x.

**Behavior:**
- Multiplier applies to the **base thickness** set by the input parameter
- Affects all selected HopperDraw shapes, or the next shape when none is selected
- Automatically resets to 1.0x (base thickness) after each shape is completed
- Preview shows the effective thickness while drawing

## Color Override

Set a custom color for the next shape:

1. Choose **Set next shape color** from the HopperDraw component's right-click menu
2. Select a color and click OK
3. The next shape will use this color instead of the default
4. Color override applies to only one shape, then clears

**Preview:**
- Preview shows the selected color override

## Line Type

Use **Cycle line type** in the HopperDraw component's right-click menu.

**Line Types:**
- 0: Solid (default)
- 1: Dash
- 2: Dot
- 3: DashDot
- 4: DashDotDot

**Behavior:**
- Applies to the **next shape** to be drawn when no selection exists
- When shapes are selected, cycles line type on **all selected shapes**
- Preview shows the effective line type while drawing

## Shape Properties

Each shape stores its own:
- **ThicknessMultiplier** - Affects how thick the shape renders (0.5x, 1x, 2x, 3x)
- **OverrideColor** - Optional custom color (null = use component default)
- **LineType** - Dash style (Solid, Dash, Dot, DashDot, DashDotDot)

## Persistence

Shapes are serialized with the Grasshopper file:
- All shape data (position, thickness, color) is saved
- Shapes restore correctly when reopening files
- Drawing order setting is also persisted

## Component Inputs

| Input | Type | Default | Description |
|-------|------|---------|-------------|
| Active | Boolean | True | Enable/disable border drawing |
| Show | Boolean | True | Show/hide borders on canvas |
| DrawMode | Integer | 0 | Drawing mode (0=Line, 1=Polyline, 2=Frame, 3=Curve) |
| Color | Color | Black | Default border color. Use the component menu for per-shape overrides |
| Thickness | Number | 8.0 | Base line thickness. Use the component menu for multipliers |
| LineType | Integer | 0 | Line style (0=Solid, 1=Dash, 2=Dot, 3=DashDot, 4=DashDotDot) |
| DrawOrder | Integer | 1 | 0=Below components, 1=Above components |

## Canvas Shortcuts

| Shortcut | Action |
|----------|--------|
| D key twice quickly | Enter drawing mode (all shape types) |
| Left Click in drawing mode | Start shape / Add point / Complete shape |
| Double Click | Complete polyline/curve |
| Shift while drawing | Snap horizontally or vertically; make frames square |
| Ctrl + Shift + click | Grasshopper wire grabbing; ignored by HopperDraw |
| Enter | Complete polyline/curve |
| Escape | Exit drawing mode and discard unfinished shape, or clear HopperDraw selection when mode is off |
| Delete | Delete selected HopperDraw shapes when no Grasshopper objects are selected |

## HopperDraw Component Menu

- **Exit drawing mode** exits and discards an unfinished shape, just like Escape
- **Select all HopperDraw shapes**
- **Shape thickness**, **Cycle line type**, and **Set next shape color** change the next shape when none is selected, or selected shapes where applicable
- **Set selected shape color**, **Duplicate selected shapes**, **Group selected shapes**, and **Ungroup selected shapes**
- **Clear all HopperDraw shapes** removes all drawn shapes

## Selection and Editing

Shapes can be selected and manipulated:
- **Click** on a shape to select it
- **Click on empty canvas**: Clear selection
- **Window select**: Click and drag on empty canvas to select multiple shapes
- **Shift + Window select**: Add shapes to current selection
- **Click on selected shape**: Start multi-drag to move all selected shapes
- Selected shapes show white handles at control points
- Drag handles to resize/move shapes
- Use the HopperDraw component menu to change thickness, line type, and color of selected shapes
- **Escape**: Clear selection (or cancel drawing)
- **Delete**: Remove all selected shapes

### Multi-Selection Features

1. **Window Selection**: Click and drag on empty canvas to create a selection window
2. **Additive Selection**: Hold Shift while window selecting to add to existing selection
3. **Multi-Drag**: Click on any selected shape and drag to move all selected shapes together
4. **Bulk Operations**: Component menu thickness, line type, and color changes apply to all selected shapes

## Limitations

- Drawn elements cannot be used to drag GH components behind them
- Selection boundary does not include drawn elements

## Technical Notes

### Regression Checks

On Windows, run `dotnet run --project Tests/InputRegression/InputRegression.csproj`. The harness checks the production gesture helper, shape completion, cancellation, snapping, handled-key behavior, and component-search validation without launching Rhino. Live keyboard/mouse integration in Rhino remains a separate manual check.

### Why Use GH_CanvasChannel.Overlay?

The Overlay channel renders after all other content, which allows borders to appear on top of everything else including Grasshopper components. This is the default behavior because users typically want their visual borders to be clearly visible.

### Serialization Format

Each shape type is serialized with a type discriminator:
- Type 0: LineShape
- Type 1: PolylineShape
- Type 2: FrameShape
- Type 3: CurveShape

Shape data includes:
- Control points (X, Y, Z as doubles)
- Type discriminator
- Thickness multiplier (float)
- Color override (optional, stored as ARGB int)

### Rendering Performance

- Shapes render using standard GDI+ Graphics methods
- Each shape creates its own Pen object for rendering
- No caching is implemented - shapes redraw on every canvas invalidation
