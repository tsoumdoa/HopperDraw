# HopperBorder Implementation Plan v2 (Updated)

## Overview

A Grasshopper plugin that lets users draw thick visual border/separator lines on the canvas for organizing definitions. Borders persist independently of the source component.

---

## User Stories (Implemented)

### US1: Per-Shape Thickness Multiplier
- **How to use:** Hold Ctrl and press 1 (0.5x), 2 (2x), or 3 (3x) to set thickness multiplier BEFORE or DURING drawing
- Multiplier applies to newly created shapes
- Preview shows correct thickness while drawing
- Each shape tracks its own `ThicknessMultiplier`

### US2: Drawing Order Control (Default: Above)
- Set `DrawOrder` input: 0=Below (Objects channel), 1=Above (Overlay channel)
- Default is **1 (Above)** - borders appear on top of GH components
- Borders render in appropriate channel based on setting

### US3: Per-Shape Color Override
- **How to use:** Right-click during drawing mode to set color override for the NEXT shape
- Right-click opens ColorDialog
- Color applies to newly created shapes
- Preview shows correct color while drawing
- Each shape stores optional `OverrideColor` (nullable Color)
- `null` = use component default color

### US4: Fixed Serialization (Read was empty)
- `Write()` now saves all shape data including new properties
- `Read()` now properly restores all shapes with their properties
- Shapes persist across save/reload

---

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| Ctrl+1 | Set thickness multiplier to 0.5x (half) |
| Ctrl+2 | Set thickness multiplier to 2x (double) |
| Ctrl+3 | Set thickness multiplier to 3x (triple) |
| Right-click (during drawing) | Open color picker for next shape |
| Shift (while drawing) | Constrain angle (45° snap) - works with thickness/color |

---

## Architecture

### Hybrid Approach: Custom GH_Component + Controller Component

```
GH Document
├── BorderAnnotation (GH_Component) - persistent border data
│   ├── Stores all DrawShape objects in Shapes list
│   ├── Renders borders on canvas (Objects or Overlay channel)
│   ├── Handles hit-testing for selection
│   ├── Persists when controller is deleted
│   └── Serializes with .gh file
│
└── BorderController (GH_Component) - UI controller
    ├── Draw mode: create new borders
    ├── Edit mode: select/move/resize/delete
    ├── Menu: Clear All, Draw Mode submenu
    └── Manages annotation lifecycle
```

---

## Class Structure

### DrawShape Hierarchy (BorderAnnotation.cs)

```csharp
internal abstract class DrawShape
{
    public int Id { get; set; }
    public float ThicknessMultiplier { get; set; } = 1.0f;  // US1: 0.5, 1.0, 2.0, 3.0
    public Color? OverrideColor { get; set; }               // US3
    
    public abstract void Render(Graphics g, Pen pen, float thickness);
    public abstract (bool hit, int pointIndex) HitTest(PointF pt, float tolerance, float thickness);
    public abstract PointF[] GetPoints();
    public abstract void Move(double dx, double dy);
    public abstract void MovePoint(int pointIndex, Point3d newPos);
    public abstract void Write(GH_IWriter writer, int index);
    public abstract void Read(GH_IReader reader, int index);
}

internal class LineShape : DrawShape { ... }      // Type 0
internal class PolylineShape : DrawShape { ... } // Type 1
internal class FrameShape : DrawShape { ... }    // Type 2
internal class CurveShape : DrawShape { ... }    // Type 3
```

### BorderAnnotation (BorderAnnotation.cs)

```csharp
internal class BorderAnnotation : GH_Component
{
    public List<DrawShape> Shapes { get; set; } = new List<DrawShape>();
    public Color BorderColor { get; set; } = Color.Black;
    public float LineThickness { get; set; } = 8f;
    public int DrawOrder { get; set; } = 1;  // US2: Default = Above
    public bool Visible { get; set; } = true;
    
    // Selection state (runtime only)
    public int SelectedShapeIndex { get; set; } = -1;
    
    // Serialization (US4)
    public override bool Write(GH_IWriter writer);
    public override bool Read(GH_IReader reader);
}
```

### BorderController (BorderController.cs)

```csharp
public class BorderController : GH_Component
{
    // Runtime state
    private float _currentThicknessMultiplier = 1.0f;  // US1
    private Color? _pendingColorOverride;               // US3
    
    // Inputs
    // 0: Active (bool)
    // 1: Show (bool)
    // 2: DrawMode (int: 0=Line, 1=Polyline, 2=Frame, 3=Curve)
    // 3: Color (Color) - "Right-click during drawing to set per-shape color override"
    // 4: Thickness (double) - "Base thickness. Use Ctrl+1 (0.5x), Ctrl+2 (2x), Ctrl+3 (3x)"
    // 5: DrawOrder (int: 0=Below, 1=Above) - Default = 1 (Above)
    
    // Exposed properties for preview rendering
    public float CurrentThicknessMultiplier => _currentThicknessMultiplier;
    public Color? PendingColorOverride => _pendingColorOverride;
}
```

