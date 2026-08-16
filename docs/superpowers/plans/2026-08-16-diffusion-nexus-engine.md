# Diffusion Nexus Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Diffusion Nexus an app-owned embedded ComfyUI ("Diffusion Nexus Engine") that the user installs from the Installation Manager, loads curated workloads into (Krea 2 Turbo first), and generates through from the Diffusion Canvas.

**Architecture:** A static tile in the Installation Manager drives a two-stage install — stage 1 runs the Installer SDK's `IInstallationCoordinator` against the `Krea-2-Turbo` configuration with all content excluded (yielding bare ComfyUI + venv + whatever torch the configuration declares), stage 2 installs workload content through the app's existing `WorkloadInstallService`. A `ManagedComfyUiEngine` hosts the ComfyUI process on a private loopback port, and a `ManagedComfyUiBackend` implements the existing `IDiffusionBackend` seam so the Canvas can select it from a dropdown.

**Tech Stack:** .NET 10, Avalonia 11.3, CommunityToolkit.Mvvm, EF Core 10 (SQLite), Serilog, Installer SDK 1.2.39 (NuGet), xUnit + FluentAssertions + Moq.

**Spec:** [docs/superpowers/specs/2026-08-16-diffusion-nexus-engine-design.md](../specs/2026-08-16-diffusion-nexus-engine-design.md)

**Repo:** `e:\Repos\DiffusionNexus`, branch `feature/diffusion-nexus-engine` (already created; spec committed as `24d7e5c`).

## Global Constraints

- **SDK packages must be exactly `1.2.39`** for all five references (`Models`, `Services`, `DataAccess`, `Shared`, `Database`). Do not bump further; 1.2.40 does not exist.
- **Engine torch/CUDA is inherited from the `Krea-2-Turbo` configuration, never authored in app code.** This plan originally asserted CUDA 13.0 + torch 2.11.0; the shipped catalog actually declares 12.8 + 2.8.0 — see the spec's Open items. The constraint that matters is the inheritance, not a specific pairing.
- **Base configuration GUID:** `E79C079A-2FD7-4FE7-8086-23731092555D` (`Krea-2-Turbo`). The first workload uses the same GUID.
- **Never use port 8188.** The engine binds `127.0.0.1` on a dynamically allocated free port; a user's own ComfyUI must never be collided with.
- **Test command:** `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` from the repo root. Add `--filter "FullyQualifiedName~<ClassName>"` to run a single test class.
- **Build command:** `dotnet build DiffusionNexus.sln -c Debug`.
- Tests use **xUnit** (`[Fact]`/`[Theory]`), **FluentAssertions** (`result.Should().Be(...)`), **Moq**. No Avalonia initialization in tests — never construct Views, only ViewModels and services.
- Every new user-visible step logs through `IUnifiedLogger` (`LogCategory.Installation`) so a hang shows the last successful step.
- Commit after every task with a `feat:` / `test:` / `chore:` prefixed message.

---

### Task 1: Bump the Installer SDK to 1.2.39

**Files:**
- Modify: `DiffusionNexus.UI/DiffusionNexus.UI.csproj:76-80`
- Modify: any other `.csproj` referencing `DiffusionNexus.Installer.SDK.*` (find them in step 1)

**Interfaces:**
- Consumes: nothing.
- Produces: SDK 1.2.39 APIs available app-wide — notably `IInstallationCoordinator`, `InstallationOptions`, `GpuGateOutcome`, `PreInstallationCheckResult`, `IUserPromptService`.

- [ ] **Step 1: Find every SDK package reference**

```bash
cd /e/Repos/DiffusionNexus
grep -rn "DiffusionNexus.Installer.SDK" --include=*.csproj .
```

Expected: five `PackageReference` lines in `DiffusionNexus.UI.csproj` at version `1.2.36`, plus possibly references in `DiffusionNexus.Service.csproj` / `DiffusionNexus.Tests.csproj`. Record every file and line you find.

- [ ] **Step 2: Capture the current test baseline**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj 2>&1 | tail -5
```

Write down the passed/failed/skipped numbers. This is the baseline every later task compares against. If anything already fails, record which tests — do not try to fix pre-existing failures in this task.

- [ ] **Step 3: Bump every reference to 1.2.39**

In each file found in step 1, change `Version="1.2.36"` to `Version="1.2.39"` for the SDK packages only. Example for `DiffusionNexus.UI.csproj`:

```xml
    <PackageReference Include="DiffusionNexus.Installer.SDK.Models" Version="1.2.39" />
    <PackageReference Include="DiffusionNexus.Installer.SDK.Services" Version="1.2.39" />
    <PackageReference Include="DiffusionNexus.Installer.SDK.DataAccess" Version="1.2.39" />
    <PackageReference Include="DiffusionNexus.Installer.SDK.Shared" Version="1.2.39" />
    <PackageReference Include="DiffusionNexus.Installer.SDK.Database" Version="1.2.39" />
```

- [ ] **Step 4: Restore and build**

```bash
dotnet restore DiffusionNexus.sln && dotnet build DiffusionNexus.sln -c Debug 2>&1 | tail -20
```

Expected: build succeeds. 1.2.39 contains one documented breaking-ish area (torch settings resolution moved behind `InstallationContext.EffectiveTorch`); if a compile error mentions torch or CUDA properties, report it and stop rather than guessing a fix.

- [ ] **Step 5: Run the full suite**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj 2>&1 | tail -5
```

Expected: same numbers as the step 2 baseline. Any new failure is caused by the bump — report it with the failing test names.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore: bump Installer SDK packages 1.2.36 -> 1.2.39"
```

---

### Task 2: Mark app-managed installations in the database

**Files:**
- Modify: `DiffusionNexus.Domain/Entities/InstallerPackage.cs`
- Create: `DiffusionNexus.DataAccess/Migrations/Core/<timestamp>_AddInstallerPackageIsAppManaged.cs` (generated by `dotnet ef`)
- Modify: `DiffusionNexus.DataAccess/Migrations/Core/DiffusionNexusCoreDbContextModelSnapshot.cs` (regenerated by `dotnet ef`)
- Test: `DiffusionNexus.Tests/InstallerManager/EngineInstallationTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `InstallerPackage.IsAppManaged` (`bool`, default `false`).

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Tests/InstallerManager/EngineInstallationTests.cs`:

```csharp
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Tests for the app-managed ("Diffusion Nexus Engine") installation record.
/// </summary>
public class EngineInstallationTests
{
    [Fact]
    public void InstallerPackage_DefaultsToNotAppManaged()
    {
        var package = new InstallerPackage
        {
            Name = "ComfyUI",
            InstallationPath = @"C:\ComfyUI",
            ExecutablePath = null,
            Type = InstallerType.ComfyUI
        };

        package.IsAppManaged.Should().BeFalse(
            "installations the user added by hand must never be treated as engine-owned");
    }

