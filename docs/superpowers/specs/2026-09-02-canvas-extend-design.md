# Canvas Extend — design

**Feature:** a Canvas Extend tool in the Image Editor: grow the canvas around the image without generating content
**Date:** 2026-09-02
**Status:** approved direction (standalone tool, "Row A" of the mockups), spec ready for planning
**Mockups:** https://claude.ai/code/artifact/3e066a50-4879-422d-b184-58d37aa57e17 (Row A is the chosen design; Rows B and C were rejected)

## The problem

The editor can make an image smaller (Crop) and can make it larger only by running the
AI Outpaint workflow. There is no way to simply add empty canvas: to give a portrait
room for text, to pad a square image to 16:9 before Fill, or to prepare a larger canvas
for the Draw and Text tools. Users reach for Outpaint, wait for ComfyUI, and throw away
the generated content.

The tool should behave like Crop: one click selects the whole canvas, the user drags
handles or types a size, the view zooms out so the growing frame stays visible, and an
attempt to make the canvas smaller is answered with "use Crop".

## What already exists

Two pieces already do most of the work and are reused rather than rewritten:

- `ImageEditor/OutpaintTool.cs` — an outward-only frame around the image with eight
  drag handles, per-edge pixel extension (`ExtendTop/Right/Bottom/Left`),
  `SetAspectRatio`, `SetExtension`, `GetNewDimensions`, a size label, and the
  screen-to-pixel drag math. It renders arrow handles 36 px outside the frame and tints
  the frame by an AI-specific "severity".
- `ImageEditorCore.ResizeLayerCanvas(newWidth, newHeight, offsetX, offsetY)` →
  `LayerStack.ResizeCanvas` → `Layer.ResizeCanvas` — grows every layer to the new size,
  draws the old content at the offset, and leaves the new pixels transparent. Its only
  caller today is the Outpaint result handler in `ImageEditView.axaml.cs`.

Two gaps in the existing code shape the design:

- The viewport's fit mode fits the **image**, so while Outpaint is active a large
  extension runs off screen. The extend tool needs the fit to include the frame, and the
  same change fixes Outpaint.
- The editor has no undo. Crop's recovery path is Reset (reload the original); Extend
  gets the same and nothing more.

## Decisions

| Question | Decision |
|---|---|
| Standalone tool, inside Crop, or Outpaint + plain Apply? | **Standalone tool** ("Extend" toggle next to Outpaint, own panel). Crop keeps its 0..1 region and its meaning; Outpaint keeps its AI panel. |
| Share code with Outpaint? | **Yes.** The frame state and drag math move into an abstract base `CanvasExtensionTool`; `OutpaintTool` and the new `CanvasExtendTool` subclass it and differ only in handle layout, rendering and fit margin. Outpaint behaviour does not change. |
| Handle style | **Crop-style round handles on the frame** (radius 6, hit radius 12), as in the mockup. Handles only move outward; the frame is drawn from the moment the tool activates, so one click "selects the whole canvas". |
| New pixels | **Transparent**, shown as a checkerboard with a green tint while previewing. Colouring is the existing Fill tool's job; no colour picker in this panel. |
| Typed width/height | Two numeric fields (spinner buttons hidden: with them the ~280 px panel showed only three digits). A typed size grows the canvas away from the **image placement** (below). Values below the image size clamp to the image size and show the shrink hint. |
| Image placement | A 3×3 anchor grid ("Image position") plus **drag-to-move**: pressing on the image (off every handle) while there is any extension slides the image inside the frame, trading extension between opposite edges; the size never changes. Default anchor is **top-left** (canvas grows right and down), chosen by the user on 2026-09-03 over the centred first cut. A drag sets `CanvasAnchor.Custom`: the grid shows no cell, later typed sizes keep the image's offset from the top-left and grow/shrink at the right and bottom edges. Picking a cell redistributes the current extension at once. `Reset` reverts Custom to the default but keeps a chosen cell. Outpaint keeps its centred default and gets no drag-to-move (`AllowsImageMove` is opt-in). |
| Multipliers | Two rows (Width / Height), each 1×, 2×, 3×. Each sets that dimension to *k* × the **image** dimension (not the current target), the other dimension keeps its current target. 1× is the way back after 2×/3× (added 2026-09-03: before it, only Cancel returned an axis to the image size); it is not a shrink, so no hint. |
| Aspect presets | 16:9, 9:16, 4:3, 3:4, 1:1 via the base class's `SetAspectRatio` (extend-only, grows away from the placement). |
| Shrink attempt | A handle dragged inward stops at the image edge and turns amber; typing a smaller size clamps. Both raise `ShrinkAttempted`; the panel shows "The canvas can only grow here. To cut the image down, use the Crop tool." with an **Open Crop** button that switches to the Crop tool. |
| Zoom-out | While an extension tool (Extend **or** Outpaint) is active and the viewport is in fit mode, fit is computed for the extended frame plus a per-tool margin for handles and label. Manual zoom is left alone; Fit brings the frame back. |
| Keyboard | Enter applies, Escape resets the extension (Crop precedent: Escape clears the region, it does not close the tool). |
| Apply | Grows layers **and** the working bitmap exactly the way `Crop()` handles both, resets the tool, closes the panel. No undo; Reset recovers. |
| Reusable control? | No. The panel is inline XAML in `ImageEditView.axaml` like the Crop and Outpaint panels; the numeric fields are Avalonia's `NumericUpDown`. Nothing new goes into `REUSABLES.md`. |

