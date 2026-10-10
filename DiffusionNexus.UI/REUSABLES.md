# Reusable UI Library — DiffusionNexus.UI

**Rule (from `.github/copilot-instructions.md`): before creating any new UI element,
check this catalog first.** Reuse or extend what is here; only add a new control when
nothing listed fits. When you *do* add a reusable piece, add a row here in the same commit.

All paths are relative to `DiffusionNexus.UI/`.

---

## 1. Base classes — start here

| Type | Path | Use when |
|------|------|----------|
| `ViewBase<TViewModel>` | [Views/ViewBase.cs](Views/ViewBase.cs) | A **feature view** that owns its ViewModel. Handles service injection. |
| `ControlBase` | [Views/Controls/ControlBase.cs](Views/Controls/ControlBase.cs) | A **reusable control** that inherits DataContext from its parent. Re-injects services when DataContext changes. |
| `ViewModelBase` | [ViewModels/ViewModelBase.cs](ViewModels/ViewModelBase.cs) | Every ViewModel. |
| `BusyViewModelBase` | [ViewModels/BusyViewModelBase.cs](ViewModels/BusyViewModelBase.cs) | ViewModel with `IsBusy` / `BusyMessage` (implements `IBusyViewModel`). |
| `IDialogServiceAware` | [Services/IDialogService.cs](Services/IDialogService.cs) | Marker: get `IDialogService` auto-injected by `ViewBase`/`ControlBase`. Do **not** new up `DialogService` yourself. |
| `IRefreshableTab` | [ViewModels/IRefreshableTab.cs](ViewModels/IRefreshableTab.cs) | A tab that exposes a per-tab refresh button. |
| `ViewLocator` | [ViewLocator.cs](ViewLocator.cs) | VM to View resolution by naming convention — new view + VM pairs wire up automatically. |

---

## 2. Reusable controls ([Views/Controls/](Views/Controls/))

### Images and media
| Control | Purpose |
|---------|---------|
| `SingleImageSlotControl` | One drop/browse image slot. Use for any "pick an input image" spot. |
| `ImageListInputControl` | Multi-image input list with add/remove. |
| `SelectableImageResultsView` (+ `SelectableImageResultsViewModel`) | Grid of result images with selection. Standard output surface for generation flows. |
| `ImageActionsBar` (+ `ImageActionsViewModel`) | "Add Selected To… / Send Selected To…" toolbar (Dataset, Training Run / Image Editor, Comparer, Batch Upscale, Batch Crop, Captioning, Workflows → Anime-To-Real, Image Edit, Batch Metadata Distiller). Each destination has a `Show*` flag. |
| `ImageStatusStrip` (+ `ImageStatusItemViewModel`) | Per-image processing status strip. |
| `ImageCompareControl` | Side-by-side / slider before-after compare. `CompareFitMode` lives in [Controls/](Controls/). |
| `ImageMetadataPanelView` (+ `ImageMetadataPanelViewModel`) | Generation-metadata panel for a selected image. |
| `RatingButtonsControl` (+ `RatingViewModel`) | Approve/reject/rating buttons. |
| `VideoPlayerControl` | [Controls/VideoPlayerControl.axaml](Controls/VideoPlayerControl.axaml) — video playback. |
| `ImageEditorControl` | [Controls/ImageEditorControl.cs](Controls/ImageEditorControl.cs) — canvas editing surface. |
| `ImageDragPreview`, `ImageFileTransfer` | Drag/drop plumbing for image files — reuse instead of hand-rolling `DataObject` code. |
| `DiffusionCanvasSurface` | [Views/Controls/DiffusionCanvasSurface.cs](Views/Controls/DiffusionCanvasSurface.cs) — world-space pan/zoom surface over an unbounded canvas, with a movable/resizable marching-ants bounding box whose handles keep a constant screen size at any zoom. Pair it with `CanvasViewport` + `GenerationBoundingBox` in [DiffusionCanvas/](DiffusionCanvas/). Prefer this over another `ZoomBorder`: its transform is a plain object you can unit-test, and it has no gesture-rotation surprise. Right-click on a result opens a flyout bound to `DeleteRasterCommand`; the marching-ants box is drawn in its own child layer so the animation never repaints the rasters underneath. Draws each raster's `IsVisible`/`Opacity` and outlines `SelectedRaster` (the layer panel's selection). |
| `LayerStackPanel` (+ `ILayerStackItem`, `LayerStackNaming`) | [Views/Controls/LayerStackPanel.axaml](Views/Controls/LayerStackPanel.axaml) — layer list: per-row show/hide, thumbnail, double-click rename, lock, opacity text, under a ↑/↓/− toolbar with a `LeadingTools` slot for host buttons. The host supplies rows top-first (`Items`, `SelectedItem`) and owns `MoveUpCommand`/`MoveDownCommand`/`DeleteCommand`; renames commit through `LayerStackNaming.Resolve`. Used by the Image Editor and the Diffusion Canvas. |

