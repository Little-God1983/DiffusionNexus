# LoRA Viewer Base Model Filter Rework Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Installed tab's base-model filter stay open, searchable, restrictable to installed base models, cover unknown-base-model files, and saveable/restorable across sessions.

**Architecture:** The shared `AvailableBaseModels` collection (mirrored by the Browse Civitai tab) stays untouched; the Installed flyout binds to a new composed `FlyoutBaseModels` view that adds an "Unknown" sentinel and applies the flyout search + only-installed narrowing. Persistence is one JSON blob in the `AppSettings` singleton row (precedent: `DistillerRuleSetsJson`).

**Tech Stack:** Avalonia 11 XAML, CommunityToolkit.Mvvm (`[ObservableProperty]`/`[RelayCommand]`), EF Core (SQLite, `DiffusionNexusCoreDbContext`), xUnit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-08-09-lora-viewer-base-model-filter-design.md`

## Global Constraints

- Repo: `e:\Repos\DiffusionNexus`, branch `feature/lora-viewer-base-model-filter` (already created off `develop`). Never commit to `develop`/`main` directly.
- IGNORE the duplicate tree under `e:\Repos\DiffusionNexus\.claude\worktrees\` — never edit files there.
- Before modifying any Database Entity class or migrations, run `publish.ps1` (repo root) first to create a backup with a working database (copilot-instructions.md rule). If the script is missing or fails, STOP and report instead of proceeding.
- Do not reintroduce global Avalonia test initialization in the test project; VM tests use the design-time constructor with no Avalonia runtime.
- Tests must not touch `Dispatcher.UIThread`-dependent paths; persistence logic is split into pure internal methods for testability (`InternalsVisibleTo` for `DiffusionNexus.Tests` already exists — the search tests already set `internal` members).
- Build the full solution (`dotnet build DiffusionNexus.sln`) and run the full test suite before any push.
- All commands below run from `e:\Repos\DiffusionNexus`.

---

### Task 1: Flyout stays open + rename to "Base Model" (XAML only)

**Files:**
- Modify: `DiffusionNexus.UI\Views\LoraViewerView.axaml:37-61`
- Modify: `DiffusionNexus.UI\Views\CivitaiBrowser\CivitaiBrowserView.axaml:68-69`

**Interfaces:**
- Consumes: existing bindings `IsBaseModelFilterActive`, `ActiveBaseModelFilterCount`, `ClearBaseModelFiltersCommand` (unchanged).
- Produces: nothing new for later tasks; Task 3 edits the same flyout body further.

- [ ] **Step 1: Installed tab — caption + ShowMode**

In `LoraViewerView.axaml`, replace lines 37-45 (the horizontal `StackPanel` opening, the `"Filter"` `TextBlock`, and the `Button` opening) so the filter matches the Browse Civitai layout (caption above the button, word "Filter" inside the button):

```xml
            <StackPanel Margin="0,4,12,4" VerticalAlignment="Center">
              <TextBlock Text="Base Model" Opacity="0.6" FontSize="11"/>
              <Button Padding="8,6"
                      MinWidth="140"
                      HorizontalContentAlignment="Left"
                      ToolTip.Tip="Filter by base model">
                <StackPanel Orientation="Horizontal" Spacing="4">
                  <TextBlock Text="Filter" VerticalAlignment="Center"/>
                  <TextBlock Text="&#x25BD;" FontSize="14"
```

(The `&#x25BD;` line shown is existing line 47 — it and everything after stay unchanged in this step.)

Then change line 61 from

```xml
                          ShowMode="TransientWithDismissOnPointerMoveAway">
```

to

```xml
                          ShowMode="Transient">
```

- [ ] **Step 2: Browse Civitai tab — ShowMode only**

In `CivitaiBrowserView.axaml` line 69, change `ShowMode="TransientWithDismissOnPointerMoveAway"` to `ShowMode="Transient"` (same one-word change; layout there already has the "Base Model" caption).

- [ ] **Step 3: Build**

Run: `dotnet build DiffusionNexus.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```powershell
git add DiffusionNexus.UI/Views/LoraViewerView.axaml DiffusionNexus.UI/Views/CivitaiBrowser/CivitaiBrowserView.axaml
git commit -m @'
feat(viewer): base-model flyout stays open, renamed to Base Model

The Installed tab filter now matches the Browse Civitai layout (caption
above the button) and both flyouts use ShowMode=Transient so they stay
open while multi-selecting instead of dismissing on pointer-move-away.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 2: "Unknown" sentinel item + filter matching

**Files:**
- Modify: `DiffusionNexus.UI\ViewModels\LoraViewerViewModel.cs` (properties ~line 132-141, ctors ~line 203/221, demo data ~line 2878, `ClearBaseModelFilters` ~line 2470, `ResetFilters` ~line 2482, `RebuildAvailableBaseModels` ~line 2589, `ApplyFilters` ~line 2769)
- Create: `DiffusionNexus.Tests\Viewer\LoraViewerViewModelBaseModelFilterTests.cs`