Rejected: extending from inside Crop (the overlay would mean two things at once, the
shrink hint would have nothing to point to, and the crop region's 0..1 clamp is load
bearing for Fit/Fill/aspect/min-size); a plain Apply on the Outpaint panel (the panel is
full of readiness, prompt and Generate controls a plain extend never needs).

## Design

### 1. `CanvasExtensionTool` (new abstract base, `ImageEditor/CanvasExtensionTool.cs`)

Moved verbatim from `OutpaintTool`: the four extension fields, `ImagePixelWidth/Height`,
`IsActive` (deactivation resets), `ExtendTop/Right/Bottom/Left`, `HasExtension`,
`GetNewDimensions()`, `SetImageBounds`, `Reset()`, `SetExtension`, `SetAspectRatio`,
`OnPointerPressed/Moved/Released`, `GetCursorForPoint`, `IsDragging`, `RegionChanged`,
and the protected `GetExtendedScreenRect()`. The `OutpaintHandle` enum stays where it is
and keeps its name; both tools use it.

Abstract members the subclasses provide:

- `protected abstract float HandleHitRadius { get; }`
- `protected abstract SKPoint GetHandleCenter(OutpaintHandle handle)` — used by the base
  hit test (corners first, then edges, same order as today).
- `public abstract float FitMargin { get; }` — screen pixels the viewport reserves on
  each side of the extended frame. Outpaint: 72 (36 offset + 26 radius + 10). Extend: 32.
- `public abstract void Render(SKCanvas canvas, SKRect canvasBounds)`.

New in the base:

- `SetTargetSize(int width, int height)`: total horizontal extension =
  `max(0, width − ImagePixelWidth)`, `left = total / 2`, `right = total − left`; same for
  height. A requested dimension below the image size clamps to the image size and raises
  `ShrinkAttempted`. Raises `RegionChanged`.
- `event EventHandler? ShrinkAttempted`: raised by `SetTargetSize` (per call) and by
  `OnPointerMoved` when the active handle's requested extension went below zero and was
  clamped, **at most once per drag gesture** (a flag cleared in `OnPointerPressed`).
- `bool IsShrinkBlocked` — true while the last pointer move of the current gesture was
  clamped (the handle is being held past the image edge); false again as soon as the
  pointer moves outward or is released. The Extend renderer colours the active handle
  amber from it.

### 2. `OutpaintTool : CanvasExtensionTool`

Keeps arrows, `AreaRatio`, `Severity`, its render code and its label offset. Its
handle centres stay 36 px outside the extended rect with hit radius 40. Behaviour is
unchanged; regression tests pin the handle geometry and the corner-drag semantics.

### 3. `CanvasExtendTool : CanvasExtensionTool` (new, `ImageEditor/CanvasExtendTool.cs`)

Handle centres are the extended rect's four corners and four edge midpoints (like
`CropTool.GetHandlePositions`). `Render`, whenever active:

1. Extension strips (top, bottom, left, right, only those > 0): a 16 px screen-space
   checkerboard (`#3B3B3B` / `#2B2B2B`) via a tiled bitmap shader, then a fill of
   `SKColor(76,175,80,40)`.
2. Frame border: 2 px dashed `SKColor(76,175,80,200)`, dash 8/4, around the extended
   rect. Drawn even with zero extension, so activation shows the selected canvas.
