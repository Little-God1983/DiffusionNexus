# Base Model Folders Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A general Base Model Folders registry (settings list + ⭐ default + auto-registration per installation) that feeds a download-target dropdown in the Core Workloads window and the model search roots everywhere, with `%LOCALAPPDATA%\DiffusionNexus\Models` as built-in fallback.

**Architecture:** New `BaseModelFolder` child table on the AppSettings singleton (mirrors `ImageGallery`); a `ModelFolderCatalog` service resolves download targets + search roots; `LocalDiffusionBackendProvider` prepends catalog roots so all consumers see them; `PipelineAssetInstaller.InstallMissingAsync` takes an explicit `downloadRoot`; auto-registration runs on package add and as idempotent startup backfill (ComfyUI roots via `ComfyUiPathDiscovery`, which expands `extra_model_paths.yaml`).

**Tech Stack:** .NET 10, Avalonia, EF Core (SQLite, `DiffusionNexusCoreDbContext`), CommunityToolkit.Mvvm, xunit + Moq + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-07-26-base-model-folders-design.md`

## Global Constraints

- Branch `feature/base-model-folders` off `develop`; single PR to `develop` only.
- Run `publish.ps1` BEFORE modifying entities/EF configs/migrations (repo rule, `.github/copilot-instructions.md`).
- Migration command: `dotnet ef migrations add AddBaseModelFolders --project DiffusionNexus.DataAccess --startup-project DiffusionNexus.UI --context DiffusionNexusCoreDbContext --output-dir Migrations/Core`
- Fallback root constant: `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffusionNexus", "Models")`
- At most one `BaseModelFolder.IsDefault == true` (service enforces, last-set wins).
- No Avalonia global test initialization in unit tests (repo gotcha).
- ComfyUI workload install flow (`WorkloadInstallService`) is untouched.
- TDD every task: failing test → verify fail → implement → verify pass → commit.

---

### Task 1: `BaseModelFolder` entity, EF config, migration

**Files:**
- Create: `DiffusionNexus.Domain/Entities/BaseModelFolder.cs`
- Modify: `DiffusionNexus.Domain/Entities/AppSettings.cs` (~line 41, after `ImageGalleries`)
- Modify: `DiffusionNexus.DataAccess/Configurations/AppSettingsConfiguration.cs` (after `ImageGalleries` HasMany)
- Modify: `DiffusionNexus.DataAccess/Data/DiffusionNexusCoreDbContext.cs` (~line 61, DbSets)
- Create (generated): `DiffusionNexus.DataAccess/Migrations/Core/*_AddBaseModelFolders.cs` + snapshot
- Test: `DiffusionNexus.Tests/DataAccess/Repositories/AppSettingsRepositoryTests.cs` (extend)

**Interfaces produced:**
```csharp
public class BaseModelFolder : BaseEntity
{
    public int AppSettingsId { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int Order { get; set; }
    public bool IsDefault { get; set; }
    public int? InstallerPackageId { get; set; }
    public AppSettings? AppSettings { get; set; }
    public InstallerPackage? InstallerPackage { get; set; }
}
// AppSettings: public ICollection<BaseModelFolder> BaseModelFolders { get; set; } = new List<BaseModelFolder>();
// DbContext:   public DbSet<BaseModelFolder> BaseModelFolders => Set<BaseModelFolder>();
```