**Interfaces:**
- Consumes: existing `BaseModelFilterItem` (ctor `new BaseModelFilterItem(string baseModelRaw)`, `bool IsSelected`, `event SelectionChanged`), existing `private static bool IsPlaceholderBaseModel(string?)` at `LoraViewerViewModel.cs:904`.
- Produces (later tasks rely on these exact names):
  - `public const string UnknownBaseModelLabel = "Unknown";`
  - `public BaseModelFilterItem UnknownBaseModelItem { get; }` — sentinel, NEVER added to `AvailableBaseModels`.
  - `IsBaseModelFilterActive` / `ActiveBaseModelFilterCount` now include the sentinel.
  - Demo data contains one model with `BaseModelRaw = "???"` named `"Legacy Style"`.

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests\Viewer\LoraViewerViewModelBaseModelFilterTests.cs`:

```csharp
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the Installed-tab base-model filter: the "Unknown" sentinel that matches
/// tiles whose base model is the "???" placeholder, and its interaction with the
/// active-filter indicator and Clear/Reset. Uses the design-time constructor's demo
/// data, which includes one placeholder-base-model tile ("Legacy Style").
/// </summary>
public class LoraViewerViewModelBaseModelFilterTests
{
    private static LoraViewerViewModel CreateViewModel() => new()
    {
        SearchDebounceInterval = TimeSpan.FromMilliseconds(50),
    };

    [Fact]
    public void UnknownSentinelIsNotPartOfTheSharedBaseModelList()
    {
        var vm = CreateViewModel();

        vm.AvailableBaseModels.Should().NotContain(vm.UnknownBaseModelItem,
            "the sentinel must never leak into the collection the Civitai browser mirrors");
        vm.AvailableBaseModels.Should().NotContain(i => i.BaseModelRaw == "???",
            "placeholder values must not appear as a raw filter entry");
    }

    [Fact]
    public void SelectingUnknownShowsOnlyPlaceholderTiles()
    {
        var vm = CreateViewModel();

        vm.UnknownBaseModelItem.IsSelected = true;

        vm.FilteredTiles.Should().NotBeEmpty("demo data contains a '???' tile");
        vm.FilteredTiles.Should().OnlyContain(t => t.DisplayName == "Legacy Style");
    }

    [Fact]
    public void UnknownCombinesWithRegularSelectionsAsUnion()
    {
        var vm = CreateViewModel();
        var sdxl = vm.AvailableBaseModels.First(i => i.BaseModelRaw == "SDXL 1.0");

        vm.UnknownBaseModelItem.IsSelected = true;
        sdxl.IsSelected = true;

        vm.FilteredTiles.Should().Contain(t => t.DisplayName == "Legacy Style");
        vm.FilteredTiles.Should().Contain(t => t.DisplayName == "Realistic Portrait");
    }

    [Fact]
    public void UnknownCountsTowardTheActiveFilterIndicator()
    {
        var vm = CreateViewModel();

        vm.IsBaseModelFilterActive.Should().BeFalse();

        vm.UnknownBaseModelItem.IsSelected = true;

        vm.IsBaseModelFilterActive.Should().BeTrue();
        vm.ActiveBaseModelFilterCount.Should().Be(1);
    }

    [Fact]
    public void ClearBaseModelFiltersAlsoClearsUnknown()
    {
        var vm = CreateViewModel();
        var allCount = vm.FilteredTiles.Count;
        vm.UnknownBaseModelItem.IsSelected = true;

        vm.ClearBaseModelFiltersCommand.Execute(null);

        vm.UnknownBaseModelItem.IsSelected.Should().BeFalse();
        vm.IsBaseModelFilterActive.Should().BeFalse();
        vm.FilteredTiles.Count.Should().Be(allCount);
    }