    [Fact]
    public void InstallerPackage_CanBeMarkedAppManaged()
    {
        var package = new InstallerPackage
        {
            Name = "Diffusion Nexus Engine",
            InstallationPath = @"C:\Engine\ComfyUI",
            ExecutablePath = null,
            Type = InstallerType.ComfyUI,
            IsAppManaged = true
        };

        package.IsAppManaged.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EngineInstallationTests" 2>&1 | tail -10
```

Expected: compile error — `'InstallerPackage' does not contain a definition for 'IsAppManaged'`.

- [ ] **Step 3: Add the property**

In `DiffusionNexus.Domain/Entities/InstallerPackage.cs`, add after `IsDefault`:

```csharp
        /// <summary>
        /// True when this installation is owned and maintained by the app itself
        /// (the Diffusion Nexus Engine), not added by the user. App-managed rows are
        /// hidden from the ordinary Installer Manager card list — the static engine
        /// tile represents them — and offer no Remove/Delete/Edit actions. They are
        /// ordinary ComfyUI installations in every other respect, so model-root
        /// resolution and extra_model_paths discovery pick them up unchanged.
        /// </summary>
        public bool IsAppManaged { get; set; } = false;
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EngineInstallationTests" 2>&1 | tail -5
```

Expected: 2 passed.

- [ ] **Step 5: Generate the migration**

```bash
cd /e/Repos/DiffusionNexus/DiffusionNexus.DataAccess
dotnet ef migrations add AddInstallerPackageIsAppManaged --context DiffusionNexusCoreDbContext --output-dir Migrations/Core
```

Expected: a new migration plus an updated snapshot. Open the generated `.cs` and confirm it contains exactly one `AddColumn<bool>` for `IsAppManaged` on the installer packages table with `defaultValue: false`, and nothing else. If it contains unrelated changes, the model snapshot was stale — stop and report rather than committing an unrelated schema change.

- [ ] **Step 6: Verify the migration applies**

```bash
cd /e/Repos/DiffusionNexus
dotnet build DiffusionNexus.sln -c Debug 2>&1 | tail -5
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj 2>&1 | tail -5
```

Expected: build succeeds, suite matches the Task 1 baseline plus the 2 new tests.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: mark app-managed installations with IsAppManaged"
```

---

### Task 3: Engine card ViewModel and Installer Manager wiring

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/InstallerPackageCardViewModel.cs`
- Modify: `DiffusionNexus.UI/ViewModels/InstallerManagerViewModel.cs:121-155` (`LoadInstallationsAsync`)
- Test: `DiffusionNexus.Tests/InstallerManager/EngineInstallationTests.cs` (extend)

**Interfaces:**
- Consumes: `InstallerPackage.IsAppManaged` (Task 2).
- Produces:
  - `InstallerPackageCardViewModel.CreateEngineCard(InstallerPackage? installed)` → card with `IsEngine == true`.
  - Properties `IsEngine`, `IsEngineInstalled`, `ShowEngineInstallButton`, `ShowEngineWorkloadsButton`, `EngineStatusMessage`, `IsEngineInstalling`, `EngineProgressPercent`.
  - Event `event Func<InstallerPackageCardViewModel, Task>? EngineInstallRequested`.
  - `InstallerManagerViewModel.IsEngineTileVisible` (`bool`, settable from `App.axaml.cs`).

- [ ] **Step 1: Write the failing tests**

Append to `DiffusionNexus.Tests/InstallerManager/EngineInstallationTests.cs` (inside the existing class):

```csharp
    [Fact]
    public void CreateEngineCard_WithoutInstall_OffersInstallOnly()
    {
        var card = InstallerPackageCardViewModel.CreateEngineCard(null);

        card.IsEngine.Should().BeTrue();
        card.IsEngineInstalled.Should().BeFalse();
        card.Name.Should().Be("Diffusion Nexus Engine");
        card.ShowEngineInstallButton.Should().BeTrue();
        card.ShowEngineWorkloadsButton.Should().BeFalse();
        card.ShowLaunchButton.Should().BeFalse("the engine is not a user-launchable app");
    }

    [Fact]
    public void CreateEngineCard_WithInstall_OffersWorkloadsOnly()
    {
        var package = new InstallerPackage
        {
            Id = 0,
            Name = "Diffusion Nexus Engine",
            InstallationPath = @"C:\Engine\ComfyUI",
            ExecutablePath = null,
            Type = InstallerType.ComfyUI,
            IsAppManaged = true
        };

        var card = InstallerPackageCardViewModel.CreateEngineCard(package);

        card.IsEngine.Should().BeTrue();
        card.IsEngineInstalled.Should().BeTrue();
        card.InstallationPath.Should().Be(@"C:\Engine\ComfyUI");
        card.ShowEngineInstallButton.Should().BeFalse();
        card.ShowEngineWorkloadsButton.Should().BeTrue();
    }

    [Fact]
    public void EngineCard_WhileInstalling_HidesInstallButton()
    {
        var card = InstallerPackageCardViewModel.CreateEngineCard(null);

        card.IsEngineInstalling = true;

        card.ShowEngineInstallButton.Should().BeFalse(
            "a second install must not be startable while one is running");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EngineInstallationTests" 2>&1 | tail -10
```

Expected: compile error — `CreateEngineCard` does not exist.

- [ ] **Step 3: Implement the engine card**

In `DiffusionNexus.UI/ViewModels/InstallerPackageCardViewModel.cs`, add the backing fields and members. Place the observable properties next to the existing ones and the computed properties next to `ShowActionsPanel`:

```csharp
    /// <summary>
    /// True when this card represents the built-in Diffusion Nexus Engine (the
    /// app-owned ComfyUI). Not derived from <see cref="Type"/>: the backing row is an
    /// ordinary <see cref="InstallerType.ComfyUI"/> installation so the rest of the app
    /// (model roots, extra_model_paths discovery) treats it normally.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLaunchButton))]
    [NotifyPropertyChangedFor(nameof(ShowUpdateButton))]
    [NotifyPropertyChangedFor(nameof(ShowWorkloadsButton))]
    [NotifyPropertyChangedFor(nameof(ShowActionsPanel))]
    [NotifyPropertyChangedFor(nameof(ShowEngineInstallButton))]
    [NotifyPropertyChangedFor(nameof(ShowEngineWorkloadsButton))]
    private bool _isEngine;

    /// <summary>True when the engine has been installed (a backing row exists on disk).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEngineInstallButton))]
    [NotifyPropertyChangedFor(nameof(ShowEngineWorkloadsButton))]
    private bool _isEngineInstalled;

    /// <summary>True while the engine install is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEngineInstallButton))]
    private bool _isEngineInstalling;

    /// <summary>Live status line shown on the engine tile during install.</summary>
    [ObservableProperty]
    private string? _engineStatusMessage;

    /// <summary>Install progress 0-100; 0 when idle.</summary>
    [ObservableProperty]
    private double _engineProgressPercent;

    /// <summary>Install button: engine tile, not yet installed, not currently installing.</summary>
    public bool ShowEngineInstallButton => IsEngine && !IsEngineInstalled && !IsEngineInstalling;

    /// <summary>Workloads button on the engine tile: only once the engine exists.</summary>
    public bool ShowEngineWorkloadsButton => IsEngine && IsEngineInstalled;

    /// <summary>Raised when the user presses Install on the engine tile.</summary>
    public event Func<InstallerPackageCardViewModel, Task>? EngineInstallRequested;

    [RelayCommand]
    private async Task InstallEngineAsync()
    {
        if (EngineInstallRequested is not null)
            await EngineInstallRequested.Invoke(this);
    }

    /// <summary>
    /// Creates the singleton "Diffusion Nexus Engine" tile. Pass the app-managed
    /// <see cref="InstallerPackage"/> when the engine is installed, or <c>null</c> when it
    /// is not — the tile then offers Install instead of Workloads.
    /// </summary>
    public static InstallerPackageCardViewModel CreateEngineCard(InstallerPackage? installed)
    {
        var card = installed is null
            ? new InstallerPackageCardViewModel(forCore: true)
            : new InstallerPackageCardViewModel(installed);

        card.Name = "Diffusion Nexus Engine";
        card.Type = InstallerType.ComfyUI;
        card.IsEngine = true;
        card.IsEngineInstalled = installed is not null;
        card.VersionDisplay = installed is null ? "Not installed" : "App-managed";
        card.InstallationPath = installed?.InstallationPath ?? string.Empty;

        return card;
    }
```

Then update the three existing computed properties so the engine tile never shows ordinary actions:

```csharp
    public bool ShowLaunchButton => !IsRunning && !IsMissing && !IsCore && !IsEngine;

    public bool ShowUpdateButton => IsUpdateAvailable && !IsUpdating && !IsRunning && !IsMissing && !IsCore && !IsEngine;

    public bool ShowWorkloadsButton => !IsEngine && (IsCore || (IsComfyUi && !IsMissing));
```

Note: the private `InstallerPackageCardViewModel(bool forCore)` constructor throws unless `forCore` is true — reusing it for the not-installed engine tile is deliberate (no backing row, `Id == 0`). The `Name`/`Type` assignments immediately after override its Core defaults.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EngineInstallationTests" 2>&1 | tail -5
```

Expected: 5 passed.

- [ ] **Step 5: Write the failing test for list filtering**

Append to the same test class:

```csharp
    [Fact]
    public async Task LoadInstallations_HidesAppManagedRowsFromTheOrdinaryList()
    {
        var packages = new List<InstallerPackage>
        {
            new() { Id = 1, Name = "My ComfyUI", InstallationPath = @"C:\A", ExecutablePath = null,
                    Type = InstallerType.ComfyUI },
            new() { Id = 2, Name = "Diffusion Nexus Engine", InstallationPath = @"C:\Engine", ExecutablePath = null,
                    Type = InstallerType.ComfyUI, IsAppManaged = true }
        };

        var vm = EngineTestHarness.CreateInstallerManagerViewModel(packages);

        await vm.LoadInstallationsCommand.ExecuteAsync(null);

        vm.InstallerCards.Should().HaveCount(3, "Core tile + engine tile + the one user installation");
        vm.InstallerCards.Count(c => c.IsEngine).Should().Be(1);
        vm.InstallerCards.Count(c => !c.IsCore && !c.IsEngine).Should().Be(1);
        vm.InstallerCards.Single(c => c.IsEngine).IsEngineInstalled.Should().BeTrue();
    }
```

Create `DiffusionNexus.Tests/InstallerManager/EngineTestHarness.cs` with the shared mock setup (the `InstallerManagerViewModel` constructor takes nine required dependencies):

```csharp
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.ViewModels;
using Moq;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Builds an <see cref="InstallerManagerViewModel"/> backed by mocks, for engine-tile tests.
/// </summary>
internal static class EngineTestHarness
{
    public static InstallerManagerViewModel CreateInstallerManagerViewModel(
        IReadOnlyList<InstallerPackage> packages)
    {
        var repo = new Mock<IInstallerPackageRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(packages.ToList());

        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.InstallerPackages).Returns(repo.Object);
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        return new InstallerManagerViewModel(
            new Mock<IDialogService>().Object,
            uow.Object,
            new PackageProcessManager(),
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IConfigurationRepository>().Object,
            new Mock<IConfigurationCheckerService>().Object,
            new Mock<IWorkloadInstallService>().Object,
            [],
            new Mock<IUnifiedLogger>().Object);
    }
}
```

- [ ] **Step 6: Run it to verify it fails**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EngineInstallationTests" 2>&1 | tail -10
```

Expected: FAIL — the engine tile is not created and the app-managed row appears as an ordinary card (count 3 with two ordinary cards, or an assertion failure on `IsEngine`).

- [ ] **Step 7: Wire the tile into the list**

In `DiffusionNexus.UI/ViewModels/InstallerManagerViewModel.cs`, replace the body of the `try` block in `LoadInstallationsAsync` (currently lines 124-144) with:

```csharp
            IsLoading = true;
            InstallerCards.Clear();

            // Static "Diffusion Nexus Core" entry — see CreateCoreCard.
            InstallerCards.Add(CreateCoreCard());

            var packages = await _unitOfWork.InstallerPackages.GetAllAsync();

            // The app-owned engine is an ordinary ComfyUI row flagged IsAppManaged. It is
            // rendered as the static engine tile and must not also appear as a normal card.
            var enginePackage = packages.FirstOrDefault(p => p.IsAppManaged);
            InstallerCards.Add(CreateEngineCard(enginePackage));

            foreach (var package in packages.Where(p => !p.IsAppManaged))
            {
                InstallerCards.Add(CreateCard(package));
            }

            // Check for updates in the background after loading
            _ = CheckAllCardsForUpdatesAsync();
```

Add the factory next to `CreateCoreCard`:

```csharp
    /// <summary>
    /// Builds the static "Diffusion Nexus Engine" tile. Wires the install handler and, once
    /// the engine exists, the workloads handler. Visibility is controlled by
    /// <see cref="IsEngineTileVisible"/>, which follows the same switch as the Diffusion Canvas.
    /// </summary>
    private InstallerPackageCardViewModel CreateEngineCard(InstallerPackage? enginePackage)
    {
        var card = InstallerPackageCardViewModel.CreateEngineCard(enginePackage);
        card.EngineInstallRequested += OnEngineInstallRequestedAsync;
        card.WorkloadsRequested += OnWorkloadsRequestedAsync;
        return card;
    }

    private Task OnEngineInstallRequestedAsync(InstallerPackageCardViewModel card)
    {
        // Implemented in the engine-install task.
        return Task.CompletedTask;
    }
```

Add the visibility property next to `IsEmpty`:

```csharp
    /// <summary>
    /// Whether the Diffusion Nexus Engine tile is shown. Bound at startup to the same
    /// hamburger switch that reveals the Diffusion Canvas, so both surfaces stay hidden
    /// together while the feature is unfinished.
    /// </summary>
    [ObservableProperty]
    private bool _isEngineTileVisible;
```

- [ ] **Step 8: Run the tests to verify they pass**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EngineInstallationTests" 2>&1 | tail -5
```

Expected: 6 passed.

- [ ] **Step 9: Render the tile in the view**

In `DiffusionNexus.UI/Views/InstallerManagerView.axaml`, inside the `ItemsControl.ItemTemplate` card layout (the `ItemsControl` starts at line 189), add the engine controls. Put this block immediately before the closing tag of the actions panel that currently binds `IsVisible="{Binding ShowActionsPanel}"` (line 351):

```xml
                                        <!-- Diffusion Nexus Engine tile actions -->
                                        <StackPanel Orientation="Vertical" Spacing="6"
                                                    IsVisible="{Binding IsEngine}">
                                            <Button Content="Install"
                                                    Command="{Binding InstallEngineCommand}"
                                                    IsVisible="{Binding ShowEngineInstallButton}" />
                                            <Button Content="Workloads"
                                                    Command="{Binding ShowWorkloadsCommand}"
                                                    IsVisible="{Binding ShowEngineWorkloadsButton}" />
                                            <TextBlock Text="{Binding EngineStatusMessage}"
                                                       TextWrapping="Wrap"
                                                       IsVisible="{Binding IsEngineInstalling}" />
                                            <ProgressBar Minimum="0" Maximum="100"
                                                         Value="{Binding EngineProgressPercent}"
                                                         IsVisible="{Binding IsEngineInstalling}" />
                                        </StackPanel>
```

Match the surrounding indentation and button styling of the existing card buttons — copy the `Classes`/`Style` attributes from the neighbouring Launch button rather than inventing new ones.

Then hide the whole card when the tile is switched off. On the card's root container inside the item template, the engine card must respect `IsEngineTileVisible` from the parent ViewModel:

```xml
                            IsVisible="{Binding !IsEngine}"
```

is *not* correct — instead bind the engine card's root visibility to the parent DataContext:

```xml
                            IsVisible="{Binding $parent[ItemsControl].((vm:InstallerManagerViewModel)DataContext).IsEngineTileVisible}"
```

Apply that only to a wrapper around the engine tile's own content (guard it with `IsEngine`); ordinary cards must stay visible regardless. If the XAML binding proves awkward, the acceptable alternative is to not add the engine card to `InstallerCards` at all when `IsEngineTileVisible` is false — in that case update the Step 5 test to set `vm.IsEngineTileVisible = true` before loading, and add a second test asserting no engine card when it is false.

- [ ] **Step 10: Bind the switch at startup**

In `DiffusionNexus.UI/App.axaml.cs`, in the block that registers the Diffusion Canvas module (around line 1160-1182), after `mainViewModel.SetDiffusionCanvasModule(diffusionCanvasModule);` add:

```csharp
                // The engine tile follows the same switch as the Canvas — both surfaces are
                // unfinished and must appear or disappear together.
                var installerManagerVm = Services!.GetService<DiffusionNexus.UI.ViewModels.InstallerManagerViewModel>();
                if (installerManagerVm is not null)
                {
                    installerManagerVm.IsEngineTileVisible = mainViewModel.IsDiffusionCanvasEnabled;
                    mainViewModel.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(mainViewModel.IsDiffusionCanvasEnabled))
                            installerManagerVm.IsEngineTileVisible = mainViewModel.IsDiffusionCanvasEnabled;
                    };
                }
```

- [ ] **Step 11: Build and run the full suite**

```bash
dotnet build DiffusionNexus.sln -c Debug 2>&1 | tail -5
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj 2>&1 | tail -5
```

Expected: build succeeds, baseline + 6 new tests pass.

- [ ] **Step 12: Commit**

```bash
git add -A
git commit -m "feat: add the Diffusion Nexus Engine tile to the Installer Manager"
```

---

### Task 4: Engine location + SDK prompt shim

**Files:**
- Create: `DiffusionNexus.UI/Services/Engine/ManagedEngineLocator.cs`
- Create: `DiffusionNexus.UI/Services/Engine/DialogUserPromptService.cs`
- Test: `DiffusionNexus.Tests/Engine/ManagedEngineLocatorTests.cs` (create)

**Interfaces:**
- Consumes: `IDialogService` (`ShowMessageAsync`, `ShowConfirmAsync`).
- Produces:
  - `static string ManagedEngineLocator.DefaultInstallRoot { get; }` → `%LocalAppData%\DiffusionNexus\Engine\ComfyUI`.
  - `static bool ManagedEngineLocator.LooksInstalled(string? installRoot)`.
  - `DialogUserPromptService : IUserPromptService` (SDK interface: `Task<bool> ConfirmAsync(string, string, string, string)`, `Task ShowErrorAsync(string, string)`, `Task ShowInfoAsync(string, string)`).

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests/Engine/ManagedEngineLocatorTests.cs`:

```csharp
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public class ManagedEngineLocatorTests
{
    [Fact]
    public void DefaultInstallRoot_LivesUnderLocalAppData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        ManagedEngineLocator.DefaultInstallRoot.Should().StartWith(localAppData);
        ManagedEngineLocator.DefaultInstallRoot.Should().EndWith(Path.Combine("DiffusionNexus", "Engine", "ComfyUI"));
    }

    [Fact]
    public void LooksInstalled_IsFalseForNullOrMissingFolder()
    {
        ManagedEngineLocator.LooksInstalled(null).Should().BeFalse();
        ManagedEngineLocator.LooksInstalled("   ").Should().BeFalse();
        ManagedEngineLocator.LooksInstalled(Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid()))
            .Should().BeFalse();
    }

    [Fact]
    public void LooksInstalled_IsTrueOnlyWhenComfyUiEntryPointExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "dn-engine-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            ManagedEngineLocator.LooksInstalled(root).Should().BeFalse("an empty folder is not an install");

            File.WriteAllText(Path.Combine(root, "main.py"), "# comfy");
            ManagedEngineLocator.LooksInstalled(root).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ManagedEngineLocatorTests" 2>&1 | tail -10
```

Expected: compile error — namespace/type `ManagedEngineLocator` not found.

- [ ] **Step 3: Implement the locator**

Create `DiffusionNexus.UI/Services/Engine/ManagedEngineLocator.cs`:

```csharp
namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// Resolves where the app-owned ComfyUI engine lives and whether it is present on disk.
/// The install root is user-choosable (the engine is 5-8 GB with torch, so forcing it onto
/// C: is not acceptable); this type only supplies the default and the presence check.
/// </summary>
public static class ManagedEngineLocator
{
    /// <summary>
    /// Default install root: <c>%LocalAppData%\DiffusionNexus\Engine\ComfyUI</c>.
    /// Offered as the pre-filled path in the folder picker, never forced.
    /// </summary>
    public static string DefaultInstallRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiffusionNexus", "Engine", "ComfyUI");

    /// <summary>
    /// True when <paramref name="installRoot"/> contains a ComfyUI entry point. Used to detect
    /// an engine whose database row exists but whose folder was deleted behind the app's back.
    /// </summary>
    public static bool LooksInstalled(string? installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
            return false;

        // The SDK clones ComfyUI either directly into the root or into a ComfyUI/ subfolder,
        // depending on the resolved layout — accept both.
        return File.Exists(Path.Combine(installRoot, "main.py"))
            || File.Exists(Path.Combine(installRoot, "ComfyUI", "main.py"));
    }
}
```

- [ ] **Step 4: Run to verify pass**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ManagedEngineLocatorTests" 2>&1 | tail -5
```

Expected: 3 passed.

- [ ] **Step 5: Implement the prompt shim**

Create `DiffusionNexus.UI/Services/Engine/DialogUserPromptService.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Shared.Services;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// Bridges the Installer SDK's prompt contract onto the app's dialog service. Against the
/// fresh, empty engine folder the pre-checks are trivial; the prompt that can genuinely fire
/// is the GPU gate's CPU-only offer, which the user must answer honestly rather than have
/// declined on their behalf.
/// </summary>
public sealed class DialogUserPromptService : IUserPromptService
{
    private readonly IDialogService _dialogService;

    public DialogUserPromptService(IDialogService dialogService)
    {
        ArgumentNullException.ThrowIfNull(dialogService);
        _dialogService = dialogService;
    }

    public Task<bool> ConfirmAsync(string title, string message,
        string yesButtonText = "Yes", string noButtonText = "No")
        => _dialogService.ShowConfirmAsync(title, message);

    public Task ShowErrorAsync(string title, string message)
        => _dialogService.ShowMessageAsync(title, message);

    public Task ShowInfoAsync(string title, string message)
        => _dialogService.ShowMessageAsync(title, message);
}
```

Note: `IDialogService.ShowConfirmAsync` has no custom button captions, so `yesButtonText`/`noButtonText` are intentionally ignored — the SDK's captions are advisory.

- [ ] **Step 6: Build**

```bash
dotnet build DiffusionNexus.sln -c Debug 2>&1 | tail -5
```

Expected: success. If `IUserPromptService` is not found, check the using — it lives in `DiffusionNexus.Installer.SDK.Shared.Services` (confirm with `grep -rn "interface IUserPromptService" ~/.nuget/packages/diffusionnexus.installer.sdk.shared/1.2.39/` or in the SDK repo at `e:\Repos\DiffusionNexus.Installer.SDK`).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add engine locator and SDK prompt shim"
```

---

### Task 5: Stage 1 — the base engine installer

**Files:**
- Create: `DiffusionNexus.UI/Services/Engine/IManagedEngineInstaller.cs`
- Create: `DiffusionNexus.UI/Services/Engine/ManagedEngineInstaller.cs`
- Test: `DiffusionNexus.Tests/Engine/ManagedEngineInstallerTests.cs` (create)

**Interfaces:**
- Consumes: `IInstallationCoordinator` (SDK), `IConfigurationRepository` (SDK), `IUserPromptService` (Task 4).
- Produces:
  - `record EngineInstallRequest(string InstallRoot, IReadOnlyList<string> SharedModelRoots)`.
  - `record EngineInstallOutcome(bool IsSuccess, bool IsCancelled, string Message, string? RepositoryPath)`.
  - `interface IManagedEngineInstaller` with
    `Task<EngineInstallOutcome> InstallBaseEngineAsync(EngineInstallRequest request, IProgress<InstallLogEntry> log, IProgress<InstallationProgress> step, CancellationToken ct)`.
  - `const string ManagedEngineInstaller.BaseConfigurationId = "E79C079A-2FD7-4FE7-8086-23731092555D"`.

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests/Engine/ManagedEngineInstallerTests.cs`:

```csharp
using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Shared;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Engine;

public class ManagedEngineInstallerTests
{
    private static InstallationConfiguration BuildKreaConfiguration()
    {
        var config = new InstallationConfiguration
        {
            Id = Guid.Parse(ManagedEngineInstaller.BaseConfigurationId),
            Name = "Krea-2-Turbo"
        };
        config.ModelDownloads.Add(new ModelDownload { Id = Guid.NewGuid(), Name = "Krea2 GGUF" });
        config.ModelDownloads.Add(new ModelDownload { Id = Guid.NewGuid(), Name = "qwen_image_vae" });
        config.GitRepositories.Add(new GitRepository { Id = Guid.NewGuid(), Name = "gguf" });
        config.Workflows.Add(new ComfUIWorkflow { Id = Guid.NewGuid(), Name = "1.Krea2-Turbo-Text2Image" });
        return config;
    }

    private static (Mock<IInstallationCoordinator> Coordinator, Mock<IConfigurationRepository> Repo)
        Mocks(InstallationConfiguration config)
    {
        var repo = new Mock<IConfigurationRepository>();
        repo.Setup(r => r.GetByIdAsync(config.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var coordinator = new Mock<IInstallationCoordinator>();
        coordinator.Setup(c => c.EvaluateGpuGateAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<IUserPromptService>(),
                It.IsAny<Action<string, LogEntryLevel>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GpuGateOutcome.Proceed);
        coordinator.Setup(c => c.RunPreChecksAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationType>(),
                It.IsAny<IUserPromptService>(), It.IsAny<Action<string, LogEntryLevel>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PreInstallationResult { Result = PreInstallationCheckResult.CanProceed });
        coordinator.Setup(c => c.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(InstallationResult.Success("done", @"C:\Engine\ComfyUI", @"C:\Engine\ComfyUI\venv"));

        return (coordinator, repo);
    }

    private static ManagedEngineInstaller Create(
        Mock<IInstallationCoordinator> coordinator, Mock<IConfigurationRepository> repo)
        => new(coordinator.Object, repo.Object, new Mock<IUserPromptService>().Object);

    [Fact]
    public async Task InstallBaseEngine_ExcludesEveryPieceOfContent()
    {
        var config = BuildKreaConfiguration();
        var (coordinator, repo) = Mocks(config);
        InstallationOptions? captured = null;

        coordinator.Setup(c => c.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<InstallationConfiguration, string, InstallationOptions, IProgress<InstallLogEntry>,
                IProgress<InstallationProgress>, IProgress<DownloadProgress>, Func<CancellationToken>?,
                CancellationToken>((_, _, o, _, _, _, _, _) => captured = o)
            .ReturnsAsync(InstallationResult.Success("done", @"C:\Engine\ComfyUI"));

        var outcome = await Create(coordinator, repo).InstallBaseEngineAsync(
            new EngineInstallRequest(@"C:\Engine\ComfyUI", []),
            new Progress<InstallLogEntry>(), new Progress<InstallationProgress>(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.ExcludedModelIds.Should().BeEquivalentTo(config.ModelDownloads.Select(m => m.Id));
        captured.ExcludedNodeIds.Should().BeEquivalentTo(config.GitRepositories.Select(g => g.Id));
        captured.ExcludedWorkflowIds.Should().BeEquivalentTo(config.Workflows.Select(w => w.Id));
    }

    [Fact]
    public async Task InstallBaseEngine_NeverCreatesShortcuts()
    {
        var config = BuildKreaConfiguration();
        var (coordinator, repo) = Mocks(config);
        InstallationOptions? captured = null;

        coordinator.Setup(c => c.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<InstallationConfiguration, string, InstallationOptions, IProgress<InstallLogEntry>,
                IProgress<InstallationProgress>, IProgress<DownloadProgress>, Func<CancellationToken>?,
                CancellationToken>((_, _, o, _, _, _, _, _) => captured = o)
            .ReturnsAsync(InstallationResult.Success("done", @"C:\Engine\ComfyUI"));

        await Create(coordinator, repo).InstallBaseEngineAsync(
            new EngineInstallRequest(@"C:\Engine\ComfyUI", [@"D:\Models"]),
            new Progress<InstallLogEntry>(), new Progress<InstallationProgress>(), CancellationToken.None);

        captured!.CreateDesktopShortcut.Should().BeFalse();
        captured.CreateStartMenuShortcut.Should().BeFalse();
        captured.GenerateExtraModelPaths.Should().BeTrue(
            "the engine must read the shared model library instead of duplicating it");
    }

    [Fact]
    public async Task InstallBaseEngine_StopsWhenTheGpuGateBlocks()
    {
        var config = BuildKreaConfiguration();
        var (coordinator, repo) = Mocks(config);
        coordinator.Setup(c => c.EvaluateGpuGateAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<IUserPromptService>(),
                It.IsAny<Action<string, LogEntryLevel>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GpuGateOutcome.NoCompatibleGpu);

        var outcome = await Create(coordinator, repo).InstallBaseEngineAsync(
            new EngineInstallRequest(@"C:\Engine\ComfyUI", []),
            new Progress<InstallLogEntry>(), new Progress<InstallationProgress>(), CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Message.Should().Contain("GPU");
        coordinator.Verify(c => c.InstallAsync(
            It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
            It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
            It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InstallBaseEngine_FailsClearlyWhenTheConfigurationIsMissing()
    {
        var repo = new Mock<IConfigurationRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InstallationConfiguration?)null);

        var outcome = await Create(new Mock<IInstallationCoordinator>(), repo).InstallBaseEngineAsync(
            new EngineInstallRequest(@"C:\Engine\ComfyUI", []),
            new Progress<InstallLogEntry>(), new Progress<InstallationProgress>(), CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Message.Should().Contain(ManagedEngineInstaller.BaseConfigurationId);
    }

    [Fact]
    public async Task InstallBaseEngine_ReportsCancellationDistinctlyFromFailure()
    {
        var config = BuildKreaConfiguration();
        var (coordinator, repo) = Mocks(config);
        coordinator.Setup(c => c.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(InstallationResult.Cancelled("user aborted"));

        var outcome = await Create(coordinator, repo).InstallBaseEngineAsync(
            new EngineInstallRequest(@"C:\Engine\ComfyUI", []),
            new Progress<InstallLogEntry>(), new Progress<InstallationProgress>(), CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.IsCancelled.Should().BeTrue("a deliberate abort must not trigger failure reporting UI");
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ManagedEngineInstallerTests" 2>&1 | tail -10
```

Expected: compile errors — `ManagedEngineInstaller`, `EngineInstallRequest` not found.

If instead you get errors about `PreInstallationResult` or `ComfUIWorkflow` property names, inspect the real SDK types before adapting the test:

```bash
grep -rn "class PreInstallationResult" -A 12 /e/Repos/DiffusionNexus.Installer.SDK/DiffusionNexus.Installer.SDK.Models/
```

Adjust the test's construction to the real shape; do not change what is being asserted.

- [ ] **Step 3: Implement the installer**

Create `DiffusionNexus.UI/Services/Engine/IManagedEngineInstaller.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Shared;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>What the caller wants installed and where.</summary>
/// <param name="InstallRoot">Target folder chosen by the user.</param>
/// <param name="SharedModelRoots">
/// Existing model libraries the engine should read through extra_model_paths.yaml, so workload
/// models are never duplicated.
/// </param>
public sealed record EngineInstallRequest(string InstallRoot, IReadOnlyList<string> SharedModelRoots);

/// <summary>Result of a base-engine install.</summary>
public sealed record EngineInstallOutcome(
    bool IsSuccess,
    bool IsCancelled,
    string Message,
    string? RepositoryPath);

/// <summary>
/// Installs the base engine: ComfyUI + venv + torch, with no models, custom nodes or workflows.
/// Workload content is added afterwards through the ordinary workload installer.
/// </summary>
public interface IManagedEngineInstaller
{
    Task<EngineInstallOutcome> InstallBaseEngineAsync(
        EngineInstallRequest request,
        IProgress<InstallLogEntry> logProgress,
        IProgress<InstallationProgress> stepProgress,
        CancellationToken cancellationToken);
}
```

Create `DiffusionNexus.UI/Services/Engine/ManagedEngineInstaller.cs`:

```csharp
using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Shared;
using DiffusionNexus.Installer.SDK.Shared.Services;
using Serilog;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// Drives the SDK's installation coordinator to produce a bare, app-owned ComfyUI.
///
/// The base engine is built from the Krea-2-Turbo configuration with every model, custom node
/// and workflow excluded. That configuration is what pins the engine to CUDA 13.0 + torch
/// whatever pairing it declares — so no torch settings are authored here. (The shipped code's
/// comment was corrected after review: the catalog declares 12.8 + 2.8.0, not 13.0 + 2.11.0.)
/// Content arrives afterwards through <see cref="IWorkloadInstallService"/>.
/// </summary>
public sealed class ManagedEngineInstaller : IManagedEngineInstaller
{
    private static readonly ILogger Logger = Log.ForContext<ManagedEngineInstaller>();

    /// <summary>Catalog id of the configuration that defines the engine base (Krea-2-Turbo).</summary>
    public const string BaseConfigurationId = "E79C079A-2FD7-4FE7-8086-23731092555D";

    private readonly IInstallationCoordinator _coordinator;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly IUserPromptService _promptService;

    public ManagedEngineInstaller(
        IInstallationCoordinator coordinator,
        IConfigurationRepository configurationRepository,
        IUserPromptService promptService)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(configurationRepository);
        ArgumentNullException.ThrowIfNull(promptService);

        _coordinator = coordinator;
        _configurationRepository = configurationRepository;
        _promptService = promptService;
    }

    public async Task<EngineInstallOutcome> InstallBaseEngineAsync(
        EngineInstallRequest request,
        IProgress<InstallLogEntry> logProgress,
        IProgress<InstallationProgress> stepProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(logProgress);
        ArgumentNullException.ThrowIfNull(stepProgress);

        void LogAction(string message, LogEntryLevel level)
        {
            Logger.Information("Engine install: {Message}", message);
            logProgress.Report(new InstallLogEntry(message, level));
        }

        var configuration = await _configurationRepository
            .GetByIdAsync(Guid.Parse(BaseConfigurationId), cancellationToken)
            .ConfigureAwait(false);

        if (configuration is null)
        {
            return new EngineInstallOutcome(false, false,
                $"The engine base configuration {BaseConfigurationId} was not found in the catalog database. " +
                "The shipped catalog may be out of date.", null);
        }

        var gate = await _coordinator
            .EvaluateGpuGateAsync(configuration, _promptService, LogAction, cancellationToken)
            .ConfigureAwait(false);

        if (gate is GpuGateOutcome.NoCompatibleGpu or GpuGateOutcome.Cancelled)
        {
            return new EngineInstallOutcome(false, gate == GpuGateOutcome.Cancelled,
                $"GPU pre-flight stopped the engine install ({gate}). The engine needs a usable NVIDIA GPU.",
                null);
        }

        var preChecks = await _coordinator
            .RunPreChecksAsync(configuration, request.InstallRoot, InstallationType.FullInstall,
                _promptService, LogAction, cancellationToken)
            .ConfigureAwait(false);

        if (preChecks.Result != PreInstallationCheckResult.CanProceed)
        {
            return new EngineInstallOutcome(false, false,
                $"Pre-installation checks did not pass: {preChecks.Result}.", null);
        }

        var options = BuildBaseOnlyOptions(configuration, request);

        var result = await _coordinator.InstallAsync(
                configuration, request.InstallRoot, options,
                logProgress, stepProgress, new Progress<DownloadProgress>(),
                skipDownloadTokenProvider: null, cancellationToken)
            .ConfigureAwait(false);

        return new EngineInstallOutcome(
            result.IsSuccess, result.IsCancelled, result.Message, result.RepositoryPath);
    }

    /// <summary>
    /// Builds options that install the environment only: every declared model, custom node and
    /// workflow is excluded, shortcuts are off (the engine is not a user-launchable app), and
    /// extra_model_paths.yaml is generated so the engine reads the shared model library.
    /// </summary>
    private static InstallationOptions BuildBaseOnlyOptions(
        InstallationConfiguration configuration, EngineInstallRequest request)
    {
        return InstallationOptions.Default with
        {
            ExcludedModelIds = [.. configuration.ModelDownloads.Select(m => m.Id)],
            ExcludedNodeIds = [.. configuration.GitRepositories.Select(g => g.Id)],
            ExcludedWorkflowIds = [.. configuration.Workflows.Select(w => w.Id)],
            CreateDesktopShortcut = false,
            CreateStartMenuShortcut = false,
            GenerateExtraModelPaths = true,
            OverwriteExtraModelPaths = true,
            ModelBaseFolder = request.SharedModelRoots.FirstOrDefault(),
            VerboseLogging = true
        };
    }
}
```

Add `using DiffusionNexus.Installer.SDK.Models.Configuration;` if `InstallationConfiguration` does not resolve.

- [ ] **Step 4: Run to verify pass**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ManagedEngineInstallerTests" 2>&1 | tail -5
```

Expected: 5 passed. If `InstallLogEntry`'s constructor shape differs, check it (`grep -rn "record InstallLogEntry" /e/Repos/DiffusionNexus.Installer.SDK/`) and adapt the `LogAction` body only.

- [ ] **Step 5: Register in DI**

In `DiffusionNexus.UI/App.axaml.cs`, immediately after the existing `services.AddSingleton<InstallationEngine>(...)` registration (around line 720), add:

```csharp
        // Installation coordinator — the same facade the standalone Installer and Wizard use.
        // AddInstallationServices() registers the pipeline but not this facade.
        services.AddSingleton<IInstallationCoordinator>(sp =>
            new InstallationCoordinator(sp.GetRequiredService<InstallationEngine>()));

        // App-owned ComfyUI engine (Diffusion Nexus Engine).
        services.AddSingleton<DiffusionNexus.Installer.SDK.Shared.Services.IUserPromptService>(sp =>
            new Services.Engine.DialogUserPromptService(sp.GetRequiredService<IDialogService>()));
        services.AddSingleton<Services.Engine.IManagedEngineInstaller>(sp =>
            new Services.Engine.ManagedEngineInstaller(
                sp.GetRequiredService<IInstallationCoordinator>(),
                sp.GetRequiredService<IConfigurationRepository>(),
                sp.GetRequiredService<DiffusionNexus.Installer.SDK.Shared.Services.IUserPromptService>()));
```

- [ ] **Step 6: Build and run the full suite**

```bash
dotnet build DiffusionNexus.sln -c Debug 2>&1 | tail -5
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj 2>&1 | tail -5
```

Expected: build succeeds, all tests pass.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: install the base Diffusion Nexus Engine through the SDK coordinator"
```

---

### Task 6: Wire the Install button

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/InstallerManagerViewModel.cs`
- Test: `DiffusionNexus.Tests/InstallerManager/EngineInstallFlowTests.cs` (create)

**Interfaces:**
- Consumes: `IManagedEngineInstaller`, `ManagedEngineLocator`, `IDialogService`, `IUnifiedLogger`, `IUnitOfWork`.
- Produces: the engine `InstallerPackage` row (`IsAppManaged = true`, `Type = InstallerType.ComfyUI`) persisted on success; `InstallerManagerViewModel` constructor gains a trailing optional `IManagedEngineInstaller? engineInstaller = null` parameter.

- [ ] **Step 1: Write the failing test**

Create `DiffusionNexus.Tests/InstallerManager/EngineInstallFlowTests.cs`:

```csharp
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Shared;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.InstallerManager;

public class EngineInstallFlowTests
{
    [Fact]
    public async Task SuccessfulInstall_PersistsAnAppManagedComfyUiRow()
    {
        InstallerPackage? saved = null;

        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineInstallOutcome(true, false, "done", @"C:\Engine\ComfyUI"));

        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            packages: [],
            engineInstaller: installer.Object,
            chosenFolder: @"C:\Engine\ComfyUI",
            onPackageAdded: p => saved = p);

        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        await vm.InstallEngineAsync();

        saved.Should().NotBeNull();
        saved!.IsAppManaged.Should().BeTrue();
        saved.Type.Should().Be(InstallerType.ComfyUI);
        saved.InstallationPath.Should().Be(@"C:\Engine\ComfyUI");
        vm.InstallerCards.Single(c => c.IsEngine).IsEngineInstalled.Should().BeTrue();
    }