3. When `HasExtension`: 1 px `SKColor(255,255,255,100)` outline around the original image.
4. Eight round handles: radius 6, white fill, `#505050` stroke. The active handle is
   `#4CAF50`; `#FFC107` while `IsShrinkBlocked`.
5. Size label "W × H" (same pill as `CropTool.DrawResolutionLabel`, 8 px above the
   frame, inside the frame when there is no room above).

### 4. Viewport: fit includes the frame

`ImageEditorCore.RenderWithZoom` and `CalculateFitRect` (used by
`ImageEditorControl.ZoomToFit` to pre-compute the zoom for the event) both go through
one new pure function:

```
internal static (SKRect ImageRect, float Scale) CalculateFitRectWithExtension(
    int imageWidth, int imageHeight,
    int extendLeft, int extendTop, int extendRight, int extendBottom,
    float margin, float containerWidth, float containerHeight)
```

Virtual canvas = image + extension, fitted into the container shrunk by `margin` on
each side, centred; the image rect is the virtual rect offset by
`(extendLeft × scale, extendTop × scale)`; `Scale` becomes `_zoomLevel`. With zero
extension and zero margin it equals today's `CalculateFitRectInternal`.

The core asks "which extension tool is active": `CanvasExtendTool` if active, else
`OutpaintTool` if active, else none (zero extension, zero margin). Only fit mode is
affected; when the user has zoomed manually nothing changes.

One existing defect has to go with it: today `RenderWithZoom` writes the fit scale through
the `Viewport.ZoomLevel` setter, and that setter clears `IsFitMode`, so fit mode switches
itself off on the first render after load. The extend rule would then never apply. The fit
branch writes through `Viewport.SetFitModeWithZoom(scale)` instead (only when the scale
changed), which keeps fit mode on until the user zooms manually.

After Apply the tool resets, the image is larger, and fit mode re-fits it on the next
frame, so the result lands at the new zoom without extra code.

### 5. Apply — `ImageEditorCore.ApplyCanvasExtend()`

Returns `false` when there is no image or `!CanvasExtendTool.HasExtension`. Otherwise,
under `_bitmapLock`, mirroring `Crop()`:

- layer mode: `_services.Layers.ResizeCanvas(newW, newH, extendLeft, extendTop)`;
- if `_workingBitmap` exists: a new `Rgba8888/Premul` bitmap of the new size, erased to
  transparent, the old bitmap drawn at `(extendLeft, extendTop)`, swapped in, the old one
  disposed outside the lock.

Then `CanvasExtendTool.Reset()`, `OnImageChanged()`, return `true`. `ImageChanged` already
drives the view's dimension, file-info and layer-panel refresh.

### 6. `CanvasExtendViewModel` (new, `ViewModels/CanvasExtendViewModel.cs`)

Mirrors `OutpaintingViewModel` without the AI half. Constructor
`(Func<bool> hasImage, Func<int> getImageWidth, Func<int> getImageHeight, Action<string> deactivateOtherTools, IUnifiedLogger? unifiedLogger = null)`.

Properties: `IsPanelOpen` (opening calls `deactivateOtherTools(ToolIds.CanvasExtend)`,
raises `ToolActivated`, `ToolToggled((ToolIds.CanvasExtend, true))`, `ToolStateChanged`
and a status message; closing raises `ToolDeactivated`), `ResolutionText` ("2048 x 1024"),
`HasExtension`, `TargetWidth` / `TargetHeight` (`int`, two-way bound to the numeric
fields; a set from the view at or above the image dimension raises
`TargetSizeRequested(width, height)`; a set below the image dimension clamps to it, shows
the hint and raises nothing; sets coming from `UpdateResolution` do not echo back —
guarded by a `_syncing` flag), `IsShrinkHintVisible`.

Commands: `ToggleCommand`, `CancelCommand` (closes), `ApplyCommand` (`hasImage && IsPanelOpen && HasExtension` → `ApplyRequested`),
`MultiplyCommand<string>` (`"2xW"`, `"3xW"`, `"2xH"`, `"3xH"` → `TargetSizeRequested`
with *k* × image dimension, other dimension = current target),
`SetAspectRatioCommand<string>` (same parse as Outpaint → `SetAspectRatioRequested`),
`OpenCropCommand` → `OpenCropRequested`.

