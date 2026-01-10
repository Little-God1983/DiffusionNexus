# LoRA Helper Module

The LoRA Helper is the primary feature of DiffusionNexus V2, providing a visual tile-based browser for LoRA models.

## Architecture

```
???????????????????????????????????????????????????????????????
?                    LoraHelperView                           ?
?              (Toolbar + Tile Grid + Filters)                ?
???????????????????????????????????????????????????????????????
?                  LoraHelperViewModel                        ?
?         (Search, Filter, Pagination, Demo Data)             ?
???????????????????????????????????????????????????????????????
?   ModelTileControl          ?    ModelTileViewModel         ?
?   (XAML Template)           ?    (Display Logic)            ?
???????????????????????????????????????????????????????????????
?                  BaseModelDisplayMapper                     ?
?           (Short names, icons, tooltips)                    ?
???????????????????????????????????????????????????????????????
```

## Components

### BaseModelDisplayMapper

Located in `DiffusionNexus.Domain/Utilities/BaseModelDisplayMapper.cs`

Converts long Civitai base model names to short display labels.

```csharp
// Examples:
BaseModelDisplayMapper.GetShortName("SDXL 1.0")      // "XL"
BaseModelDisplayMapper.GetShortName("SD 1.5")        // "1.5"
BaseModelDisplayMapper.GetShortName("Pony")          // "Pony" + ?? icon
BaseModelDisplayMapper.GetShortName("Illustrious")   // "IL"
BaseModelDisplayMapper.GetShortName("Z-Image-Turbo") // "ZIT" + ? icon

// Full display info
var info = BaseModelDisplayMapper.GetDisplayInfo("Pony");
// info.ShortName = "Pony"
// info.Icon = "??"
// info.ToolTip = "Pony Diffusion"

// Multiple base models
BaseModelDisplayMapper.FormatMultiple(["SDXL 1.0", "SD 1.5"]);
// Returns: "XL, 1.5"
```

**Mappings:**

| Raw Name | Short Name | Icon | Notes |
|----------|-----------|------|-------|
| SD 1.5 | 1.5 | - | Stable Diffusion 1.5 |
| SDXL 1.0 | XL | - | Stable Diffusion XL |
| Pony | Pony | ?? | Pony Diffusion |
| Illustrious | IL | - | Illustrious |
| Flux.1 D | Flux D | - | Flux Dev |
| Z-Image-Turbo | ZIT | ? | Custom turbo model |
| Wan Video * | Wan * | ?? | Video models |
| SVD * | SVD * | ?? | Video diffusion |

### ModelTileViewModel

Located in `DiffusionNexus.UI-V2/ViewModels/ModelTileViewModel.cs`

ViewModel for a single model tile with:
- **Computed Properties**: DisplayName, BaseModelsDisplay, DownloadCountDisplay
- **Commands**: OpenOnCivitai, CopyTriggerWords, OpenFolder
- **Thumbnail Loading**: From BLOB data in ModelImage

```csharp
public string BaseModelsDisplay
{
    get
    {
        // Collects all unique base models from versions
        // Formats each using BaseModelDisplayMapper
        // Returns: "?? Pony, XL" or "1.5" etc.
    }
}
```

### ModelTileControl

Located in `DiffusionNexus.UI-V2/Views/Controls/ModelTileControl.axaml`

XAML template for model tiles:

```
???????????????????????????????
? [NSFW]            [2 vers] ? ? Badges
?                             ?
?      [Thumbnail/Preview]    ?
?                             ?
???????????????????????????????
? Model Name                  ?
? Creator                     ?
???????????????????????????????
? [?? Pony, XL]       ? 25K  ? ? Base models + downloads
???????????????????????????????
```

**Features:**
- Thumbnail display with placeholder
- NSFW badge (red)
- Version count badge (blue)
- Base model chips with icons
- Download count with K/M formatting
- Hover overlay with action buttons

### LoraHelperViewModel

Located in `DiffusionNexus.UI-V2/ViewModels/LoraHelperViewModel.cs`

Main ViewModel managing:
- **Collections**: AllTiles, FilteredTiles
- **Filtering**: SearchText, ShowNsfw
- **Commands**: Refresh, DownloadMissingMetadata, ScanDuplicates
- **Demo Data**: Pre-populated sample models for testing

### LoraHelperView

Located in `DiffusionNexus.UI-V2/Views/LoraHelperView.axaml`

Main view with:
- **Toolbar**: Search, NSFW toggle, action buttons
- **Model Count**: "X of Y models"
- **Tile Grid**: UniformGridLayout with ItemsRepeater
- **Empty State**: Shown when no models found
- **Loading Overlay**: Shown during async operations

## Usage

### Adding a New Base Model Mapping

Edit `BaseModelDisplayMapper.cs`:

```csharp
// In the Mappings dictionary:
["New Base Model"] = new("NBM", "??", "New Base Model Full Name"),
```

### Creating a Demo Model

```csharp
var model = LoraHelperViewModel.CreateDemoModel(
    name: "My Model",
    creator: "CreatorName",
    baseModels: new[] { "SDXL 1.0", "Pony" },
    downloads: 15000
);
```

## Future Enhancements

1. **File Scanner Service** - Scan folders for .safetensors files
2. **Database Integration** - Load/save from SQLite
3. **Civitai Sync** - Download metadata by hash
4. **Thumbnail Generation** - Create previews from videos
5. **Folder Tree** - Filter by directory structure
6. **Sorting** - By name, date, downloads
7. **Details View** - Full model information panel

## File Locations

| Component | Path |
|-----------|------|
| BaseModelDisplayMapper | `DiffusionNexus.Domain/Utilities/BaseModelDisplayMapper.cs` |
| ModelTileViewModel | `DiffusionNexus.UI-V2/ViewModels/ModelTileViewModel.cs` |
| ModelTileControl | `DiffusionNexus.UI-V2/Views/Controls/ModelTileControl.axaml` |
| LoraHelperViewModel | `DiffusionNexus.UI-V2/ViewModels/LoraHelperViewModel.cs` |
| LoraHelperView | `DiffusionNexus.UI-V2/Views/LoraHelperView.axaml` |