    [Fact]
    public async Task CancelledInstall_PersistsNothing()
    {
        InstallerPackage? saved = null;

        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineInstallOutcome(false, true, "aborted", null));

        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            packages: [], engineInstaller: installer.Object,
            chosenFolder: @"C:\Engine\ComfyUI", onPackageAdded: p => saved = p);

        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        await vm.InstallEngineAsync();

        saved.Should().BeNull();
        vm.InstallerCards.Single(c => c.IsEngine).IsEngineInstalled.Should().BeFalse();
    }

    [Fact]
    public async Task CancelledFolderPicker_DoesNotStartAnInstall()
    {
        var installer = new Mock<IManagedEngineInstaller>();

        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            packages: [], engineInstaller: installer.Object,
            chosenFolder: null, onPackageAdded: _ => { });

        vm.IsEngineTileVisible = true;
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        await vm.InstallEngineAsync();

        installer.Verify(i => i.InstallBaseEngineAsync(
            It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
            It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
```

**Replace** the `CreateInstallerManagerViewModel` method added in Task 3 with this richer version — do not add an overload, the two signatures would be ambiguous at the existing `CreateInstallerManagerViewModel(packages)` call site. That call site keeps compiling because every new parameter is optional:

```csharp
    public static InstallerManagerViewModel CreateInstallerManagerViewModel(
        IReadOnlyList<InstallerPackage> packages,
        IManagedEngineInstaller? engineInstaller = null,
        string? chosenFolder = null,
        Action<InstallerPackage>? onPackageAdded = null)
    {
        var repo = new Mock<IInstallerPackageRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(packages.ToList());
        repo.Setup(r => r.AddAsync(It.IsAny<InstallerPackage>(), It.IsAny<CancellationToken>()))
            .Callback<InstallerPackage, CancellationToken>((p, _) => onPackageAdded?.Invoke(p))
            .Returns(Task.CompletedTask);

        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.InstallerPackages).Returns(repo.Object);
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>())).ReturnsAsync(chosenFolder);

        return new InstallerManagerViewModel(
            dialog.Object, uow.Object, new PackageProcessManager(),
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IConfigurationRepository>().Object,
            new Mock<IConfigurationCheckerService>().Object,
            new Mock<IWorkloadInstallService>().Object,
            [], new Mock<IUnifiedLogger>().Object,
            engineInstaller: engineInstaller);
    }
