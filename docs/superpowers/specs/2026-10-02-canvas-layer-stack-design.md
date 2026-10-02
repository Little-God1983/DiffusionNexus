# Diffusion Canvas layer stack — design (#594, #518 region D1)

Issue: #594, sub-issue of #518. One PR, branch `feature/canvas-layer-stack`.

## Goal

The canvas's flat `Frames` list becomes a **layer stack** in a right-hand panel, with an inspector
below it that follows the selection. This slice ships the stack and the **Raster** kind only, which is
what every accepted candidate already is. Mask (#595), control (#596) and regional-prompt (#597)
kinds plug into the same stack later.

The list is a **shared control** extracted from the Image Editor's inline layer panel. Both screens
move onto it, and the editor gains rename and a lock toggle as a side effect.

## Decisions taken with the owner

| Question | Decision |
|---|---|
| Split #518? | Yes: #594–#600 filed and linked as sub-issues. This is #594. |
| What does lock mean? | **Protect from removal.** A locked layer cannot be deleted, and Clear canvas leaves it in place. Reorder, rename, hide and opacity stay allowed. |
| Reuse the Image Editor panel? | **Extract a shared `LayerStackPanel` and move both screens onto it.** |

## What the user gets

**Canvas**

- A right-hand column, 280 px wide, shown by default. The disabled **Layers** tool-strip button
  becomes its show/hide toggle (enabled, with a tooltip).
- The list shows the **top layer first**. Each row has a visibility toggle, a thumbnail, the name, a
  lock toggle and the opacity percentage.
- The toolbar above the list has ↑ / ↓ (reorder) and − (delete).
- Double-click a name to rename it in place. Enter or losing focus commits; Escape cancels. A
  blank name reverts to the old one.
- The inspector below the list shows the selected raster:
  - Opacity slider (0–100 %)
  - Lock checkbox
  - Read-only provenance: prompt, seed, size and world position
- The selected layer gets a thin solid accent outline on the canvas, unlike the box's marching ants.
- Accepting a candidate (Enter or the Accept button) adds a new **top** layer named `Layer N` and
  selects it. N counts up per session and is never reused.
- Right-click → Delete on a locked raster is disabled, with the tooltip "Unlock the layer to delete it".
- **Clear canvas** removes every unlocked layer. Its log line says how many locked layers it kept, and
  the command is disabled when every layer is locked.

**What the model sees.** The issue's rule is that what sits under the box is what the model gets.
So:

- A **hidden** layer is skipped by the surface's drawing, the right-click hit test, the
  "this will be img2img" region readout, and `CanvasRegionCompositor`.
- **Opacity** is applied when drawing the layer on the canvas *and* when the compositor assembles
  the region. A 50 % layer therefore reaches the model at 50 %, flattened over the existing neutral
  grey fill. (The canvas background is dark rather than grey, so on screen and in the input such a
  layer differs slightly. That is accepted and documented in the compositor.)
- A layer at 0 % opacity counts as not contributing, the same as hidden.
- The region readout is recomputed whenever a layer's visibility or opacity changes, not only when
  the collection changes as it does today.

**Image Editor**

- The panel looks and works as before, now rendered by the shared control. The editor's own `+`
  and `D` buttons sit in the control's leading-tools slot. Merge Down / Flatten All and the
  opacity inspector stay in the editor's XAML.
- New: rename by double-click, and a lock toggle on each row. Lock keeps its existing meaning there
  (the Move/Transform tool refuses a locked layer, `ImageEditorCore.LayerTransform.cs`) and now also
  disables delete.

## Components

### `LayerStackPanel` (new, `Views/Controls/`)

A reusable control on `ControlBase`, listed in `REUSABLES.md` in the same commit.

- `Items` (`IEnumerable`): rows implementing `ILayerStackItem`, already in display order (top
  first). The host owns the order; the control never reverses anything.
- `SelectedItem` (two-way)
- `MoveUpCommand`, `MoveDownCommand`, `DeleteCommand`: bound to the toolbar buttons and owned by the
  host, which keeps the reorder and delete rules in the host's view model where they can be tested
- `LeadingTools` (`object?`): host content placed before the shared buttons
- The list is a `ListBox` (keyboard selection, a selected state) instead of the editor's
  `ItemsControl` of `Button`s.
- Rename is control-local UI state. Only the committed `Name` reaches the row.

### `ILayerStackItem` (new)

`Name` (get/set), `IsVisible` (get/set), `IsLocked` (get/set), `OpacityText`, `Thumbnail`
(`Bitmap?`). The editor's `LayerViewModel` already has every member. On the canvas,
`GenerationFrameViewModel` implements it and uses `FrameImage` as the thumbnail.

### `GenerationFrameViewModel` (changed)

New properties: `Kind` (`CanvasLayerKind.Raster`, the only value so far), `Name`, `IsVisible`
(default true), `Opacity` (0–1, default 1, clamped), `OpacityPercent`/`OpacityText` for binding,
and `IsLocked`. `ICanvasRaster` gains `IsVisible` and `Opacity`.

### `CanvasLayerStackViewModel` (new sub-VM, like `Staging`)

`DiffusionCanvasViewModel` is already 1,680 lines, so the stack's rules live in a new sub-VM:

- It wraps the existing `Frames` collection. `Frames` stays the canonical **bottom-to-top** order,
  so the surface, compositor and hit test keep iterating it unchanged.
- `DisplayLayers`: a top-first mirror of `Frames`, updated incrementally on `CollectionChanged`.
  It is not rebuilt, because clearing a collection bound to a `ListBox` drops the selection.
- `SelectedLayer`, and `MoveUp`/`MoveDown` (in display terms). They are disabled at the ends of the
  stack.
- `Delete` is disabled for a locked layer, and `Clear` keeps locked layers. Both detach before
  disposing, as `DeleteFrame` does today.
- `AddAccepted(frame)` assigns the name, puts the frame on top and selects it.
- Every operation logs one line to the Unified Console through the canvas's existing `EmitInfo`
  (standing rule).

`DeleteFrameCommand` and `ClearCanvasCommand` on the canvas VM delegate to it. `Frames` stays public
so the existing tests and bindings keep working.

### `DiffusionCanvasSurface` (changed)

It skips hidden rasters, draws with `PushOpacity`, and its hit test ignores hidden rasters. A new
`SelectedRaster` styled property draws the selection outline. It already invalidates on any raster
`PropertyChanged`, so the new properties need no extra wiring.

### `CanvasRegionCompositor` (changed)

`CanvasCompositeSource` gains `Opacity`, applied through the paint's alpha. `LoadIntersecting` skips
rasters that are hidden or at 0 % opacity.

## Keyboard

The canvas's tunnel key handler exempts keys individually. In this slice:

- The rename `TextBox` must receive every key, including Enter, Escape, Delete and the arrows.
- Delete stays reserved for staging discard. There is **no** keyboard delete for layers in this
  slice, so one key can never discard a candidate in one moment and a layer the next.
- ↑/↓ inside the focused `ListBox` move the selection. They do not reorder.

## Out of scope

Undo; drag-reorder (the repo has none to reuse; ↑/↓ buttons, as the editor uses); persisting the
canvas across sessions (it is session-only today); blend modes; other layer kinds.

## Testing

Unit tests (xUnit + FluentAssertions, existing `DiffusionNexus.Tests/DiffusionCanvas/` style):

- **Compositor**
  - A hidden source contributes nothing.
  - A 50 % source yields roughly half the alpha.
  - A 0 % source counts as uncovered.
- **`CanvasLayerStackViewModel`**
  - `DisplayLayers` mirrors `Frames` reversed through add, remove, move and clear.
  - Move up/down is disabled at the ends and moves by one.
  - Delete is refused for a locked layer.
  - Clear keeps locked layers and reports the count.
  - An accepted candidate lands on top, selected, named `Layer N`, and N is never reused.
  - Rename trims, and a blank name reverts.
  - Hiding a layer, or setting its opacity to 0, updates the region mode.
- **Image Editor**: `LayerPanelViewModel` delete is disabled for a locked layer.
- The existing canvas, staging and editor layer tests stay green.

GUI smoke, done by hand:

- **Canvas:** accept three candidates; reorder; hide one under the box and confirm the img2img
  readout changes; set 50 % opacity; rename; lock and then try delete and Clear canvas; toggle the
  panel; type a rename containing F/G/B/1 and confirm the canvas shortcuts do not fire.
- **Editor:** reorder, delete, add, duplicate, rename, lock (the Move tool refuses a locked layer,
  and delete is disabled).
