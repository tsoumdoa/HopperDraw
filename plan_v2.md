# HopperBorder Implementation Plan v2

## Overview

A Grasshopper plugin that lets users draw thick visual border/separator lines on the canvas for organizing definitions. Borders persist independently of the source component.

---

## Architecture

### Hybrid Approach: Custom GH_Annotation + Controller Component

```
GH Document
├── BorderAnnotation (GH_Annotation) - persistent border data
│   ├── Renders borders on canvas
│   ├── Handles hit-testing for selection
│   ├── Persists when controller is deleted
│   └── Serializes with .gh file
│
└── BorderController (GH_Component) - UI controller
    ├── Draw mode: create new borders
    ├── Edit mode: select/move/resize/delete
    ├── Menu: clear all, color picker, lock/unlock
    └── Manages annotation lifecycle
```

---

## Class Structure

### 1. BorderAnnotation.cs

Inherits from `GH_Annotation` - provides native canvas annotation persistence.

```csharp
public class BorderAnnotation : GH_Annotation
{
    // Data
    public List<BorderLine> Borders { get; set; } = new();
    public Color BorderColor { get; set; } = Color.Black;
    public float LineThickness { get; set; } = 3f;
    public bool IsLocked { get; set; } = false;
    
    // Selection state (runtime only)
    public int SelectedBorderIndex { get; set; } = -1;
    public int HoveredHandleIndex { get; set; } = -1;
    
    // GH_Annotation overrides
    public override Guid ComponentGuid => new Guid("...");
    public override string Name => "Canvas Border";
    public override string Category => "Draw";
    public override string SubCategory => "Annotation";
    
    // Bounds for hit testing
    public override RectangleF Bounds => CalculateUnionBounds();
    
    // Rendering
    protected override void Render(GH_Canvas canvas, Graphics g, GH_CanvasChannel ch)
    {
        if (ch != GH_CanvasChannel.Annotation) return;
        
        // Render each border with color/thickness
        // Draw selection handles on selected border
    }
    
    // Hit testing
    public override bool HitTest(PointF pt, GH_Canvas canvas)
    {
        // Test each border with tolerance
    }
    
    // Serialization
    public override bool Write(GH_IWriter writer);
    public override bool Read(GH_IReader reader);
}

public class BorderLine
{
    public Point3d Start { get; set; }
    public Point3d End { get; set; }
}
```

### 2. BorderController.cs

Inherits from `GH_Component` - provides UI controls and event handling.

```csharp
public class BorderController : GH_Component
{
    // State
    private bool _isActivated;
    private bool _isDrawingMode = true;
    private Point3d _drawStart;
    private Point3d _drawEnd;
    private bool _isDrawing;
    private bool _isDragging;
    private int _dragBorderIndex = -1;
    private int _dragHandleIndex = -1;
    
    // Reference to annotation
    private BorderAnnotation _annotation;
    
    // Inputs
    private GH_Boolean _inputEnable;
    private GH_Colour _inputColor;
    private GH_Number _inputThickness;
    
    // Canvas event registration
    private GH_Canvas _canvas;
    private bool _eventsRegistered;
}
```

### 3. BorderControllerAttributes.cs

Custom attributes - minimal visible UI (collapsed icon).

```csharp
public class BorderControllerAttributes : GH_ComponentAttributes
{
    protected override void Render(GH_Canvas canvas, Graphics g, GH_CanvasChannel ch)
    {
        // Render minimal collapsed icon
    }
}
```

---

## Persistence (Requirement 1)

### Problem
Current borders disappear when the component that created them is deleted.

### Solution: GH_Annotation

1. **BorderAnnotation** is a `GH_Annotation` subclass added to the document
2. Borders are stored in the annotation's `Borders` list
3. When controller is deleted, annotation remains
4. Serialization via Write/Read happens automatically

### Flow

```
User draws → Controller creates BorderAnnotation
           → Adds to document: doc.AddObject(annotation, false)
           → All border data stored in annotation

Controller deleted → Annotation persists on canvas

Document saved → Annotation.Write() serializes borders
Document loaded → Annotation.Read() restores borders
```

---

## Transparent Rectangle Suppression (Requirement 2)

### Problem
During drawing/dragging, a transparent preview rectangle appears.

### Solution

In `BorderController.Canvas_MouseDown`:

```csharp
private void Canvas_MouseDown(object sender, MouseEventArgs e)
{
    if (!_isActivated) return;
    
    // Suppress default selection rectangle
    if (_canvas?.Viewport != null)
    {
        _canvas.Viewport.SuppressSelectionRectangle = true;
    }
    
    // Drawing logic...
}
```

Reset in `Canvas_MouseUp`:

```csharp
private void Canvas_MouseUp(object sender, MouseEventArgs e)
{
    if (_canvas?.Viewport != null)
    {
        _canvas.Viewport.SuppressSelectionRectangle = false;
    }
}
```

---

## Colored Border Rendering (Requirement 3)

### Rendering Pipeline

```
GH_CanvasChannel order:
1. Wires
2. Objects  
3. Annotation ← BorderAnnotation renders here
4. Overlay (post-paint)
```

