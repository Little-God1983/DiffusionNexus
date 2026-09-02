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
| `ImageActionsBar` (+ `ImageActionsViewModel`) | Per-image action row (send to editor, save as, delete, ...). |
| `ImageStatusStrip` (+ `ImageStatusItemViewModel`) | Per-image processing status strip. |
| `ImageCompareControl` | Side-by-side / slider before-after compare. `CompareFitMode` lives in [Controls/](Controls/). |
| `ImageMetadataPanelView` (+ `ImageMetadataPanelViewModel`) | Generation-metadata panel for a selected image. |
| `RatingButtonsControl` (+ `RatingViewModel`) | Approve/reject/rating buttons. |
| `VideoPlayerControl` | [Controls/VideoPlayerControl.axaml](Controls/VideoPlayerControl.axaml) — video playback. |
| `ImageEditorControl` | [Controls/ImageEditorControl.cs](Controls/ImageEditorControl.cs) — canvas editing surface. |
| `ImageDragPreview`, `ImageFileTransfer` | Drag/drop plumbing for image files — reuse instead of hand-rolling `DataObject` code. |

### Model / LoRA pickers
| Control | Purpose |
|---------|---------|
| `SearchableBaseModelPicker` (+ `SearchableBaseModelPickerViewModel`) | **The** base-model ComboBox. Never bind a raw `ComboBox` to the base-model catalog. |
| `MultiLoraPickerControl` (+ `LoraPickerItemViewModel`) | Mandatory/optional multi-LoRA selection over `ILoraCatalog`. |
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
| `FeatureReadinessPanel` (+ `FeatureReadinessViewModel`) | "Is this feature ready to run" preflight panel. |

### Charts
| Control | Purpose |
|---------|---------|
| `RadarChart` | [Views/Controls/RadarChart.cs](Views/Controls/RadarChart.cs) |
| `ScoreTrendChart` | [Views/Controls/ScoreTrendChart.cs](Views/Controls/ScoreTrendChart.cs) |

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
| `AvaloniaClipboardService` | [Services/AvaloniaClipboardService.cs](Services/AvaloniaClipboardService.cs) | Clipboard access. |
| `FileConflictDetector`, `IFileOperations` / `FileOperations`, `MediaFileExtensions` | [Utilities/](Utilities/) | File-level helpers backing the dialogs above. |
| `DatasetEventAggregator` (`IDatasetEventAggregator`) | [Services/DatasetEventAggregator.cs](Services/DatasetEventAggregator.cs) | Cross-view dataset state sync — use instead of ad-hoc events between tabs. |

---

## 6. Known gaps

- **No shared style / theme resource dictionary.** `App.axaml` pulls in only `FluentTheme`
  plus the ColorPicker and DataGrid themes; every view declares its own brushes, paddings
  and `Style` blocks inline, so colors and spacing drift between views. If you find
  yourself pasting the same `<Style>` into a third view, that is the signal to create
  `Styles/Shared.axaml`, merge it in `App.axaml`, and note it here.
- The `Controls/` vs `Views/Controls/` split is historical, not meaningful. New reusable
  controls go in `Views/Controls/` next to their peers.
