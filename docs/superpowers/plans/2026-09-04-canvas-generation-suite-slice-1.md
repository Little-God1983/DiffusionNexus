# Diffusion Canvas — Generation Suite, Slice 1 (bounding box + staging + cancel)

**Issue:** [#518](https://github.com/Little-God1983/DiffusionNexus/issues/518) — regions **C** (canvas & bounding box),
**E** (staging strip) and the cancel half of **F**. Regions A, B, D, G ship on later branches.

**Goal:** replace the canvas's spatial model. Today Generate appends a frame at a walking 40 px diagonal offset and
that is the whole story. After this slice there is one movable, resizable **generation bounding box**: its size is the
latent size, its position is where the pixels land, and whatever is underneath it is what the model sees. Results
arrive as **candidates** in a staging strip and only reach the canvas when the user accepts them. Cancel really stops
the batch.

**Branch:** `feature/canvas-generation-suite` (already cut from `develop`). Never commit to `develop`.

---

## Definition of done (verbatim from the issue)

- [ ] Pan, zoom, and fit work with a mouse and with the keyboard.
- [ ] The bounding box can be moved and resized; its size and world position are visible and correct.
- [ ] Generating with the box over empty canvas produces an image **inside the box**, at the box's size.
- [ ] Generating with the box overlapping an accepted result uses those pixels as input.
- [ ] Results appear in staging and only reach the canvas on accept.
- [ ] Cancel stops an in-flight batch.
- [ ] Every step of the flow is traced to the Unified Console, so a hang shows the last successful step.

Scope decision recorded on the branch: "the box over existing pixels" must work on **both** backends — the local
stable-diffusion.cpp core *and* the ComfyUI engine.

---

## Architecture

### The viewport is hand-written, not `ZoomBorder`

`Avalonia.Controls.PanAndZoom.ZoomBorder` is dropped. Verified reasons, not preference:

| Fact | Consequence for a bounding box |
|---|---|
| `Matrix`, `ZoomX/Y`, `OffsetX/Y` are **read-only** (`CanWrite=False`); the only write paths are `SetMatrix`/`ZoomTo`/`PanDelta`/`ResetMatrix` | "Fit", "1:1" and "restore viewport" all have to go through a foreign API we cannot unit-test |
| It transforms via `RenderTransform` | Every border, handle and label scales with zoom — a 3 px frame is 0.75 px at 0.25× and a 16 px handle is 64 px at 4×. The existing resize handle already has this defect |
| `EnableGestureRotation` defaults **True** and the XAML never turns it off | A touchpad user can rotate the world; every axis-aligned hit-test and the whole resize math silently break, with no affordance to reset |
| `MinZoomX`…`MaxOffsetY` all default to ±∞ | Zoom and pan are unbounded with no way back — `ResetMatrix` is bound to nothing |
| The "infinite canvas" is a hardcoded `Canvas Width=10000 Height=10000` | Not infinite, and the world origin is pinned to a layout element |

The repo already owns the better pattern: `ImageEditorControl` (custom `Control`, overrides `Render` /
`OnPointer*` / `OnKeyDown`) driven by `ImageEditor/Services/ViewportManager` as the single source of truth for
zoom/pan/fit, with `CropTool` supplying the 8-handle movable/resizable box. This slice mirrors that shape with three
differences: it renders through Avalonia's own `DrawingContext` (not an `ICustomDrawOperation` Skia lease — there is
no per-frame Skia work here, and it avoids the compositor-render-thread bitmap race `ImageEditorCoreRenderRaceTests`
exists to guard), its box carries **world pixels** rather than `CropTool`'s normalised 0–1 coordinates (the box's
size *is* the latent size), and its transform lives in a POCO so it is unit-testable with no Avalonia platform.

### Layers

```
DiffusionCanvasSurface (Avalonia Control)      ← pointer, keys, Render(DrawingContext)
   ├── CanvasViewport            (POCO)        ← zoom/pan, WorldToScreen/ScreenToWorld, Fit, OneToOne
   ├── GenerationBoundingBox     (POCO)        ← world rect, 8 handles, snap 64, min/max
   └── PlacedRaster[]            (POCO)        ← accepted results: world rect + Bitmap

DiffusionCanvasViewModel                       ← batch runner, run-epoch CTS, staging, logging
   ├── CanvasStagingViewModel                  ← candidates; nothing touches the canvas unasked
   └── CanvasRegionCompositor    (SkiaSharp)   ← "what is under the box" → PNG bytes
```