### Implementation

In `BorderAnnotation.Render()`:

```csharp
protected override void Render(GH_Canvas canvas, Graphics g, GH_CanvasChannel ch)
{
    if (ch != GH_CanvasChannel.Annotation) return;
    
    using (var pen = new Pen(BorderColor, LineThickness))
    {
        foreach (var border in Borders)
        {
            var start = border.Start;
            var end = border.End;
            
            // Constrain to orthogonal
            var constrained = ConstrainToOrthogonal(start, end);
            
            // Draw line or rectangle based on size
            if (IsRectangular(start, constrained))
            {
                g.DrawRectangle(pen, ...);
            }
            else
            {
                g.DrawLine(pen, ...);
            }
        }
    }
    
    // Draw selection/hover states
    if (SelectedBorderIndex >= 0)
    {
        DrawSelectionHandles(g, Borders[SelectedBorderIndex]);
    }
}
```

### Selection/Hover States

| State | Visual |
|-------|--------|
| Normal | Border color, configured thickness |
| Hovered | Slight brightness increase |
| Selected | Handles at endpoints, different cursor |

---

## Feature Implementation (Requirement 4)

### Adjustable Line Thickness

- Input parameter: `GH_Number` for thickness
- Stored in `BorderAnnotation.LineThickness`
- Applied in render pen creation

### Edit Mode

- Menu toggle: "Edit Mode" checkbox
- When enabled: MouseDown hits borders for selection
- When disabled: MouseDown starts new border draw

### Selecting Borders

```csharp
private void Canvas_MouseDown(object sender, MouseEventArgs e)
{
    var pt = ScreenToCanvas(e.Location);
    
    if (_annotation.HitTestHandles(pt, out int handleIdx))
    {
        _dragHandleIndex = handleIdx;
        _isDragging = true;
        _annotation.SelectedBorderIndex = handleIdx / 2;
    }
    else if (_annotation.HitTest(pt, out int borderIdx))
    {
        _annotation.SelectedBorderIndex = borderIdx;
        _dragBorderIndex = borderIdx;
        _isDragging = true;
    }
}
```

### Moving / Resizing

- **Move entire border**: Drag center of border
- **Reshape**: Drag endpoints (handled via handle indices)
- Update coordinates in real-time during MouseMove

### Delete Selected Border

```csharp
public void DeleteSelected()
{
    if (_annotation.SelectedBorderIndex >= 0)
    {
        var doc = OnPingDocument();
        
        // Create undo record
        var before = _annotation.Borders.ToList();
        _annotation.Borders.RemoveAt(_annotation.SelectedBorderIndex);
        var after = _annotation.Borders.ToList();
        
        doc.UndoManager.Add(new GH_UndoRecord("Delete Border",
            () => _annotation.Borders = before.ToList(),
            () => _annotation.Borders = after.ToList()
        ));
        
        _annotation.SelectedBorderIndex = -1;
    }
}
```

### Delete All Borders

```csharp
public void ClearAll()
{
    var doc = OnPingDocument();
    var before = _annotation.Borders.ToList();
    
    _annotation.Borders.Clear();
    
    doc.UndoManager.Add(new GH_UndoRecord("Clear All Borders",
        () => _annotation.Borders = before.ToList(),
        () => _annotation.Borders = new List<BorderLine>()
    ));
}
```

### Color Picker

```csharp
private void ShowColorPicker()
{
    var dialog = new ColorDialog();
    dialog.Color = _annotation.BorderColor;
    
    if (dialog.ShowDialog() == DialogResult.OK)
    {
        _annotation.BorderColor = dialog.Color;
    }
}
```

### Lock/Unlock

```csharp
public void ToggleLock()
{
    _annotation.IsLocked = !_annotation.IsLocked;
}

// In hit testing
public override bool HitTest(...)
{
    if (IsLocked) return false;
    // ... hit test logic
}
```

### Z-Order / Draw Order

- Order in `Borders` list = draw order
- "Bring to Front": Move selected index to end
- "Send to Back": Move selected index to 0

### Snap/Alignment Behavior

Optional feature:

```csharp
private Point3d ApplySnap(Point3d pt)
{
    if (!EnableSnap) return pt;
    
    var gridSize = 10.0; // Configurable
    return new Point3d(
        Math.Round(pt.X / gridSize) * gridSize,
        Math.Round(pt.Y / gridSize) * gridSize,
        0
    );
}
```

### Save/Load

Automatic via `GH_Annotation` base class:

```csharp
public override bool Write(GH_IWriter writer)
{
    writer.SetInt32("Count", Borders.Count);
    for (int i = 0; i < Borders.Count; i++)
    {
        writer.SetPoint3d($"Start{i}", Borders[i].Start);
        writer.SetPoint3d($"End{i}", Borders[i].End);
    }
    writer.SetColor("Color", BorderColor);
    writer.SetFloat("Thickness", LineThickness);
    writer.SetBoolean("Locked", IsLocked);
    return base.Write(writer);
}
```

### Undo/Redo Integration