- [ ] **Step 1:** Run `.\publish.ps1` (repo rule before entity/migration changes).
- [ ] **Step 2:** Failing test in `AppSettingsRepositoryTests` (follow the file's existing SQLite fixture pattern): add a `BaseModelFolder` to settings, save, reload with includes, assert row + `IsDefault` round-trip. Expect compile failure (`BaseModelFolder` missing).
- [ ] **Step 3:** Create entity (code above); add collection to `AppSettings`; EF config:
```csharp
entity.HasMany(e => e.BaseModelFolders)
    .WithOne(f => f.AppSettings)
    .HasForeignKey(f => f.AppSettingsId)
    .OnDelete(DeleteBehavior.Cascade);
```
plus a separate `builder.Entity<BaseModelFolder>()` block if the file configures children that way for `FolderPath` max length 1000, index on `FolderPath`, and `HasOne(f => f.InstallerPackage).WithMany().HasForeignKey(f => f.InstallerPackageId).OnDelete(DeleteBehavior.SetNull)`. Add DbSet. Include `BaseModelFolders` in `AppSettingsRepository.GetSettingsWithIncludesAsync`.
- [ ] **Step 4:** Generate migration (command in Global Constraints); inspect it (TEXT 1000, FKs cascade/SetNull, indexes).
- [ ] **Step 5:** Test passes; full `DiffusionNexus.Tests` still green. Commit `feat(data): BaseModelFolder entity + migration`.

### Task 2: Repository + AppSettingsService (sync, invariant, targeted APIs)

**Files:**
- Modify: `DiffusionNexus.DataAccess/Repositories/Interfaces/IAppSettingsRepository.cs` (after `RemoveImageGallery`)
- Modify: `DiffusionNexus.DataAccess/Repositories/AppSettingsRepository.cs`
- Modify: `DiffusionNexus.Domain/Services/IAppSettingsService.cs`
- Modify: `DiffusionNexus.Service/Services/AppSettingsService.cs` (snapshot ~line 134, sync block ~line 222)
- Test: create `DiffusionNexus.Tests/Service/Services/AppSettingsServiceBaseModelFolderTests.cs`

**Interfaces produced:**
```csharp
// IAppSettingsRepository
Task AddBaseModelFolderAsync(BaseModelFolder folder, CancellationToken cancellationToken = default);
void RemoveBaseModelFolder(BaseModelFolder folder);
// IAppSettingsService
Task<IReadOnlyList<BaseModelFolder>> GetEnabledBaseModelFoldersAsync(CancellationToken cancellationToken = default);
Task AddBaseModelFolderAsync(string folderPath, int? installerPackageId = null, CancellationToken cancellationToken = default);
```

- [ ] **Step 1:** Failing tests (SQLite in-memory UoW, copy fixture from `AppSettingsServiceCivitaiKeyTests`):
  1. `SaveSettingsAsync` round-trips added/updated/removed `BaseModelFolders` rows (mirror an existing ImageGallery sync test).
  2. Saving two rows with `IsDefault = true` persists only the **last** one as default.
  3. `AddBaseModelFolderAsync("C:\\x", packageId)` twice → one row; second call re-links `InstallerPackageId`; path match is OrdinalIgnoreCase.
  4. `GetEnabledBaseModelFoldersAsync` returns only `IsEnabled`, ordered by `Order`.
- [ ] **Step 2:** Verify failure (missing members).
- [ ] **Step 3:** Implement: repo methods (copy gallery impls); snapshot incoming list in `SaveSettingsAsync` (like `incomingGalleryData`); `SyncChildCollection` call copying `FolderPath/IsEnabled/Order/IsDefault/InstallerPackageId`; after sync enforce invariant:
```csharp
var defaults = existingSettings.BaseModelFolders.Where(f => f.IsDefault).ToList();
foreach (var f in defaults.Take(defaults.Count - 1)) f.IsDefault = false; // keep last
```
(keep the row matching the *last* incoming default; simplest: iterate incoming order). Targeted service methods follow `GetFavoriteLoraSourceAsync`/`AddLoraSourceAsync` patterns (no full save; `_unitOfWork.SaveChangesAsync`).
- [ ] **Step 4:** Tests pass; suite green. Commit `feat(service): BaseModelFolders persistence + single-default invariant`.

### Task 3: Settings export/import round-trip

**Files:**
- Modify: `DiffusionNexus.Domain/Models/SettingsExportData.cs` (`BaseModelFolderExport` record + list, next to `ImageGalleryExport` ~line 106)
- Modify: `DiffusionNexus.Service/Services/SettingsExportService.cs` (export ~line 90 block, import ~line 181 block)
- Test: extend the existing SettingsExportService test file (or create `DiffusionNexus.Tests/Service/Services/SettingsExportServiceBaseModelFolderTests.cs`)

**Interfaces produced:**
```csharp
public sealed record BaseModelFolderExport
{
    public string FolderPath { get; init; } = string.Empty;
    public bool IsEnabled { get; init; } = true;
    public int Order { get; init; }
    public bool IsDefault { get; init; }
}
// SettingsExportData: public List<BaseModelFolderExport> BaseModelFolders { get; init; } = [];
```

- [ ] **Step 1:** Failing test: settings with 2 folders (one default) → export → import into fresh settings → rows + IsDefault preserved. (`InstallerPackageId` intentionally NOT exported — machine-specific.)
- [ ] **Step 2:** Verify fail → implement export/import loops (copy the `ImageGalleries` blocks verbatim, plus `IsDefault`) → pass → commit `feat(service): export/import BaseModelFolders`.

### Task 4: `IModelFolderCatalog`

**Files:**
- Create: `DiffusionNexus.UI/Services/Diffusion/IModelFolderCatalog.cs`
- Create: `DiffusionNexus.UI/Services/Diffusion/ModelFolderCatalog.cs`
- Modify: DI registration site of `PipelineAssetInstaller` (grep `PipelineAssetInstaller` in `DiffusionNexus.UI/App.axaml.cs`; register `IModelFolderCatalog` → `ModelFolderCatalog` with the same lifetime)
- Test: create `DiffusionNexus.Tests/Services/ModelFolderCatalogTests.cs` (Moq `IAppSettingsService`)

**Interfaces produced:**
```csharp
public sealed record ModelFolderOption(string Path, bool IsDefault, bool Exists);

public interface IModelFolderCatalog
{
    /// <summary>Dropdown items: enabled folders, default first; fallback-only when none configured.</summary>
    Task<IReadOnlyList<ModelFolderOption>> GetDownloadTargetsAsync(CancellationToken cancellationToken = default);
    /// <summary>First download target; directory created on demand; falls back to <see cref="FallbackRoot"/> on create failure (logged warning).</summary>
    Task<string> GetDefaultDownloadRootAsync(CancellationToken cancellationToken = default);
    /// <summary>Enabled folders + fallback root, deduped OrdinalIgnoreCase, existing dirs only.</summary>
    Task<IReadOnlyList<string>> GetSearchRootsAsync(CancellationToken cancellationToken = default);
}
// ModelFolderCatalog ctor: (IAppSettingsService settings)
// public static string FallbackRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffusionNexus", "Models");
```

- [ ] **Step 1:** Failing tests:
  1. Empty registry → `GetDownloadTargetsAsync` = exactly `[ (FallbackRoot, IsDefault: true, …) ]`.
  2. Folders A (Order 1), B (Order 0, IsDefault) → targets = [B, A]; `GetDefaultDownloadRootAsync` = B and creates the directory (temp paths).
  3. No default flagged → first enabled by Order is the default target.
  4. `GetSearchRootsAsync` skips nonexistent dirs, dedupes case-insensitively, includes FallbackRoot when it exists.
  5. Default folder uncreatable (invalid char path) → `GetDefaultDownloadRootAsync` returns `FallbackRoot`.
- [ ] **Step 2:** Verify fail → implement → pass → register in DI → commit `feat(ui): ModelFolderCatalog (download targets + search roots)`.

### Task 5: Auto-registration (package add + startup backfill)

**Files:**
- Create: `DiffusionNexus.UI/Services/Diffusion/BaseModelFolderRegistrar.cs`
- Modify: `DiffusionNexus.UI/ViewModels/InstallerManagerViewModel.cs` (in `AddExistingInstallation` success path, next to `LinkOutputFolderAsync` ~line 168)
- Modify: App startup where `OutputsFolderRegistrar.EnsureRegisteredAsync` is called (grep in `App.axaml.cs` / startup data loader) — call backfill alongside it; register registrar in DI
- Test: create `DiffusionNexus.Tests/Services/BaseModelFolderRegistrarTests.cs`

**Interfaces produced:**
```csharp
public sealed class BaseModelFolderRegistrar
{
    public BaseModelFolderRegistrar(IAppSettingsService settingsService);
    /// <summary>Registers all model roots of one package (idempotent by path, links InstallerPackageId).</summary>
    public Task RegisterPackageFoldersAsync(InstallerPackage package, CancellationToken cancellationToken = default);
    /// <summary>Startup backfill over all packages; never throws (logs warnings).</summary>
    public Task EnsureRegisteredAsync(IEnumerable<InstallerPackage> packages, CancellationToken cancellationToken = default);
    internal static IReadOnlyList<string> ResolveModelRoots(InstallerPackage package); // testable core
}
```
Root resolution: `package.Type == InstallerType.ComfyUI` → `ComfyUiPathDiscovery.EnumerateModelSearchPaths(package.InstallationPath)` (own models/ + **extra_model_paths.yaml roots** + portable sibling); other types → `Path.Combine(InstallationPath, "models")` when the directory exists; invalid/missing `InstallationPath` → empty.

- [ ] **Step 1:** Failing tests (temp dirs):
  1. ComfyUI layout (`main.py` + `models/`) + `extra_model_paths.yaml` declaring an existing extra base path → `ResolveModelRoots` returns both.
  2. Forge-style package (`models/` only, Type != ComfyUI) → `[{install}\models]`.
  3. `RegisterPackageFoldersAsync` twice → rows unchanged (idempotent); rows carry `InstallerPackageId`; never sets `IsDefault`.
- [ ] **Step 2:** Verify fail → implement (persist via `IAppSettingsService.AddBaseModelFolderAsync(path, package.Id, ct)`) → pass.
- [ ] **Step 3:** Wire: `await _baseModelFolderRegistrar.RegisterPackageFoldersAsync(package);` in `InstallerManagerViewModel` add-flow (after `LinkOutputFolderAsync`, inside the try); startup backfill next to `OutputsFolderRegistrar` call using packages from `IUnitOfWork.InstallerPackages.GetAllAsync`. Commit `feat(ui): auto-register installation model folders (incl. extra_model_paths.yaml)`.

### Task 6: Root plumbing — provider prepend + installer downloadRoot

**Files:**
- Modify: `DiffusionNexus.UI/Services/Diffusion/LocalDiffusionBackendProvider.cs` (`ResolveModelsRootsAsync` ~line 93)
- Modify: `DiffusionNexus.UI/Services/Pipelines/IPipelineAssetInstaller.cs` (`InstallMissingAsync` ~line 44)
- Modify: `DiffusionNexus.UI/Services/Pipelines/PipelineAssetInstaller.cs` (`InstallMissingAsync`; ctor gains `IModelFolderCatalog`)
- Modify: `DiffusionNexus.UI/ViewModels/PipelinesViewModel.cs` (~lines 94-117, 191-199: "No ComfyUI install" gates can no longer trigger — update copy to reflect roots always exist)
- Test: extend `DiffusionNexus.Tests/InstallerManager/PipelineAssetInstallerTests.cs`

**Interfaces:**
- Consumes: `IModelFolderCatalog.GetSearchRootsAsync` (Task 4).
- Produces: `Task<PipelineReadiness> InstallMissingAsync(PipelineManifest manifest, int vramGb, string downloadRoot, CancellationToken cancellationToken = default);`

- [ ] **Step 1:** Failing tests:
  1. `InstallMissingAsync(manifest, 0, tempRootB, ct)` downloads into `tempRootB\loras` even while `tempRootA` is first in catalog roots (coordinator mock records target via civitai mock + file assertions — reuse existing mock harness; assert enqueue happened and, after simulating success by pre-creating the file, sidecar lands in tempRootB).
  2. Readiness (via `CheckAsync` with provider… keep unit-level: `BuildReadiness(manifest, [catalogRoot, comfyRoot])` already covered — add case where asset exists only in catalog root).
  3. `InstallMissingAsync` with empty registry + zero ComfyUI installs does NOT throw (backend provider mocked/bypassed: pass `downloadRoot` explicitly; remove of throw verified by absence).
- [ ] **Step 2:** Verify fail → implement:
  - `LocalDiffusionBackendProvider.ResolveModelsRootsAsync`: resolve `IModelFolderCatalog` from the scope, `var catalogRoots = await catalog.GetSearchRootsAsync(ct)`, prepend before ComfyUI-derived roots with OrdinalIgnoreCase dedupe; drop the "returns empty when no ComfyUI" doc wording.
  - Installer: remove `roots[0]`/throw block; use the `downloadRoot` parameter; update `CoreWorkloadsViewModel` call site minimally (`await _catalog.GetDefaultDownloadRootAsync(ct)` until Task 7 adds the dropdown).
  - `PipelinesViewModel`: keep null-guards but change message to point at the Workloads dialog (root can no longer be null).
- [ ] **Step 3:** Pass; full suite green; commit `feat(ui): catalog-aware search roots + explicit pipeline download root`.

### Task 7: Core Workloads window dropdown

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/CoreWorkloadsViewModel.cs` (ctor + `LoadAsync` + `CreatePipelineInstallCallback` ~line 222)
- Modify: `DiffusionNexus.UI/Views/Dialogs/CoreWorkloadsDialog.axaml` (Grid row 0 area, above `TabControl`)
- Test: create `DiffusionNexus.Tests/InstallerManager/CoreWorkloadsViewModelTests.cs`

**Interfaces:**
- Consumes: `IModelFolderCatalog.GetDownloadTargetsAsync` / `GetDefaultDownloadRootAsync` (Task 4), `InstallMissingAsync(manifest, vramGb, downloadRoot, ct)` (Task 6).
- Produces (VM):
```csharp
public ObservableCollection<ModelFolderOption> DownloadTargets { get; }
[ObservableProperty] private ModelFolderOption? _selectedDownloadTarget;
// ctor gains IModelFolderCatalog? modelFolderCatalog (nullable like the other optional deps)
```

- [ ] **Step 1:** Failing VM tests: after `LoadAsync` — (a) targets from catalog listed, default preselected; (b) empty registry → single fallback entry selected; (c) install callback passes `SelectedDownloadTarget.Path` to `InstallMissingAsync` (Moq `IPipelineAssetInstaller`… it's an interface — verify arg).
- [ ] **Step 2:** Verify fail → implement VM + XAML:
```xml
<StackPanel Grid.Row="0" Orientation="Horizontal" Spacing="8" Margin="0,0,0,8">
    <TextBlock Text="Diffusion Nexus Core" FontSize="18" FontWeight="Bold" Foreground="White" VerticalAlignment="Center"/>
    <TextBlock Text="Download to:" Foreground="#AAAAAA" VerticalAlignment="Center" Margin="16,0,0,0"/>
    <ComboBox MinWidth="320" ItemsSource="{Binding DownloadTargets}" SelectedItem="{Binding SelectedDownloadTarget}">
        <ComboBox.ItemTemplate>
            <DataTemplate x:DataType="services:ModelFolderOption">
                <TextBlock Text="{Binding Display}"/>
            </DataTemplate>
        </ComboBox.ItemTemplate>
    </ComboBox>
</StackPanel>
```
(`Display` = computed property on `ModelFolderOption`: `Path` + `" (default)"` when `IsDefault` — add to the record in Task 4 if not present.) Selection is per-window; nothing written back to settings.
- [ ] **Step 3:** Pass; commit `feat(ui): download-target dropdown in Core Workloads window`.

### Task 8: Settings UI — "Base Model Folders" section

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/SettingsViewModel.cs` (collection + row VM + commands + Load ~line 373 / Save ~line 569 mapping)
- Modify: `DiffusionNexus.UI/Views/SettingsView.axaml` (new Expander after Generation Galleries ~line 400)
- Test: create `DiffusionNexus.Tests/ViewModels/SettingsViewModelBaseModelFolderTests.cs` (no Avalonia init; construct VM with mocked services like existing settings VM tests)

**Interfaces produced (row VM, mirrors `LoraSourceViewModel` + `ImageGalleryViewModel`):**
```csharp
public partial class BaseModelFolderViewModel : ObservableObject
{
    public int Id { get; set; }
    public int? InstallerPackageId { get; set; }
    [ObservableProperty] private string _folderPath = string.Empty;
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private bool _isDefault;
    public int Order { get; set; }
    public event EventHandler? SourceChanged;      // any property change → HasChanges
    public event EventHandler? DefaultSelected;    // parent clears other rows' IsDefault
}
```

- [ ] **Step 1:** Failing VM tests: (a) Load populates rows from settings; (b) setting `IsDefault` on row B clears it on row A and sets `HasChanges`; (c) Save snapshot contains the rows incl. `InstallerPackageId` pass-through; (d) Add/Remove commands mutate the collection + `HasChanges`.
- [ ] **Step 2:** Verify fail → implement VM parts (copy the ImageGallery collection wiring: `ObservableCollection<BaseModelFolderViewModel> BaseModelFolders`, `AddBaseModelFolderCommand`, `RemoveBaseModelFolderCommand(row)`, `BrowseBaseModelFolderAsync(row)` via `DialogService.ShowOpenFolderDialogAsync("Select Base Model Folder")`, load/save mapping, `DefaultSelected` handler enforcing exclusivity).
- [ ] **Step 3:** XAML Expander (copy the Generation Galleries block structure; row template: CheckBox `IsEnabled` → ToggleButton "⭐" bound `IsDefault` (LoRA favorite-star pattern, `SettingsView.axaml:245-258`) → TextBox `FolderPath` → Browse → Remove; description text: "Folders where Diffusion Nexus Core stores and looks up models. The ⭐ folder is the default download target. Folders of added installations appear here automatically.").
- [ ] **Step 4:** Tests pass; commit `feat(ui): Base Model Folders settings section`.

### Task 9: Verification + PR

- [ ] **Step 1:** `dotnet build DiffusionNexus.sln` — 0 errors/warnings.
- [ ] **Step 2:** `dotnet test DiffusionNexus.sln` — full suite green.
- [ ] **Step 3:** Manual sanity greps: no remaining caller of old `InstallMissingAsync(manifest, vramGb, ct)` overload; `GetComfyUiModelsRootsAsync` doc updated.
- [ ] **Step 4:** Push branch; PR → `develop` referencing the spec; note follow-up candidates (auto-register LoRA sources on package add).