```

Note: `InstallerManagerViewModel`'s existing optional parameters (`captioningModelManager`, `captioningService`, `activityLogService`, `downloadCoordinator`, `baseModelFolderRegistrar`, `unitOfWorkFactory`) all default to null, so the named `engineInstaller:` argument is enough.

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EngineInstallFlowTests" 2>&1 | tail -10
```

Expected: compile error — no `engineInstaller` parameter and no `InstallEngineAsync` method.

- [ ] **Step 3: Implement the flow**

In `InstallerManagerViewModel`, add the field, constructor parameter (last, after `unitOfWorkFactory`), and assignment:

```csharp
    private readonly Services.Engine.IManagedEngineInstaller? _engineInstaller;
```

```csharp
        Func<IUnitOfWork>? unitOfWorkFactory = null,
        Services.Engine.IManagedEngineInstaller? engineInstaller = null)
```

```csharp
        _engineInstaller = engineInstaller;
```

Replace the `OnEngineInstallRequestedAsync` stub from Task 3 and add the public entry point:

```csharp
    private Task OnEngineInstallRequestedAsync(InstallerPackageCardViewModel card) => InstallEngineAsync();

    /// <summary>
    /// Installs the base engine: asks for a target folder, runs the SDK install with all content
    /// excluded, and on success records it as an app-managed ComfyUI installation. Progress goes
    /// to the Unified Console so a stalled install shows its last successful step.
    /// </summary>
    public async Task InstallEngineAsync()
    {
        var card = InstallerCards.FirstOrDefault(c => c.IsEngine);
        if (card is null || _engineInstaller is null) return;

        var folder = await _dialogService.ShowOpenFolderDialogAsync(
            $"Choose where to install the Diffusion Nexus Engine (default: {Services.Engine.ManagedEngineLocator.DefaultInstallRoot})");

        if (string.IsNullOrWhiteSpace(folder)) return;

        UnifiedConsolePanelRequested?.Invoke(this, EventArgs.Empty);

        card.IsEngineInstalling = true;
        card.EngineProgressPercent = 0;
        card.EngineStatusMessage = "Starting engine install...";
        _unifiedLogger.Info(LogCategory.Installation, "Diffusion Nexus Engine",
            $"Starting base engine install into {folder}.");

        var logProgress = new Progress<DiffusionNexus.Installer.SDK.Shared.InstallLogEntry>(entry =>
        {
            card.EngineStatusMessage = entry.Message;
            _unifiedLogger.Info(LogCategory.Installation, "Diffusion Nexus Engine", entry.Message);
        });

        var stepProgress = new Progress<DiffusionNexus.Installer.SDK.Services.InstallationProgress>(p =>
        {
            card.EngineProgressPercent = p.ProgressPercentage;
            card.EngineStatusMessage = $"[{p.StepIndex + 1}/{p.TotalSteps}] {p.Message}";
        });

        try
        {
            var sharedRoots = await ResolveSharedModelRootsAsync();

            var outcome = await _engineInstaller.InstallBaseEngineAsync(
                new Services.Engine.EngineInstallRequest(folder, sharedRoots),
                logProgress, stepProgress, CancellationToken.None);

            if (outcome.IsSuccess)
            {
                var package = new InstallerPackage
                {
                    Name = "Diffusion Nexus Engine",
                    InstallationPath = outcome.RepositoryPath ?? folder,
                    Type = InstallerType.ComfyUI,
                    ExecutablePath = null,
                    Arguments = string.Empty,
                    IsAppManaged = true,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                await _unitOfWork.InstallerPackages.AddAsync(package);
                await _unitOfWork.SaveChangesAsync();

                card.InstallationPath = package.InstallationPath;
                card.IsEngineInstalled = true;
                card.VersionDisplay = "App-managed";

                _unifiedLogger.Info(LogCategory.Installation, "Diffusion Nexus Engine",
                    $"Engine installed at {package.InstallationPath}.");
                _eventAggregator.PublishInstallerPackagesChanged(new InstallerPackagesChangedEventArgs());
            }
            else if (outcome.IsCancelled)
            {
                _unifiedLogger.Info(LogCategory.Installation, "Diffusion Nexus Engine",
                    "Engine install cancelled by the user. Nothing was registered.");
            }
            else
            {
                _unifiedLogger.Error(LogCategory.Installation, "Diffusion Nexus Engine",
                    $"Engine install failed: {outcome.Message}");

                // A genuine failure (not a cancel) is worth reporting — offer the existing
                // feedback dialog instead of leaving the user with a dead end.
                var report = await _dialogService.ShowConfirmAsync("Engine install failed",
                    $"{outcome.Message}\n\nSend a report so this can be fixed?");
                if (report)
                    await _dialogService.ShowFeedbackDialogAsync();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Diffusion Nexus Engine install failed");
            _unifiedLogger.Error(LogCategory.Installation, "Diffusion Nexus Engine",
                $"Engine install failed: {ex.Message}", ex);
            await _dialogService.ShowMessageAsync("Engine install failed", ex.Message);
        }
        finally
        {
            card.IsEngineInstalling = false;
            card.EngineStatusMessage = null;
            card.EngineProgressPercent = 0;
        }
    }

    /// <summary>
    /// Model libraries the engine should read instead of duplicating. Uses the registered base
    /// model folders when available; an empty list simply means the engine keeps models locally.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveSharedModelRootsAsync()
    {
        try
        {
            var settings = await _unitOfWork.AppSettings.GetSettingsWithIncludesAsync();
            return settings.BaseModelFolders
                .Select(f => f.FolderPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not resolve shared model roots for the engine install.");
            return [];
        }
    }
```

If `AppSettings.BaseModelFolders` has a different member name, confirm it first:

```bash
grep -n "BaseModelFolder" /e/Repos/DiffusionNexus/DiffusionNexus.Domain/Entities/AppSettings.cs
```

and use the real name. In the harness, `GetSettingsWithIncludesAsync` is unmocked and returns null by default, so guard with `settings?.BaseModelFolders ?? []` — otherwise the test throws before installing.

- [ ] **Step 4: Run to verify pass**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EngineInstallFlowTests" 2>&1 | tail -5
```

Expected: 3 passed.

- [ ] **Step 5: Pass the installer through DI**

In `App.axaml.cs`, find the `InstallerManagerViewModel` registration (around line 905-920) and add the new argument:

```csharp
            engineInstaller: sp.GetRequiredService<Services.Engine.IManagedEngineInstaller>()
```

Keep the existing arguments unchanged; use a named argument so the optional parameters before it keep their defaults.

- [ ] **Step 6: Build and run the full suite**

```bash
dotnet build DiffusionNexus.sln -c Debug 2>&1 | tail -5
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj 2>&1 | tail -5
```

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: install the engine from the Installer Manager tile"
```

---

### Task 7: Stage 2 — engine workloads

**Files:**
- Create: `DiffusionNexus.UI/Services/Engine/EngineWorkloadCatalog.cs`
- Modify: `DiffusionNexus.UI/ViewModels/WorkloadsViewModel.cs`
- Modify: `DiffusionNexus.UI/ViewModels/InstallerManagerViewModel.cs` (`OnWorkloadsRequestedAsync`)
- Test: `DiffusionNexus.Tests/Engine/EngineWorkloadCatalogTests.cs` (create)