---

## Preview Rendering (BorderModeUIAttributes.cs)

The preview correctly applies thickness multiplier and color override:

```csharp
float effectiveThickness = annotation.LineThickness * owner.CurrentThicknessMultiplier;
Color effectiveColor = owner.PendingColorOverride ?? annotation.BorderColor;

using (var pen = new Pen(effectiveColor, effectiveThickness))
{
    // Draw preview with correct thickness and color
}
```

---

## Drawing Flow with Thickness/Color Overrides

```
1. User presses Ctrl+1/2/3 → Sets _currentThicknessMultiplier
2. User right-clicks → Opens ColorDialog → Sets _pendingColorOverride
3. User Ctrl+clicks to start drawing
4. User optionally holds Shift while dragging to constrain angle
5. On completion (left click to finish):
   - Creates shape with _currentThicknessMultiplier applied
   - Creates shape with _pendingColorOverride applied
   - Clears _pendingColorOverride (but keeps multiplier for next shape)
```

---

## Serialization

### Write()

```csharp
public override bool Write(GH_IWriter writer)
{
    writer.SetInt32("ShapeCount", Shapes.Count);
    writer.SetInt32("Color", BorderColor.ToArgb());
    writer.SetDouble("Thickness", LineThickness);
    writer.SetInt32("DrawOrder", DrawOrder);
    
    for (int i = 0; i < Shapes.Count; i++)
    {
        Shapes[i].Write(writer, i);
    }
    return base.Write(writer);
}
```

### Per-Shape Write (example: LineShape)

```csharp
public override void Write(GH_IWriter writer, int index)
{
    writer.SetDouble($"StartX{index}", Start.X);
    writer.SetDouble($"StartY{index}", Start.Y);
    writer.SetDouble($"StartZ{index}", Start.Z);
    writer.SetDouble($"EndX{index}", End.X);
    writer.SetDouble($"EndY{index}", End.Y);
    writer.SetDouble($"EndZ{index}", End.Z);
    writer.SetInt32($"Type{index}", 0);
    writer.SetDouble($"ThickMult{index}", ThicknessMultiplier);
    if (OverrideColor.HasValue)
    {
        writer.SetInt32($"Color{index}", OverrideColor.Value.ToArgb());
    }
}
```

---

## Right-Click Menu (Simplified)

Only two items remain:
1. **Draw Mode** submenu → Line, Polyline, Frame, Curve
2. **Clear All** → Removes all shapes

Removed: Thickness Override menu, Set Shape Color (now handled via keyboard shortcuts and right-click during drawing)

---

## Files Modified

| File | Changes |
|------|---------|
| `BorderAnnotation.cs` | Added `ThicknessMultiplier` (float), `OverrideColor`; Added `DrawOrder` (default=1); Fixed `Write()`/`Read()`; Updated `Render()` for Objects/Overlay channel |
| `BorderController.cs` | Added `DrawOrder` input; Added `CurrentThicknessMultiplier`, `PendingColorOverride` properties; Added right-click color picker; Added Ctrl+1/2/3 keyboard handling; Wired up all shape creations |
| `BorderModeUIAttributes.cs` | Updated preview to use `effectiveThickness` and `effectiveColor` |
| `plan_v2.md` | This document |

---

## Implementation Status

| Feature | Status | Location |
|---------|--------|----------|
| Per-shape thickness (0.5x/2x/3x) | ✅ Implemented | Ctrl+1/2/3 keys, `_currentThicknessMultiplier` |
| Per-shape color override | ✅ Implemented | Right-click during drawing, `_pendingColorOverride` |
| Drawing order control | ✅ Implemented | `DrawOrder` input, default=1 (Above) |
| Fix empty Read() | ✅ Implemented | Full `Read()` with shape restoration |
| Render in Objects/Overlay | ✅ Implemented | `BorderAnnotationAttributes.Render()` |
| Preview with overrides | ✅ Implemented | `BorderModeUIAttributes.Render()` |
| Tooltips | ✅ Implemented | Input parameter descriptions |

---

## Build Output

```
Build succeeded.
- net48: hopperborder.gha
- net7.0-windows: hopperborder.gha (locked by Rhino)
```

---

## Open Questions / Future Enhancements

1. **Remove color override** - Currently once set, user cannot easily remove the override to go back to default color. Could add "Use Default Color" option.

2. **Undo/Redo** - Not yet implemented for any operations.

3. **Default thickness multiplier input** - Currently only via Ctrl+1/2/3 before drawing. Could add component input for default multiplier.