`CanvasViewport`, `GenerationBoundingBox` and `CanvasRegionCompositor` contain **no Avalonia visual types**, so all of
their behaviour is testable in `DiffusionNexus.Tests` — which is required, because that project deliberately
initialises no Avalonia platform and adding one "deadlocks the suite".

### "What is under the box"

There is **no `RenderTargetBitmap` or `SKSurface` anywhere in this repo** and no precedent for snapshotting a live
Avalonia visual tree. So the region is composited arithmetically, not captured:

`CanvasRegionCompositor.Composite(rasters, worldRect, outW, outH)` draws each `PlacedRaster` whose world rect
intersects the box into an `SKBitmap` sized to the box's latent size, mapping world→pixel itself. It returns the
bitmap plus an **opaque-coverage fraction**. The view model then:

- coverage `== 0` → plain **text2img**, no `InitImage`.
- coverage `> 0` → **img2img**: transparent pixels are flattened onto neutral mid-grey `#808080`, the result is
  encoded (through an `SKAlphaType.Unpremul` bitmap — encoding premultiplied data straight to PNG "can result in
  blank/transparent output", documented at `ImageEditorCore.Inpainting.cs:266-271`), written to
  `%TEMP%/diffnexus_canvas_{guid}.png`, and sent as `DiffusionRequest.InitImage` with the Denoise slider's strength.
  The temp file is deleted in a `finally`.

**Honest limitation, stated in the UI and the log:** true outpainting — keep the known pixels exactly, generate only
the empty part — needs `DiffusionRequest.MaskImage`, which neither backend implements
(`StableDiffusionCppBackend.cs:257` is still `TODO(v2-inpaint)`). With coverage between 0 and 1 the uncovered area is
mid-grey input to a normal img2img denoise, so the known pixels are *re-generated*, not preserved. The status line
reports the coverage percentage and the Denoise readout says so. Masked outpainting belongs to region D.

### Batch and cancel

Batch width is **1**, deliberately: `DiffusionContextHost` holds a per-model `SemaphoreSlim(1,1)` with a
single-resident policy, so "concurrent" canvas generations either serialise behind that lock or thrash VRAM.
The batch is a sequential loop over `BatchCount`.

The run-epoch rule is copied verbatim from `CivitaiDownloadQueue.cs:543-552` — *"Both halves are the invariant —
never cancel without nulling."* `Generate` joins with `_runCts ??= new()`; `Cancel` calls `Cancel()` **and** nulls the
field. Cancelling without nulling would make the next Generate join a dead epoch and abort instantly.

Cancellation reaches the two backends differently, and the UI says which:

- **Core (sd.cpp):** the token is observed at phase boundaries only; the native `GenerateImage` call cannot be
  interrupted. Cancel drops every pending batch item and discards the in-flight image when it returns.
- **Engine (ComfyUI):** gains a real interrupt. `IComfyUIWrapperService.InterruptAsync` (`POST /interrupt`) is new;
  `ManagedComfyUiBackend` registers it on the caller's token so cancel stops sampling immediately.

### Engine img2img

The shipped `Krea2-Text2Image-API.json` already carries a `VAELoader` (node 57), so no new asset and **no new custom
node** is needed — `LoadImage` and `VAEEncode` are core ComfyUI. When `request.InitImage` is set, the backend uploads
the composited PNG with the existing `ComfyUIWrapperService.UploadImageAsync`, and `Krea2WorkflowPatcher` injects:

```
"9001": LoadImage  { image: <uploaded name> }
"9002": VAEEncode  { pixels: ["9001",0], vae: ["57",0] }
KSampler(37).latent_image = ["9002", 0]
KSampler(37).denoise      = strength
```

`EmptySD3LatentImage` (36) becomes unreachable and ComfyUI never executes it — the same trick the existing patcher
already uses to strand the `AI2GoResolutionSelector`.

---

## Global constraints

- Repo `e:\Repos\DiffusionNexus`; every search needs an explicit path (the default Glob/Grep root is the Installer SDK repo).
- Build: `dotnet build DiffusionNexus.sln -c Debug` before every push — the UI project has no test coverage and XAML errors only surface at build time. Baseline is 0 errors / 12 pre-existing CA1416 warnings.
- Test: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release`; fall back to `-c Debug` only if `bin\Release` is locked by the running app. Known pre-existing flakes: `GenerationGalleryViewModelTests.TagCloudSearchText…`, `CheckScoreAdapterTests`.
- **Never** add Avalonia platform initialisation to `DiffusionNexus.Tests`. No real `Avalonia.Media.Imaging.Bitmap` in unit tests — use the `RuntimeHelpers.GetUninitializedObject(typeof(Bitmap))` sentinel (`BatchUpscaleTabViewModelSchedulerTests.cs:39-44`) or keep bitmaps out of the seam.
- `DiffusionCanvasViewModel`'s **parameterless design-time ctor must stay parameterless** — `CanvasBackendSelectionTests` calls `new DiffusionCanvasViewModel()`, so a new required parameter breaks the whole test project's build. New dependencies go on the production ctor as optional trailing parameters.
- The VM is a **DI singleton** that never dies; it must dispose its CTS and candidate bitmaps explicitly.
- Bitmap lifetime rule: **detach from the collection first, dispose second** — "disposing a bitmap still bound into the visual tree faults the render" (`SelectableImageResultsViewModel.cs:110-122`).
- Dual logging is the house rule: one `EmitInfo(string)` per class doing `Logger.Information(...)` **and** `_unifiedLogger?.Info(...)`. Logger is `IUnifiedLogger? unifiedLogger = null`, optional and nullable, resolved in `App.axaml.cs` with `sp.GetService<…>()`. `LogCategory.General`, `LogSource = "DiffusionCanvas"`. Trace at step boundaries, not per sampling step — the console keeps only 2000 entries.
- German locale: no culture-sensitive assertions.
- Check `DiffusionNexus.UI\REUSABLES.md` before adding UI; add a row for `DiffusionCanvasSurface` in the same commit.
- New shortcuts must be documented in `DiffusionNexus.UI\Doc\Shortcuts.md`.
- No `AppSettings`/EF work in this slice — that would trip the `copilot-instructions.md` rule requiring `publish.ps1` to run before any entity/migration change. Canvas state stays in memory (the VM is a singleton, so it survives navigation).
- Commit trailer: `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.

---

## File map

| File | Responsibility |
|---|---|
| `DiffusionNexus.UI/DiffusionCanvas/CanvasViewport.cs` (new) | Zoom/pan POCO: `Zoom`, `PanX/Y`, `WorldToScreen`, `ScreenToWorld`, `ZoomAt`, `Fit`, `OneToOne`, `PanBy`, clamps. |
| `DiffusionNexus.UI/DiffusionCanvas/GenerationBoundingBox.cs` (new) | World rect + 8-handle resize/move, snap to alignment, min/max clamp, `HitTest`. |
| `DiffusionNexus.UI/DiffusionCanvas/BoxHandle.cs` (new) | `None, Move, N, NE, E, SE, S, SW, W, NW`. |
| `DiffusionNexus.UI/DiffusionCanvas/PlacedRaster.cs` (new) | Accepted result on the canvas: world rect + `Bitmap` + source path + seed. |
| `DiffusionNexus.UI/DiffusionCanvas/CanvasRegionCompositor.cs` (new) | SkiaSharp region composite → `(SKBitmap, coverage)`; `EncodeAsPng` with the Unpremul step. |
| `DiffusionNexus.UI/Views/Controls/DiffusionCanvasSurface.cs` (new) | Custom `Control`: dot grid, rasters, marching-ants box + constant-screen-size handles, pointer + key routing, `PointerCaptureLost`. |
| `DiffusionNexus.UI/ViewModels/DiffusionCanvas/StagedCandidateViewModel.cs` (new) | One candidate: state, bitmap, seed, world rect, PNG bytes. |
| `DiffusionNexus.UI/ViewModels/DiffusionCanvas/CanvasStagingViewModel.cs` (new) | Candidate buffer, `Next/Prev/Accept/Discard/AcceptAll/DiscardAll`, `IsComparing`. |
| `DiffusionNexus.UI/ViewModels/DiffusionCanvas/DiffusionCanvasViewModel.cs` (modify) | Bounding box, batch runner, run-epoch CTS, real Cancel, denoise, unified logging. |
| `DiffusionNexus.UI/ViewModels/DiffusionCanvas/GenerationFrameViewModel.cs` (modify) | Becomes the accepted-raster model; drops the per-frame drag/resize role. |
| `DiffusionNexus.UI/Views/DiffusionCanvas/DiffusionCanvasView.axaml(.cs)` (rewrite) | Surface host + staging strip + status bar; drops `ZoomBorder`, the 10000×10000 Canvas and the `canvas OK` diagnostic. |
| `DiffusionNexus.Domain/Services/IComfyUIWrapperService.cs` (modify) | `Task InterruptAsync(CancellationToken)`. |
| `DiffusionNexus.Service/Services/ComfyUIWrapperService.cs` (modify) | `POST /interrupt`. |
| `DiffusionNexus.UI/Services/Diffusion/Krea2WorkflowPatcher.cs` (modify) | img2img injection + `denoise`. |
| `DiffusionNexus.UI/Services/Diffusion/ManagedComfyUiBackend.cs` (modify) | Upload init image, patch img2img, interrupt on cancel. |
| `DiffusionNexus.Inference/Abstractions/DiffusionRequest.cs` (modify) | Fix the stale "Currently ignored" doc on `InitImage`. |
| `DiffusionNexus.UI/App.axaml.cs` (modify) | Pass `IUnifiedLogger` into the canvas VM. |
| `DiffusionNexus.UI/Doc/Shortcuts.md`, `REUSABLES.md` (modify) | Docs. |
| `DiffusionNexus.Tests/DiffusionCanvas/*.cs` (new) | Viewport, box, compositor, staging, batch/cancel, patcher tests. |

---

## Tasks

- [ ] **1 — `CanvasViewport`** + tests: screen↔world round-trip, `ZoomAt` keeps the anchor point fixed, `Fit` centres a content rect, zoom clamped 0.05–8, `OneToOne`.
- [ ] **2 — `GenerationBoundingBox`** + tests: move; each of the 8 handles resizes the right edges; snap to 64 with min 512 / max 2048; opposite edge stays pinned; `HitTest` prefers handles over the body.
- [ ] **3 — `CanvasRegionCompositor`** + tests: empty region → coverage 0 and a fully transparent bitmap; a raster fully under the box → coverage 1 and pixels copied; partial overlap → fractional coverage; world→pixel scaling when the box size ≠ the raster's; PNG encode is not blank (the Unpremul trap).
- [ ] **4 — `DiffusionCanvasSurface`**: render grid + rasters + box; handles drawn at constant screen size (`handlePx / zoom`); marching ants via an animated dash offset; wheel-zoom at the cursor, space-drag and middle-drag pan, `F` fit, `1` one-to-one; `PointerCaptureLost` clears gesture state (no repo precedent — authored here); focus on press.
- [ ] **5 — Staging** (`StagedCandidateViewModel`, `CanvasStagingViewModel`, strip view) + tests: pending slots render dimmed; `←/→` step; `Space` held flips to the canvas underneath; `Enter` accepts into `Frames`; `Del` discards; nothing reaches `Frames` without accept; discard follows detach-then-dispose.
- [ ] **6 — Batch runner + real Cancel** in `DiffusionCanvasViewModel` + tests with a hand-rolled `FakeDiffusionBackend` (async iterator, `[EnumeratorCancellation]`): `BatchCount` runs sequentially; `IsGenerating` carries `[NotifyCanExecuteChangedFor]` for both commands so a second click cannot clobber `_cts`; cancel-then-generate runs on a fresh epoch; `OperationCanceledException` is caught **before** the generic handler and reported as cancelled, not failed.
- [ ] **7 — Region → backend**: compose under the box, choose text2img vs img2img, write/delete the temp PNG, pass `Width`/`Height` from the box; validate the box against the descriptor's `DimensionAlignment` **before** submitting (the backend's `ValidateRequest` throws lazily on the first `MoveNextAsync`, i.e. after the candidate already exists).
- [ ] **8 — Engine img2img + interrupt**: `InterruptAsync` on the wrapper + interface; patcher img2img injection + tests; backend uploads and interrupts.
- [ ] **9 — Unified Console tracing** at every step boundary: resolve backend → resolve model → compose region (with coverage %) → submit → each progress phase → save → stage → accept/discard → cancel.
- [ ] **10 — Docs + cleanup**: Shortcuts.md "Diffusion Canvas" section, REUSABLES.md row, delete the `canvas OK` diagnostic, fix the two contradictory selector comments' fate (both leave with the `ItemsControl`), correct the stale `InitImage` doc.
- [ ] **11 — Verify**: full solution build, full test run, then a manual GUI smoke (owed by the user: pan/zoom/fit, box move+resize, generate on empty canvas, generate over a result, accept/discard, cancel mid-batch).