All modifications use `GH_UndoRecord`:

```csharp
private void ModifyWithUndo(string actionName, Action undo, Action redo)
{
    var doc = OnPingDocument();
    if (doc == null) return;
    
    redo(); // Apply change
    
    doc.UndoManager.Add(new GH_UndoRecord(actionName, undo, redo));
    ExpireSolution(true);
}
```

### Clean Hit-Testing

- Use tolerance-based distance check
- Test endpoints first, then line segments
- Return closest hit

```csharp
public (int borderIndex, int handleIndex) HitTest(Point3d pt, float tolerance)
{
    for (int i = 0; i < Borders.Count; i++)
    {
        // Test handles first
        if (Distance(pt, Borders[i].Start) < tolerance)
            return (i, 0);
        if (Distance(pt, Borders[i].End) < tolerance)
            return (i, 1);
        
        // Test line segment
        if (DistanceToLine(pt, Borders[i]) < tolerance)
            return (i, -1);
    }
    return (-1, -1);
}
```

---

## Event Flow

### Activation Flow

```
User sets Enable = true
  → RegisterCanvasEvents()
  → FindOrCreateAnnotation()
  → Subscribe to MouseDown/Move/Up
```

### Drawing Flow

```
MouseDown:
  1. SuppressSelectionRectangle = true
  2. Store _drawStart at cursor position
  
MouseMove:
  1. Update _drawEnd
  2. Invalidate canvas for preview
  
MouseUp:
  1. SuppressSelectionRectangle = false
  2. Create border from start to end
  3. Add to annotation with undo record
  4. Expire solution
```

### Selection/Edit Flow

```
MouseDown:
  1. Hit test borders and handles
  2. If hit: set _dragBorderIndex, _dragHandleIndex
  3. Set SelectedBorderIndex
  
MouseMove:
  1. If dragging: update coordinates
  2. Invalidate canvas
  
MouseUp:
  1. Commit changes with undo record
```

### Delete Flow

```
Delete key pressed:
  1. If selected: remove with undo record
  2. Clear selection
  3. Invalidate
```

---

## Key Grasshopper/RhinoCommon Classes

| Class | Purpose |
|-------|---------|
| `GH_Annotation` | Base class for canvas annotations |
| `GH_Component` | Base class for components |
| `GH_ComponentAttributes` | Custom rendering for components |
| `GH_Canvas` | Canvas control |
| `GH_CanvasChannel` | Rendering channels (Wires, Objects, Annotation, Overlay) |
| `GH_UndoRecord` | Undo/redo actions |
| `GH_IWriter` / `GH_IReader` | Serialization |
| `GH_Document` | Document container |

---

## Serialization Details

### Data to Persist

| Field | Type | Key |
|-------|------|-----|
| Borders | List<BorderLine> | "Start{i}", "End{i}" |
| BorderColor | Color | "Color" |
| LineThickness | float | "Thickness" |
| IsLocked | bool | "Locked" |

### Write Pattern

```csharp
public override bool Write(GH_IWriter writer)
{
    writer.SetInt32("Count", Borders.Count);
    for (int i = 0; i < Borders.Count; i++)
    {
        writer.SetPoint3d($"Start{i}", Borders[i].Start);
        writer.SetPoint3d($"End{i}", Borders[i].End);
    }
    writer.SetColor("Color", BorderColor);
    writer.SetFloat("Thickness", LineThickness);
    writer.SetBoolean("Locked", IsLocked);
    return base.Write(writer);
}
```

### Read Pattern

```csharp
public override bool Read(GH_IReader reader)
{
    Borders.Clear();
    int count = reader.GetInt32("Count");
    for (int i = 0; i < count; i++)
    {
        Borders.Add(new BorderLine(
            reader.GetPoint3d($"Start{i}"),
            reader.GetPoint3d($"End{i}")
        ));
    }
    BorderColor = reader.GetColor("Color");
    LineThickness = reader.GetFloat("Thickness");
    IsLocked = reader.GetBoolean("Locked");
    return base.Read(reader);
}
```

---

## Tradeoffs

| Approach | Pros | Cons |
|----------|------|------|
| **GH_Annotation (chosen)** | Native persistence, independent, built-in selection | More complex hit-testing |
| Hidden helper component | Simple, keeps current architecture | Data conceptually tied to component |
| Document user data | Pure document-level | No visual representation |

---

## Implementation Order

1. Create `BorderAnnotation.cs` with data model
2. Add Write/Read serialization
3. Implement Render method
4. Add HitTest method
5. Create `BorderController.cs` component
6. Add canvas event handlers
7. Implement draw mode
8. Add transparent rect suppression
9. Implement selection/move/resize
10. Add undo records
11. Add menu items (clear, color, lock)
12. Test persistence when component deleted

---

## File Structure

```
hopperborder/
├── hopperborder.csproj
├── hopperborderInfo.cs
├── BorderAnnotation.cs      # NEW - annotation class
├── BorderController.cs      # NEW - controller component
└── BorderAnnotationAttributes.cs  # NEW - annotation attributes
```