### Model / LoRA pickers
| Control | Purpose |
|---------|---------|
| `SearchableBaseModelPicker` (+ `SearchableBaseModelPickerViewModel`) | **The** base-model ComboBox. Never bind a raw `ComboBox` to the base-model catalog. |
| `MultiLoraPickerControl` (+ `LoraPickerItemViewModel`) | Mandatory/optional multi-LoRA selection over `ILoraCatalog`. It does **not** filter — the owner hands it an already-filtered list. |
| `ModelBaseModelLabels` ([Services/Lora/](Services/Lora/ModelBaseModelLabels.cs)) | Which raw Civitai base-model labels a generation model's LoRAs are published under. Authored, never derived: a descriptor's `DisplayName` is not the Civitai label. Use this to build the filter you pass to `ILoraCatalog`, and never pass null — the catalog reads null as "return every installed LoRA". |
| `ModelTileControl` (+ `ModelTileViewModel`, `ModelTileDependencies`) | Model card tile (grid item). |
| `ModelDetailView` (+ `ModelDetailViewModel`) | Model detail panel — shared by the Browse and Installed tabs. |

### Generation inputs
| Control | Purpose |
|---------|---------|
| `OutputResolutionControl` (+ `OutputResolutionViewModel`) | Width/height/aspect-ratio picker. |
| `CaptionEditorControl` | Caption text editing with tag handling. |
| `SpellCheckTextBox` | [Controls/SpellCheckTextBox.cs](Controls/SpellCheckTextBox.cs) — TextBox with spell check (`TextHighlightRange`). |

### Status, logging, diagnostics
| Control | Purpose |
|---------|---------|
| `UnifiedConsoleView` (+ `UnifiedConsoleViewModel`) | The unified console. **Standing rule: every new feature logs its working steps here.** |
| `ActivityLogPanel` (+ `ActivityLogViewModel`) | Scoped activity log panel. |
| `StatusBarControl` (+ `StatusBarViewModel`) | App status bar. |
| `ResourceMonitorView` (+ `ResourceMonitorViewModel`) | CPU/GPU/VRAM monitor. |
| `FeatureReadinessPanel` (+ `FeatureReadinessViewModel`) | "Is this feature ready to run" preflight panel. Also shows "Running on <backend> · change" (change only for the features in `FeatureBackendRouter.ServerModeFeatures` — Inpaint, Outpaint, Outpaint Vision, Batch Upscale, Batch Upscale Vision — opens Settings at ComfyUI Server) and, when the Engine answered with something missing, "Not installed on the Engine · Install …" (opens the Engine Features dialog with that row ticked). Both links need the optional 3rd constructor parameter `IDatasetEventAggregator`. |

### Charts
| Control | Purpose |
|---------|---------|
| `RadarChart` | [Views/Controls/RadarChart.cs](Views/Controls/RadarChart.cs) |
| `ScoreTrendChart` | [Views/Controls/ScoreTrendChart.cs](Views/Controls/ScoreTrendChart.cs) |

