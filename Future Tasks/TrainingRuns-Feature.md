# Training Runs Feature — Dataset Management Redesign

## Problem Statement

The current Dataset Management design couples **training input** (images + captions) with **training output** (Epochs, Presentation, Notes, Release) at the version level. This creates a 1:1 relationship between a dataset version and a trained LoRA.

**Real-world scenario:** Training 3 LoRAs for 3 different Base Models (e.g., SDXL, Flux, SD 1.5) from the **same dataset version**. The current design forces the user to duplicate the entire dataset version — copying all images and captions — just to get separate Epochs/Presentation/Notes folders for each LoRA.

---

## Current Architecture

### Folder Structure (Before)

```
DatasetName/
  .dataset/config.json
  V1/                           ← version 1 (a caption variant)
    images + captions            ← training INPUT
    Epochs/                      ← training OUTPUT (coupled!)
    Presentation/                ← training OUTPUT (coupled!)
    Notes/                       ← training OUTPUT (coupled!)
    Release/                     ← training OUTPUT (coupled!)
  V2/                           ← version 2 (branched, different captions)
    images + captions
    Epochs/
    Presentation/
    Notes/
    Release/
```

### Current Sub-Tabs (VersionSubTab enum)

```
Training | Epochs | Notes | Presentation
```

All 4 tabs operate at the version level. No concept of multiple training outputs per version.

---

## Proposed Solution: Training Runs Tab

### Core Concept

Decouple training **input** from training **output** by introducing a "Training Runs" tab that contains all training outputs for a given dataset version. Each Training Run groups its own Epochs, Notes, and Presentation.

### Folder Structure (After)

```
DatasetName/
  .dataset/config.json          ← add TrainingRuns metadata here
  V1/
    image1.png, image1.txt      ← training input (UNCHANGED, shared)
    TrainingRuns/                ← NEW container
      SDXL_MyLoRA/
        Epochs/
        Notes/
        Presentation/
        Release/
      Flux_MyLoRA/
        Epochs/
        Notes/
        Presentation/
        Release/
      SD15_MyLoRA/
        Epochs/
        Notes/
        Presentation/
        Release/
```

---

## UI Design

### Navigation Flow

```
Dataset Card Grid
  → click card → Version Detail View
      → Version Bar: [V1] [V2] [V3] [+]
      → Two top-level tabs:
          📁 Training Data    ← images + captions (UNCHANGED)
          🏋 Training Runs    ← NEW tab, replaces Epochs/Notes/Presentation
              → Training Run cards + [+ Add Training Run] button
              → click a run card → Run Detail View:
                  ← Back to runs
                  Epochs | Notes | Presentation   ← sub-tabs of the run
```

### Visual Layout — Training Runs Tab (Run List)

```
┌─────────────────────────────────────────────────────────┐
│  ← Back    My Character Dataset                         │
│  [V1]  [V2]  [V3]  [+]              ← version bar      │
│─────────────────────────────────────────────────────────│
│  📁 Training Data  │  🏋 Training Runs                   │
│═════════════════════════════════════════════════════════│
│                                                         │
│  ┌─────────────┐ ┌─────────────┐ ┌──────────────┐      │
│  │  SDXL Run   │ │  Flux Run   │ │  + Add       │      │
│  │  3 epochs   │ │  5 epochs   │ │  Training    │      │
│  │  2 notes    │ │  1 note     │ │  Run         │      │
│  │  📅 Mar 10  │ │  📅 Mar 12  │ │              │      │
│  └─────────────┘ └─────────────┘ └──────────────┘      │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

### Visual Layout — Training Run Detail (After Clicking a Run)

```
┌─────────────────────────────────────────────────────────┐
│  ← Back    My Character Dataset                         │
│  [V1]  [V2]  [V3]  [+]              ← version bar      │
│─────────────────────────────────────────────────────────│
│  📁 Training Data  │  🏋 Training Runs                   │
│─────────────────────────────────────────────────────────│
│  ← SDXL Run                       [Rename] [Delete]    │
│  Epochs │ Notes │ Presentation        ← sub-tabs of run │
│═════════════════════════════════════════════════════════│
│                                                         │
│  📦 my_character_sdxl-e10.safetensors    420 MB         │
│  📦 my_character_sdxl-e15.safetensors    420 MB         │
│  📦 my_character_sdxl-e20.safetensors    420 MB         │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

### Why This UI Design Works

