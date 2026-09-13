# Image Editor � Architecture Documentation

## Overview

The Image Editor is a layer-based bitmap editor embedded as a tab in the DiffusionNexus UI.
It combines manual editing tools (crop, draw, shapes) with AI-powered operations
(background removal, inpainting, upscaling).

Layers have their own bounds: `Layer.OffsetX/OffsetY` place a layer in canvas pixels and its
bitmap may be any size. The compositor draws at the offset and clips to the canvas; Crop clips
layers, Canvas Extend grows them to the union with the canvas, Merge Down unions both layers,
rotate/flip remap offsets (`LayerOffsetRemap`). Content dragged past the canvas edge by the
Move / Transform tool therefore survives. The inpaint mask layer is always canvas-sized at (0, 0).

## Architecture

`ImageEditorCore` is the pixel-operation engine. It delegates coordination to
the **EditorServices** graph, which owns the single source of truth for:

- **Layers** ? `LayerManager` owns the `LayerStack` lifecycle
- **Viewport** ? `ViewportManager` owns zoom, pan, and fit-mode state
- **Save/Export** ? `DocumentService` handles file I/O
- **Tools** ? `ToolManager` handles mutual-exclusion activation

Services communicate via standard C# events (no event bus or mediator).

---

## Component Map

```
???????????????????????????????????????????????????????????????????????
?  ImageEditView.axaml.cs  (View code-behind)                        ?
?  - Wires ViewModel events to EditorCore method calls                ?
?  - Calls SetEditorServices() to share services between VM & Core    ?
?  - Calls EditorCore.ApplyColorBalance, RotateRight, etc.            ?
?  - Calls EditorCore.Layers for SyncLayers after every mutation      ?
???????????????????????????????????????????????????????????????????????
           ?                            ?
           ?                            ?
???????????????????????     ?????????????????????????????????????????
? ImageEditorViewModel ?     ? ImageEditorControl (Avalonia)         ?
? (ViewModel)          ?     ? - Owns ImageEditorCore instance       ?
?                      ?     ? - Handles pointer/keyboard input      ?
? Creates:             ?     ? - Delegates to EditorCore for:        ?
? - EditorServices     ?     ?   rendering, load, crop, draw,        ?
?                      ?     ?   shapes, inpaint stroke              ?
? Uses:                ?     ? - SetEditorServices() injects shared  ?
? - Services.Viewport  ?     ?   services into EditorCore            ?
? - Services.Tools     ?     ???????????????????????????????????????
? - Sub-ViewModels     ?                ?
?   (ColorTools,       ?                ?
?    DrawingTools,     ?     ????????????????????????????????????????
?    BackgroundRemoval,?     ? ImageEditorCore  (partial class)      ?
?    Inpainting, etc.) ?     ?                                      ?
?                      ?     ? Pixel engine. Delegates to services:  ?
? Raises events like   ?     ? - Layer ops ? LayerManager            ?
? RotateLeftRequested  ?     ? - Zoom/Pan ? ViewportManager          ?
? that View subscribes ?     ? - Save I/O ? DocumentService          ?
? to and forwards to   ?     ?                                      ?
? EditorCore           ?     ? Owns: _workingBitmap, _originalBitmap ?
?                      ?     ?       _previewBitmap, CropTool,       ?
???????????????????????     ?       DrawingTool, ShapeTool           ?
                             ?                                      ?
                             ? Files (partial class split):          ?
                             ?  .cs                 (core + wiring) ?
                             ?  .Transforms.cs      (rotate/flip)   ?
                             ?  .ColorAdjustments.cs (color/BC)     ?
                             ?  .BackgroundOps.cs   (bg removal)    ?
                             ?  .Inpainting.cs      (inpaint mask)  ?
                             ????????????????????????????????????????
```

---

## Service Graph

Created via `EditorServiceFactory.Create()`, shared between ViewModel and EditorCore.

```
EditorServices (record)
??? ViewportManager ? IViewportManager   (zoom, pan, fit mode)
??? ToolManager     ? IToolManager       (tool activation / mutual exclusion)
??? DocumentService ? IDocumentService   (save, export, format detection)
??? LayerManager    ? ILayerManager      (layer stack lifecycle, CRUD)
```

### Wiring Flow
1. `ImageEditorViewModel` creates `EditorServices` via factory
2. `ImageEditView.axaml.cs` calls `_imageEditorCanvas.SetEditorServices(vm.Services)`
3. `ImageEditorControl.SetEditorServices()` ? `ImageEditorCore.SetServices()`
4. EditorCore subscribes to `LayerManager.ContentChanged`, `LayerManager.LayersChanged`, `Viewport.Changed`
5. All layer/viewport/save operations flow through services

---

## Data Flow Examples

### Rotate Right
```
User ? RotateRightCommand ? ViewModel raises RotateRightRequested
     ? View handler calls EditorCore.RotateRight()
     ? Transforms.cs gets active layer bitmap from _layers (via LayerManager.Stack)
     ? Applies rotation ? fires ImageChanged ? re-render
```

### Add Layer
```
View ? EditorCore.AddLayer("New Layer")
     ? delegates to LayerManager.AddLayer()
     ? LayerManager creates layer on its Stack
     ? fires LayerManager.LayersChanged
     ? EditorCore.OnLayersCollectionChanged ? fires LayersChanged + ImageChanged
```

### Save Image
```
View ? EditorCore.SaveImage(path)
     ? flattens via LayerManager.Flatten()
     ? delegates file I/O to DocumentService.Save()
```

### Zoom In
```
ViewModel ? _services.Viewport.ZoomIn()
         ? ViewportManager updates state, fires Changed event
         ? ViewModel receives Changed event ? updates ZoomPercentage
ImageEditorControl ? EditorCore.ZoomIn()
                   ? delegates to ViewportManager.ZoomIn()
                   ? same ViewportManager instance ? single source of truth
```

