# Layer Move / Transform tool — design

**Feature:** a Move / Transform tool in the Image Editor: move, scale, rotate and flip the active layer on the canvas, GIMP-style, with layers that keep content outside the canvas
**Issue:** https://github.com/Little-God1983/DiffusionNexus/issues/568
**Date:** 2026-09-13
**Status:** approved direction (2026-09-13: Move + Scale + Rotate; layers keep off-canvas content; layer offset + own size), spec ready for planning

## The problem

Every layer in the editor is a bitmap the exact size of the canvas, drawn at (0, 0). Once
a layer exists there is no way to move its content: a subject cut out by background removal,
a pasted or duplicated layer, or a text layer that landed in the wrong place can only be
redone. There is no scale or free rotation either; the toolbar rotates the whole image in
90° steps. The issue asks for a move tool and, ideally, GIMP's unified transform.

The tool should feel like the editor's existing placed-object tools: one click selects the
active layer, a bounding box with round handles appears, dragging the body moves it, the
corners scale it, a handle above rotates it, Enter commits and Escape resets. Content
dragged past the canvas edge must not be lost: the user chose GIMP's model, where a layer
has its own position and size and only the canvas clips what is shown.

## What already exists

- `ImageEditor/ShapeTool.cs` and `ImageEditor/TextTool.cs` — a placed object with four
  round corner handles (radius 6, hit radius 12), a rotation handle 30 px above the box,
  body drag to move, Ctrl to constrain proportions, Enter commits, Escape cancels, and
  commit-on-deactivate so a placed object is never lost silently. Their hit test rotates the
  pointer into the box's local space before testing handles; the transform tool copies this
  vocabulary and geometry.
- `ImageEditor/CanvasExtendTool.cs` + `ViewModels/CanvasExtendViewModel.cs` + the
  `WireCanvasExtendEvents` block in `Views/Tabs/ImageEditView.axaml.cs` — the current
  template for a tool with an inline right-column panel: panel state in a sub-view-model that
  owns no pixels, request events the view forwards to the tool, result callbacks the view
  pushes back, spinner-less `NumericUpDown` fields, a status-bar hint on open, and an amber
  hint box for a refused action.
- `ImageEditor/LayerCompositor.cs`, `LayerStack.cs`, `Layer.cs` — the layer model. Every
  draw of a layer is `DrawBitmap(layer.Bitmap, 0, 0)`; every operation that changes the
  canvas (`CropAll`, `ResizeCanvas`, `TransformAll`) reallocates each layer to the canvas
  size. `LayerStack.ResizeCanvas` already allocates every replacement bitmap before swapping
  any of them in, so a failure is a no-op; the same all-or-nothing rule applies here.
- `ImageEditorCore._bitmapLock` — every bitmap the Avalonia render thread can reach is
  swapped under this lock and the replaced bitmap is disposed after the lock is released.
- `ImageEditor/TiffExporter.cs` — layered TIFF with a `key=value|key=value` description per
  page (`LayerName`, `Opacity`, `BlendMode`, `Visible`, `Index`). The loader sizes the stack
  from the first page.

## Decisions