| Concern                    | How Addressed                                                                  |
|----------------------------|--------------------------------------------------------------------------------|
| No excessive hidden layers | Only 2 top-level tabs instead of 4; Training Runs is a natural grouping        |
| Discoverability            | `+ Add Training Run` button mirrors the familiar `+New Version` pattern        |
| Reuses existing patterns   | Run cards work like dataset cards; sub-tabs reuse existing Epochs/Notes/Pres.  |
| Training tab untouched     | Zero changes to the image/caption workflow                                     |
| Clear mental model         | INPUT (Training Data) vs OUTPUT (Training Runs) is immediately obvious         |

### Navigation Depth Comparison

```
BEFORE:  Card Grid → Dataset → [V1] → Epochs tab
AFTER:   Card Grid → Dataset → [V1] → Training Runs tab → SDXL Run → Epochs tab
```

One extra click, justified because you're selecting **which** training output to view.

---

## Implementation Plan

### New Files to Create

| File | Purpose |
|------|---------|
| `DiffusionNexus.Domain\Models\TrainingRunInfo.cs` | Metadata model for a training run |
| `DiffusionNexus.Domain\Enums\TrainingRunSubTab.cs` | Enum for run-level sub-tabs (Epochs, Notes, Presentation) |
| `DiffusionNexus.UI\ViewModels\TrainingRunCardViewModel.cs` | ViewModel for run cards in the list |
| `DiffusionNexus.UI\Utilities\TrainingRunMigrationUtility.cs` | Legacy → new structure migration |
| `DiffusionNexus.UI\Views\Tabs\TrainingRunsTabView.axaml` | XAML for the Training Runs tab |
| `DiffusionNexus.UI\Views\Tabs\TrainingRunsTabView.axaml.cs` | Code-behind for the Training Runs tab |
| `DiffusionNexus.Tests\LoraDatasetHelper\TrainingRunMigrationTests.cs` | Unit tests for migration |

### Files to Modify

| File | Change |
|------|--------|
| `DiffusionNexus.Domain\Enums\VersionSubTab.cs` | Simplify to 2 values: `TrainingData=0`, `TrainingRuns=1` (mark old `Training` as `[Obsolete]`) |
| `DiffusionNexus.UI\ViewModels\DatasetCardViewModel.cs` | Mark `EpochsFolderPath`, `NotesFolderPath`, `PresentationFolderPath` as `[Obsolete]`; add `TrainingRuns` metadata |
| `DiffusionNexus.UI\ViewModels\DatasetCardViewModel.DatasetMetadata` | Add `Dictionary<int, List<TrainingRunInfo>> TrainingRuns` |
| `DiffusionNexus.UI\ViewModels\Tabs\DatasetManagementViewModel.cs` | Wire up new 2-tab layout; add Training Runs tab logic |
| `DiffusionNexus.UI\Views\Tabs\DatasetManagementView.axaml` | Replace 4-tab bar with 2-tab bar; add Training Runs list + detail views |

### Files That Stay Unchanged

| File | Reason |
|------|--------|
| `EpochsTabViewModel.cs` | Already accepts folder path via `Initialize(string)` — just receives path from `TrainingRunCardViewModel` instead |
| `NotesTabViewModel.cs` | Same pattern — `Initialize(string)` is path-agnostic |
| `PresentationTabViewModel.cs` | Same pattern — `Initialize(string)` is path-agnostic |
| `EpochsSubTabView.axaml` | View is decoupled from where the path comes from |
| `PresentationSubTabView.axaml.cs` | Same |

---

## Data Model

### TrainingRunInfo (new)

```csharp
namespace DiffusionNexus.Domain.Models;

/// <summary>
/// Metadata for a single training run within a dataset version.
/// Serialized as part of the dataset's config.json.
/// </summary>
public class TrainingRunInfo
{
    /// <summary>
    /// Display name (also used as folder name under TrainingRuns/).
    /// Example: "SDXL_MyCharacter", "Flux_MyCharacter"
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional base model identifier (e.g., "SDXL", "Flux", "SD 1.5", "Pony").
    /// </summary>
    public string? BaseModel { get; set; }

    /// <summary>
    /// When this training run was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// Optional short description or training parameters summary.
    /// </summary>
    public string? Description { get; set; }
}
```

### TrainingRunSubTab (new enum)