### Icons
| Control | Purpose |
|---------|---------|
| `CommunityLinkIcon` | [Views/Controls/CommunityLinkIcon.cs](Views/Controls/CommunityLinkIcon.cs) — monochrome vector glyph for a community-link `icon` key (`youtube`, `patreon`, `civitai`, `globe`, `mail`, `linktree`), drawn in the inherited `Foreground`; unknown keys get a neutral link glyph. Same glyphs as the 3.x installer. Use it wherever a community link is shown, and add a new key's path here rather than shipping another PNG. |

### Datasets
| Control | Purpose |
|---------|---------|
| `DatasetVersionSelectorControl` | Dataset + version selection. |

---

## 3. Dialogs — always go through `IDialogService`

**Never open a `Window` directly from a ViewModel.** Everything below is exposed as a
method on [IDialogService](Services/IDialogService.cs); that interface is the API surface
and is what keeps ViewModels testable.

Generic building blocks — try these before writing a new dialog:

| Method | Dialog |
|--------|--------|
| `ShowMessageAsync` | `MessageDialog` |
| `ShowConfirmAsync` | `ConfirmDialog` (Yes/No) |
| `ShowInputAsync` | `TextInputDialog` |
| `ShowOptionsAsync` | `OptionsDialog` (N buttons, returns index) |
| `ShowOpenFileDialogAsync` / `ShowSaveFileDialogAsync` / `ShowOpenFolderDialogAsync` | Storage pickers |
| `ShowFileDropDialogAsync` (+ `...WithConflictDetectionAsync`) | `FileDropDialog`, optionally chained into `FileConflictDialog` |
| `ShowImageViewerDialogAsync` | `ImageViewerDialog` — full-screen browse, rating, favorites, metadata |

Domain dialogs already covered (check here before building a new one): dataset create /
version / export / add-to, training-run create / export, caption compare, captioning and
captioning models, Civitai token, assign Civitai IDs, download LoRA (and version),
download preflight, sync plan / sync report, workloads / workload details / core
workloads, VRAM selection, add / edit / remove installation, backup compare, feedback,
save-as, replace image, file conflict, select versions to delete (dataset and LoRA).

Fixer **windows** (long-running triage surfaces, also reached via `IDialogService`):
`DuplicateFixerWindow`, `LoraDuplicateFixerWindow`, `ColorFixerWindow`,
`ImageQualityFixerWindow`.

---

## 4. Converters ([Converters/](Converters/))

Check [Converters/BoolConverters.cs](Converters/BoolConverters.cs) **first** — it is a
static grab-bag of roughly 25 ready-made `IValueConverter` instances (`Not`,
`BoolToOpacity`, `BoolToSelectionBorder`, `PercentageToWidth`, `RatingStatusTo*`,
`BoolToProgressBrush`, approve/reject brushes, crop-ratio flags, ...). Most new
"bool to brush / visibility / size" needs are already there; extend that class rather
than adding a one-off converter file.

Others worth knowing:

- `PathToBitmapConverter` + `ThumbnailMultiConverter` — image loading in bindings. Use
  these instead of constructing a `Bitmap` in a ViewModel property.
- `EnumEqualsToBrushConverter` — generic enum to brush; prefer it over new enum converters.
- `AspectRatioToWidthConverter`, `BoolToStretchConverter`, `CompareFitMode*`,
  `DatasetTypeDisplayConverter`, `LogCategoryDisplayConverter`,
  `ImageProcessingStatusToBrushConverter`, `UpscaleEnumDisplayConverter`.

---

## 5. Helpers, utilities, services

