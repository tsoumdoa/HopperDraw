# HopperBorder

A Grasshopper plugin for drawing visual border/separator lines on the Grasshopper canvas to help organize definitions.

## Overview

HopperBorder allows users to draw thick visual lines, polylines, curves, and frames on the Grasshopper canvas. These borders are persistent and independent of the source components, helping to visually separate and organize complex definitions.

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

### Line Mode (DrawMode = 0)

1. Press **Ctrl + Left Click** to start drawing
2. Move cursor to desired endpoint
3. **Left Click** to complete the line
4. Line snaps to 45° angles when **Shift** is held

### Polyline Mode (DrawMode = 1)

1. Press **Ctrl + Left Click** to add the first point
2. Continue clicking to add more points
3. **Double Click** or press **Enter** to complete
4. Press **Escape** to cancel
5. **Shift** constrains angle while adding points

### Frame Mode (DrawMode = 2)

1. Press **Ctrl + Left Click** to set first corner
2. Move cursor to see frame preview
3. **Left Click** to set second corner and complete
4. **Shift** constrains to square frame (equal width/height)

### Curve Mode (DrawMode = 3)

1. Press **Ctrl + Left Click** to add the first point
2. Continue clicking to add more control points
3. Preview shows the actual curve shape including cursor position
4. **Double Click** or press **Enter** to complete
5. **Shift** constrains angle while adding points

## Thickness Override

Control line thickness with keyboard shortcuts:

| Shortcut | Effect |
|----------|--------|
| Ctrl + 1 | Set thickness to 0.5x (half) |
| Ctrl + 2 | Set thickness to 2x (double) |
| Ctrl + 3 | Set thickness to 3x (triple) |

**Behavior:**
- Multiplier applies to the **base thickness** set by the input parameter
- Only affects the **next shape** to be drawn
- Automatically resets to 1.0x (base thickness) after each shape is completed
- Preview shows the effective thickness while drawing

## Color Override

Set a custom color for the next shape:

1. **Right Click** (during drawing mode) to open color picker
2. Select a color and click OK
3. The next shape will use this color instead of the default
4. Color override applies to only one shape, then clears

**Preview:**
- Preview shows the selected color override

## Shape Properties

Each shape stores its own:
- **ThicknessMultiplier** - Affects how thick the shape renders (0.5x, 1x, 2x, 3x)
- **OverrideColor** - Optional custom color (null = use component default)

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
| Color | Color | Black | Default border color. Right-click during drawing for per-shape override |
| Thickness | Number | 8.0 | Base line thickness. Use Ctrl+1/2/3 for multipliers |
| DrawOrder | Integer | 1 | 0=Below components, 1=Above components |

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| Ctrl + A | Select all shapes |
| Ctrl + Left Click | Start drawing (all modes) |
| Left Click | Add point / Complete shape |
| Double Click | Complete polyline/curve |
| Shift + Drag | Constrain angle (45° snap) |
| Ctrl + 0 | Reset thickness to 1.0x (default) |
| Ctrl + 1 | Set 0.5x thickness multiplier |
| Ctrl + 2 | Set 2x thickness multiplier |
| Ctrl + 3 | Set 3x thickness multiplier |
| Right Click (during drawing) | Open color picker for next shape |
| Right Click (on selected) | Open color picker for selected shape(s) |
| Enter | Complete polyline/curve |
| Escape | Complete or cancel drawing |
| Delete | Delete selected shape(s) |

## Right-Click Menu

- **Draw Mode** submenu:
  - Line
  - Polyline
  - Frame
  - Curve
- **Clear All** - Removes all drawn shapes

## Selection and Editing

Shapes can be selected and manipulated:
- **Click** on a shape to select it
- **Click on empty canvas**: Clear selection
- **Window select**: Click and drag on empty canvas to select multiple shapes
- **Ctrl + Window select**: Add shapes to current selection
- **Click on selected shape**: Start multi-drag to move all selected shapes
- Selected shapes show white handles at control points
- Drag handles to resize/move shapes
- **Ctrl + 0**: Reset thickness to 1.0x (default)
- **Ctrl + 1/2/3**: Change thickness of selected shapes (0.5x, 2x, 3x)
- **Right-click on selected**: Change color of selected shapes
- **Escape**: Clear selection (or cancel drawing)
- **Delete**: Remove all selected shapes

### Multi-Selection Features

1. **Window Selection**: Click and drag on empty canvas to create a selection window
2. **Additive Selection**: Hold Ctrl while window selecting to add to existing selection
3. **Multi-Drag**: Click on any selected shape and drag to move all selected shapes together
4. **Bulk Operations**: Ctrl+1/2/3 and right-click color change apply to ALL selected shapes

## Limitations

- Drawn elements cannot be used to drag GH components behind them
- Selection boundary does not include drawn elements
- Ctrl+A (select all) creates a selection bounding box that incorrectly includes objects far up in the top-left

## Technical Notes

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