| Question | Decision |
|---|---|
| Which transforms? | **Move + Scale + Rotate + Flip.** Body drag moves, corner and edge handles scale, a handle above rotates, Flip H / Flip V buttons in the panel. Shear and perspective are out of scope. |
| Content outside the canvas? | **Kept.** A layer gains `OffsetX`/`OffsetY` (canvas pixels) and its bitmap may be any size. The canvas clips what is drawn; the layer's pixels survive until Crop or a merge into a canvas-sized result discards them. |
| Persistent matrix or rasterize on commit? | **Rasterize on commit.** The tool holds a translation, scale, rotation and flip flags and never touches pixels while dragging. Enter resamples the untouched source **once** into a new bitmap sized to the transformed bounds. Re-opening the tool on the same layer resamples again from the committed pixels, as GIMP does. Rejected: a persistent per-layer matrix (every pixel tool would have to bake or invert it). |
| Handle set | Four **corner** handles (scale about the opposite corner), four **edge** handles (scale one axis about the opposite edge), one **rotate** handle above the top edge, body drag to move. Round handles: radius 6, hit radius 12, rotate handle 30 px above the box — the Shape tool's numbers, copied by value. Edge handles have a 6 px hit radius (implementation decision, see §3). |
| Keep aspect | A **Keep aspect** toggle in the panel, default **on**. Holding Ctrl during a corner drag inverts it for that drag (Shape's Ctrl convention). Edge handles always scale one axis. |
| Rotation snapping | Shift while rotating snaps to 15°. |
| Keyboard nudge | Arrow keys move 1 canvas px, Shift + arrows 10 px, while the tool is active and the canvas has focus. |
| Enter / Escape | **Enter applies. Escape resets the transform and keeps the tool open** (Extend and Crop precedent). Cancel discards the pending transform and closes (Extend precedent); switching tool or layer still commits. |
| Switching tool or layer with a pending transform | **Commit first** (Shape and Text precedent: a deliberate move is never lost silently). Selecting another layer in the Layers panel commits the pending transform, then arms the new layer. |
| Ineligible layer | The inpaint mask layer and locked layers cannot be transformed. The panel shows an amber hint ("This layer can't be moved. Select another layer.") and the canvas ignores drags. |
| Not in layer mode | Opening the tool enables layer mode (Inpaint precedent). |
| Off-canvas preview | While the tool is active the part of the transformed layer outside the canvas is drawn at 50 % opacity on top of the editor background. With the tool closed, off-canvas content is hidden (GIMP default). |
| Viewport | Untouched. No fit change, no auto zoom-out. |
| Resampling | Pure integer translations copy pixels exactly. Anything else resamples once with `SKSamplingOptions(SKCubicResampler.Mitchell)` (SkiaSharp 3.119.4; `SKFilterQuality` is obsolete there). |
| Size guard | A transformed bitmap wider or taller than 16 384 px, or larger than 256 M pixels, is refused with an amber hint; the layer is untouched. |
| Where does Crop leave a moved layer? | Crop clips every layer to the new canvas: bitmap = intersection of layer bounds and crop rect, offset relative to the new canvas. A layer wholly outside becomes a 1×1 transparent bitmap at (0, 0) so it stays valid. |
| Canvas Extend on a moved layer? | Each layer grows to the **union** of its bounds and the new canvas, so drawing on the new area of the background keeps working as today. |
| Merge Down | The result is the **union** of both layers' bounds, so off-canvas content survives a merge. Flatten, Merge Visible and export produce canvas-sized results and clip. |
| Rotate / flip whole image | Each layer's bitmap is transformed as today and its offset is remapped around the canvas (formulas below). |
| Layers created from the active layer's pixels | Background removal's Subject and Background layers inherit the active layer's offset and size. Text still creates a canvas-sized layer at (0, 0). |
| Toolbar and panel | A **"Move"** toggle right after "Crop". Panel in the right column with the Crop/Extend layout: title, dark card, hint line, thin dividers, Cancel + green (#2D7D46) Apply. No new reusable control; nothing added to `REUSABLES.md`. |
| Undo | None (editor-wide). Escape before Enter is the recovery path. |

## Design

### 1. Layer bounds (`ImageEditor/Layer.cs`)

New members:

- `int OffsetX`, `int OffsetY` — the layer's top-left in canvas pixels. Default 0.
- `SKRectI Bounds => new(OffsetX, OffsetY, OffsetX + Width, OffsetY + Height)`.
- `Layer(SKBitmap source, string name, SKPointI offset)` constructor overload.
- `internal void AdoptBitmap(SKBitmap bitmap, SKPointI offset)` — swaps bitmap and offset
  together, updates the thumbnail, raises `ContentChanged`. The existing
  `AdoptBitmap(SKBitmap)` keeps the current offset.
- `internal void SetOffset(int x, int y)` — raises `PropertyChanged("Offset")` and
  `ContentChanged` so the compositor redraws.
- `Clone()` copies the offset. `ReplaceBitmap` keeps the offset (colour tools replace
  same-size bitmaps).

Invariants: the inpaint mask layer is always canvas-sized at (0, 0). Thumbnails stay
layer-local (they show the layer's own bitmap).

### 2. Offset-aware sites

Every site is mechanical; the rule is "draw at the offset, clip to the canvas, or translate
canvas coordinates into layer coordinates".

| Site | Change |
|---|---|
| `LayerCompositor.CompositeToCanvas` | `ClipRect(0, 0, layers.Width, layers.Height)` around the loop; `DrawBitmap(layer.Bitmap, layer.OffsetX, layer.OffsetY, paint)`. Accepts an optional `LayerRenderOverride? previewOverride` (see §4). |
| `LayerCompositor.RenderLayer`, `CreateFlattenedBitmap`, `ExportLayersAsBitmaps` | Draw at the offset / return the offset alongside the bitmap. |
| `LayerStack.Flatten` | Canvas-sized result; draw each layer at its offset. |
| `LayerStack.MergeDown` | New bitmap = union of both bounds; draw the lower layer at its delta, the upper at its delta with opacity and blend; `belowLayer.AdoptBitmap(union, unionTopLeft)`. |
| `LayerStack.MergeVisible` | Unchanged apart from `Flatten` (canvas-sized "Merged" layer at (0, 0)). |
| `LayerStack.CropAll` → `Layer.Crop` | Intersect bounds with the crop rect; copy that region; new offset = intersection.TopLeft − crop.TopLeft; empty intersection → 1×1 transparent at (0, 0). |
| `LayerStack.ResizeCanvas` → `Layer.CreateResizedBitmap` | Shift the offset by (offsetX, offsetY), then grow to the union with the new canvas. Two-phase allocation stays. The mask layer stays canvas-sized. |
| `LayerStack.TransformAll` | Takes `Func<Layer, (SKBitmap Bitmap, SKPointI Offset)?>`; the five callers in `ImageEditorCore.Transforms.cs` remap the offset (formulas below). Stack size still updates from the canvas, not from the first layer. |
| `ImageEditorCore.ApplyShape`, `ApplyStroke`, `ApplyInpaintStroke` | Normalised coordinates are scaled by the **canvas** size (`_layers.Width/Height`), not the target bitmap's; the canvas gets `Translate(-OffsetX, -OffsetY)` before drawing. Painting outside the layer's bounds draws nothing (GIMP rule). The mask layer is canvas-sized, so the inpaint path is unchanged in effect. |
| `ImageEditorCore.ApplyText` | Unchanged: creates a canvas-sized layer at (0, 0). |
| `ImageEditorCore.BackgroundOps` | Subject and Background layers are created with the active layer's offset (`AddLayerFromBitmap(bitmap, name, offset)`). |
| `ImageEditorCore.ColorAdjustments`, brightness/contrast | Unchanged: they replace the active layer's bitmap with one of the same size; offset survives. |
| `ILayerManager` / `LayerManager` / `LayerStack.AddLayerFromBitmap` | New optional `SKPointI offset` parameter. New `EnableLayerMode(int canvasWidth, int canvasHeight)` overload that creates an empty stack, for TIFF import. |
| `ImageEditorCore` TIFF import (`LoadLayeredTiff` consumer) | Uses the empty-stack overload plus `AddLayerFromBitmap(bitmap, name, offset)` so a moved layer is not re-centred. |
| `TiffExporter` | Writes `OffsetX`, `OffsetY`, `CanvasWidth`, `CanvasHeight` into the page description. Loader: stack size from the first page's `CanvasWidth/Height` when present, else the first page's size (legacy files load exactly as before); each page becomes a layer at its offset, pages may differ in size. |
| `Layer.UpdateThumbnail` | Unchanged. |

Offset remap for whole-image rotate/flip, canvas `W × H`, layer bounds `(x, y, w, h)`:

| Operation | New canvas | New offset |
|---|---|---|
| Rotate right (90° CW) | `H × W` | `(H − (y + h), x)` |
| Rotate left (90° CCW) | `H × W` | `(y, W − (x + w))` |
| Rotate 180° | `W × H` | `(W − (x + w), H − (y + h))` |
| Flip horizontal | `W × H` | `(W − (x + w), y)` |
| Flip vertical | `W × H` | `(x, H − (y + h))` |

### 3. `LayerTransformTool` (new, `ImageEditor/LayerTransformTool.cs`)

Platform-independent, SkiaSharp only, same shape as `ShapeTool`.

**Lifecycle.** `IsActive` (setting false commits a pending transform through
`CommitRequested`, then disarms). `Arm(Layer layer)` captures the layer and its current
`Bounds` as the **source bounds** and resets the transform. `Disarm()`. `IsArmed`.

**Screen mapping.** `SetImageBounds(SKRect imageRect)`, `ImagePixelWidth`,
`ImagePixelHeight` — identical to the other tools; `Scale = imageRect.Width / ImagePixelWidth`
converts canvas px ↔ screen px.

**State.** `Translation` (SKPoint, canvas px), `ScaleX`, `ScaleY` (sign carries flips),
`RotationDegrees`, `KeepAspect` (default true). Pivot = centre of the source bounds.

```
Matrix = Translate(cx + tx, cy + ty) · Rotate(RotationDegrees) · Scale(ScaleX, ScaleY) · Translate(-cx, -cy)
```

`Matrix` is in canvas coordinates. `TransformedBounds = Matrix.MapRect(sourceBounds)`.
`HasTransform` is false when the matrix is the identity within 1e-3 (scale/rotation) and
0.5 px (translation).

**Handles.** `enum TransformHandle { None, Body, TopLeft, Top, TopRight, Right, BottomRight,
Bottom, BottomLeft, Left, Rotate }`. `HitTest(SKPoint screenPoint)` maps the point into the
box's local (unrotated) space around the transformed centre and tests corners, then edges,
then the rotate handle, then the body — the Shape tool's order. Hit testing is
nearest-handle-wins; the four straight-edge handles use a 6 px hit radius (corners 12 px,
rotate 18 px) so a small layer's body stays draggable — decided during implementation
(Task 6). Constants: `HandleRadius 6`, `HandleHitRadius 12`, `RotateHandleOffset 30`.

**Gestures.** `OnPointerPressed/Moved/Released(SKPoint)` return whether they consumed the
event. Body drag adds `screenDelta / Scale` to `Translation`. Corner drag scales about the
opposite corner; with `KeepAspect XOR Ctrl` the uniform factor is the larger of the two axis
factors projected on the drag. Edge drag scales one axis about the opposite edge. Rotate drag
sets `RotationDegrees` from the angle centre→pointer minus the angle at press; `SnapRotation`
(Shift) rounds to 15°. Modifier flags are properties the control sets from the key state
(`ConstrainProportionsOverride`, `SnapRotation`), as `ShapeTool.ConstrainProportions` is.

**Panel setters.** `SetPosition(float x, float y)` (top-left of `TransformedBounds` →
adjusts `Translation`), `SetSize(float w, float h)` (→ scale magnitudes, honouring
`KeepAspect`), `SetRotation(float degrees)`, `FlipHorizontal()`, `FlipVertical()` (negate one
scale about the centre), `Nudge(int dx, int dy)`, `Reset()`.

**Events.** `TransformChanged` after every state change; `CommitRequested` from `Commit()`,
which returns false and raises nothing when `!HasTransform`.

**Render(canvas, canvasBounds).** When armed: (1) the off-canvas part of the preview — the
source bitmap drawn through `Matrix` and the screen mapping at 50 % opacity, clipped to the
region **outside** `imageRect` (`ClipRect(imageRect, SKClipOperation.Difference)`); (2) a
dashed 1.5 px box around the transformed bounds, rotated; (3) the eight handles and the rotate
handle with its connector line, drawn like the Shape tool's; (4) a size / angle label under
the box in the Extend tool's label style ("1024 × 768 · 12°"). The in-canvas part of the
preview is **not** drawn by the tool; the compositor draws it (§4) so opacity and blend mode
are honoured.

`GetCursorForPoint(SKPoint)` returns the `TransformHandle` for the control's cursor map.

### 4. Live preview through the compositor

```csharp
public readonly record struct LayerRenderOverride(Layer Layer, SKMatrix Matrix);
```

`LayerCompositor.CompositeToCanvas(canvas, layers, destRect, LayerRenderOverride? previewOverride = null)`.
For the overridden layer: `Save(); Concat(Matrix); DrawBitmap(bitmap, OffsetX, OffsetY,
paint); Restore()` — still inside the canvas clip. `ImageEditorCore.RenderWithZoom` passes
`new LayerRenderOverride(tool.Layer, tool.Matrix)` when `LayerTransformTool.IsActive &&
IsArmed && HasTransform`, else null. Other layers are unaffected; nothing about the layer
itself changes until commit.

### 5. `ImageEditorCore` additions

- `public LayerTransformTool LayerTransformTool { get; } = new();` plus the usual
  `SetImageBounds / ImagePixelWidth / ImagePixelHeight / Render` block in `RenderWithZoom`.
- `public enum LayerTransformEligibility { Ok, NoLayer, InpaintMask, Locked }` and
  `public LayerTransformEligibility ArmLayerTransform()` — enables layer mode if needed, checks
  the active layer, arms the tool or disarms it and returns the reason.
- The `ActiveLayer` setter: when the tool is active, commit a pending transform (through
  the tool's `Commit()`), then arm the new layer.
- `public bool ApplyLayerTransform()` — the pixel operation, all-or-nothing:
  1. `bounds = tool.Matrix.MapRect(layer.Bounds)`, rounded outward to `SKRectI`.
  2. Size guard: width or height > 16 384, or area > 256 M px → raise
     `LayerTransformFailed(LayerTransformFailure.TooLarge)`, return false, layer untouched.
  3. **Pure integer translation** (rotation 0, |scale| 1, translation within 1e-3 of an
     integer, no flips): `newBitmap = source.Copy()`, `offset += translation`. Exact.
  4. Otherwise: allocate `bounds.Size` (throw on an empty allocation as
     `CreateResizedBitmap` does); `Translate(-bounds.Left, -bounds.Top); Concat(Matrix);
     DrawBitmap(source, layer.OffsetX, layer.OffsetY, highQualityPaint)`.
  5. `lock (_bitmapLock) layer.AdoptBitmap(newBitmap, bounds.Location)`; the replaced bitmap
     is disposed after the lock (render-thread rule). `OnImageChanged()`. The tool is re-armed
     on the same layer with an identity transform so the user can continue.
  6. Any exception → `LayerTransformFailed(LayerTransformFailure.Allocation)`, false, layer
     untouched.
- Events: `LayerTransformApplied`, `LayerTransformFailed(reason)`.

### 6. Panel, view model and wiring — matched to Crop and Extend

**`ToolIds.LayerTransform = "LayerTransform"`.**

**`ViewModels/LayerTransformViewModel.cs`** (mirrors `CanvasExtendViewModel`; owns no
pixels):

- State: `IsPanelOpen`, `LayerName`, `X`, `Y`, `Width`, `Height` (int, canvas px),
  `RotationDegrees` (float, one decimal), `KeepAspect`, `HasTransform`, `HintText`,
  `IsHintVisible`. A `_syncing` guard stops field edits echoing back while the tool updates
  the fields.
- Commands: `ToggleCommand`, `CancelCommand` (raises `ResetRequested` to discard the pending
  transform, then closes the panel — Reset first so the deactivate-on-close commit is a no-op),
  `ResetCommand`, `ApplyCommand` (`HasTransform`), `FlipHorizontalCommand`,
  `FlipVerticalCommand`.
- Request events the view forwards to the tool: `ToolActivated`, `ToolDeactivated`,
  `PositionRequested(x, y)`, `SizeRequested(w, h)`, `RotationRequested(deg)`,
  `KeepAspectChanged(bool)`, `FlipRequested(horizontal)`, `ResetRequested`, `ApplyRequested`.
- Result callbacks: `UpdateFromTool(layerName, x, y, w, h, rotation, hasTransform)`,
  `OnIneligible(LayerTransformEligibility)`, `OnApplied()`, `OnApplyFailed(reason)`.
- Standard plumbing shared with the other sub-view-models: `ToolToggled`, `ToolStateChanged`,
  `StatusMessageChanged`, `RefreshCommandStates()`, `ClosePanel()`; opening calls
  `DeactivateOtherTools(ToolIds.LayerTransform)`.
- Status message on open: "Move: Drag the layer to move it, use the handles to scale or
  rotate. Enter applies, Escape resets." Working steps go to the Unified Console at
  info/debug through `IUnifiedLogger` with source `LayerTransform` (standing rule).

**`ImageEditorViewModel`**: `LayerTransform` property constructed like `CanvasExtend`; the
`DeactivateOtherTools`, close-all and `RefreshCommandStates` branches; `ToolToggled` →
`_services.Tools.Activate/Deactivate`.

**`ImageEditView.axaml`**: toggle after Crop —
`<ToggleButton Content="Move" IsChecked="{Binding ImageEditor.LayerTransform.IsPanelOpen, Mode=TwoWay}" Padding="12,6" ToolTip.Tip="Move / Transform - Move, scale, rotate or flip the selected layer"/>`.
Panel after the Crop panel, same skeleton as the Extend panel:

```
Move / Transform                                (title, SemiBold 14)
┌ card #2A2A2A ─────────────────────────────────┐
│ Drag the layer to move it. Handles scale,     │  hint 11 / 0.7
│ the top handle rotates.                       │
│ Layer: Background                             │  12 SemiBold, #4CAF50
│ ─────────────                                 │
│ Position   X [   120]  Y [    64]             │  spinner-less NumericUpDown
│ Size       W [  1024]  H [   768]  [Keep aspect ☑] │  W/H show the unrotated box, not TransformedBounds
│ Rotation   [  12.0]°                          │
│ [Flip H] [Flip V]                             │  small buttons, centred
│ ─────────────                                 │
│ ⚠ This layer can't be moved. Select another   │  amber box, Extend style, IsHintVisible
│ ─────────────                                 │
│ [ Cancel ]  [ Reset ]  [ Apply ]              │  Apply #2D7D46
└───────────────────────────────────────────────┘
```

**`ImageEditView.axaml.cs`**: `WireLayerTransformEvents(imageEditor)` mirroring
`WireCanvasExtendEvents`: activation sets `IsLayerTransformToolActive`, asks the core to arm
and reports ineligibility; the request events call the tool's setters; the control's
`LayerTransformChanged / Applied / Failed` push back into the view model.

**`Controls/ImageEditorControl.cs`**:

- `IsLayerTransformToolActive` (plain property like `IsCanvasExtendToolActive`) sets
  `_editorCore.LayerTransformTool.IsActive`.
- Pointer routing block after the Canvas Extend block, before Crop's fallback; sets
  `ConstrainProportionsOverride` from Ctrl and `SnapRotation` from Shift on press and move.
- `OnKeyDown`: Enter → `ApplyLayerTransform()`; Escape → `tool.Reset()`; arrow keys →
  `tool.Nudge(±1 or ±10 with Shift)`; all `Handled`.
- Cursor map: `Body` and corners → `SizeAll`; `Top`/`Bottom` → `SizeNorthSouth`; `Left`/`Right` →
  `SizeWestEast`; `Rotate` → `Hand`; else `Default`.
- Events `LayerTransformChanged`, `LayerTransformApplied`, `LayerTransformFailed`
  forwarding the core's; `ApplyLayerTransform()` wrapper.

### 7. Error handling

- Ineligible layer → amber hint, drags ignored, tool disarmed; selecting an eligible layer
  arms it.
- Allocation failure or size guard → `LayerTransformFailed`, amber hint ("The transformed
  layer would be too large." / "Could not allocate the transformed layer."), layer untouched,
  transform kept so the user can shrink it and retry.
- No image, no layer stack → the Move toggle is disabled (`HasImage` gate, as the others).
- Render-thread safety: the tool only reads the layer's bitmap during a render pass, which
  already holds `_bitmapLock`; the commit swaps under the lock and disposes outside it.

### 8. Testing

Headless unit tests (xUnit + FluentAssertions + SkiaSharp, `DiffusionNexus.Tests/ImageEditor/`):

| File | Covers |
|---|---|
| `LayerBoundsTests.cs` | offset defaults, `Bounds`, offset constructor, `AdoptBitmap(bitmap, offset)`, `Clone` copies offset, `ReplaceBitmap` keeps it, `SetOffset` raises events |
| `LayerCompositorOffsetTests.cs` | pixel assertions: a layer at (10, 5) lands at (10, 5); content outside the canvas is clipped; opacity and blend still apply; `LayerRenderOverride` moves only the overridden layer |
| `LayerStackOffsetTests.cs` | `Flatten` honours offsets; `MergeDown` yields the union and keeps off-canvas pixels; `CropAll` clips and re-offsets, wholly-outside → 1×1; `ResizeCanvas` grows to the union and shifts; `TransformAll` offset remap for all five operations against hand-computed positions |
| `TiffExporterOffsetTests.cs` | round trip of mixed-size, offset layers; legacy description without the new keys loads as before |
| `LayerTransformToolTests.cs` | hit test order and radii (with rotation); body move; corner scale with and without aspect; edge scale one axis; rotate with and without 15° snap; flips; `Matrix`/`TransformedBounds`; `SetPosition/SetSize/SetRotation` round trips; `HasTransform` tolerance; `Reset`; `Commit` raises only with a transform; `IsActive = false` commits |
| `ImageEditorCoreLayerTransformTests.cs` | integer move is pixel-exact and keeps off-canvas pixels; scale produces the expected size; rotation bounds; eligibility for mask and locked layers; size-guard refusal leaves the layer untouched; active-layer switch commits then arms; `ApplyShape` on an offset layer lands on the right pixels |
| `LayerTransformViewModelTests.cs` | field ↔ tool sync with the guard; command enablement; hint visibility; status messages; `DeactivateOtherTools` on open |

Manual GUI smoke (owed before merge, recorded in the PR): drag / scale / rotate / flip on a
real image, off-canvas dimming, Enter / Escape / arrows, tool switch commits, layer switch
commits, Crop and Extend after a move, TIFF save and reload of a moved layer, ineligible
mask layer hint.

### 9. Documentation

- `ImageEditor/ARCHITECTURE.md`: new "Layer bounds" paragraph under Overview, a
  "Move / Transform" data-flow example, `LayerTransformTool.cs` in the file inventory, the
  new test files in the coverage table.
- `Doc/Shortcuts.md` (Image Editor table): Enter applies, Escape resets, arrows nudge
  (Shift ×10), Ctrl inverts Keep aspect on a corner drag, Shift snaps rotation to 15°.
- `docs/CHANGELOG.md` entry.

### 10. Out of scope

Shear and perspective, undo, multi-layer selection, snapping to canvas edges or other
layers, a persistent non-destructive transform, changing the viewport to follow off-canvas
content, and any change to the Diffusion Canvas.

## Risks and mitigations

- **Many touch points in the layer model.** Every offset-aware site has a pixel-level test;
  legacy behaviour (all layers canvas-sized at (0, 0)) is a special case that all of them must
  still pass, so the existing editor tests act as regression coverage.
- **Memory.** Off-canvas content and repeated up-scaling can grow layers; the size guard caps
  a single commit, Crop clips, and thumbnails stay small.
- **Resampling quality.** One resample per commit from the untouched source; the sampler is
  the best SkiaSharp offers. Integer moves never resample.
- **TIFF compatibility.** New keys are additive; readers of old files see no difference.