| Type | Path | Purpose |
|------|------|---------|
| `BatchObservableCollection<T>` | [Utilities/BatchObservableCollection.cs](Utilities/BatchObservableCollection.cs) | Bulk add without per-item `CollectionChanged` storms. |
| `BatchedListFiller` | [Helpers/BatchedListFiller.cs](Helpers/BatchedListFiller.cs) | Progressive list fill that keeps the UI responsive. |
| `FileSizeFormatter` | [Helpers/FileSizeFormatter.cs](Helpers/FileSizeFormatter.cs) | Bytes to human-readable size. |
| `HtmlTextHelper` | [Helpers/HtmlTextHelper.cs](Helpers/HtmlTextHelper.cs) | Civitai HTML to display text. |
| `SafeAssetExtension` | [Markup/SafeAssetExtension.cs](Markup/SafeAssetExtension.cs) | XAML markup extension for assets that may be missing (pairs with `SafeAssetBitmap`). |
| `IUiScheduler` / `AvaloniaUiScheduler` | [Services/](Services/) | Marshal to the UI thread — inject this rather than calling `Dispatcher.UIThread` from a ViewModel. |
| `IThumbnailOrchestrator` / `ThumbnailService` / `LruKeyTracker` | [Services/](Services/) | Thumbnail generation and caching. Any new image grid uses this. |
| `UrlLauncher` | [Services/UrlLauncher.cs](Services/UrlLauncher.cs) | Opening a web URL in the system browser. Give the ViewModel a swappable `Action<string>` opener defaulting to `UrlLauncher.Open` (tests capture the URL) and launch through `UrlLauncher.TryOpen(url, opener, logger, source)`, which logs a warning instead of throwing when there is no default browser. Never call `Process.Start(url)` from a command. |
| `CommunityLinkSlots` | [ViewModels/CommunityLinkSlots.cs](ViewModels/CommunityLinkSlots.cs) | Fitting a list of any length into a fixed number of slots: all items when they fit, otherwise all but the last slot plus a "More" overflow (`Split` returns inline + overflow). Used by the sidebar's community links; reuse it for any "N visible, the rest behind More" list. |
| `AvaloniaClipboardService` | [Services/AvaloniaClipboardService.cs](Services/AvaloniaClipboardService.cs) | Clipboard access. |
| `FileConflictDetector`, `IFileOperations` / `FileOperations`, `MediaFileExtensions` | [Utilities/](Utilities/) | File-level helpers backing the dialogs above. |
| `FilePaths.AreSame` | [Utilities/FilePaths.cs](Utilities/FilePaths.cs) | Whether two paths name the same file or folder: full paths, trailing separators and casing forgiven, never throws. Use it for any "is this that file/folder" check instead of a raw string compare. For "is this under that folder" use `Domain.Utilities.LocalPathRoots.IsUnder`. |
| `WorkflowIds` | [Services/Pipelines/WorkflowIds.cs](Services/Pipelines/WorkflowIds.cs) | The built-in Workflow ids (`AnimeToReal`, `ImageToImage`, `BatchMetadataDistiller`, …). Reference these (`x:Static` in XAML) instead of spelling an id: a check against a misspelt copy fails open. |
| `ZipMediaExtractor`, `FileDropSelectionHelper` | [Utilities/ZipMediaExtractor.cs](Utilities/ZipMediaExtractor.cs), [Utilities/FileDropSelectionHelper.cs](Utilities/FileDropSelectionHelper.cs) | Expand a dropped ZIP's media entries into a flat temp folder and merge/finalize a file-drop selection; pure, unit-tested, used by `FileDropDialog`. Every `ShowFileDropDialogAsync` result carries `TemporaryDirectories`; the caller deletes them after its copy step via `ZipMediaExtractor.DeleteExtractionDirectories` (only our own prefixed folders are ever touched). |
| `ImageRatingStore` | [Services/ImageRatingStore.cs](Services/ImageRatingStore.cs) | The Ready / Trash rating of a media file, kept in one `.ratings.json` per folder (keyed by full file name). Reads fall back to legacy per-image `.rating` sidecars until the folder has a ratings file; the first rating change in a folder converts it. Any code that deletes, moves, renames or copies a rated file calls `Remove` / `Move` / `Copy` so the rating follows it, and a newly copied file gets `Set(path, Unrated)` — never touch rating files directly. Rating many files at once (a version copy): `SetMany`, one write per folder. Non-media paths (captions) are ignored. |
| `MaskFeathering` | [ImageEditor/MaskFeathering.cs](ImageEditor/MaskFeathering.cs) | The one feather rule for inpaint masks (dilate by half the radius, blur by the radius), shared by the Image Editor's Inpaint tool and the Diffusion Canvas's mask layer. Any new mask feather goes through it, so a feather value means the same on every screen. |
| `CanvasMaskRasterizer` (+ `CanvasMaskStroke`) | [DiffusionCanvas/CanvasMaskRasterizer.cs](DiffusionCanvas/CanvasMaskRasterizer.cs) | World-space brush/eraser strokes to an opaque grey `MaskImage` (white = repaint) for any region: feather, invert, repaint share, and `IsEmpty` / `EmptyReason` (paint before the feather must be real, and the feathered mask inside the region strong enough to change something; the reason names erased / out of reach / too faint / inverted covers all). Pure and unit-tested; the canvas surface draws its overlay with the same `DrawStrokes`. |
| `CanvasMaskCompositor` | [DiffusionCanvas/CanvasMaskCompositor.cs](DiffusionCanvas/CanvasMaskCompositor.cs) | Puts the original pixels back outside a mask after an inpaint run (backends VAE-decode the whole latent, so the "kept" area comes back altered). Build one per batch from the region and the mask, call `KeepUnmasked(png)` per result; null means "keep the result as it is". |
| `CanvasBrush` | [DiffusionCanvas/CanvasPaintTool.cs](DiffusionCanvas/CanvasPaintTool.cs) | The canvas brush's size rule: `MinSize`/`MaxSize`, `Clamp`, and `Step` (a quarter per step, already clamped). Every writer of a brush size goes through it, so a two-way binding can never hold a size the view model refused. |
| `QwenVlGguf` | [Services/Vision/QwenVlGguf.cs](Services/Vision/QwenVlGguf.cs) | The Qwen3-VL GGUF describer's settings for the `SimpleQwenVLggufV2` ComfyUI node: the model/projector names to look up in readiness `ModelPaths` (`TryGetPaths`), the `config_override` JSON (`BuildConfig`) and the node's 32-bit `Seed`. Any feature that runs that node uses it, so every describer loads the same model the same way. |
| `ImageDescriber` (+ `ImageDescriptionFailedException`) | [Services/Vision/ImageDescriber.cs](Services/Vision/ImageDescriber.cs) | Describes an uploaded image with Qwen3-VL on a ComfyUI server (`Qwen3-VL-Describe.json`). `DescribeAsync(keepLoaded)` keeps the model loaded between images of a batch; `FreeAsync` unloads it when a batch stops early (time-limited, never throws). The node reports a failed inference as text; that becomes `ImageDescriptionFailedException`, never a description. |
| `DatasetEventAggregator` (`IDatasetEventAggregator`) | [Services/DatasetEventAggregator.cs](Services/DatasetEventAggregator.cs) | Cross-view dataset state sync — use instead of ad-hoc events between tabs. |
| `ScrollKeyNavigation` | [Behaviors/ScrollKeyNavigation.cs](Behaviors/ScrollKeyNavigation.cs) | `behaviors:ScrollKeyNavigation.IsEnabled="True"` on a `ScrollViewer`: Home / End / Page Up / Page Down scroll it, and a click inside focuses it. Text boxes, sliders, NumericUpDowns, lists and trees keep their own keys, so does another key-scrolling area (behavior switched on) that holds focus, and a grid that fits its viewport leaves the key alone. For a view's main grid also call `ScrollKeyNavigation.ForwardKeys(this, scrollViewer)` once from the constructor: it puts a tunnel handler on the window while the view is attached, so the keys work with focus anywhere — right after opening a page focus is on the module button in the sidebar, and a focused tab header would otherwise switch tabs. Use this on any new scrolling grid instead of hand-rolling key handlers. |

---

## 6. Known gaps

- **No shared style / theme resource dictionary.** `App.axaml` pulls in only `FluentTheme`
  plus the ColorPicker and DataGrid themes; every view declares its own brushes, paddings
  and `Style` blocks inline, so colors and spacing drift between views. If you find
  yourself pasting the same `<Style>` into a third view, that is the signal to create
  `Styles/Shared.axaml`, merge it in `App.axaml`, and note it here.
- The `Controls/` vs `Views/Controls/` split is historical, not meaningful. New reusable
  controls go in `Views/Controls/` next to their peers.
