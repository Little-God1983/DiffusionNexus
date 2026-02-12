# Image Editor – Architecture Documentation

## Overview

The Image Editor is a layer-based bitmap editor embedded as a tab in the DiffusionNexus UI.
It combines manual editing tools (crop, draw, shapes) with AI-powered operations
(background removal, inpainting, upscaling).

## Architecture

`ImageEditorCore` is the pixel-operation engine. It delegates coordination to
the **EditorServices** graph, which owns the single source of truth for:

- **Layers** ? `LayerManager` owns the `LayerStack` lifecycle
- **Viewport** ? `ViewportManager` owns zoom, pan, and fit-mode state
- **Save/Export** ? `DocumentService` handles file I/O
- **Tools** ? `ToolManager` handles mutual-exclusion activation
- **Events** ? `EventBus` provides decoupled pub/sub between services

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
??? EventBus        ? IEventBus          (shared pub/sub backbone)
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
     ? publishes LayerStackChangedEvent via EventBus
     ? fires LayerManager.LayersChanged
     ? EditorCore.OnLayersCollectionChanged ? fires LayersChanged + ImageChanged
```

### Save Image
```
View ? EditorCore.SaveImage(path)
     ? flattens via LayerManager.Flatten()
     ? delegates file I/O to DocumentService.Save()
     ? DocumentService publishes ImageSavedEvent via EventBus
```

### Zoom In
```
ViewModel ? _services.Viewport.ZoomIn()
         ? ViewportManager updates state, publishes ViewportChangedEvent
         ? ViewModel receives Changed event ? updates ZoomPercentage
ImageEditorControl ? EditorCore.ZoomIn()
                   ? delegates to ViewportManager.ZoomIn()
                   ? same ViewportManager instance ? single source of truth
```

---

## File Inventory

### `ImageEditor/` — Core Types

| File | Description |
|------|-------------|
| `ImageEditorCore.cs` | Main engine: fields, properties, events, load/save, render, crop |
| `ImageEditorCore.Transforms.cs` | Rotate/Flip operations (partial) |
| `ImageEditorCore.ColorAdjustments.cs` | Color balance + brightness/contrast (partial) |
| `ImageEditorCore.BackgroundOps.cs` | Background removal + fill (partial) |
| `ImageEditorCore.Inpainting.cs` | Inpaint mask, stroke, base capture, feathering (partial) |
| `Layer.cs` | Single layer: bitmap, opacity, visibility, blend mode |
| `LayerStack.cs` | Ordered collection of layers |
| `LayerCompositor.cs` | Composites layers to canvas |
| `CropTool.cs` | Crop region management and rendering |
| `DrawingTool.cs` | Freehand drawing tool |
| `ShapeTool.cs` | Shape tool (rectangle, ellipse, arrow, etc.) |
| `TiffExporter.cs` | Multi-page TIFF save/load |

### `ImageEditor/Services/` — Service Layer

| File | Description |
|------|-------------|
| `EditorServiceFactory.cs` | Creates and wires all services; defines `EditorServices` record |
| `IToolManager.cs` / `ToolManager.cs` | Tool activation with mutual exclusion |
| `ToolIds.cs` | String constants for tool identifiers |
| `IViewportManager.cs` / `ViewportManager.cs` | Zoom/pan state — single source of truth |
| `IDocumentService.cs` / `DocumentService.cs` | Save/export file I/O |
| `ILayerManager.cs` / `LayerManager.cs` | Layer stack lifecycle and CRUD |

### `ImageEditor/Events/` — Event Types

| File | Description |
|------|-------------|
| `IEventBus.cs` / `EventBus.cs` | Pub/sub backbone |
| `EditorEvents.cs` | All event record types |

---

## Test Coverage

| Test File | Tests |
|-----------|-------|
| `EditorServiceFactoryTests.cs` | Factory creates all services, services are independent |
| `EventBusTests.cs` | Pub/sub, unsubscribe, multiple subscribers |
| `LayerManagerTests.cs` | Layer CRUD, flatten, merge, mode toggle |
| `ToolManagerTests.cs` | Activation, deactivation, mutual exclusion, callbacks |
| `ViewportManagerTests.cs` | Zoom, pan, fit mode, clamping |