Methods: `UpdateResolution(newWidth, newHeight, hasExtension)`,
`OnShrinkAttempted()` (hint on), `OnApplied(newWidth, newHeight)` (closes the panel,
status "Canvas extended to W x H"), `ClosePanel()`, `RefreshCommandStates()`.

Hint lifetime: `IsShrinkHintVisible` turns off when `UpdateResolution` reports a larger
size than the last one it saw, on `OnApplied`, and on close.

Logging (standing rule: every feature logs its steps to the Unified Console): open/close,
target size, multiplier, aspect preset, shrink attempt, apply with old → new size, via the
same `EmitInfo` pattern as `OutpaintingViewModel` with `LogSource = "CanvasExtend"`.

### 7. `ImageEditorViewModel`

- `public CanvasExtendViewModel CanvasExtend { get; }`, created next to `Outpainting`.
- `ToolToggled` → `_services.Tools.Activate/Deactivate`; `ToolStateChanged` →
  `NotifyToolCommandsCanExecuteChanged`; `StatusMessageChanged` → `StatusMessage`.
- `OpenCropRequested` → the same path as the Crop toolbar toggle when Crop is not active
  (`IsCropToolActive = true` closes the Extend panel through `DeactivateOtherTools`,
  then `_services.Tools.Activate(ToolIds.Crop)` and `CropToolActivated`).
- `DeactivateOtherTools`, `CloseAllTools`, `NotifyToolCommandsCanExecuteChanged` gain the
  `CanvasExtend` lines that `Outpainting` has.
- `ToolIds.CanvasExtend = "CanvasExtend"`.

### 8. `ImageEditorControl`

- `public bool IsCanvasExtendToolActive` (plain property like `IsOutpaintToolActive`;
  sets `_editorCore.CanvasExtendTool.IsActive`, invalidates).
- Pointer pressed / moved / released: a block identical to the Outpaint block, placed
  right after it, guarded by `_isCanvasExtendToolActive`.
- Cursor: the Outpaint mapping (edges → `SizeNorthSouth` / `SizeWestEast`, corners →
  `SizeAll`) applied when the Extend tool is active.
- Keys: Enter → `ApplyCanvasExtend()`; Escape → `CanvasExtendTool.Reset()` + invalidate.
  Both only while the tool is active.
- Events: `CanvasExtendRegionChanged`, `CanvasExtendShrinkAttempted`,
  `CanvasExtendApplied`; subscribed and unsubscribed beside the Outpaint ones.
- `public bool ApplyCanvasExtend()` → core; raises `CanvasExtendApplied` on success.

### 9. View — `ImageEditView.axaml` / `.axaml.cs`

Toolbar: `ToggleButton Content="Extend"` after Outpaint, bound to
`ImageEditor.CanvasExtend.IsPanelOpen`, `IsEnabled="{Binding ImageEditor.HasImage}"`,
tooltip "Extend Canvas - Grow the canvas without generating content. The new area stays
transparent".

Panel (after the Outpaint panel, visible on `IsPanelOpen`), same chrome as the Crop panel
(`#2A2A2A` card, 8 px padding, `#444` separators, green `#4CAF50` resolution text):

1. Hint: "Drag a handle outward or type a new size. The new area stays transparent, so
   use Fill afterwards to colour it."
2. `ResolutionText` (green, centred) and a small line "from W × H" (image size).
3. "Canvas size": two `NumericUpDown` (W, H) with `Minimum` bound to
   `ImageEditor.ImageWidth` / `ImageHeight`, `Increment="1"`, `FormatString="0"`,
   `Value` two-way to `TargetWidth` / `TargetHeight`.
4. Multiplier row (2× W, 3× W, 2× H, 3× H) and "Extend to aspect ratio" row
   (16:9, 9:16, 4:3, 3:4, 1:1), small buttons like the Crop presets.
5. Shrink hint: the amber `#2A2210` / `#FFD54F` block used by the Outpaint
   "needs extension" warning, with the hint text and an **Open Crop** button.
6. Cancel | Apply (`#2D7D46`).

`WireCanvasExtendEvents(imageEditor)` mirrors `WireOutpaintingEvents`: activated → set
the control flag and push the initial `UpdateResolution`; deactivated → clear the flag,
`Reset`, invalidate; `TargetSizeRequested` → `tool.SetTargetSize`;
`SetAspectRatioRequested` → `tool.SetAspectRatio`; `ApplyRequested` →
`control.ApplyCanvasExtend()`; `CanvasExtendRegionChanged` → `UpdateResolution`;
`CanvasExtendShrinkAttempted` → `OnShrinkAttempted`; `CanvasExtendApplied` →
`OnApplied(core.Width, core.Height)`.