    [Fact]
    public void ResetFiltersAlsoClearsUnknown()
    {
        var vm = CreateViewModel();
        vm.UnknownBaseModelItem.IsSelected = true;

        vm.ResetFiltersCommand.Execute(null);

        vm.UnknownBaseModelItem.IsSelected.Should().BeFalse();
        vm.IsBaseModelFilterActive.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests --filter "FullyQualifiedName~LoraViewerViewModelBaseModelFilterTests"`
Expected: compile FAILS — `UnknownBaseModelItem` does not exist. (Compile failure is the failing state for the first TDD cycle.)

- [ ] **Step 3: Implement**

All edits in `DiffusionNexus.UI\ViewModels\LoraViewerViewModel.cs`.

**3a.** In the `#region Observable Properties` block, replace the two computed properties (lines 132-140):

```csharp
    /// <summary>
    /// Whether any base model filter is currently active (for visual indicator on the filter button).
    /// Includes the "Unknown" sentinel.
    /// </summary>
    public bool IsBaseModelFilterActive
        => UnknownBaseModelItem.IsSelected || AvailableBaseModels.Any(f => f.IsSelected);

    /// <summary>
    /// Count of currently active base model filters (including the "Unknown" sentinel).
    /// </summary>
    public int ActiveBaseModelFilterCount
        => AvailableBaseModels.Count(f => f.IsSelected) + (UnknownBaseModelItem.IsSelected ? 1 : 0);
```

**3b.** In the `#region Collections` block, after the `AvailableBaseModels` property (line 177), add:

```csharp
    /// <summary>Display label of the "Unknown" pseudo base model.</summary>
    public const string UnknownBaseModelLabel = "Unknown";

    /// <summary>
    /// Sentinel filter item matching tiles whose base model is the "???" placeholder
    /// (local files without metadata). Owned by the Installed tab only — it is NEVER
    /// added to <see cref="AvailableBaseModels"/>, which the Civitai browser mirrors
    /// and whose entries are sent to the Civitai API.
    /// </summary>
    public BaseModelFilterItem UnknownBaseModelItem { get; } = new(UnknownBaseModelLabel);
```

**3c.** In BOTH constructors, subscribe the sentinel before any demo/catalog load. Design-time ctor (line ~204, before `LoadDemoData()`):

```csharp
        UnknownBaseModelItem.SelectionChanged += OnBaseModelFilterChanged;
```

Runtime ctor (line ~230, right after `_selectedSortOption = SortOptions[0];`): same line.

**3d.** In `LoadDemoData()` (line ~2886), add a placeholder-base-model demo model to the `allDemoModels` list, after the `"Turbo Generator"` entry:

```csharp
            // A local file discovered without metadata — exercises the "Unknown" filter.
            CreateDemoModel("Legacy Style", "OldTimer", "???", 100),
```

**3e.** In `RebuildAvailableBaseModels()`, in the fallback branch (line ~2592), change the `Where` so placeholder values never appear as a raw entry:

```csharp
                .Where(raw => !IsPlaceholderBaseModel(raw))
```

(replacing `.Where(raw => !string.IsNullOrWhiteSpace(raw))`).

**3f.** In `ClearBaseModelFilters()` (line ~2471) and `ResetFilters()` (line ~2483), add before the loop:

```csharp
        UnknownBaseModelItem.IsSelected = false;
```

**3g.** In `ApplyFilters()` (line ~2769), replace the base-model block:

```csharp
        var activeBaseModels = AvailableBaseModels
            .Where(f => f.IsSelected)
            .Select(f => f.BaseModelRaw)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var includeUnknown = UnknownBaseModelItem.IsSelected;

        if (activeBaseModels.Count > 0 || includeUnknown)
        {
            query = query.Where(t =>
                t.Versions.Any(v =>
                    (includeUnknown && IsPlaceholderBaseModel(v.BaseModelRaw)) ||
                    (!string.IsNullOrEmpty(v.BaseModelRaw) &&
                     activeBaseModels.Contains(v.BaseModelRaw))));
        }
```

- [ ] **Step 4: Run the new tests**

Run: `dotnet test DiffusionNexus.Tests --filter "FullyQualifiedName~LoraViewerViewModelBaseModelFilterTests"`
Expected: 6 PASS.

- [ ] **Step 5: Run the pre-existing viewer tests (the demo-data change must not break them)**

Run: `dotnet test DiffusionNexus.Tests --filter "FullyQualifiedName~Viewer"`
Expected: all PASS.

- [ ] **Step 6: Commit**

```powershell
git add DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs DiffusionNexus.Tests/Viewer/LoraViewerViewModelBaseModelFilterTests.cs
git commit -m @'
feat(viewer): Unknown entry matches tiles without a base model

Tiles whose base model is the "???" placeholder (files discovered without
metadata) could never be matched by any base-model selection. A new
Unknown sentinel item — owned by the Installed tab only, never part of
the shared list the Civitai browser mirrors — matches them, counts
toward the active-filter badge, and is cleared by Clear all / Reset.
The distinct-from-installed fallback list no longer surfaces "???" as a
raw entry.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 3: Composed flyout list — search box + "only installed" checkbox

**Files:**
- Modify: `DiffusionNexus.UI\ViewModels\LoraViewerViewModel.cs`
- Modify: `DiffusionNexus.UI\Views\LoraViewerView.axaml` (flyout body from Task 1)
- Test: `DiffusionNexus.Tests\Viewer\LoraViewerViewModelBaseModelFilterTests.cs` (append)

**Interfaces:**
- Consumes: `UnknownBaseModelItem`, `UnknownBaseModelLabel`, `IsPlaceholderBaseModel` (Task 2); `AvailableBaseModels`, `AllTiles`.
- Produces (later tasks + XAML rely on these exact names):
  - `[ObservableProperty] string? _baseModelFilterSearchText` → `BaseModelFilterSearchText`
  - `[ObservableProperty] bool _onlyInstalledBaseModels` → `OnlyInstalledBaseModels`
  - `public ObservableCollection<BaseModelFilterItem> FlyoutBaseModels { get; }`
  - `private void RebuildFlyoutBaseModels()` — called at the end of `RebuildAvailableBaseModels()`.

- [ ] **Step 1: Write the failing tests**

Append to `LoraViewerViewModelBaseModelFilterTests.cs`:

```csharp
    [Fact]
    public void FlyoutListContainsUnknownFirstThenAllSharedItems()
    {
        var vm = CreateViewModel();

        vm.FlyoutBaseModels.First().Should().BeSameAs(vm.UnknownBaseModelItem);
        vm.FlyoutBaseModels.Skip(1).Should().Equal(vm.AvailableBaseModels);
    }

    [Fact]
    public void FlyoutSearchNarrowsTheListCaseInsensitively()
    {
        var vm = CreateViewModel();

        vm.BaseModelFilterSearchText = "sdxl";

        vm.FlyoutBaseModels.Should().NotBeEmpty();
        vm.FlyoutBaseModels.Should().OnlyContain(i =>
            i.BaseModelRaw.Contains("SDXL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FlyoutSearchMatchesTheUnknownEntryByLabel()
    {
        var vm = CreateViewModel();

        vm.BaseModelFilterSearchText = "unk";

        vm.FlyoutBaseModels.Should().ContainSingle()
            .Which.Should().BeSameAs(vm.UnknownBaseModelItem);
    }

    [Fact]
    public void ClearingTheFlyoutSearchRestoresTheFullList()
    {
        var vm = CreateViewModel();
        var fullCount = vm.FlyoutBaseModels.Count;

        vm.BaseModelFilterSearchText = "sdxl";
        vm.BaseModelFilterSearchText = null;

        vm.FlyoutBaseModels.Count.Should().Be(fullCount);
    }

    [Fact]
    public void OnlyInstalledNarrowsToBaseModelsPresentInTheLibrary()
    {
        var vm = CreateViewModel();

        vm.OnlyInstalledBaseModels = true;

        // Demo data installs SDXL 1.0 (among others) but the shared list may hold more.
        vm.FlyoutBaseModels.Should().Contain(i => i.BaseModelRaw == "SDXL 1.0");
        vm.FlyoutBaseModels.Where(i => i != vm.UnknownBaseModelItem)
            .Should().OnlyContain(i => vm.AllTiles.Any(t =>
                t.Versions.Any(v => string.Equals(v.BaseModelRaw, i.BaseModelRaw,
                    StringComparison.OrdinalIgnoreCase))));
    }

    [Fact]
    public void OnlyInstalledKeepsUnknownWhenPlaceholderTilesExist()
    {
        var vm = CreateViewModel();

        vm.OnlyInstalledBaseModels = true;

        vm.FlyoutBaseModels.Should().Contain(vm.UnknownBaseModelItem,
            "demo data contains a '???' tile, so Unknown is an installed option");
    }

    [Fact]
    public void SelectionSurvivesFlyoutNarrowing()
    {
        var vm = CreateViewModel();
        var sdxl = vm.AvailableBaseModels.First(i => i.BaseModelRaw == "SDXL 1.0");
        sdxl.IsSelected = true;

        vm.BaseModelFilterSearchText = "pony";
        vm.BaseModelFilterSearchText = null;

        sdxl.IsSelected.Should().BeTrue("narrowing the visible list must not touch selections");
        vm.ActiveBaseModelFilterCount.Should().Be(1);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests --filter "FullyQualifiedName~LoraViewerViewModelBaseModelFilterTests"`
Expected: compile FAILS — `FlyoutBaseModels` / `BaseModelFilterSearchText` / `OnlyInstalledBaseModels` do not exist.

- [ ] **Step 3: Implement the ViewModel side**

In `LoraViewerViewModel.cs`:

**3a.** In `#region Observable Properties`, after `_showNsfw` (line ~74), add:

```csharp
    /// <summary>
    /// Search text typed inside the base-model flyout. Narrows the visible option
    /// list (<see cref="FlyoutBaseModels"/>) only — selections are untouched.
    /// </summary>
    [ObservableProperty]
    private string? _baseModelFilterSearchText;

    /// <summary>
    /// When true, the base-model flyout lists only base models actually present
    /// among the installed LoRAs (plus "Unknown" when placeholder tiles exist).
    /// Off by default.
    /// </summary>
    [ObservableProperty]
    private bool _onlyInstalledBaseModels;
```

**3b.** In `#region Collections`, after `UnknownBaseModelItem` (Task 2), add:

```csharp
    /// <summary>
    /// The option list the Installed tab's flyout renders: "Unknown" first, then the
    /// shared <see cref="AvailableBaseModels"/> items, narrowed by
    /// <see cref="BaseModelFilterSearchText"/> and <see cref="OnlyInstalledBaseModels"/>.
    /// Holds the SAME item instances as the shared list, so toggling a checkbox here
    /// drives the same selection state the filter pipeline and the browser mirror use.
    /// </summary>
    public ObservableCollection<BaseModelFilterItem> FlyoutBaseModels { get; } = [];
```

**3c.** In `#region Property Changed Handlers` (after `OnShowNsfwChanged`, line ~2532), add:

```csharp
    partial void OnBaseModelFilterSearchTextChanged(string? value) => RebuildFlyoutBaseModels();

    partial void OnOnlyInstalledBaseModelsChanged(bool value) => RebuildFlyoutBaseModels();
```

**3d.** At the END of `RebuildAvailableBaseModels()` (after the `foreach` that refills the list, line ~2621), add:

```csharp
        RebuildFlyoutBaseModels();
```

(`RebuildAvailableBaseModels` runs on every tile replace, tile delete, and catalog reload, so the flyout view stays current on all those paths.)

**3e.** In `#region Private Methods`, after `RebuildAvailableBaseModels()`, add:

```csharp
    /// <summary>
    /// Recomputes <see cref="FlyoutBaseModels"/>: "Unknown" first (hidden when
    /// "only installed" is on and no placeholder tiles exist), then the shared items,
    /// filtered by the flyout search text and the only-installed toggle. Reuses the
    /// shared item instances — never copies — so selection state stays single-sourced.
    /// </summary>
    private void RebuildFlyoutBaseModels()
    {
        var search = BaseModelFilterSearchText?.Trim();
        var hasSearch = !string.IsNullOrWhiteSpace(search);

        HashSet<string>? installed = null;
        var hasUnknownInstalled = false;
        if (OnlyInstalledBaseModels)
        {
            installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var version in AllTiles.SelectMany(t => t.Versions))
            {
                if (IsPlaceholderBaseModel(version.BaseModelRaw))
                    hasUnknownInstalled = true;
                else
                    installed.Add(version.BaseModelRaw!);
            }
        }

        FlyoutBaseModels.Clear();

        var showUnknown = (!OnlyInstalledBaseModels || hasUnknownInstalled)
            && (!hasSearch || UnknownBaseModelLabel.Contains(search!, StringComparison.OrdinalIgnoreCase));
        if (showUnknown)
            FlyoutBaseModels.Add(UnknownBaseModelItem);

        foreach (var item in AvailableBaseModels)
        {
            if (installed is not null && !installed.Contains(item.BaseModelRaw))
                continue;
            if (hasSearch && !item.BaseModelRaw.Contains(search!, StringComparison.OrdinalIgnoreCase))
                continue;
            FlyoutBaseModels.Add(item);
        }
    }
```

**3f.** In `ResetFilters()` (line ~2483), also clear the new narrowing state (after the `UnknownBaseModelItem.IsSelected = false;` from Task 2):

```csharp
        BaseModelFilterSearchText = null;
        OnlyInstalledBaseModels = false;
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test DiffusionNexus.Tests --filter "FullyQualifiedName~LoraViewerViewModelBaseModelFilterTests"`
Expected: 13 PASS (6 from Task 2 + 7 new).

- [ ] **Step 5: Wire up the XAML flyout body**

In `LoraViewerView.axaml`, inside the flyout `Border` (Task 1 state), replace the `Grid` (previously `RowDefinitions="Auto,Auto,*"` with title / Clear all / ScrollViewer) with:

```xml
                      <Grid RowDefinitions="Auto,Auto,Auto,Auto,*">
                        <TextBlock Grid.Row="0"
                                   Text="Filter by base model"
                                   FontWeight="SemiBold"
                                   Margin="4,0,4,8"
                                   Opacity="0.7"/>

                        <TextBox Grid.Row="1"
                                 Text="{Binding BaseModelFilterSearchText, Mode=TwoWay}"
                                 Watermark="Search base models..."
                                 Margin="4,0,4,8"/>

                        <CheckBox Grid.Row="2"
                                  Content="Only models I have installed"
                                  IsChecked="{Binding OnlyInstalledBaseModels, Mode=TwoWay}"
                                  Margin="4,0,4,8"
                                  FontSize="12"/>

                        <Button Grid.Row="3"
                                Content="Clear all"
                                Command="{Binding ClearBaseModelFiltersCommand}"
                                IsVisible="{Binding IsBaseModelFilterActive}"
                                Padding="4,2"
                                Margin="4,0,4,8"
                                FontSize="11"
                                HorizontalAlignment="Left"/>

                        <ScrollViewer Grid.Row="4"
                                      MaxHeight="300"
                                      HorizontalScrollBarVisibility="Disabled"
                                      VerticalScrollBarVisibility="Auto">
                          <ItemsControl ItemsSource="{Binding FlyoutBaseModels}">
                            <ItemsControl.ItemsPanel>
                              <ItemsPanelTemplate>
                                <StackPanel Spacing="2"/>
                              </ItemsPanelTemplate>
                            </ItemsControl.ItemsPanel>
                            <ItemsControl.ItemTemplate>
                              <DataTemplate x:DataType="vm:BaseModelFilterItem">
                                <ToggleButton IsChecked="{Binding IsSelected, Mode=TwoWay}"
                                              HorizontalAlignment="Stretch"
                                              HorizontalContentAlignment="Left"
                                              Padding="8,6"
                                              Background="Transparent"
                                              BorderThickness="1"
                                              BorderBrush="#333"
                                              CornerRadius="4">
                                  <TextBlock Text="{Binding BaseModelRaw}"
                                             FontSize="13"/>
                                </ToggleButton>
                              </DataTemplate>
                            </ItemsControl.ItemTemplate>
                          </ItemsControl>
                        </ScrollViewer>
                      </Grid>
```

(Changes vs before: two new rows — search `TextBox` and the checkbox; `ItemsSource` now `FlyoutBaseModels`; `ScrollViewer MaxHeight` 340→300 so the taller header still fits `MaxHeight="400"` on the Border. The Browse Civitai flyout keeps binding `AvailableBaseModels` — do not change it.)

- [ ] **Step 6: Build**

Run: `dotnet build DiffusionNexus.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```powershell
git add DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs DiffusionNexus.UI/Views/LoraViewerView.axaml DiffusionNexus.Tests/Viewer/LoraViewerViewModelBaseModelFilterTests.cs
git commit -m @'
feat(viewer): searchable base-model flyout with only-installed toggle

The Installed tab flyout gets a search box and an off-by-default "Only
models I have installed" checkbox. The flyout renders a composed view
(Unknown first, then the shared catalog items, narrowed by both) that
reuses the shared item instances, so selections stay single-sourced and
the Browse Civitai mirror is unaffected.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 4: Save filter + restore on open (AppSettings JSON column)

**Files:**
- Modify: `DiffusionNexus.Domain\Entities\AppSettings.cs` (~line 110)
- Modify: `DiffusionNexus.Domain\Services\IAppSettingsService.cs` (end of interface)
- Modify: `DiffusionNexus.Service\Services\AppSettingsService.cs` (end of class)
- Create: `DiffusionNexus.DataAccess\Migrations\Core\<timestamp>_AddLoraViewerFilterJson.cs` (+ Designer, snapshot update — generated)
- Create: `DiffusionNexus.UI\Models\LoraViewerFilterData.cs`
- Modify: `DiffusionNexus.UI\ViewModels\LoraViewerViewModel.cs` (runtime ctor line ~257, new command + restore methods)
- Modify: `DiffusionNexus.UI\Views\LoraViewerView.axaml` (toolbar, after the Base Model StackPanel)
- Test: `DiffusionNexus.Tests\Viewer\LoraViewerViewModelBaseModelFilterTests.cs` (append)

**Interfaces:**
- Consumes: `UnknownBaseModelItem`, `OnlyInstalledBaseModels`, `AvailableBaseModels` (Tasks 2-3); `IAppSettingsService` injected as `_settingsService`; `_logger` (`IUnifiedLogger`, `Warn(LogCategory, string, string)`); `Dispatcher.UIThread`.
- Produces:
  - `AppSettings.LoraViewerFilterJson` (`string?`)
  - `IAppSettingsService.GetLoraViewerFilterJsonAsync(CancellationToken)` / `SetLoraViewerFilterJsonAsync(string?, CancellationToken)`
  - `DiffusionNexus.UI.Models.LoraViewerFilterData` DTO
  - `internal LoraViewerFilterData CaptureFilter()` / `internal void ApplySavedFilter(LoraViewerFilterData data)` on the VM (pure, dispatcher-free — the test seam)
  - `SaveFilterCommand` (generated from `SaveFilterAsync`)

- [ ] **Step 1: Database-change guard (MANDATORY before touching the entity)**

Run `Get-ChildItem e:\Repos\DiffusionNexus -Filter publish.ps1 -Depth 1`, then execute the script found (repo rule: backup with a working database before any entity/migration change). If it is missing or fails, STOP the task and report — do not modify the entity without the backup.

- [ ] **Step 2: Write the failing tests**

Append to `LoraViewerViewModelBaseModelFilterTests.cs` (add `using DiffusionNexus.UI.Models;` at the top):

```csharp
    [Fact]
    public void CaptureFilterSerializesSelectionUnknownAndOnlyInstalled()
    {
        var vm = CreateViewModel();
        vm.AvailableBaseModels.First(i => i.BaseModelRaw == "SDXL 1.0").IsSelected = true;
        vm.AvailableBaseModels.First(i => i.BaseModelRaw == "Pony").IsSelected = true;
        vm.UnknownBaseModelItem.IsSelected = true;
        vm.OnlyInstalledBaseModels = true;

        var data = vm.CaptureFilter();

        data.SelectedBaseModels.Should().BeEquivalentTo("SDXL 1.0", "Pony");
        data.IncludeUnknown.Should().BeTrue();
        data.OnlyInstalled.Should().BeTrue();
    }

    [Fact]
    public void ApplySavedFilterRestoresTheCapturedState()
    {
        var vm = CreateViewModel();
        var data = new LoraViewerFilterData
        {
            SelectedBaseModels = ["SDXL 1.0", "Pony"],
            IncludeUnknown = true,
            OnlyInstalled = true,
        };

        vm.ApplySavedFilter(data);

        vm.AvailableBaseModels.Where(i => i.IsSelected).Select(i => i.BaseModelRaw)
            .Should().BeEquivalentTo("SDXL 1.0", "Pony");
        vm.UnknownBaseModelItem.IsSelected.Should().BeTrue();
        vm.OnlyInstalledBaseModels.Should().BeTrue();
        vm.IsBaseModelFilterActive.Should().BeTrue();
    }

    [Fact]
    public void ApplySavedFilterIgnoresNamesNotInTheCurrentList()
    {
        var vm = CreateViewModel();
        var data = new LoraViewerFilterData
        {
            SelectedBaseModels = ["SDXL 1.0", "No Such Base Model 9000"],
        };

        vm.ApplySavedFilter(data);

        vm.ActiveBaseModelFilterCount.Should().Be(1, "unknown saved names are ignored silently");
    }

    [Fact]
    public void CaptureThenApplyRoundTripsThroughJson()
    {
        var vm = CreateViewModel();
        vm.AvailableBaseModels.First(i => i.BaseModelRaw == "Illustrious").IsSelected = true;
        vm.UnknownBaseModelItem.IsSelected = true;

        var json = System.Text.Json.JsonSerializer.Serialize(vm.CaptureFilter());

        var vm2 = CreateViewModel();
        vm2.ApplySavedFilter(
            System.Text.Json.JsonSerializer.Deserialize<LoraViewerFilterData>(json)!);

        vm2.AvailableBaseModels.Where(i => i.IsSelected).Select(i => i.BaseModelRaw)
            .Should().BeEquivalentTo("Illustrious");
        vm2.UnknownBaseModelItem.IsSelected.Should().BeTrue();
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests --filter "FullyQualifiedName~LoraViewerViewModelBaseModelFilterTests"`
Expected: compile FAILS — `LoraViewerFilterData` / `CaptureFilter` / `ApplySavedFilter` do not exist.

- [ ] **Step 4: DTO**

Create `DiffusionNexus.UI\Models\LoraViewerFilterData.cs`:

```csharp
namespace DiffusionNexus.UI.Models;

/// <summary>
/// Serialized form of the LoRA Viewer's saved base-model filter, stored as JSON in
/// <c>AppSettings.LoraViewerFilterJson</c>. Owned and (de)serialized by
/// <c>LoraViewerViewModel</c>. Single slot — saving overwrites the previous filter.
/// </summary>
public sealed class LoraViewerFilterData
{
    /// <summary>Raw base-model names that were selected (case-insensitive match on restore).</summary>
    public List<string> SelectedBaseModels { get; set; } = [];

    /// <summary>Whether the "Unknown" pseudo entry was selected.</summary>
    public bool IncludeUnknown { get; set; }

    /// <summary>Whether the "only models I have installed" narrowing was on.</summary>
    public bool OnlyInstalled { get; set; }
}
```

- [ ] **Step 5: VM capture/apply/save/restore**

In `LoraViewerViewModel.cs` (add `using DiffusionNexus.UI.Models;` and `using System.Text.Json;` if not present):

**5a.** In `#region Commands` (after `ResetFilters`, line ~2493):

```csharp
    /// <summary>
    /// Persists the current base-model filter (selections + Unknown + only-installed)
    /// to AppSettings. Restored automatically the next time the viewer opens.
    /// </summary>
    [RelayCommand]
    private async Task SaveFilterAsync()
    {
        if (_settingsService is null) return;
        try
        {
            var json = JsonSerializer.Serialize(CaptureFilter());
            await _settingsService.SetLoraViewerFilterJsonAsync(json);
            SyncStatus = "Base-model filter saved.";
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.General, "LoraViewer",
                $"Could not save base-model filter: {ex.Message}");
            SyncStatus = "Could not save the filter.";
        }
    }
```

**5b.** In `#region Private Methods` (near `RebuildFlyoutBaseModels`):

```csharp
    /// <summary>Snapshots the current base-model filter state for persistence.</summary>
    internal LoraViewerFilterData CaptureFilter() => new()
    {
        SelectedBaseModels = AvailableBaseModels
            .Where(f => f.IsSelected)
            .Select(f => f.BaseModelRaw)
            .ToList(),
        IncludeUnknown = UnknownBaseModelItem.IsSelected,
        OnlyInstalled = OnlyInstalledBaseModels,
    };

    /// <summary>
    /// Applies a saved filter: selects matching names (case-insensitive; names no
    /// longer in the list are ignored silently), the Unknown sentinel, and the
    /// only-installed toggle. Must run on the UI thread (mutates bound state).
    /// </summary>
    internal void ApplySavedFilter(LoraViewerFilterData data)
    {
        var wanted = data.SelectedBaseModels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in AvailableBaseModels)
        {
            if (wanted.Contains(item.BaseModelRaw))
                item.IsSelected = true;
        }
        UnknownBaseModelItem.IsSelected = data.IncludeUnknown;
        OnlyInstalledBaseModels = data.OnlyInstalled;
    }

    /// <summary>
    /// Loads the saved filter from AppSettings and applies it on the UI thread.
    /// Runs once at startup, after the catalog load so the full option list exists.
    /// Corrupt or missing data degrades silently to the unfiltered default.
    /// </summary>
    private async Task RestoreSavedFilterAsync()
    {
        if (_settingsService is null) return;
        try
        {
            var json = await _settingsService.GetLoraViewerFilterJsonAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return;

            var data = JsonSerializer.Deserialize<LoraViewerFilterData>(json);
            if (data is null) return;

            await Dispatcher.UIThread.InvokeAsync(() => ApplySavedFilter(data));
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.General, "LoraViewer",
                $"Could not restore saved base-model filter: {ex.Message}");
        }
    }

    /// <summary>
    /// Startup sequence for the base-model filter: catalog first (builds the full
    /// option list), then the saved-filter restore (selection by name needs the
    /// list to exist). A later catalog refresh preserves selections by name.
    /// </summary>
    private async Task InitializeBaseModelFilterAsync()
    {
        await LoadBaseModelCatalogAsync().ConfigureAwait(false);
        await RestoreSavedFilterAsync().ConfigureAwait(false);
    }
```

**5c.** In the runtime ctor (line ~257), replace `_ = LoadBaseModelCatalogAsync();` with:

```csharp
        _ = InitializeBaseModelFilterAsync();
```

- [ ] **Step 6: Run the new tests (VM side is complete; entity/service compile next)**

Run: `dotnet test DiffusionNexus.Tests --filter "FullyQualifiedName~LoraViewerViewModelBaseModelFilterTests"`
Expected: still compile-fails on `SetLoraViewerFilterJsonAsync` — proceed to Step 7; re-run after it.

- [ ] **Step 7: Entity + service**

**7a.** `AppSettings.cs` — after `DistillerRuleSetsJson` (line 110), add:

```csharp
    /// <summary>
    /// The LoRA Viewer's saved base-model filter (selected base models, Unknown flag,
    /// only-installed toggle), serialized as JSON. Null when the user never saved one.
    /// Owned and (de)serialized by the LoRA Viewer ViewModel.
    /// </summary>
    public string? LoraViewerFilterJson { get; set; }
```

**7b.** `IAppSettingsService.cs` — at the end of the interface (after `SetFeedbackReporterEmailAsync`, line 124), add:

```csharp
    /// <summary>Gets the LoRA Viewer's saved base-model filter JSON, or null if never saved.</summary>
    Task<string?> GetLoraViewerFilterJsonAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores the LoRA Viewer's base-model filter JSON; whitespace/empty clears it to null.</summary>
    Task SetLoraViewerFilterJsonAsync(string? json, CancellationToken cancellationToken = default);
```

**7c.** `AppSettingsService.cs` — at the end of the class, following the existing narrow-helper pattern (`GetSettingsAsync` + `_unitOfWork.SaveChangesAsync`):

```csharp
    /// <inheritdoc />
    public async Task<string?> GetLoraViewerFilterJsonAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return settings.LoraViewerFilterJson;
    }

    /// <inheritdoc />
    public async Task SetLoraViewerFilterJsonAsync(string? json, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        settings.LoraViewerFilterJson = string.IsNullOrWhiteSpace(json) ? null : json;
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
```

Check for other `IAppSettingsService` implementations (test fakes): `Grep "IAppSettingsService" DiffusionNexus.Tests DiffusionNexus.UI --files_with_matches`. Any fake/mock implementing the interface member-by-member needs the two new members added (return `Task.FromResult<string?>(null)` / `Task.CompletedTask`).

- [ ] **Step 8: EF migration**

Run from `e:\Repos\DiffusionNexus`:

```powershell
dotnet ef migrations add AddLoraViewerFilterJson --project DiffusionNexus.DataAccess --startup-project DiffusionNexus.UI --context DiffusionNexusCoreDbContext --output-dir Migrations/Core
```

Expected: new `<timestamp>_AddLoraViewerFilterJson.cs` + `.Designer.cs` in `DiffusionNexus.DataAccess\Migrations\Core\` and an updated `DiffusionNexusCoreDbContextModelSnapshot.cs`. The Up migration must be exactly one nullable TEXT `AddColumn` on `AppSettings` (mirror of `20260714120143_AddDistillerRuleSetsJson.cs`). If `dotnet ef` is not installed, install the pinned tool (`dotnet tool restore` if a manifest exists, else `dotnet tool install --global dotnet-ef`) rather than hand-writing the Designer/snapshot.

`schema.sql` is NOT touched — the `DistillerRuleSetsJson` precedent (commit `af40cef`) changed only the entity + migration; migrations apply automatically at app start.

- [ ] **Step 9: Run the tests**

Run: `dotnet test DiffusionNexus.Tests --filter "FullyQualifiedName~LoraViewerViewModelBaseModelFilterTests"`
Expected: 17 PASS.

- [ ] **Step 10: Toolbar Save button**

In `LoraViewerView.axaml`, directly after the Base Model `StackPanel`'s closing tag (before the `Show NSFW` CheckBox), add:

```xml
            <Button Command="{Binding SaveFilterCommand}"
                    VerticalAlignment="Center"
                    Margin="0,4,12,4"
                    ToolTip.Tip="Save the current base-model filter — it is applied automatically the next time the viewer opens">
              <StackPanel Orientation="Horizontal" Spacing="6">
                <TextBlock Text="&#x1F4BE;" FontSize="13" VerticalAlignment="Center"/>
                <TextBlock Text="Save filter" VerticalAlignment="Center"/>
              </StackPanel>
            </Button>
```

- [ ] **Step 11: Full build + full test suite**

Run: `dotnet build DiffusionNexus.sln` then `dotnet test DiffusionNexus.Tests`
Expected: build 0 errors; full suite green (no unrelated regressions — the interface change in 7b is the likely breaker if a fake was missed).

- [ ] **Step 12: Commit**

```powershell
git add DiffusionNexus.Domain/Entities/AppSettings.cs DiffusionNexus.Domain/Services/IAppSettingsService.cs DiffusionNexus.Service/Services/AppSettingsService.cs DiffusionNexus.DataAccess/Migrations/Core DiffusionNexus.UI/Models/LoraViewerFilterData.cs DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs DiffusionNexus.UI/Views/LoraViewerView.axaml DiffusionNexus.Tests/Viewer/LoraViewerViewModelBaseModelFilterTests.cs
git commit -m @'
feat(viewer): saveable base-model filter, restored on open

A "Save filter" toolbar button persists the base-model selections, the
Unknown flag and the only-installed toggle as JSON in the AppSettings
singleton (new LoraViewerFilterJson column + EF migration, applied
automatically at app start). The viewer restores the saved filter after
the catalog load; saved names no longer in the list are ignored and
corrupt data degrades silently to the unfiltered default.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 5: Documentation + final verification

**Files:**
- Modify: `DiffusionNexus.UI\Doc\LoraViewer.md` (section 8 "Filtering Pipeline", ~line 355)

**Interfaces:** none — docs + verification only.

- [ ] **Step 1: Update the module doc**

In `LoraViewer.md` section 8, document (matching the doc's existing tone, a short paragraph or list): the Base Model flyout's search box and only-installed toggle, the Unknown entry (matches `"???"` placeholder tiles; never part of the shared browser-mirrored list), the Transient (click-outside) dismiss behavior, and the Save filter button persisting to `AppSettings.LoraViewerFilterJson` with automatic restore after the catalog load.

- [ ] **Step 2: Full build + full suite (final gate)**

Run: `dotnet build DiffusionNexus.sln` then `dotnet test DiffusionNexus.Tests`
Expected: 0 errors, full suite green.

- [ ] **Step 3: Commit**

```powershell
git add DiffusionNexus.UI/Doc/LoraViewer.md
git commit -m @'
docs(viewer): document the reworked base-model filter

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

- [ ] **Step 4: Manual GUI smoke (report to user — cannot be fully automated)**

Launch the app (Debug), open the LoRA Viewer and verify: flyout stays open while toggling several base models and closes on outside click; flyout search narrows the list; only-installed hides catalog-only entries; selecting Unknown shows metadata-less files; Save filter → close app → reopen → filter is applied; Browse Civitai tab's Base Model list is unaffected by the only-installed checkbox. Report results; leave push/PR to the user's explicit go-ahead.