```csharp
namespace DiffusionNexus.Domain.Enums;

public enum TrainingRunSubTab
{
    Epochs = 0,
    Notes = 1,
    Presentation = 2
}
```

### VersionSubTab (modified)

```csharp
namespace DiffusionNexus.Domain.Enums;

public enum VersionSubTab
{
    [Obsolete("Use TrainingData instead.")]
    Training = 0,

    TrainingData = 0,
    TrainingRuns = 1
}
```

### config.json Extension

```json
{
  "CurrentVersion": 1,
  "VersionDescriptions": { "1": "Base captions" },
  "VersionBranchedFrom": {},
  "VersionNsfwFlags": {},
  "TrainingRuns": {
    "1": [
      {
        "Name": "SDXL_MyCharacter",
        "BaseModel": "SDXL",
        "CreatedAt": "2025-03-10T14:30:00+01:00",
        "Description": "lr=1e-4, 20 epochs, dim=32"
      },
      {
        "Name": "Flux_MyCharacter",
        "BaseModel": "Flux",
        "CreatedAt": "2025-03-12T09:15:00+01:00",
        "Description": "lr=5e-5, 15 epochs, dim=16"
      }
    ]
  }
}
```

---

## Legacy Migration

### Detection

A version folder uses the **legacy layout** if any of these folders exist with content directly under the version root:
- `V1/Epochs/`
- `V1/Notes/`
- `V1/Presentation/`
- `V1/Release/`

### Migration Strategy

```
V1/Epochs/        → V1/TrainingRuns/Default/Epochs/
V1/Notes/         → V1/TrainingRuns/Default/Notes/
V1/Presentation/  → V1/TrainingRuns/Default/Presentation/
V1/Release/       → V1/TrainingRuns/Default/Release/
```

A `TrainingRunInfo` with `Name = "Default"` and `Description = "Migrated from legacy layout"` is created in config.json.

### Migration Utility

```csharp
namespace DiffusionNexus.UI.Utilities;

public static class TrainingRunMigrationUtility
{
    private static readonly string[] OutputFolders = ["Epochs", "Notes", "Presentation", "Release"];
    private const string DefaultRunName = "Default";
    private const string TrainingRunsFolder = "TrainingRuns";

    public static bool IsLegacyLayout(string versionFolderPath);
    public static bool HasTrainingRunsStructure(string versionFolderPath);
    public static TrainingRunInfo? MigrateLegacyLayout(string versionFolderPath);
    public static string CreateTrainingRunFolder(string versionFolderPath, string runName);
    public static List<string> GetTrainingRunNames(string versionFolderPath);
}
```

---

## Implementation Order

1. **`TrainingRunInfo` model** + **`TrainingRunSubTab` enum** — pure data, no dependencies
2. **`TrainingRunMigrationUtility`** + **unit tests** — can be tested in isolation
3. **`TrainingRunCardViewModel`** — reuses existing patterns from `VersionButtonViewModel`
4. **Update `VersionSubTab` enum** — mark old values `[Obsolete]`
5. **Update `DatasetCardViewModel`** — add `TrainingRuns` metadata, mark old path properties `[Obsolete]`
6. **Update `DatasetMetadata`** (inner class) — add `TrainingRuns` dictionary
7. **Create `TrainingRunsTabView.axaml`** — run list + detail view
8. **Wire into `DatasetManagementViewModel`** — replace 4-tab with 2-tab, add run selection logic
9. **Update `DatasetManagementView.axaml`** — XAML changes for new tab layout

---

## Key Design Decisions

- **Existing `EpochsTabViewModel`, `NotesTabViewModel`, `PresentationTabViewModel` need ZERO logic changes** — they already accept a folder path via `Initialize(string versionFolderPath)`. Only the caller changes (from `DatasetCardViewModel.EpochsFolderPath` to `TrainingRunCardViewModel.EpochsFolderPath`).
- **The `+ Add Training Run` button** follows the same UX pattern as `+New Version` for consistency.
- **Run cards** are similar to dataset cards — showing name, base model, epoch count, note count, and creation date.
- **Backward compatible** — legacy datasets auto-migrate on first access, similar to existing legacy → versioned migration in `DatasetVersionUtilities`.

---

## Notes

- Per project rules: before modifying any database entities, run `publish.ps1` first.
- Per project rules: mark refactored methods/properties as `[Obsolete]` before removal.
- `TODO: Linux Implementation for Training Run folder creation` should be added where filesystem operations occur.