### Canvas Extend
```
User → Extend toggle → CanvasExtendViewModel.IsPanelOpen = true
     → View sets ImageEditorControl.IsCanvasExtendToolActive → CanvasExtendTool.IsActive
     → handle drag / image drag (moves the image inside the frame) / SetTargetSize / SetAspectRatio / SetAnchor
       → RegionChanged → View → ViewModel.UpdateResolution + UpdateAnchor (grid cell, none when dragged)
     → RenderWithZoom fit includes the frame + FitMargin while any extension tool is active
     → Apply (button / Enter) → ImageEditorCore.ApplyCanvasExtend()
     → LayerManager.ResizeCanvas + working bitmap grown, transparent new pixels → ImageChanged
```

### Move / Transform
```
User → Move toggle → LayerTransformViewModel.IsPanelOpen = true
     → View sets ImageEditorControl.IsLayerTransformToolActive → LayerTransformTool.IsActive, core ArmLayerTransform()
     → drag body / corner / edge / rotate handle, arrows, panel fields → tool state (translation, scale, rotation, flips)
       → TransformChanged → View → ViewModel.UpdateFromTool (X/Y/W/H/rotation)
     → RenderWithZoom passes LayerRenderOverride(layer, tool.Matrix) to the compositor (live preview);
       the tool draws the off-canvas part at 50 % plus handles
     → Apply (button / Enter) → ImageEditorCore.ApplyLayerTransform()
     → one resample into a bitmap sized to the transformed bounds → Layer.AdoptBitmap(bitmap, offset) → ImageChanged
     → Escape resets (tool stays open); switching tool or layer commits first
```

---

## File Inventory

### `ImageEditor/` � Core Types

| File | Description |
|------|-------------|
| `ImageEditorCore.cs` | Main engine: fields, properties, events, load/save, render, crop |
| `ImageEditorCore.Transforms.cs` | Rotate/Flip operations (partial) |
| `ImageEditorCore.ColorAdjustments.cs` | Color balance + brightness/contrast (partial) |
| `ImageEditorCore.BackgroundOps.cs` | Background removal + fill (partial) |
| `ImageEditorCore.Inpainting.cs` | Inpaint mask, stroke, base capture, feathering (partial) |
| `ImageEditorCore.LayerTransform.cs` | Arm / eligibility / `ApplyLayerTransform` rasterization with size guard (partial) |
| `Layer.cs` | Single layer: bitmap, opacity, visibility, blend mode |
| `LayerStack.cs` | Ordered collection of layers |
| `LayerCompositor.cs` | Composites layers to canvas |
| `LayerOffsetRemap.cs` | Where a layer's offset lands after a whole-image rotate/flip |
| `TransparencyCheckerboard.cs` | See-through checkerboard painted under the image in `RenderWithZoom` so transparent pixels stay visible |
| `CropTool.cs` | Crop region management and rendering |
| `CanvasExtensionTool.cs` | Abstract base for tools that grow the canvas outward: extension state, outward-only drag math, aspect/target-size presets, `ShrinkAttempted` |
| `OutpaintTool.cs` | Outpaint frame (arrow handles, AI severity tint) on top of `CanvasExtensionTool` |
| `CanvasExtendTool.cs` | Canvas Extend frame (round handles on the frame, checkerboard preview of the transparent new area) |
| `DrawingTool.cs` | Freehand drawing tool |
| `ShapeTool.cs` | Shape tool (rectangle, ellipse, arrow, etc.) |
| `LayerTransformTool.cs` | Move / Transform tool: Shape-style handles on the active layer, canvas-space matrix, commit-on-deactivate |
| `TiffExporter.cs` | Multi-page TIFF save/load |

### `ImageEditor/Services/` � Service Layer

| File | Description |
|------|-------------|
| `EditorServiceFactory.cs` | Creates and wires all services; defines `EditorServices` record |
| `IToolManager.cs` / `ToolManager.cs` | Tool activation with mutual exclusion |
| `ToolIds.cs` | String constants for tool identifiers |
| `IViewportManager.cs` / `ViewportManager.cs` | Zoom/pan state � single source of truth |
| `IDocumentService.cs` / `DocumentService.cs` | Save/export file I/O |
| `ILayerManager.cs` / `LayerManager.cs` | Layer stack lifecycle and CRUD |

---

## Test Coverage

| Test File | Tests |
|-----------|-------|
| `EditorServiceFactoryTests.cs` | Factory creates all services, services are independent |
| `LayerManagerTests.cs` | Layer CRUD, flatten, merge, mode toggle |
| `ToolManagerTests.cs` | Activation, deactivation, mutual exclusion, callbacks |
| `ViewportManagerTests.cs` | Zoom, pan, fit mode, clamping |
| `LayerBoundsTests.cs` | Layer offset/size bounds math |
| `LayerCompositorOffsetTests.cs` | Compositing with offset, out-of-canvas, and clipped layers |
| `LayerStackOffsetTests.cs` | Layer stack offset propagation through flatten/merge |
| `TiffExporterOffsetTests.cs` | Multi-page TIFF save/load round-trips layer offsets |
| `LayerTransformToolTests.cs` | Handle hit testing, drag math, matrix composition, commit-on-deactivate |
| `ImageEditorCoreLayerTransformTests.cs` | Arm/eligibility guards and `ApplyLayerTransform` rasterization |
| `ImageEditorCoreOffsetLayerTests.cs` | ApplyStroke / ApplyShape land in layer-local pixels on an offset layer; painting outside the layer draws nothing |
| `LayerTransformViewModelTests.cs` | Panel state, UpdateFromTool, command wiring |