### 10. Documentation

- `DiffusionNexus.UI/Doc/Shortcuts.md`: an Image Editor table with Enter (apply
  extension) and Escape (reset extension) for the Extend tool (copilot-instructions rule).
- `DiffusionNexus.UI/ImageEditor/ARCHITECTURE.md`: `CanvasExtensionTool`,
  `CanvasExtendTool` and the fit-with-extension rule in the file inventory.

## Error handling

- Apply with nothing to apply: `false`, the panel's Apply button is disabled anyway.
- Apply on a very large target (e.g. 3× both ways on a 4k image) allocates a bitmap per
  layer; the failure (Skia raises a plain exception, or hands back an empty bitmap that the
  allocation guard rejects) is caught in `ApplyCanvasExtend`, logged, the tool is left as it
  was, and the status message says the canvas could not be extended.
- Image cleared while the panel is open: `HasImage` turns false, commands refresh through
  the existing `NotifyToolCommandsCanExecuteChanged` path, and `CloseAllTools` closes the
  panel as it does for Outpaint.
- Image *replaced* while the panel is open (loading another image into the editor): the
  load path does not call `CloseAllTools`, so the panel stays open on the new image. This is
  a known gap shared with Outpaint, tracked for a follow-up rather than fixed here.

## Testing

xUnit + FluentAssertions, in `DiffusionNexus.Tests/ImageEditor/`, TDD per task:

- `CanvasExtendToolTests`: default placement top-left (2000×1500 → 0/0/1000/500); each of
  the nine anchors redistributes the current extension without changing the size; an
  aspect preset grows away from the anchor; dragging the image shifts extension between
  opposite edges (half zoom → 2 image px per screen px), clamps at the frame, sets
  `Custom`, and a no-move drag keeps the anchor; `IsMovePoint` needs an extension and
  loses to handles; Custom keeps the offset and grows/shrinks at right/bottom; `Reset`
  reverts Custom only; below-image request clamps and raises `ShrinkAttempted`; `SetExtension` clamps negatives;
  `GetNewDimensions`; a right-handle drag of *n* screen px at scale *s* adds *n/s* image
  px; an inward drag clamps at 0 and raises `ShrinkAttempted` once per gesture;
  `IsShrinkBlocked` true during the clamped gesture and false after release; handle hit
  test at the frame corners/edges within radius 12 and a miss outside; `Reset` and
  `IsActive = false` zero the extension; `FitMargin` is 32.
- `OutpaintToolRegressionTests`: handle centres 36 px outside the extended rect; hit
  radius 40; a corner drag extends two edges; `FitMargin` is 72.
- `ImageEditorCoreCanvasExtendTests`: layer-mode apply grows `Width/Height`, the pixel at
  `(extendLeft, extendTop)` equals the old `(0,0)`, the pixel at `(0,0)` is transparent,
  the tool is reset, `ImageChanged` fires; no extension → `false`.
- `ViewportFitTests`: `CalculateFitRectWithExtension` keeps the virtual canvas inside the
  container minus margin, offsets the image rect, and reduces to the plain fit with zero
  extension and margin.
- `CanvasExtendViewModelTests`: opening calls `deactivateOtherTools(ToolIds.CanvasExtend)`
  and raises `ToolToggled`; Apply is disabled until `HasExtension`; a `TargetWidth` below
  the image clamps, shows the hint and raises no `TargetSizeRequested`; `"2xW"` raises
  `TargetSizeRequested(2048, 1024)` for a 1024² image; `UpdateResolution` does not echo
  `TargetSizeRequested`; the hint clears on growth and on `OnApplied`; `OnApplied` closes
  the panel.
- `ToolManagerTests`: `ToolIds.CanvasExtend` activation is mutually exclusive with Crop.

Manual smoke (owed before merge, in the real app with a 1024² PNG): activate Extend,
drag each handle, 2× W, type 1500 for H, 16:9, Escape, Enter; Fill the new area; save as
PNG and confirm transparent strips; open Outpaint and confirm its frame no longer runs
off screen at 3× width.

## Out of scope

Colour fill inside the panel (Fill tool covers it); an anchor grid for typed sizes; undo;
any change to Outpaint beyond the shared base class and the fit rule; Escape closing the
tool; persisting the last target size.