**Interfaces:**
- Consumes: `IConfigurationRepository`, `IConfigurationCheckerService`, `IWorkloadInstallService`, `IResourceMonitorService`.
- Produces:
  - `static IReadOnlyList<Guid> EngineWorkloadCatalog.WorkloadIds`.
  - `static bool EngineWorkloadCatalog.Contains(Guid id)`.
  - `static int EngineWorkloadCatalog.SuggestVramTier(long vramTotalMb, IReadOnlyList<int> configuredTiers)`.
  - `WorkloadsViewModel` constructor gains a trailing `IReadOnlyList<Guid>? allowedConfigurationIds = null` parameter.

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests/Engine/EngineWorkloadCatalogTests.cs`:

```csharp
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public class EngineWorkloadCatalogTests
{
    [Fact]
    public void Catalog_ContainsKrea2Turbo()
    {
        var krea = Guid.Parse("E79C079A-2FD7-4FE7-8086-23731092555D");

        EngineWorkloadCatalog.WorkloadIds.Should().Contain(krea);
        EngineWorkloadCatalog.Contains(krea).Should().BeTrue();
        EngineWorkloadCatalog.Contains(Guid.NewGuid()).Should().BeFalse();
    }

    [Theory]
    [InlineData(8192, 8)]     // 8 GB card -> smallest tier
    [InlineData(12288, 12)]
    [InlineData(16384, 16)]
    [InlineData(24576, 24)]
    [InlineData(49152, 32)]   // above the top tier -> top tier
    [InlineData(6144, 8)]     // below the smallest tier -> smallest tier, never 0
    [InlineData(0, 8)]        // unknown VRAM -> smallest tier
    public void SuggestVramTier_PicksTheLargestTierThatFits(long vramMb, int expected)
    {
        int[] tiers = [8, 12, 16, 24, 32];

        EngineWorkloadCatalog.SuggestVramTier(vramMb, tiers).Should().Be(expected);
    }

    [Fact]
    public void SuggestVramTier_ReturnsZeroWhenTheWorkloadDeclaresNoTiers()
    {
        EngineWorkloadCatalog.SuggestVramTier(24576, []).Should().Be(0,
            "0 means 'no VRAM filtering' to the workload installer");
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EngineWorkloadCatalogTests" 2>&1 | tail -10
```

Expected: compile error — `EngineWorkloadCatalog` not found.

- [ ] **Step 3: Implement the catalog**

Create `DiffusionNexus.UI/Services/Engine/EngineWorkloadCatalog.cs`:

```csharp
namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// The curated set of workloads offered inside the Diffusion Nexus Engine tile. Deliberately a
/// short allow-list rather than "every ComfyUI configuration": the engine is app-owned, so only
/// workloads we have verified against it are offered. Adding one is a single entry here.
/// </summary>
public static class EngineWorkloadCatalog
{
    /// <summary>Krea 2 Turbo — the first supported engine workload, and the engine's torch source.</summary>
    public static readonly Guid Krea2Turbo = Guid.Parse("E79C079A-2FD7-4FE7-8086-23731092555D");

    /// <summary>Configurations offered in the engine tile, in display order.</summary>
    public static IReadOnlyList<Guid> WorkloadIds { get; } = [Krea2Turbo];

    /// <summary>True when the configuration is an offered engine workload.</summary>
    public static bool Contains(Guid id) => WorkloadIds.Contains(id);

    /// <summary>
    /// Picks the default VRAM tier for a card: the largest configured tier that fits in the
    /// detected VRAM, falling back to the smallest tier when VRAM is unknown or below every tier
    /// (a too-small quantization still runs; refusing to preselect anything would not help).
    /// Returns 0 when the workload declares no tiers — the workload installer reads 0 as
    /// "no VRAM filtering".
    /// </summary>
    public static int SuggestVramTier(long vramTotalMb, IReadOnlyList<int> configuredTiers)
    {
        if (configuredTiers is null || configuredTiers.Count == 0)
            return 0;

        var ordered = configuredTiers.OrderBy(t => t).ToList();
        var vramGb = vramTotalMb / 1024.0;

        var best = ordered.LastOrDefault(t => t <= vramGb);
        return best == 0 ? ordered[0] : best;
    }
}
```

- [ ] **Step 4: Run to verify pass**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EngineWorkloadCatalogTests" 2>&1 | tail -5
```

Expected: 9 passed (1 + 7 theory cases + 1).

- [ ] **Step 5: Write the failing test for the filtered workload list**

Create `DiffusionNexus.Tests/Engine/EngineWorkloadsViewModelTests.cs`:

```csharp
using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Engine;

public class EngineWorkloadsViewModelTests
{
    private static InstallationConfiguration Config(Guid id, string name)
    {
        var config = new InstallationConfiguration { Id = id, Name = name };
        config.Repository.Type = RepositoryType.ComfyUI;
        config.Vram.VramProfiles = "8,12,16,24,32";
        return config;
    }

    [Fact]
    public async Task WithAllowList_OnlyTheAllowedWorkloadsAreListed()
    {
        var configs = new List<InstallationConfiguration>
        {
            Config(EngineWorkloadCatalog.Krea2Turbo, "Krea-2-Turbo"),
            Config(Guid.NewGuid(), "Some other workload")
        };

        var repo = new Mock<IConfigurationRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(configs);

        var vm = new WorkloadsViewModel(
            repo.Object,
            new Mock<IConfigurationCheckerService>().Object,
            new Mock<IWorkloadInstallService>().Object,
            @"C:\Engine\ComfyUI",
            allowedConfigurationIds: EngineWorkloadCatalog.WorkloadIds);

        await vm.LoadWorkloadsCommand.ExecuteAsync(null);

        vm.DiffusionNexusWorkloads.Concat(vm.InstallerWorkloads)
            .Select(w => w.Name)
            .Should().BeEquivalentTo(["Krea-2-Turbo"]);
    }

    [Fact]
    public async Task WithoutAllowList_EveryComfyUiWorkloadIsListed()
    {
        var configs = new List<InstallationConfiguration>
        {
            Config(EngineWorkloadCatalog.Krea2Turbo, "Krea-2-Turbo"),
            Config(Guid.NewGuid(), "Some other workload")
        };

        var repo = new Mock<IConfigurationRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(configs);

        var vm = new WorkloadsViewModel(
            repo.Object,
            new Mock<IConfigurationCheckerService>().Object,
            new Mock<IWorkloadInstallService>().Object,
            @"C:\ComfyUI");

        await vm.LoadWorkloadsCommand.ExecuteAsync(null);

        vm.DiffusionNexusWorkloads.Concat(vm.InstallerWorkloads).Should().HaveCount(2,
            "the ordinary Workloads dialog must keep showing everything");
    }
}
```

- [ ] **Step 6: Run to verify failure**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EngineWorkloadsViewModelTests" 2>&1 | tail -10
```

Expected: compile error — no `allowedConfigurationIds` parameter.

- [ ] **Step 7: Add the filter**

In `DiffusionNexus.UI/ViewModels/WorkloadsViewModel.cs`, add the field and constructor parameter:

```csharp
    private readonly IReadOnlyList<Guid>? _allowedConfigurationIds;
```

```csharp
        string comfyUIRootPath,
        IReadOnlyList<Guid>? allowedConfigurationIds = null)
```

```csharp
        _allowedConfigurationIds = allowedConfigurationIds;
```

Then in `LoadWorkloadsAsync`, narrow the ComfyUI configuration list:

```csharp
            var comfyConfigurations = configurations
                .Where(c => c.Repository.Type == RepositoryType.ComfyUI)
                .Where(c => _allowedConfigurationIds is null || _allowedConfigurationIds.Contains(c.Id))
                .ToList();
```

- [ ] **Step 8: Run to verify pass**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EngineWorkloadsViewModelTests" 2>&1 | tail -5
```

Expected: 2 passed.

- [ ] **Step 9: Open the filtered dialog from the engine tile**

In `InstallerManagerViewModel.OnWorkloadsRequestedAsync`, before the existing `try` block that builds `WorkloadsViewModel`, add the engine branch (the Core branch stays first):

```csharp
        if (card.IsEngine)
        {
            if (!card.IsEngineInstalled || string.IsNullOrWhiteSpace(card.InstallationPath))
            {
                await _dialogService.ShowMessageAsync("Diffusion Nexus Engine",
                    "Install the engine first — workloads are installed into it.");
                return;
            }

            await ShowWorkloadsDialogAsync(card.InstallationPath,
                Services.Engine.EngineWorkloadCatalog.WorkloadIds);
            return;
        }
```

Refactor the existing dialog construction into the shared helper so both paths use one code path:

```csharp
    /// <summary>
    /// Opens the workloads dialog against a ComfyUI root. <paramref name="allowedConfigurationIds"/>
    /// narrows the list to the curated engine workloads; null shows every ComfyUI workload.
    /// </summary>
    private async Task ShowWorkloadsDialogAsync(string comfyUiRoot, IReadOnlyList<Guid>? allowedConfigurationIds)
    {
        try
        {
            var vm = new WorkloadsViewModel(
                _configurationRepository, _checkerService, _installService,
                comfyUiRoot, allowedConfigurationIds);
            await vm.LoadWorkloadsCommand.ExecuteAsync(null);

            var dialog = new Views.Dialogs.WorkloadsDialog { DataContext = vm };

            var parentWindow = (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;

            if (parentWindow is not null)
                await dialog.ShowDialog(parentWindow);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to open workloads dialog for {Path}", comfyUiRoot);
            await _dialogService.ShowMessageAsync("Error", $"Failed to load workloads: {ex.Message}");
        }
    }
```

and make the existing non-engine path call `await ShowWorkloadsDialogAsync(card.InstallationPath, null);`.

- [ ] **Step 10: Default the VRAM tier from detected VRAM**

The workload install dialog (`WorkloadDetailsDialog`) already takes `ConfiguredVramProfiles` and asks the user to pick. Wire the suggestion as the preselected value:

```bash
grep -n "ConfiguredVramProfiles\|SelectedVram" DiffusionNexus.UI/Views/Dialogs/WorkloadDetailsDialog.axaml.cs | head -20
```

Add a `SuggestedVramGb` input to that dialog (mirroring how `ConfiguredVramProfiles` is passed) and set it from `WorkloadsViewModel.ShowDetailsAsync`:

```csharp
            ConfiguredVramProfiles = item.ConfiguredVramProfiles,
            SuggestedVramGb = Services.Engine.EngineWorkloadCatalog.SuggestVramTier(
                (await _resourceMonitor.GetSnapshotAsync()).VramTotalMB,
                item.ConfiguredVramProfiles),
```

`WorkloadsViewModel` needs an optional `IResourceMonitorService? resourceMonitor = null` constructor parameter for this; when null, skip the suggestion and leave the dialog's current default untouched. Pass `sp.GetRequiredService<IResourceMonitorService>()` from `InstallerManagerViewModel` (which needs the same optional constructor dependency, defaulted to null so existing tests keep compiling).

- [ ] **Step 11: Build and run the full suite**

```bash
dotnet build DiffusionNexus.sln -c Debug 2>&1 | tail -5
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj 2>&1 | tail -5
```

- [ ] **Step 12: Commit**

```bash
git add -A
git commit -m "feat: offer curated engine workloads in the engine tile"
```

---

### Task 8: Engine process host

**Files:**
- Create: `DiffusionNexus.UI/Services/Engine/ManagedComfyUiEngine.cs`
- Test: `DiffusionNexus.Tests/Engine/ManagedComfyUiEngineTests.cs` (create)

**Interfaces:**
- Consumes: `ManagedEngineLocator`, `IUnifiedLogger`.
- Produces:
  - `sealed class ManagedComfyUiEngine : IAsyncDisposable`
  - `Task<EngineStartResult> EnsureRunningAsync(string installRoot, CancellationToken ct)`
  - `record EngineStartResult(bool IsRunning, string? BaseUrl, string? FailureReason)`
  - `static int ManagedComfyUiEngine.AllocateFreePort()`
  - `static string ManagedComfyUiEngine.BuildArguments(string mainPyPath, int port)`
  - `static string? ManagedComfyUiEngine.ResolveVenvPython(string installRoot)`

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests/Engine/ManagedComfyUiEngineTests.cs`:

```csharp
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public class ManagedComfyUiEngineTests
{
    [Fact]
    public void AllocateFreePort_NeverReturns8188()
    {
        for (var i = 0; i < 20; i++)
        {
            var port = ManagedComfyUiEngine.AllocateFreePort();
            port.Should().NotBe(8188, "a user's own ComfyUI owns the default port");
            port.Should().BeInRange(1024, 65535);
        }
    }

    [Fact]
    public void BuildArguments_BindsLoopbackOnlyAndDisablesTheBrowser()
    {
        var args = ManagedComfyUiEngine.BuildArguments(@"C:\Engine\ComfyUI\main.py", 51234);

        args.Should().Contain("--listen 127.0.0.1");
        args.Should().Contain("--port 51234");
        args.Should().Contain("--disable-auto-launch");
        args.Should().Contain("\"C:\\Engine\\ComfyUI\\main.py\"",
            "the script path must be quoted so folders with spaces work");
    }

    [Fact]
    public void ResolveVenvPython_FindsTheEngineVenvInterpreter()
    {
        var root = Path.Combine(Path.GetTempPath(), "dn-engine-" + Guid.NewGuid());
        var scripts = Path.Combine(root, "venv", "Scripts");
        Directory.CreateDirectory(scripts);
        try
        {
            ManagedComfyUiEngine.ResolveVenvPython(root).Should().BeNull("no interpreter exists yet");

            File.WriteAllText(Path.Combine(scripts, "python.exe"), "");
            ManagedComfyUiEngine.ResolveVenvPython(root).Should().Be(Path.Combine(scripts, "python.exe"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureRunning_FailsClearlyWhenTheEngineIsNotInstalled()
    {
        await using var engine = new ManagedComfyUiEngine(unifiedLogger: null);

        var result = await engine.EnsureRunningAsync(
            Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid()),
            CancellationToken.None);

        result.IsRunning.Should().BeFalse();
        result.BaseUrl.Should().BeNull();
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ManagedComfyUiEngineTests" 2>&1 | tail -10
```

Expected: compile error — `ManagedComfyUiEngine` not found.

- [ ] **Step 3: Implement the process host**

Create `DiffusionNexus.UI/Services/Engine/ManagedComfyUiEngine.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using Serilog;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>Outcome of trying to bring the engine up.</summary>
public sealed record EngineStartResult(bool IsRunning, string? BaseUrl, string? FailureReason);

/// <summary>
/// Hosts the app-owned ComfyUI process. Bound to loopback on a dynamically allocated port so a
/// user's own ComfyUI on 8188 is never disturbed, started on demand, and killed when the app
/// exits. Health is confirmed against /system_stats before the engine is declared ready.
/// </summary>
public sealed class ManagedComfyUiEngine : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<ManagedComfyUiEngine>();

    private readonly IUnifiedLogger? _unifiedLogger;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private Process? _process;
    private string? _baseUrl;

    public ManagedComfyUiEngine(IUnifiedLogger? unifiedLogger)
    {
        _unifiedLogger = unifiedLogger;
    }

    /// <summary>Base URL of the running engine, or null when it is not running.</summary>
    public string? BaseUrl => _baseUrl;

    /// <summary>
    /// Starts the engine if it is not already running and waits until it answers /system_stats.
    /// Never throws for ordinary failures — the reason is returned so the Canvas can show it.
    /// </summary>
    public async Task<EngineStartResult> EnsureRunningAsync(string installRoot, CancellationToken ct)
    {
        if (_process is { HasExited: false } && _baseUrl is not null)
            return new EngineStartResult(true, _baseUrl, null);

        await _startLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } && _baseUrl is not null)
                return new EngineStartResult(true, _baseUrl, null);

            var mainPy = ResolveMainPy(installRoot);
            if (mainPy is null)
                return new EngineStartResult(false, null,
                    $"No ComfyUI entry point (main.py) was found under '{installRoot}'. Install the engine first.");

            var python = ResolveVenvPython(Path.GetDirectoryName(mainPy)!) ?? ResolveVenvPython(installRoot);
            if (python is null)
                return new EngineStartResult(false, null,
                    $"The engine's Python environment was not found under '{installRoot}'. The install may be incomplete.");

            var port = AllocateFreePort();
            var startInfo = new ProcessStartInfo(python, BuildArguments(mainPy, port))
            {
                WorkingDirectory = Path.GetDirectoryName(mainPy)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            Log($"Starting engine on 127.0.0.1:{port}...");
            _process = Process.Start(startInfo);
            if (_process is null)
                return new EngineStartResult(false, null, "The engine process could not be started.");

            _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
            _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            var baseUrl = $"http://127.0.0.1:{port}";
            var ready = await WaitForReadyAsync(baseUrl, _process, ct).ConfigureAwait(false);
            if (!ready)
            {
                await StopAsync().ConfigureAwait(false);
                return new EngineStartResult(false, null,
                    "The engine started but never became ready (no answer from /system_stats). " +
                    "See the Unified Console for its output.");
            }

            _baseUrl = baseUrl;
            Log($"Engine ready at {baseUrl}.");
            return new EngineStartResult(true, baseUrl, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error(ex, "Failed to start the managed ComfyUI engine.");
            return new EngineStartResult(false, null, ex.Message);
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>Polls /system_stats until the engine answers, the process dies, or ~120 s elapse.</summary>
    private async Task<bool> WaitForReadyAsync(string baseUrl, Process process, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                Log($"Engine process exited during startup with code {process.ExitCode}.");
                return false;
            }

            try
            {
                using var response = await _httpClient.GetAsync($"{baseUrl}/system_stats", ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException)
            {
                // Not up yet — expected while the server binds.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-request timeout, not caller cancellation.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Stops the engine if it is running. Safe to call repeatedly.</summary>
    public async Task StopAsync()
    {
        var process = _process;
        _process = null;
        _baseUrl = null;

        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to stop the managed ComfyUI engine cleanly.");
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Allocates a free TCP port on loopback. Never 8188: that belongs to the user's own ComfyUI,
    /// and colliding with it is the one failure mode this engine must never cause.
    /// </summary>
    public static int AllocateFreePort()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            if (port != 8188) return port;
        }

        throw new InvalidOperationException("Could not allocate a free TCP port for the engine.");
    }

    /// <summary>Command line for the engine: loopback-only, private port, no browser.</summary>
    public static string BuildArguments(string mainPyPath, int port) =>
        $"\"{mainPyPath}\" --listen 127.0.0.1 --port {port} --disable-auto-launch";

    /// <summary>The engine venv's interpreter, or null when it does not exist.</summary>
    public static string? ResolveVenvPython(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot)) return null;

        // TODO: Linux Implementation - venv/bin/python
        var windows = Path.Combine(installRoot, "venv", "Scripts", "python.exe");
        return File.Exists(windows) ? windows : null;
    }

    private static string? ResolveMainPy(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot)) return null;

        var direct = Path.Combine(installRoot, "main.py");
        if (File.Exists(direct)) return direct;

        var nested = Path.Combine(installRoot, "ComfyUI", "main.py");
        return File.Exists(nested) ? nested : null;
    }

    private void Log(string message)
    {
        Logger.Information("Engine: {Message}", message);
        _unifiedLogger?.Info(LogCategory.Installation, "Diffusion Nexus Engine", message);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _httpClient.Dispose();
        _startLock.Dispose();
    }
}
```

- [ ] **Step 4: Run to verify pass**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ManagedComfyUiEngineTests" 2>&1 | tail -5
```

Expected: 4 passed. If `LogCategory.Installation` is wrong for engine runtime messages, check the enum (`grep -n "enum LogCategory" -A 15 DiffusionNexus.Domain/Services/UnifiedLogging/*.cs`) and pick the closest existing category — do not add a new one in this task.

- [ ] **Step 5: Register it and stop it on shutdown**

In `App.axaml.cs`, next to the other engine registrations:

```csharp
        services.AddSingleton<Services.Engine.ManagedComfyUiEngine>(sp =>
            new Services.Engine.ManagedComfyUiEngine(
                sp.GetService<Domain.Services.UnifiedLogging.IUnifiedLogger>()));
```

Find the app shutdown handler (search for `ShutdownRequested` or `OnExit` in `App.axaml.cs`) and stop the engine there:

```csharp
            var engine = Services?.GetService<Services.Engine.ManagedComfyUiEngine>();
            if (engine is not null)
                engine.StopAsync().GetAwaiter().GetResult();
```

If no shutdown hook exists, add one on `IClassicDesktopStyleApplicationLifetime.ShutdownRequested` at the same place the lifetime is configured.

- [ ] **Step 6: Build and run the full suite**

```bash
dotnet build DiffusionNexus.sln -c Debug 2>&1 | tail -5
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj 2>&1 | tail -5
```

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: host the app-owned ComfyUI engine on a private loopback port"
```

---

### Task 9: Canvas backend dropdown + ManagedComfyUiBackend

**Files:**
- Create: `DiffusionNexus.UI/Services/Diffusion/ManagedComfyUiBackend.cs`
- Modify: `DiffusionNexus.UI/ViewModels/DiffusionCanvas/DiffusionCanvasViewModel.cs`
- Modify: `DiffusionNexus.UI/Views/DiffusionCanvas/DiffusionCanvasView.axaml`
- Test: `DiffusionNexus.Tests/Engine/ManagedComfyUiBackendTests.cs` (create)

**Interfaces:**
- Consumes: `IDiffusionBackend` seam, `ManagedComfyUiEngine` (Task 8), `ManagedEngineLocator` (Task 4).
- Produces:
  - `sealed class ManagedComfyUiBackend : IDiffusionBackend` with constructor
    `(ManagedComfyUiEngine engine, Func<Task<string?>> resolveInstallRootAsync, IWorkflowTemplateSource? templateSource)`.
  - `interface IWorkflowTemplateSource { bool HasTemplate { get; } string? LoadTemplateJson(); }` (implemented in Task 10).
  - `record CanvasBackendOption(string Key, string DisplayName)` and `DiffusionCanvasViewModel.SelectedBackend`.

- [ ] **Step 1: Verify how the Canvas renders a failed generation**

```bash
sed -n '430,470p' DiffusionNexus.UI/ViewModels/DiffusionCanvas/DiffusionCanvasViewModel.cs
```

Confirm that a `Completed` item whose `Result` is null sets the frame to `Failed` and shows `item.Progress.Message`. If it does not, add that `else` branch now — the backend below depends on it:

```csharp
                else
                {
                    frame.State = GenerationFrameState.Failed;
                    frame.StatusText = item.Progress.Message ?? "Generation failed.";
                    StatusText = frame.StatusText;
                }
```

- [ ] **Step 2: Write the failing tests**

Create `DiffusionNexus.Tests/Engine/ManagedComfyUiBackendTests.cs`:

```csharp
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.UI.Services.Diffusion;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public class ManagedComfyUiBackendTests
{
    private static ManagedComfyUiBackend Create(string? installRoot, bool hasTemplate)
        => new(new ManagedComfyUiEngine(unifiedLogger: null),
               () => Task.FromResult(installRoot),
               new StubTemplateSource(hasTemplate));

    private sealed class StubTemplateSource(bool hasTemplate) : IWorkflowTemplateSource
    {
        public bool HasTemplate => hasTemplate;
        public string? LoadTemplateJson() => hasTemplate ? "{}" : null;
    }

    [Fact]
    public void DisplayName_IdentifiesTheEngine()
    {
        Create(null, hasTemplate: false).DisplayName.Should().Be("Diffusion Nexus Engine");
    }

    [Fact]
    public async Task IsAvailable_IsFalseAndSaysSoWhenTheEngineIsNotInstalled()
    {
        var backend = Create(null, hasTemplate: true);

        var available = await backend.IsAvailableAsync();

        available.Should().BeFalse();
        backend.MissingRequirements.Should().ContainSingle()
            .Which.Should().Contain("not installed");
    }

    [Fact]
    public async Task IsAvailable_IsFalseAndSaysSoWhenNoWorkflowIsConfigured()
    {
        var root = Path.Combine(Path.GetTempPath(), "dn-engine-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "main.py"), "# comfy");
        try
        {
            var backend = Create(root, hasTemplate: false);

            var available = await backend.IsAvailableAsync();

            available.Should().BeFalse();
            backend.MissingRequirements.Should().Contain(r => r.Contains("workflow"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Generate_WithoutWorkflow_YieldsACompletedItemCarryingTheReason()
    {
        var backend = Create(null, hasTemplate: false);
        var request = new DiffusionRequest
        {
            ModelKey = "krea2", Prompt = "a cat", Width = 1024, Height = 1024
        };

        var items = new List<DiffusionStreamItem>();
        await foreach (var item in backend.GenerateAsync(request))
            items.Add(item);

        items.Should().NotBeEmpty();
        var last = items[^1];
        last.Progress.Phase.Should().Be(DiffusionPhase.Completed);
        last.Result.Should().BeNull("failures are data, not exceptions, on this seam");
        last.Progress.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Generate_PropagatesCallerCancellation()
    {
        var backend = Create(null, hasTemplate: false);
        var request = new DiffusionRequest
        {
            ModelKey = "krea2", Prompt = "a cat", Width = 1024, Height = 1024
        };

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () =>
        {
            await foreach (var _ in backend.GenerateAsync(request, cts.Token)) { }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
```

- [ ] **Step 3: Run to verify failure**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ManagedComfyUiBackendTests" 2>&1 | tail -10
```

Expected: compile error — `ManagedComfyUiBackend` not found.

- [ ] **Step 4: Implement the backend**

Create `DiffusionNexus.UI/Services/Diffusion/ManagedComfyUiBackend.cs`:

```csharp
using System.Runtime.CompilerServices;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.Inference.Models;
using DiffusionNexus.Inference.StableDiffusionCpp;
using DiffusionNexus.UI.Services.Engine;
using Serilog;

namespace DiffusionNexus.UI.Services.Diffusion;

/// <summary>Supplies the API-format workflow the engine submits for a text2image request.</summary>
public interface IWorkflowTemplateSource
{
    /// <summary>True when a template is available.</summary>
    bool HasTemplate { get; }

    /// <summary>The template JSON, or null when none is configured.</summary>
    string? LoadTemplateJson();
}

/// <summary>
/// The Diffusion Canvas's second backend: the app-owned ComfyUI engine. Implements the same
/// <see cref="IDiffusionBackend"/> seam as the local sd.cpp backend, so the Canvas never learns
/// which engine it is talking to.
///
/// Generation is submitted as an API-format workflow. Until a template is supplied, the backend
/// says so honestly through the seam's error-as-data contract rather than pretending to work.
/// </summary>
public sealed class ManagedComfyUiBackend : IDiffusionBackend
{
    private static readonly ILogger Logger = Log.ForContext<ManagedComfyUiBackend>();

    private readonly ManagedComfyUiEngine _engine;
    private readonly Func<Task<string?>> _resolveInstallRootAsync;
    private readonly IWorkflowTemplateSource? _templateSource;
    private readonly List<string> _missingRequirements = [];

    public ManagedComfyUiBackend(
        ManagedComfyUiEngine engine,
        Func<Task<string?>> resolveInstallRootAsync,
        IWorkflowTemplateSource? templateSource)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(resolveInstallRootAsync);

        _engine = engine;
        _resolveInstallRootAsync = resolveInstallRootAsync;
        _templateSource = templateSource;
    }

    public string DisplayName => "Diffusion Nexus Engine";

    /// <summary>
    /// Models discovered under the engine's install root. Empty until the engine is installed —
    /// the catalog walks the ComfyUI folder layout, which does not exist before then.
    /// </summary>
    public IModelCatalog Catalog { get; private set; } = new ComfyUiModelCatalog([]);

    public IReadOnlyList<string> MissingRequirements => _missingRequirements;

    public IReadOnlyList<string> Warnings => [];

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        _missingRequirements.Clear();

        var installRoot = await _resolveInstallRootAsync().ConfigureAwait(false);

        if (!ManagedEngineLocator.LooksInstalled(installRoot))
        {
            _missingRequirements.Add(
                "The Diffusion Nexus Engine is not installed. Install it from the Installation Manager.");
            return false;
        }

        Catalog = new ComfyUiModelCatalog(ComfyUiPathDiscovery.EnumerateModelSearchPaths(installRoot!).ToList());

        if (_templateSource is null || !_templateSource.HasTemplate)
        {
            _missingRequirements.Add(
                "No text2image workflow is configured for the engine yet, so it cannot generate.");
            return false;
        }

        var start = await _engine.EnsureRunningAsync(installRoot!, ct).ConfigureAwait(false);
        if (!start.IsRunning)
        {
            _missingRequirements.Add(start.FailureReason ?? "The engine could not be started.");
            return false;
        }

        return true;
    }

    public async IAsyncEnumerable<DiffusionStreamItem> GenerateAsync(
        DiffusionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var available = await IsAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (!available)
        {
            var reason = _missingRequirements.Count > 0
                ? string.Join(" ", _missingRequirements)
                : "The Diffusion Nexus Engine is not ready.";

            Logger.Warning("Engine generation refused: {Reason}", reason);

            yield return new DiffusionStreamItem(new DiffusionProgress
            {
                Phase = DiffusionPhase.Completed,
                Message = reason
            });
            yield break;
        }

        // Submission is added with the workflow template (see the workflow task). Until then this
        // point is unreachable: IsAvailableAsync returns false without a template.
        yield return new DiffusionStreamItem(new DiffusionProgress
        {
            Phase = DiffusionPhase.Completed,
            Message = "The engine is ready but workflow submission is not wired up yet."
        });
    }
}
```

Verify the two reused types resolve — `ComfyUiModelCatalog` and `ComfyUiPathDiscovery`:

```bash
grep -rn "class ComfyUiModelCatalog\|class ComfyUiPathDiscovery" --include=*.cs DiffusionNexus.Inference DiffusionNexus.UI
```

Adjust the `using` statements to the real namespaces.

- [ ] **Step 5: Run to verify pass**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ManagedComfyUiBackendTests" 2>&1 | tail -5
```

Expected: 5 passed.

- [ ] **Step 6: Write the failing test for backend selection**

Create `DiffusionNexus.Tests/Engine/CanvasBackendSelectionTests.cs`:

```csharp
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public class CanvasBackendSelectionTests
{
    [Fact]
    public void Canvas_OffersBothBackendsAndDefaultsToTheLocalOne()
    {
        var vm = new DiffusionCanvasViewModel();

        vm.AvailableBackends.Select(b => b.Key)
            .Should().BeEquivalentTo([CanvasBackendKeys.Local, CanvasBackendKeys.Engine]);
        vm.SelectedBackend!.Key.Should().Be(CanvasBackendKeys.Local,
            "the engine is opt-in until it can generate");
    }

    [Fact]
    public void Canvas_KeepsTheSelectedBackend()
    {
        var vm = new DiffusionCanvasViewModel();

        vm.SelectedBackend = vm.AvailableBackends.Single(b => b.Key == CanvasBackendKeys.Engine);

        vm.SelectedBackend.Key.Should().Be(CanvasBackendKeys.Engine);
    }
}
```

`DiffusionCanvasViewModel` has a parameterless design-mode constructor (it sets `_backendProvider = null` at line 133) — that is what these tests use.

- [ ] **Step 7: Run to verify failure**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CanvasBackendSelectionTests" 2>&1 | tail -10
```

Expected: compile error — `AvailableBackends` / `CanvasBackendKeys` not found.

- [ ] **Step 8: Add the dropdown to the Canvas ViewModel**

In `DiffusionCanvasViewModel.cs`, next to the existing `CanvasModelOption` record:

```csharp
/// <summary>Stable keys for the canvas backend dropdown.</summary>
public static class CanvasBackendKeys
{
    /// <summary>In-process stable-diffusion.cpp backend (the original canvas engine).</summary>
    public const string Local = "local";

    /// <summary>The app-owned ComfyUI engine.</summary>
    public const string Engine = "engine";
}

/// <summary>A selectable generation backend in the canvas toolbar.</summary>
public sealed record CanvasBackendOption(string Key, string DisplayName);
```

Inside the class, next to `AvailableModels`:

```csharp
    /// <summary>Backends the canvas can generate with.</summary>
    public ObservableCollection<CanvasBackendOption> AvailableBackends { get; } =
    [
        new(CanvasBackendKeys.Local, "Diffusion Nexus Core (local)"),
        new(CanvasBackendKeys.Engine, "Diffusion Nexus Engine (ComfyUI)")
    ];

    /// <summary>
    /// The selected backend. In-memory only for now — the canvas is still behind its switch, so
    /// there is nothing worth persisting yet.
    /// </summary>
    [ObservableProperty]
    private CanvasBackendOption? _selectedBackend;
```

Initialize it in both constructors (design-mode at line ~133 and the DI one at ~146):

```csharp
        _selectedBackend = AvailableBackends[0];
```

Then make `GenerateAsync` route through the selection. Replace the backend resolution block (currently `var backend = await _backendProvider.TryGetAsync()...`) with:

```csharp
            IDiffusionBackend? backend;
            if (SelectedBackend?.Key == CanvasBackendKeys.Engine)
            {
                backend = _engineBackend;
                if (backend is null)
                {
                    BackendUnavailableMessage = "The Diffusion Nexus Engine is not available in this session.";
                    StatusText = "Backend unavailable";
                    return;
                }

                if (!await backend.IsAvailableAsync().ConfigureAwait(true))
                {
                    BackendUnavailableMessage = string.Join(" ", backend.MissingRequirements);
                    StatusText = "Backend unavailable";
                    return;
                }
            }
            else
            {
                backend = await _backendProvider.TryGetAsync().ConfigureAwait(true);
                if (backend is null)
                {
                    BackendUnavailableMessage =
                        "Cannot locate the models folder. The local backend generates entirely on your GPU (no ComfyUI process), " +
                        "but it expects a ComfyUI-layout models folder (DiffusionModels/, TextEncoders/, VAE/). " +
                        "Check the Unified Logger for details, or ensure at least one installation is registered as 'ComfyUI' type in the Installer Manager.";
                    StatusText = "Backend unavailable";
                    return;
                }
            }
```

Add the field and an optional constructor dependency:

```csharp
    private readonly IDiffusionBackend? _engineBackend;
```

```csharp
    public DiffusionCanvasViewModel(
        LocalDiffusionBackendProvider backendProvider,
        ResourceMonitorViewModel? resourceMonitor = null,
        IDiffusionBackend? engineBackend = null)
```

```csharp
        _engineBackend = engineBackend;
```

- [ ] **Step 9: Run to verify pass**

```bash
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CanvasBackendSelectionTests" 2>&1 | tail -5
```

Expected: 2 passed.

- [ ] **Step 10: Add the ComboBox to the Canvas view**

In `DiffusionNexus.UI/Views/DiffusionCanvas/DiffusionCanvasView.axaml`, find the toolbar `ComboBox` bound to `AvailableModels` and add a sibling immediately before it:

```xml
                <ComboBox ItemsSource="{Binding AvailableBackends}"
                          SelectedItem="{Binding SelectedBackend}"
                          MinWidth="220"
                          ToolTip.Tip="Which engine generates the image">
                    <ComboBox.ItemTemplate>
                        <DataTemplate>
                            <TextBlock Text="{Binding DisplayName}" />
                        </DataTemplate>
                    </ComboBox.ItemTemplate>
                </ComboBox>
```

Copy the `Classes`/margin attributes from the neighbouring model ComboBox so it matches the toolbar styling.

- [ ] **Step 11: Register the engine backend in DI**

In `App.axaml.cs`, register the backend and pass it into the Canvas ViewModel registration (line ~655):

```csharp
        services.AddSingleton<Services.Diffusion.ManagedComfyUiBackend>(sp =>
            new Services.Diffusion.ManagedComfyUiBackend(
                sp.GetRequiredService<Services.Engine.ManagedComfyUiEngine>(),
                async () =>
                {
                    using var scope = sp.CreateScope();
                    var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    var packages = await uow.InstallerPackages.GetAllAsync();
                    return packages.FirstOrDefault(p => p.IsAppManaged)?.InstallationPath;
                },
                sp.GetService<Services.Diffusion.IWorkflowTemplateSource>()));

        services.AddSingleton<DiffusionNexus.UI.ViewModels.DiffusionCanvas.DiffusionCanvasViewModel>(sp =>
            new DiffusionNexus.UI.ViewModels.DiffusionCanvas.DiffusionCanvasViewModel(
                sp.GetRequiredService<Services.Diffusion.LocalDiffusionBackendProvider>(),
                sp.GetService<ViewModels.ResourceMonitorViewModel>(),
                sp.GetRequiredService<Services.Diffusion.ManagedComfyUiBackend>()));
```

Replace the existing plain `services.AddSingleton<DiffusionCanvasViewModel>()` line with the factory version above. Check the real `ResourceMonitorViewModel` namespace first (`grep -rn "class ResourceMonitorViewModel" --include=*.cs DiffusionNexus.UI`).

- [ ] **Step 12: Build and run the full suite**

```bash
dotnet build DiffusionNexus.sln -c Debug 2>&1 | tail -5
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj 2>&1 | tail -5
```

- [ ] **Step 13: Commit**

```bash
git add -A
git commit -m "feat: let the Canvas choose between the local backend and the engine"
```

---

### Task 10: Workflow submission and real generation

The workflow is **already supplied** and verified API-format: `DiffusionNexus.UI/Assets/Pipelines/Krea2-Text2Image-API.json`, 11 nodes. It ships as an **AvaloniaResource** (`<AvaloniaResource Include="Assets\**\*.*" />` in `DiffusionNexus.UI.csproj:27`, with `Assets\Workflows\**` excluded — the Pipelines folder is included), so it is loaded through `AssetLoader` from an `avares://` URI, not from disk.

**The real graph** (verified, do not re-derive):

| Node | class_type | What the patcher does with it |
|---|---|---|
| `17` | `CLIPTextEncode` | **positive** prompt (`KSampler.positive` → `["17", 0]`) — write `inputs.text` |
| `35` | `CLIPTextEncode` | negative prompt — write `inputs.text` from `request.NegativePrompt` (empty string when null) |
| `36` | `EmptySD3LatentImage` | `width`/`height` are **links** to node `65`, not literals — replace with literal ints |
| `37` | `KSampler` | `seed`, `steps` (8), `cfg` (1), `sampler_name` (euler), `scheduler` (simple) |
| `62` | `LoaderGGUF` | `gguf_name` is hardcoded to `krea2_turbo-Q8_0.gguf` — must be repointed at whichever quant is actually installed |
| `21` | `SaveImage` | `filename_prefix` is hardcoded to a dated folder — repoint at a stable prefix |
| `55` | `Power Lora Loader (rgthree)` | Leave as authored. Carries a **disabled** `Jinx_Arcane_Season_1_LoRA_Z_Image_Turbo.safetensors` entry (`on: false`), which is harmless but means the graph needs rgthree — installed by the Krea 2 workload. Wiring `DiffusionRequest.Loras` here is a later step, not this one. |

Three of these are load-bearing and easy to get wrong:

1. **Width/height come from a custom node.** `36.inputs.width` is `["65", 0]` — a link to `AI2GoResolutionSelector`. Writing a literal int replaces the link, which is what we want (the Canvas dictates the size); node `65` then becomes unreachable and ComfyUI simply never executes it.
2. **The GGUF quant is machine-specific.** The template names `krea2_turbo-Q8_0.gguf` (the 32 GB tier). A user whose workload install picked `Q5_K_S` would get a "file not found" from ComfyUI. Resolve the installed file from the engine at submit time.
3. `SaveImage` writes into the engine's own output folder. That is fine — the backend fetches the image over `/view` regardless — but the prefix should not pretend to be a dated batch.

**Files:**
- Create: `DiffusionNexus.UI/Services/Diffusion/AvaresWorkflowTemplateSource.cs`
- Create: `DiffusionNexus.UI/Services/Diffusion/Krea2WorkflowPatcher.cs`
- Modify: `DiffusionNexus.UI/Services/Diffusion/ManagedComfyUiBackend.cs` (`GenerateAsync`)
- Test: `DiffusionNexus.Tests/Engine/Krea2WorkflowPatcherTests.cs` (create)

**Interfaces:**
- Consumes: `IWorkflowTemplateSource` (Task 9), `IComfyUIWrapperService` (`QueueWorkflowAsync`, `WaitForCompletionAsync`, `GetResultAsync`, `DownloadImageAsync`, `GetModelsInFolderAsync`).
- Produces: `static string Krea2WorkflowPatcher.Patch(string templateJson, DiffusionRequest request, long seed, string? ggufFileName)`.

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests/Engine/Krea2WorkflowPatcherTests.cs`. The template literal below mirrors the real graph's shape, including the link-valued width/height:

```csharp
using System.Text.Json;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.UI.Services.Diffusion;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public class Krea2WorkflowPatcherTests
{
    private const string Template = """
    {
      "9":  { "class_type": "VAEDecode", "inputs": { "samples": ["37", 0], "vae": ["57", 0] } },
      "17": { "class_type": "CLIPTextEncode", "inputs": { "text": "OLD POSITIVE", "clip": ["55", 1] } },
      "21": { "class_type": "SaveImage", "inputs": { "filename_prefix": "2026-08-16/Krea-Turbo", "images": ["9", 0] } },
      "35": { "class_type": "CLIPTextEncode", "inputs": { "text": "OLD NEGATIVE", "clip": ["55", 1] } },
      "36": { "class_type": "EmptySD3LatentImage", "inputs": { "width": ["65", 0], "height": ["65", 1], "batch_size": 1 } },
      "37": { "class_type": "KSampler", "inputs": { "seed": 637067905137781, "steps": 8, "cfg": 1, "sampler_name": "euler", "scheduler": "simple", "denoise": 1, "model": ["55", 0], "positive": ["17", 0], "negative": ["35", 0], "latent_image": ["36", 0] } },
      "62": { "class_type": "LoaderGGUF", "inputs": { "gguf_name": "krea2_turbo-Q8_0.gguf" } },
      "65": { "class_type": "AI2GoResolutionSelector", "inputs": { "width": 1000, "height": 1000 } }
    }
    """;

    private static DiffusionRequest Request(
        int width = 1216, int height = 832, int? steps = null, string? negative = null)
        => new()
        {
            ModelKey = "krea2",
            Prompt = "a lighthouse at dusk",
            Width = width,
            Height = height,
            Steps = steps,
            NegativePrompt = negative
        };

    private static JsonElement Inputs(string json, string nodeId)
        => JsonDocument.Parse(json).RootElement.GetProperty(nodeId).GetProperty("inputs").Clone();

    [Fact]
    public void Patch_WritesThePositivePromptIntoNode17()
    {
        var patched = Krea2WorkflowPatcher.Patch(Template, Request(), seed: 4242, ggufFileName: null);

        Inputs(patched, "17").GetProperty("text").GetString().Should().Be("a lighthouse at dusk");
    }

    [Fact]
    public void Patch_WritesTheNegativePromptIntoNode35_EmptyWhenUnset()
    {
        Inputs(Krea2WorkflowPatcher.Patch(Template, Request(), 1, null), "35")
            .GetProperty("text").GetString().Should().BeEmpty();

        Inputs(Krea2WorkflowPatcher.Patch(Template, Request(negative: "blurry"), 1, null), "35")
            .GetProperty("text").GetString().Should().Be("blurry");
    }

    [Fact]
    public void Patch_ReplacesTheLinkedResolutionWithLiteralCanvasDimensions()
    {
        var patched = Krea2WorkflowPatcher.Patch(Template, Request(1216, 832), seed: 1, ggufFileName: null);
        var latent = Inputs(patched, "36");

        latent.GetProperty("width").ValueKind.Should().Be(JsonValueKind.Number,
            "the AI2GoResolutionSelector link must be replaced, or the canvas size is ignored");
        latent.GetProperty("width").GetInt32().Should().Be(1216);
        latent.GetProperty("height").GetInt32().Should().Be(832);
    }

    [Fact]
    public void Patch_SetsTheSeedAndKeepsTheWorkflowsTunedSamplerSettings()
    {
        var sampler = Inputs(Krea2WorkflowPatcher.Patch(Template, Request(), seed: 4242, ggufFileName: null), "37");

        sampler.GetProperty("seed").GetInt64().Should().Be(4242);
        sampler.GetProperty("steps").GetInt32().Should().Be(8, "8 steps is the turbo model's tuned default");
        sampler.GetProperty("cfg").GetDouble().Should().Be(1);
        sampler.GetProperty("sampler_name").GetString().Should().Be("euler");
    }

    [Fact]
    public void Patch_OverridesStepsAndCfgOnlyWhenTheRequestSuppliesThem()
    {
        var sampler = Inputs(Krea2WorkflowPatcher.Patch(Template, Request(steps: 20), seed: 1, ggufFileName: null), "37");

        sampler.GetProperty("steps").GetInt32().Should().Be(20);
    }

    [Fact]
    public void Patch_RepointsTheGgufLoaderAtTheInstalledQuant()
    {
        var patched = Krea2WorkflowPatcher.Patch(
            Template, Request(), seed: 1, ggufFileName: "krea2_turbo-Q5_K_S.gguf");

        Inputs(patched, "62").GetProperty("gguf_name").GetString()
            .Should().Be("krea2_turbo-Q5_K_S.gguf",
                "a machine that installed a smaller quant has no Q8_0 file");
    }

    [Fact]
    public void Patch_LeavesTheGgufNameAloneWhenNoInstalledQuantWasResolved()
    {
        var patched = Krea2WorkflowPatcher.Patch(Template, Request(), seed: 1, ggufFileName: null);

        Inputs(patched, "62").GetProperty("gguf_name").GetString().Should().Be("krea2_turbo-Q8_0.gguf");
    }

    [Fact]
    public void Patch_ReplacesTheHardcodedDatedSavePrefix()
    {
        var patched = Krea2WorkflowPatcher.Patch(Template, Request(), seed: 1, ggufFileName: null);

        Inputs(patched, "21").GetProperty("filename_prefix").GetString()
            .Should().Be("DiffusionNexus/Canvas");
    }

    [Fact]
    public void Patch_FailsLoudlyWhenTheTemplateLosesAnExpectedNode()
    {
        var act = () => Krea2WorkflowPatcher.Patch(
            """{"99":{"class_type":"KSampler","inputs":{}}}""", Request(), seed: 1, ggufFileName: null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*17*", "a silently unpatched workflow would generate somebody else's prompt");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~Krea2WorkflowPatcherTests"`
Expected: compile error — `Krea2WorkflowPatcher` not found.

- [ ] **Step 3: Implement the patcher**

Create `DiffusionNexus.UI/Services/Diffusion/Krea2WorkflowPatcher.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using DiffusionNexus.Inference.Abstractions;

namespace DiffusionNexus.UI.Services.Diffusion;

/// <summary>
/// Writes a canvas request into the shipped API-format Krea 2 text2image workflow
/// (<c>Assets/Pipelines/Krea2-Text2Image-API.json</c>).
///
/// Node ids are constants because the template is an app asset we control — the same approach
/// the inpaint/outpaint flows already take. A missing node throws instead of silently producing
/// an image that ignores the user's prompt or size.
/// </summary>
public static class Krea2WorkflowPatcher
{
    /// <summary>Positive prompt (KSampler.positive points here).</summary>
    private const string PositivePromptNodeId = "17";

    /// <summary>Negative prompt.</summary>
    private const string NegativePromptNodeId = "35";

    /// <summary>Empty latent. Its width/height ship as links to the AI2Go resolution selector.</summary>
    private const string LatentNodeId = "36";

    /// <summary>Sampler: seed, steps, cfg.</summary>
    private const string SamplerNodeId = "37";

    /// <summary>GGUF UNet loader (calcuis/gguf). Its quant is machine-specific.</summary>
    private const string GgufLoaderNodeId = "62";

    /// <summary>SaveImage — ships with a hardcoded dated prefix.</summary>
    private const string SaveImageNodeId = "21";

    /// <summary>Output prefix used for canvas generations inside the engine's output folder.</summary>
    private const string CanvasFilenamePrefix = "DiffusionNexus/Canvas";

    /// <param name="ggufFileName">
    /// The Krea 2 GGUF actually present on this machine, or null to keep whatever the template
    /// names. The template ships the Q8_0 quant, which only exists on a 32 GB-tier install.
    /// </param>
    public static string Patch(
        string templateJson, DiffusionRequest request, long seed, string? ggufFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateJson);
        ArgumentNullException.ThrowIfNull(request);

        var graph = JsonNode.Parse(templateJson)?.AsObject()
            ?? throw new InvalidOperationException("The workflow template is not a JSON object.");

        Inputs(graph, PositivePromptNodeId)["text"] = request.Prompt;
        Inputs(graph, NegativePromptNodeId)["text"] = request.NegativePrompt ?? string.Empty;

        // The template drives the latent size from an AI2GoResolutionSelector link. The canvas
        // owns the frame size, so replace the links with literals; node 65 then becomes
        // unreachable and ComfyUI never executes it.
        var latent = Inputs(graph, LatentNodeId);
        latent["width"] = request.Width;
        latent["height"] = request.Height;

        var sampler = Inputs(graph, SamplerNodeId);
        sampler["seed"] = seed;
        if (request.Steps is { } steps) sampler["steps"] = steps;
        if (request.Cfg is { } cfg) sampler["cfg"] = cfg;

        if (!string.IsNullOrWhiteSpace(ggufFileName))
            Inputs(graph, GgufLoaderNodeId)["gguf_name"] = ggufFileName;

        Inputs(graph, SaveImageNodeId)["filename_prefix"] = CanvasFilenamePrefix;

        return graph.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static JsonObject Inputs(JsonObject graph, string nodeId)
    {
        if (graph[nodeId] is not JsonObject node)
            throw new InvalidOperationException(
                $"The Krea 2 workflow template has no node '{nodeId}'. The asset and the patcher are out of sync.");

        if (node["inputs"] is not JsonObject inputs)
            throw new InvalidOperationException($"Workflow node '{nodeId}' has no inputs object.");

        return inputs;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~Krea2WorkflowPatcherTests"`
Expected: 9 passed.

- [ ] **Step 5: Implement the template source**

Create `DiffusionNexus.UI/Services/Diffusion/AvaresWorkflowTemplateSource.cs`:

```csharp
using Avalonia.Platform;

namespace DiffusionNexus.UI.Services.Diffusion;

/// <summary>
/// Loads the API-format workflow template embedded as an Avalonia resource under
/// <c>Assets/Pipelines/</c> — the same mechanism <c>PipelineManifestProvider</c> uses for its
/// manifests. Not unit-tested: it is a thin adapter over <see cref="AssetLoader"/>, which needs
/// an initialized Avalonia runtime. Consumers depend on <see cref="IWorkflowTemplateSource"/>
/// and are tested against a stub.
/// </summary>
public sealed class AvaresWorkflowTemplateSource : IWorkflowTemplateSource
{
    private readonly Uri _uri;

    public AvaresWorkflowTemplateSource(string assetFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetFileName);
        _uri = new Uri($"avares://DiffusionNexus.UI/Assets/Pipelines/{assetFileName}");
    }

    public bool HasTemplate
    {
        get
        {
            try { return AssetLoader.Exists(_uri); }
            catch { return false; }
        }
    }

    public string? LoadTemplateJson()
    {
        if (!HasTemplate) return null;

        using var stream = AssetLoader.Open(_uri);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
```

Register it in `App.axaml.cs` **before** the `ManagedComfyUiBackend` registration, replacing the placeholder registration from Task 9 if one was added:

```csharp
        services.AddSingleton<Services.Diffusion.IWorkflowTemplateSource>(_ =>
            new Services.Diffusion.AvaresWorkflowTemplateSource("Krea2-Text2Image-API.json"));
```

Confirm the resource is really embedded before moving on:

```bash
grep -n "AvaloniaResource" DiffusionNexus.UI/DiffusionNexus.UI.csproj
```

Expected: `<AvaloniaResource Include="Assets\**\*.*" />` with only `Assets\Workflows\**` removed — the Pipelines folder is covered, so no csproj change is needed.

- [ ] **Step 6: Resolve the installed GGUF quant**

Add to `ManagedComfyUiBackend` a helper that asks the running engine which Krea 2 GGUF it actually has. `IComfyUIWrapperService.GetModelsInFolderAsync` already exists for this; check its exact signature first:

```bash
sed -n '509,555p' DiffusionNexus.Service/Services/ComfyUIWrapperService.cs
```

```csharp
    /// <summary>
    /// The Krea 2 GGUF present on this machine. The template names the Q8_0 quant, but the
    /// workload downloads whichever quant matches the card's VRAM tier, so submitting the
    /// template unchanged fails on every machine below the top tier. Returns null when the
    /// engine cannot be asked, in which case the template's own name is kept.
    /// </summary>
    private static async Task<string?> ResolveInstalledKreaGgufAsync(
        ComfyUIWrapperService wrapper, CancellationToken ct)
    {
        try
        {
            var models = await wrapper.GetModelsInFolderAsync("diffusion_models", ct).ConfigureAwait(false);
            return models.FirstOrDefault(m =>
                m.Contains("krea2", StringComparison.OrdinalIgnoreCase) &&
                m.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Could not resolve the installed Krea 2 GGUF; keeping the template's name.");
            return null;
        }
    }
```

If `GetModelsInFolderAsync` takes a different folder key than `"diffusion_models"`, use the one the real ComfyUI `/object_info` reports — check by curling the running engine during the manual smoke rather than guessing.

- [ ] **Step 7: Submit the workflow**

In `ManagedComfyUiBackend.GenerateAsync`, replace the "workflow submission is not wired up yet" placeholder with the real submission. Add `using System.Diagnostics;` and `using DiffusionNexus.Service.Services;`:

```csharp
        var seed = request.Seed ?? Random.Shared.NextInt64(0, int.MaxValue);
        var startedAt = Stopwatch.GetTimestamp();

        yield return new DiffusionStreamItem(new DiffusionProgress
        {
            Phase = DiffusionPhase.Loading,
            Message = "Submitting to the Diffusion Nexus Engine…"
        });

        using var wrapper = new ComfyUIWrapperService(_engine.BaseUrl!);

        DiffusionResult? result = null;
        string? failure = null;
        try
        {
            var gguf = await ResolveInstalledKreaGgufAsync(wrapper, cancellationToken).ConfigureAwait(false);
            var workflowJson = Krea2WorkflowPatcher.Patch(
                _templateSource!.LoadTemplateJson()!, request, seed, gguf);

            var promptId = await wrapper.QueueWorkflowAsync(workflowJson, cancellationToken).ConfigureAwait(false);
            await wrapper.WaitForCompletionAsync(promptId, ct: cancellationToken).ConfigureAwait(false);

            var comfyResult = await wrapper.GetResultAsync(promptId, cancellationToken).ConfigureAwait(false);
            var image = comfyResult.Images.FirstOrDefault();
            if (image is null)
            {
                failure = "The engine finished but returned no image.";
            }
            else
            {
                var bytes = await wrapper.DownloadImageAsync(image, cancellationToken).ConfigureAwait(false);
                result = new DiffusionResult(bytes, request.Width, request.Height, seed,
                    Stopwatch.GetElapsedTime(startedAt));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Engine generation failed.");
            failure = ex.Message;
        }

        yield return new DiffusionStreamItem(
            new DiffusionProgress { Phase = DiffusionPhase.Completed, Message = failure },
            result);
```

`yield return` cannot sit inside a `try` that has a `catch`, which is why the result is captured first and yielded afterwards — keep that shape.

Check the real signatures before wiring, and adapt the calls (not the behaviour) if they differ:

```bash
sed -n '139,200p' DiffusionNexus.Service/Services/ComfyUIWrapperService.cs
sed -n '358,412p' DiffusionNexus.Service/Services/ComfyUIWrapperService.cs
```

`WaitForCompletionAsync` accepts a progress callback in some form. If it exposes per-step progress, map it onto `DiffusionPhase.Sampling` items with `Step`/`TotalSteps`; if it does not, leave the stream at Loading → Completed and say so in the commit message rather than fabricating step counts.

- [ ] **Step 8: Verify the Canvas can reach the new path**

`ManagedComfyUiBackend.IsAvailableAsync` (Task 9) refuses to run without a template. With the asset registered it now proceeds to `EnsureRunningAsync`, so re-run the Task 9 tests to confirm nothing regressed:

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ManagedComfyUiBackendTests"`
Expected: 5 passed (the stub template source still drives those tests).

- [ ] **Step 9: Build and run the full suite**

```bash
dotnet build DiffusionNexus.sln -c Debug 2>&1 | tail -5
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj 2>&1 | tail -5
```

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat: generate Krea 2 images through the Diffusion Nexus Engine"
```

## Manual verification (human gates)

These cannot be proven by the test suite. Run them before the branch is considered done, and report what actually happened — not what should happen.

- [ ] **Base engine install:** enable the Canvas switch in the hamburger menu, confirm the engine tile appears, press Install, choose a folder on a drive with 20+ GB free. Expect the Unified Console to stream steps and the tile to end with the Workloads button. Verify `venv/Scripts/python.exe` and `main.py` exist, and that `python -c "import torch; print(torch.__version__, torch.version.cuda)"` in that venv reports whatever the base configuration declared — read it back rather than assuming, and compare it against the "Engine torch settings" line the installer now logs at install start.
- [ ] **Engine hidden when switched off:** turn the hamburger switch off, reopen the Installation Manager, confirm the tile is gone and no duplicate ComfyUI card appeared.
- [ ] **Workload install:** open Workloads on the engine tile, confirm only Krea 2 Turbo is listed, confirm the preselected VRAM tier matches the machine's card, install it. Verify models the shared library already had were **not** re-downloaded (check the download log and the folder sizes).
- [ ] **Engine start:** select the engine in the Canvas backend dropdown and generate. Confirm from the Unified Console that ComfyUI started on a port that is **not** 8188, and that a user-owned ComfyUI (if running) was unaffected.
- [ ] **First Krea 2 image** (after Task 10): generate on the Canvas and confirm the image appears in a frame and is written to the outputs folder.
- [ ] **Shutdown:** close the app and confirm no orphaned `python.exe` running ComfyUI remains in Task Manager.

## Open items carried from the spec

- Wiring `DiffusionRequest.Loras` into the workflow's `Power Lora Loader (rgthree)` node (`55`), and deciding whether the disabled `Jinx_Arcane_Season_1` entry it ships should be stripped.
- Torch policy if a future engine workload disagrees with the pairing the base configuration declares.
- Whether backend selection should later persist to `AppSettings` and replace `DiffusionFeatureFlags.UseLocalDiffusionBackend`.
