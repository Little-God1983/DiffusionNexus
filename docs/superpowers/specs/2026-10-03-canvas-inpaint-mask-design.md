# Diffusion Canvas inpaint mask — design (#595, #518 region D2)

Issue: #595, sub-issue of #518. One PR, branch `feature/canvas-inpaint-mask`. Builds on the layer stack
(#594, PR #601, merged).

## Goal

Paint where to repaint on the canvas, inside the bounding box, and have a backend honour it. Both
backends declared `BackendFeature.Inpainting` unsupported, each telling the user to switch to the
other; after this slice both honour the mask.

## Decisions taken with the owner

| Question | Decision |
|---|---|
| Which backend gets an inpainting path? | **Both**: local sd.cpp (`WithMaskImage`) and the engine (`SetLatentNoiseMask`). |
| How many mask layers? | **One**, like the Image Editor. `+ Mask` creates it once, then selects it. |
| How is the mask stored? | **World-space strokes**, rasterised to the box's region only at Generate. |

## What the user gets

- **`+ Mask`** (the tool strip's formerly disabled Mask button) creates the inpaint mask layer and
  selects it, or selects it when it exists.
  - The mask row is always at the **top** of the layer list: it is an instruction to the model, not
    pixels in the picture. ↑/↓ are disabled for it and a raster cannot move above it.
  - Hide, lock, rename and delete work as for rasters. A hidden mask is neither drawn nor sent. A
    locked mask survives Clear canvas; an unlocked one is cleared with the rest.
  - The row shows no opacity and no thumbnail.
- **Brush** and **Eraser** toggles in the tool strip, enabled while the mask layer is selected and
  visible.
  - Left-drag on the canvas paints or erases. While a tool is active, a left press never grabs the
    box, so the box cannot be dragged by accident; space-pan, middle-pan, wheel zoom and right-click
    still work.
  - Brush size is in **world pixels** (the generated image's pixels), 4–512, shown as a circle cursor
    at its true on-screen size. `[` and `]` shrink and grow it.
  - Escape leaves the tool. Selecting another layer, hiding or deleting the mask leaves it too.
  - The repaint area draws as a translucent red overlay above the rasters and below the box. With
    Invert on, the overlay shows the inverted area, so the screen always shows what will be repainted.
- **Inspector** for the mask:
  - **Feather** 0–64 px: dilate-then-blur on the painted edges (the Image Editor's feather, now shared).
  - **Invert**: repaint everything inside the box except what is painted.
  - **Denoise** 0.05–1.00, default 0.75: replaces the panel's Denoise whenever the mask takes part in a
    run. The panel's Denoise line says so while that is the case.
  - **Clear mask** removes every stroke.
  - Lock checkbox, as for rasters.
- **When the mask takes part in a run** (all must hold): the mask is visible, it has strokes, the box
  is over existing pixels (an image-to-image run), and the painted area meets the box (always true when
  inverted). The run sends the box's composite plus the mask, and only the masked area changes.
- **The region readout** (badge + tooltip) says which mode will run and why:
  - `Inpaint` — "Inpaint — the mask meets the box over 1 result".
  - When a mask exists but is left out, the image-to-image or text-to-image text gains the reason:
    hidden, empty, outside the box, or "nothing under the box to keep".
- Generate **refuses** (status line + console warning) when the readout promised inpaint but the
  rasterised mask comes out empty inside the box (everything erased there). Running without it would
  repaint the whole box, which is not what the screen promised.

## Components

### `CanvasMaskStroke` (new, `DiffusionCanvas/`)

Immutable record: `IReadOnlyList<Point> Points` (world), `double Size` (world px diameter),
`bool IsErase`. `Bounds` = points' bounding box inflated by half the size.

### `CanvasMaskRasterizer` (new, `DiffusionCanvas/`, pure)

`Rasterize(strokes, region, width, height, feather, invert)` → an `SKBitmap` (white = repaint, black =
keep) plus the fraction of the region marked for repaint.

1. Render the strokes into an alpha surface covering the region plus a margin of the feather's reach
   (so a stroke just outside the box feathers in correctly), brush opaque, eraser `Clear`.
2. Feather with the shared helper.
3. Crop to the region, invert if asked, convert alpha to an opaque grey PNG-ready bitmap.

Also `EncodePng`. No Avalonia types beyond `Rect`/`Point`, so it is unit-testable.

### `MaskFeathering` (new, `ImageEditor/`, extracted)

The Image Editor's private `FeatherMask` (dilate by half the radius, blur by the radius) becomes a
public static helper. The editor calls it unchanged; the canvas rasteriser reuses it. Listed in
`REUSABLES.md`.

### `InpaintMaskLayerViewModel` (new, `ViewModels/DiffusionCanvas/`)

Implements `ILayerStackItem` (`Name`, `IsVisible`, `IsLocked`; `OpacityText` empty, `Thumbnail` null).
Holds the strokes, `Feather`, `Invert`, `Denoise` (clamped), `AddStroke`, `Clear`, a `Revision` that
bumps on every stroke change (the surface invalidates on it), `HasStrokes` and `WorldBounds`.
`CanvasLayerKind` gains `InpaintMask`.

### `CanvasLayerStackViewModel` (changed)

- `DisplayLayers` becomes `ObservableCollection<ILayerStackItem>`; the mask row, when present, is
  index 0 and the rasters follow top-first. `SelectedLayer` becomes `ILayerStackItem?`, with typed
  `SelectedRaster` / `SelectedMask` for the inspector and the surface.
- `Mask`, `AddMask()`, and delete/clear/lock rules covering the mask. ↑/↓ apply to rasters only.
- `LayersChanged` also fires on the mask's visibility, strokes, invert and feather changes.
- Every operation traces (standing rule): mask added, selected, painted (one line per stroke with its
  size), erased, cleared, hidden/shown, inverted, deleted, kept by Clear canvas.

### `DiffusionCanvasSurface` (changed)

New styled properties `Mask` (the layer), `PaintTool` (`None`/`Brush`/`Eraser`), `BrushSize`. Draws
the overlay through a Skia custom draw operation over an immutable stroke snapshot (no bitmaps cross
to the render thread). A paint gesture captures the pointer, collects world points, and on release
hands one finished stroke to the mask, so one drag is one stroke and one trace line. Capture loss
commits what was drawn.

### `DiffusionCanvasViewModel` (changed)

- `AddMaskCommand` replaces the `ActivateMaskTool` placeholder; `ActivateBrushTool` /
  `ActivateEraserTool` placeholders become real toggles (`PaintTool`), plus `BrushSize`.
- The readout adds the inpaint mode and the reasons above.
- Generate: when the mask takes part, rasterise it off the UI thread from a snapshot to a second
  scratch PNG, send it as `MaskImage` with the init image's strength set to the mask's Denoise, and
  trace the masked percentage, feather, invert and denoise. The scratch file is deleted with the
  region's.

### Backends

- **Local (`StableDiffusionCppBackend`)**: `WithMaskImage` when both an init image and a mask are
  present. sd.cpp does not encode the init image at strength 1.0, which would leave nothing to keep
  outside the mask, so a masked run caps the strength at 0.99. `Inpainting` leaves `LocalCapabilities`.
  A mask without an init image is refused as data, not silently dropped.
- **Engine (`ManagedComfyUiBackend` + `Krea2WorkflowPatcher`)**: upload the mask like the init image
  (its own upload cache), then inject `LoadImage` (mask) → `ImageToMask` (red) → `SetLatentNoiseMask`
  between the injected `VAEEncode` and the sampler. All core ComfyUI nodes; `RequiredCustomNodeTypes`
  is unchanged. A missing mask file fails the run instead of running unmasked. `Inpainting` leaves
  `EngineCapabilities`.
- `DiffusionRequest.MaskImage` docs say both backends honour it, and only together with `InitImage`.

## Keyboard

- `[` / `]` resize the brush while a paint tool is active (canvas key handler, same per-key exemption
  rules; a focused TextBox still gets them).
- Escape leaves the paint tool (after cancelling any box gesture, as today).

## Testing

- Unit: rasteriser (brush, eraser, feather, invert, region offset, margin, empty), mask layer VM
  (clamps, revision, bounds), stack VM (mask row placement, selection, delete/clear/lock, ↑/↓ rules,
  LayersChanged), canvas VM (readout modes and reasons, Generate sends `MaskImage` with the mask's
  denoise, refuses an empty-in-box mask, capabilities), Krea patcher (mask nodes and wiring, collision
  guard), the feather extraction (editor behaviour unchanged).
- Integration (headless): the surface turns a drag into exactly one stroke, and does not move the box
  while a tool is active.
- GUI smoke in the app: add mask, paint, erase, invert, readout, hide, delete. A real masked
  generation needs a GPU run per backend.

## Out of scope

Automatic outpaint masking of the uncovered part of a partly covered box (sd.cpp cannot run a masked
area at denoise 1.0, which outpainting needs); undo; pressure; mask from selection; several masks.
