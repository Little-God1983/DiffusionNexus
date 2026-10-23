# Image Editor Inpaint + Outpaint on the Diffusion Nexus Engine — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Inpaint and Outpaint in the Image Editor run on the app-owned embedded ComfyUI (the Diffusion Nexus Engine), chosen by one app-wide setting, with the Engine's models installed from a per-feature checklist on the Engine tile.

**Architecture:** A persisted `ComfyUiServerMode` (Engine | CustomUrl) is read by two new seams: `IComfyUiClientProvider`, which hands Inpaint/Outpaint a ComfyUI client for the active mode (starting the Engine on demand), and a mode-aware `FeatureBackendRouter` that sends their readiness check to a new Engine-scoped `EngineFeatureBackend`. The Engine tile's Workloads button opens a new Features dialog whose rows map app features to catalog workloads and install only what is missing through the existing `IWorkloadInstallService`. The workflow JSON and node mutation are untouched.

**Tech Stack:** .NET 10, Avalonia 11 (compiled bindings), CommunityToolkit.Mvvm, EF Core + SQLite, xUnit 2.9 + FluentAssertions 7.2 + Moq 4.20, Serilog + the app's `IUnifiedLogger`.

**Spec:** `docs/superpowers/specs/2026-10-07-editor-inpaint-outpaint-on-engine-design.md` (issue #606, milestone #2).

## Global Constraints

- Branch: `feature/editor-inpaint-outpaint-on-engine` (already exists, spec committed). One PR for the whole issue. Commit **and push** after every task.
- gh account: `gh auth switch --user Little-God1983` before any push or PR.
- Repo root: `E:\Repos\DiffusionNexus`. Always pass explicit paths to search tools (the session's default directory is a different repo).
- Build: `dotnet build E:\Repos\DiffusionNexus\DiffusionNexus.sln -c Debug`. Tests: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~<Name>"`.
- Known pre-existing test failures, not caused by this work: `CivitaiWaitlistTests.PersistRestore_RoundTripsEntries`, `DownloadPreflightChoiceTests.OpenWebsite_NsfwModel_OpensCivitaiRedHost`; `GenerationGalleryViewModelTests.TagCloudSearchText_*` is order-dependent flaky.
- EF migrations are generated with `dotnet ef`, never hand-written; the generated `Up` may be extended with SQL. CI runs `dotnet ef migrations has-pending-model-changes`.
- Enums in the database are stored as strings: `HasConversion<string>().HasMaxLength(n)`.
- `ComfyUiServerMode` default for a **fresh** database is `Engine`; an **existing** settings row is migrated to `CustomUrl`; recovery column default `'CustomUrl'`; settings import without the field → `CustomUrl`.
- Display strings, verbatim: `"Diffusion Nexus Engine"`, `"Custom URL"`, `"Inpaint & Outpaint"`, `"Canvas · Krea 2 Turbo"`, `"Starting Diffusion Nexus Engine…"`, `"Diffusion Nexus Engine is not installed"`, `"Not installed on the Engine"`, `"Running on"`, `"Outpaint Vision is not available on the Diffusion Nexus Engine yet"`, `"Generation failed – is the Diffusion Nexus Engine running?"`.
- Features whose backend the Settings dropdown governs in #606: `Inpainting`, `Outpaint`, `OutpaintVision`. Nothing else changes backend.
- Every new user-visible step logs to the Unified Console (`IUnifiedLogger`) as well as Serilog (standing rule).
- Before adding any UI control, check `DiffusionNexus.UI/REUSABLES.md` (standing rule). `FeatureReadinessPanel` is the reusable being extended here.
- Windows-only app: no cross-platform hooks.
- UI smoke tests run on the BenQ monitor only (hardware id `BNQ7F05`, the non-primary 16:9 screen at `X < 0`), never on the LG.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A user upgrading with their own ComfyUI** keeps working Inpaint/Outpaint after the update, because their existing settings row migrates to `CustomUrl`. Pinned by the migration test in Task 1.
2. **The Engine fails to start or is missing at Generate time** (cold start fails, folder deleted after the readiness check): the panel shows the reason, `HasError` is set and the panel is not left busy. Pinned in Task 9.
3. **The user switches the Settings dropdown while the Inpaint or Outpaint panel is open**: the readiness line re-checks and shows the new backend without reopening the tool. Pinned in Task 9.
4. **Installing a feature whose files are partly present** (shared files, or an earlier install that stopped half-way): only the missing items are passed to the installer, and each workload is re-checked right before its install. Pinned in Task 6.
5. **Outpaint Vision in Engine mode**: the Vision button greys out with an honest message rather than failing at run time. Pinned in Task 5.

---

## File Structure

| File | Responsibility |
|---|---|
| `DiffusionNexus.Domain/Enums/ComfyUiServerMode.cs` (new) | `Engine` / `CustomUrl` |
| `DiffusionNexus.Domain/Enums/BackendKind.cs` | gains `Engine` |
| `DiffusionNexus.Domain/Services/IComfyUiClientProvider.cs` (new) | provider contract, `ComfyUiClientLease`, `ComfyUiUnavailableException` |
| `DiffusionNexus.Domain/Services/FeatureBackendRouter.cs`, `IFeatureBackendRouter.cs` | mode-aware routing for the three governed features |
| `DiffusionNexus.UI/Services/Engine/IManagedComfyUiEngine.cs` (new) | test seam over the sealed engine host |
| `DiffusionNexus.UI/Services/Engine/EngineRootResolver.cs` (new) | the install-root lambda from `App.axaml.cs`, as a class |
| `DiffusionNexus.UI/Services/Engine/EngineFeatureCatalog.cs` (new, replaces `EngineWorkloadCatalog.cs`) | app feature rows → catalog workload ids, VRAM helpers |
| `DiffusionNexus.UI/Services/Engine/EngineFeatureBackend.cs` (new) | Engine-scoped readiness |
| `DiffusionNexus.UI/Services/Diffusion/ComfyUiClientProvider.cs` (new) | provider implementation |
| `DiffusionNexus.UI/ViewModels/EngineFeaturesViewModel.cs`, `EngineFeatureRowViewModel.cs` (new) | Features dialog logic |
| `DiffusionNexus.UI/Views/Dialogs/EngineFeaturesDialog.axaml(.cs)` (new) | Features dialog view |
| `DiffusionNexus.UI/Services/DatasetEventAggregator.cs` | Settings section hint + Engine Features navigation event |
| `DiffusionNexus.UI/ViewModels/FeatureReadinessViewModel.cs`, `Views/Controls/FeatureReadinessPanel.axaml` | "Running on" / "Not installed on the Engine" line |
| `DiffusionNexus.UI/ViewModels/SettingsViewModel.cs`, `Views/SettingsView.axaml(.cs)` | Server dropdown |
| `DiffusionNexus.UI/ViewModels/InpaintingViewModel.cs`, `OutpaintingViewModel.cs`, `ImageEditorViewModel.cs`, `Tabs/ImageEditTabViewModel.cs`, `LoraDatasetHelperViewModel.cs` | switch to the provider |
| `DiffusionNexus.UI/ViewModels/InstallerManagerViewModel.cs`, `Views/InstallerManagerView.axaml` | Features button, tile visibility |
| `DiffusionNexus.UI/App.axaml.cs` | DI and navigation wiring |

---

### Task 1: Persist `ComfyUiServerMode`

**Files:**
- Create: `DiffusionNexus.Domain/Enums/ComfyUiServerMode.cs`
- Modify: `DiffusionNexus.Domain/Entities/AppSettings.cs` (region "ComfyUI Settings", ~line 225)
- Modify: `DiffusionNexus.DataAccess/Configurations/AppSettingsConfiguration.cs:21`
- Create (generated): `DiffusionNexus.DataAccess/Migrations/Core/<timestamp>_AddComfyUiServerMode.cs` + `.Designer.cs`; snapshot updated
- Modify: `DiffusionNexus.DataAccess/Recovery/DatabaseRecoveryService.cs:282-296`
- Modify: `DiffusionNexus.Service/Services/AppSettingsService.cs:313`
- Modify: `DiffusionNexus.Domain/Models/SettingsExportData.cs:105-107` and `:161-175`
- Modify: `DiffusionNexus.Service/Services/SettingsExportService.cs:79` and `:192`
- Modify: `publish.ps1:293-294`
- Test: `DiffusionNexus.Tests/DataAccess/CoreDbMigrationTests.cs`, `DiffusionNexus.Tests/Service/Services/SettingsExportServiceTests.cs`

**Interfaces:**
- Produces: `DiffusionNexus.Domain.Enums.ComfyUiServerMode { Engine, CustomUrl }`; `AppSettings.ComfyUiServerMode` (default `Engine`); `SettingsExportData.ComfyUiServerMode` (`ComfyUiServerMode?`); `SettingsExportSchema.CurrentVersion = 5`.

- [ ] **Step 1: Write the failing migration tests**

Append to `CoreDbMigrationTests` (same file, same style as the existing tests):

```csharp
    /// <summary>
    /// A database that already has a settings row belongs to someone whose Inpaint and Outpaint ran on
    /// their own ComfyUI. The AddComfyUiServerMode migration must keep them there (CustomUrl), while a
    /// row created after the migration — a fresh install, or publish.ps1's seed — gets Engine.
    /// </summary>
    [Fact]
    public void AddComfyUiServerMode_ExistingRowBecomesCustomUrl_RowInsertedAfterwardsIsEngine()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var dbPath = Path.Combine(tempDir.FullName, "servermode-test.db");
        try
        {
            var options = new DbContextOptionsBuilder<DiffusionNexusCoreDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=False")
                .Options;

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                var migrator = ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
                migrator.Migrate("20260829104419_AddCivitaiBrowserFilterJson");

                // Same column list publish.ps1 seeds with; every later column has a default.
                ctx.Database.ExecuteSqlRaw(
                    "INSERT INTO [AppSettings] (Id, ShowNsfw, GenerateVideoThumbnails, ShowVideoPreview, " +
                    "UseForgeStylePrompts, MergeLoraSources, DeleteEmptySourceFolders, BackupDatasetImagesEnabled, " +
                    "BackupDatabaseEnabled, AutoBackupIntervalDays, AutoBackupIntervalHours, MaxBackups, " +
                    "ComfyUiServerUrl, UpdatedAt) VALUES (1, 0, 1, 0, 1, 0, 0, 0, 1, 1, 0, 10, " +
                    "'http://127.0.0.1:8188/', datetime('now'))");

                ctx.Database.Migrate();
            }

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                ctx.AppSettings.Single().ComfyUiServerMode.Should().Be(ComfyUiServerMode.CustomUrl,
                    "an upgrading user's editor must keep running on their own ComfyUI");

                ctx.Database.ExecuteSqlRaw("DELETE FROM [AppSettings]");
                ctx.Database.ExecuteSqlRaw(
                    "INSERT INTO [AppSettings] (Id, ShowNsfw, GenerateVideoThumbnails, ShowVideoPreview, " +
                    "UseForgeStylePrompts, MergeLoraSources, DeleteEmptySourceFolders, BackupDatasetImagesEnabled, " +
                    "BackupDatabaseEnabled, AutoBackupIntervalDays, AutoBackupIntervalHours, MaxBackups, " +
                    "ComfyUiServerUrl, UpdatedAt) VALUES (1, 0, 1, 0, 1, 0, 0, 0, 1, 1, 0, 10, " +
                    "'http://127.0.0.1:8188/', datetime('now'))");
            }

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                ctx.AppSettings.Single().ComfyUiServerMode.Should().Be(ComfyUiServerMode.Engine,
                    "a row created after the migration (fresh install / publish seed) defaults to the Engine");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            tempDir.Delete(recursive: true);
        }
    }
```

Add `using Microsoft.EntityFrameworkCore.Infrastructure;` at the top of the file (for `GetService<T>()` on a `DbContext`). If the first raw `INSERT` fails on a NOT NULL column without a default, add that column to both INSERTs with its CLR default from `AppSettings.cs`, and add the same column to `publish.ps1` in Step 9.

- [ ] **Step 2: Write the failing export tests**

In `SettingsExportServiceTests.cs`, find the round-trip test that sets `ComfyUiServerUrl = "http://192.168.0.42:9999/"` (line ~111) and asserts it at ~149. In its arranged `AppSettings` add `ComfyUiServerMode = ComfyUiServerMode.CustomUrl,` next to the URL, and after the URL assertion add:

```csharp
        imported.ComfyUiServerMode.Should().Be(ComfyUiServerMode.CustomUrl);
```

Then add a new test at the end of the class:

```csharp
    [Fact]
    public void Import_OfFileWithoutServerMode_YieldsCustomUrl()
    {
        // A file written before the setting existed (schema <= 4) came from a user whose editor ran
        // on their own ComfyUI. Importing it must not silently move them onto the Engine.
        var export = new SettingsExportData { Version = 4, ComfyUiServerUrl = "http://127.0.0.1:8188/" };

        var settings = SettingsExportService.ToAppSettings(export);

        settings.ComfyUiServerMode.Should().Be(ComfyUiServerMode.CustomUrl);
    }
```

Look at how the existing tests in this file call the import mapping (line ~490-501, the test asserting `imported.ComfyUiServerUrl.Should().Be("http://127.0.0.1:8188/")`). If the mapping at `SettingsExportService.cs:185-198` is not already a static `ToAppSettings(SettingsExportData)`, call the import the same way that test does instead of `ToAppSettings`, and keep the assertion. Add `using DiffusionNexus.Domain.Enums;` if missing.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CoreDbMigrationTests|FullyQualifiedName~SettingsExportServiceTests"`
Expected: compile errors — `ComfyUiServerMode` does not exist.

- [ ] **Step 4: Add the enum and the entity property**

`DiffusionNexus.Domain/Enums/ComfyUiServerMode.cs`:

```csharp
namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// Which ComfyUI runs the Image Editor's Inpaint and Outpaint (and, from #608, Batch Upscale).
/// Chosen once, app-wide, in Settings → ComfyUI Server.
/// </summary>
public enum ComfyUiServerMode
{
    /// <summary>The app-owned embedded ComfyUI (Diffusion Nexus Engine), started on demand.</summary>
    Engine,

    /// <summary>A ComfyUI the user runs themselves, at <c>AppSettings.ComfyUiServerUrl</c>.</summary>
    CustomUrl
}
```

In `AppSettings.cs`, inside `#region ComfyUI Settings`, below `ComfyUiServerUrl`:

```csharp
    /// <summary>
    /// Which ComfyUI runs Inpaint and Outpaint. A fresh install uses the Engine; a database that had
    /// a settings row before this column existed is migrated to <see cref="Enums.ComfyUiServerMode.CustomUrl"/>.
    /// </summary>
    public Enums.ComfyUiServerMode ComfyUiServerMode { get; set; } = Enums.ComfyUiServerMode.Engine;
```

- [ ] **Step 5: Configure the column**

In `AppSettingsConfiguration.cs`, after the `ComfyUiServerUrl` line:

```csharp
        entity.Property(e => e.ComfyUiServerMode)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(Domain.Enums.ComfyUiServerMode.Engine);
```

(Use whatever namespace prefix the file already uses for `Domain` types; add `using DiffusionNexus.Domain.Enums;` if that reads cleaner.)

- [ ] **Step 6: Generate the migration**

Run from `E:\Repos\DiffusionNexus`:

```powershell
dotnet ef migrations add AddComfyUiServerMode --project DiffusionNexus.DataAccess --startup-project DiffusionNexus.DataAccess --context DiffusionNexusCoreDbContext --output-dir Migrations/Core
```

Expected: a new `Migrations/Core/<timestamp>_AddComfyUiServerMode.cs` whose `Up` contains `AddColumn<string>(name: "ComfyUiServerMode", table: "AppSettings", type: "TEXT", maxLength: 32, nullable: false, defaultValue: "Engine")`.

Then edit **only** the generated `Up`, appending after the `AddColumn` call:

```csharp
            // Rows that exist when this migration runs belong to users whose Inpaint/Outpaint ran on
            // their own ComfyUI. Keep them there; only rows created later (fresh installs, the
            // publish.ps1 seed) get the column default, Engine. See the #606 spec, section 4.1.
            migrationBuilder.Sql("UPDATE AppSettings SET ComfyUiServerMode = 'CustomUrl';");
```

Verify no drift:

```powershell
dotnet ef migrations has-pending-model-changes --project DiffusionNexus.DataAccess --startup-project DiffusionNexus.DataAccess --context DiffusionNexusCoreDbContext
```

Expected: "No changes have been made to the model since the last migration."

- [ ] **Step 7: Recovery column and save whitelist**

`DatabaseRecoveryService.cs`, in `requiredColumns`, after the `ComfyUiServerUrl` entry:

```csharp
                { "ComfyUiServerMode", "ALTER TABLE AppSettings ADD COLUMN ComfyUiServerMode TEXT NOT NULL DEFAULT 'CustomUrl'" },
```

(Recovery only ever patches an existing database, so it follows the upgrade rule, not the fresh-install default.)

`AppSettingsService.cs`, below `existingSettings.ComfyUiServerUrl = settings.ComfyUiServerUrl;`:

```csharp
        existingSettings.ComfyUiServerMode = settings.ComfyUiServerMode;
```

- [ ] **Step 8: Export and import**

`SettingsExportData.cs`, under `ComfyUiServerUrl`:

```csharp
    /// <summary>Null in files written before schema v5; import treats null as CustomUrl.</summary>
    public ComfyUiServerMode? ComfyUiServerMode { get; init; }
```

(add `using DiffusionNexus.Domain.Enums;` if missing). In `SettingsExportSchema`, add `/// v5: ComfyUI server mode (Engine | CustomUrl).` to the version list and set `public const int CurrentVersion = 5;`.

`SettingsExportService.cs` export (line ~79): change `ComfyUiServerUrl = settings.ComfyUiServerUrl` to

```csharp
            ComfyUiServerUrl = settings.ComfyUiServerUrl,
            ComfyUiServerMode = settings.ComfyUiServerMode
```

Import (line ~192): change `ComfyUiServerUrl = export.ComfyUiServerUrl` to

```csharp
            ComfyUiServerUrl = export.ComfyUiServerUrl,
            // A file without the field predates the setting: its owner ran their own ComfyUI.
            ComfyUiServerMode = export.ComfyUiServerMode ?? ComfyUiServerMode.CustomUrl
```

- [ ] **Step 9: publish.ps1 seed**

In `publish.ps1:293-294`, add the column explicitly so the shipped database is not relying on a default:

```sql
    INSERT INTO [AppSettings] (Id, ShowNsfw, GenerateVideoThumbnails, ShowVideoPreview, UseForgeStylePrompts, MergeLoraSources, DeleteEmptySourceFolders, BackupDatasetImagesEnabled, BackupDatabaseEnabled, AutoBackupIntervalDays, AutoBackupIntervalHours, MaxBackups, ComfyUiServerUrl, ComfyUiServerMode, UpdatedAt)
    VALUES (1, 0, 1, 0, 1, 0, 0, 0, 1, 1, 0, 10, 'http://127.0.0.1:8188/', 'Engine', datetime('now'));
```

- [ ] **Step 10: Run the tests to verify they pass**

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CoreDbMigrationTests|FullyQualifiedName~SettingsExportServiceTests|FullyQualifiedName~DatabaseRecoveryServiceTests|FullyQualifiedName~AppSettingsService"`
Expected: all PASS.

- [ ] **Step 11: Commit and push**

```powershell
cd E:\Repos\DiffusionNexus
git add DiffusionNexus.Domain DiffusionNexus.DataAccess DiffusionNexus.Service DiffusionNexus.Tests publish.ps1
git commit -m "feat(settings): #606 persist ComfyUiServerMode (Engine | CustomUrl)" -m "Fresh databases default to the Engine; an existing settings row is migrated to CustomUrl so upgrading users keep their own ComfyUI. Export schema v5." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
gh auth switch --user Little-God1983; git push
```

---

### Task 2: Engine seams — `IManagedComfyUiEngine` and `IEngineRootResolver`

Pure refactor that later tasks depend on. No behaviour change.

**Files:**
- Create: `DiffusionNexus.UI/Services/Engine/IManagedComfyUiEngine.cs`
- Modify: `DiffusionNexus.UI/Services/Engine/ManagedComfyUiEngine.cs:19`
- Create: `DiffusionNexus.UI/Services/Engine/EngineRootResolver.cs`
- Modify: `DiffusionNexus.UI/App.axaml.cs:632-656` (the `ManagedComfyUiBackend` registration)
- Test: `DiffusionNexus.Tests/Engine/EngineRootResolverTests.cs` (new)

**Interfaces:**
- Produces:
  - `interface IManagedComfyUiEngine { string? BaseUrl { get; } Task<EngineStartResult> EnsureRunningAsync(string installRoot, CancellationToken ct); }`
  - `interface IEngineRootResolver { Task<string?> ResolveAsync(CancellationToken ct = default); }` — returns the app-managed install path (or null) and syncs `extra_model_paths.yaml` first.
  - DI: `IManagedComfyUiEngine` → the `ManagedComfyUiEngine` singleton; `IEngineRootResolver` → `EngineRootResolver` singleton.

- [ ] **Step 1: Write the failing resolver tests**

`DiffusionNexus.Tests/Engine/EngineRootResolverTests.cs`:

```csharp
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Engine;

public class EngineRootResolverTests
{
    private static IServiceScopeFactory Scopes(IReadOnlyList<InstallerPackage> packages)
    {
        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.InstallerPackages.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(packages.ToList());

        var services = new ServiceCollection();
        services.AddScoped(_ => uow.Object);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task NoAppManagedRow_ReturnsNull()
    {
        var resolver = new EngineRootResolver(Scopes(
        [
            new InstallerPackage { Id = 1, Name = "My ComfyUI", InstallationPath = @"D:\ComfyUI", Type = InstallerType.ComfyUI }
        ]), syncModelPaths: (_, _) => Task.CompletedTask);

        (await resolver.ResolveAsync()).Should().BeNull("a user-managed ComfyUI is never the Engine");
    }

    [Fact]
    public async Task AppManagedRow_ReturnsItsPath_AndSyncsModelPathsFirst()
    {
        var synced = new List<string>();
        var resolver = new EngineRootResolver(Scopes(
        [
            new InstallerPackage { Id = 2, Name = "Diffusion Nexus Engine", InstallationPath = @"C:\Engine\ComfyUI", Type = InstallerType.ComfyUI, IsAppManaged = true }
        ]), syncModelPaths: (root, _) => { synced.Add(root); return Task.CompletedTask; });

        (await resolver.ResolveAsync()).Should().Be(@"C:\Engine\ComfyUI");
        synced.Should().Equal(@"C:\Engine\ComfyUI");
    }
}
```

If `IInstallerPackageRepository.GetAllAsync` has no `CancellationToken` parameter, drop `It.IsAny<CancellationToken>()` from the setup. If its return type is not `List<InstallerPackage>`, adapt `.ToList()` to that type.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EngineRootResolverTests"`
Expected: compile error — `EngineRootResolver` does not exist.

- [ ] **Step 3: Add the engine interface**

`DiffusionNexus.UI/Services/Engine/IManagedComfyUiEngine.cs`:

```csharp
namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// The part of <see cref="ManagedComfyUiEngine"/> that callers outside the Canvas need: start it on
/// demand and learn its loopback URL. Exists so the ComfyUI client provider can be unit-tested
/// without spawning Python.
/// </summary>
public interface IManagedComfyUiEngine
{
    /// <summary>Base URL of the running engine, or null when it is not running.</summary>
    string? BaseUrl { get; }

    /// <summary>Starts the engine if needed. Never throws for ordinary failures; see <see cref="EngineStartResult"/>.</summary>
    Task<EngineStartResult> EnsureRunningAsync(string installRoot, CancellationToken ct);
}
```

In `ManagedComfyUiEngine.cs` change the class declaration to:

```csharp
public sealed class ManagedComfyUiEngine : IManagedComfyUiEngine, IAsyncDisposable
```

- [ ] **Step 4: Add the resolver**

`DiffusionNexus.UI/Services/Engine/EngineRootResolver.cs`:

```csharp
using DiffusionNexus.DataAccess.UnitOfWork;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>Finds the Diffusion Nexus Engine's install folder.</summary>
public interface IEngineRootResolver
{
    /// <summary>
    /// Returns the app-managed ComfyUI install path, or null when the Engine was never installed.
    /// Rewrites the Engine's extra_model_paths.yaml first, so a model folder added in Settings is
    /// visible to the check or start that follows.
    /// </summary>
    Task<string?> ResolveAsync(CancellationToken ct = default);
}

/// <summary>
/// Moved verbatim from the lambda that used to live in the ManagedComfyUiBackend registration in
/// App.axaml.cs, so the client provider and the Engine readiness backend resolve the root the same way.
/// </summary>
public sealed class EngineRootResolver : IEngineRootResolver
{
    private readonly IServiceScopeFactory _scopes;
    private readonly Func<string, CancellationToken, Task>? _syncModelPaths;

    /// <param name="scopes">Creates the scope that owns the IUnitOfWork for one resolve.</param>
    /// <param name="syncModelPaths">
    /// Test seam. When null, <see cref="EngineModelPathsSynchronizer"/> is resolved from the same scope.
    /// </param>
    public EngineRootResolver(IServiceScopeFactory scopes, Func<string, CancellationToken, Task>? syncModelPaths = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes;
        _syncModelPaths = syncModelPaths;
    }

    public async Task<string?> ResolveAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var packages = await uow.InstallerPackages.GetAllAsync(ct);
        var installRoot = packages.FirstOrDefault(p => p.IsAppManaged)?.InstallationPath;

        // The engine reads extra_model_paths.yaml once, at process start, and the disk check derives
        // its search paths from the same file. This resolver runs immediately before both, so it is
        // the one point where "the folder list in Settings changed" can still be acted on. Never
        // fails the resolve: the synchronizer swallows its own errors.
        if (!string.IsNullOrWhiteSpace(installRoot))
        {
            if (_syncModelPaths is not null)
                await _syncModelPaths(installRoot, ct);
            else
                await scope.ServiceProvider.GetRequiredService<EngineModelPathsSynchronizer>()
                    .SyncAsync(installRoot, ct);
        }

        return string.IsNullOrWhiteSpace(installRoot) ? null : installRoot;
    }
}
```

(Same `GetAllAsync` parameter note as Step 1.)

- [ ] **Step 5: Rewire DI**

In `App.axaml.cs`, directly after the existing `ManagedComfyUiEngine` singleton registration (line ~761), add:

```csharp
        services.AddSingleton<Services.Engine.IManagedComfyUiEngine>(sp =>
            sp.GetRequiredService<Services.Engine.ManagedComfyUiEngine>());
        services.AddSingleton<Services.Engine.IEngineRootResolver>(sp =>
            new Services.Engine.EngineRootResolver(sp.GetRequiredService<IServiceScopeFactory>()));
```

Replace the inline lambda in the `ManagedComfyUiBackend` registration (lines ~635-655) so the registration reads:

```csharp
        services.AddSingleton<Services.Diffusion.ManagedComfyUiBackend>(sp =>
        {
            var rootResolver = sp.GetRequiredService<Services.Engine.IEngineRootResolver>();
            return new Services.Diffusion.ManagedComfyUiBackend(
                sp.GetRequiredService<Services.Engine.ManagedComfyUiEngine>(),
                () => rootResolver.ResolveAsync(),
                sp.GetService<Services.Diffusion.IWorkflowTemplateSource>());
        });
```

- [ ] **Step 6: Run the tests and build**

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~DiffusionNexus.Tests.Engine"`
Expected: all PASS, including the existing `ManagedComfyUiBackendTests` and `CanvasBackendSelectionTests`.

- [ ] **Step 7: Commit and push**

```powershell
git add DiffusionNexus.UI DiffusionNexus.Tests
git commit -m "refactor(engine): #606 IManagedComfyUiEngine + IEngineRootResolver seams" -m "Moves the engine install-root lambda out of App.axaml.cs into EngineRootResolver so the upcoming client provider and Engine readiness backend share it." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 3: `EngineFeatureCatalog` replaces `EngineWorkloadCatalog`

**Files:**
- Create: `DiffusionNexus.UI/Services/Engine/EngineFeatureCatalog.cs`
- Delete: `DiffusionNexus.UI/Services/Engine/EngineWorkloadCatalog.cs`
- Modify: `DiffusionNexus.UI/ViewModels/WorkloadsViewModel.cs:192` and its private `ParseVramProfiles`
- Modify: `DiffusionNexus.UI/ViewModels/InstallerManagerViewModel.cs:784` (temporary: keeps the old dialog working until Task 6)
- Rename + modify: `DiffusionNexus.Tests/Engine/EngineWorkloadCatalogTests.cs` → `EngineFeatureCatalogTests.cs`
- Modify: `DiffusionNexus.Tests/Engine/EngineWorkloadsViewModelTests.cs`, `DiffusionNexus.Tests/Catalog/CatalogSeedTests.cs`

**Interfaces:**
- Produces (namespace `DiffusionNexus.UI.Services.Engine`):

```csharp
public enum EngineFeature { InpaintOutpaint, Canvas }
public sealed record EngineFeatureDefinition(EngineFeature Feature, string DisplayName, string Description, IReadOnlyList<Guid> WorkloadIds);
public static class EngineFeatureCatalog
{
    public static readonly Guid InpaintingQwen2512;      // 4C486765-A4C1-4E94-ACC2-BBAC0E405B6A
    public static readonly Guid Krea2Turbo;              // E79C079A-2FD7-4FE7-8086-23731092555D
    public static IReadOnlyList<EngineFeatureDefinition> All { get; }
    public static IReadOnlyList<Guid> AllWorkloadIds { get; }
    public static EngineFeatureDefinition Get(EngineFeature feature);
    public static EngineFeature? ForAppFeature(DiffusionNexus.Domain.Enums.Feature feature);
    public static int SuggestVramTier(long vramTotalMb, IReadOnlyList<int> configuredTiers);
    public static int[] ParseVramProfiles(string? vramProfiles);
}
```

- [ ] **Step 1: Write the failing catalog tests**

Rename `EngineWorkloadCatalogTests.cs` to `EngineFeatureCatalogTests.cs` (`git mv`), rename the class to `EngineFeatureCatalogTests`, replace every `EngineWorkloadCatalog` with `EngineFeatureCatalog`, and replace the first test (the one asserting `WorkloadIds.Should().Contain(krea)` and `Contains(...)`) with:

```csharp
    [Fact]
    public void Rows_MapToTheirCatalogWorkloads()
    {
        EngineFeatureCatalog.Get(EngineFeature.InpaintOutpaint).WorkloadIds
            .Should().Equal(Guid.Parse("4C486765-A4C1-4E94-ACC2-BBAC0E405B6A"));
        EngineFeatureCatalog.Get(EngineFeature.Canvas).WorkloadIds
            .Should().Equal(Guid.Parse("E79C079A-2FD7-4FE7-8086-23731092555D"));
        EngineFeatureCatalog.AllWorkloadIds.Should().HaveCount(2);
    }

    [Fact]
    public void Rows_AreListedInDisplayOrder_WithTheirLabels()
    {
        EngineFeatureCatalog.All.Select(r => r.DisplayName)
            .Should().Equal("Inpaint & Outpaint", "Canvas · Krea 2 Turbo");
    }

    [Theory]
    [InlineData(Feature.Inpainting, EngineFeature.InpaintOutpaint)]
    [InlineData(Feature.Outpaint, EngineFeature.InpaintOutpaint)]
    public void ForAppFeature_MapsEditorToolsToTheirRow(Feature feature, EngineFeature expected)
    {
        EngineFeatureCatalog.ForAppFeature(feature).Should().Be(expected);
    }

    [Theory]
    [InlineData(Feature.OutpaintVision)]
    [InlineData(Feature.BatchUpscale)]
    [InlineData(Feature.BatchUpscaleVision)]
    [InlineData(Feature.Captioning)]
    public void ForAppFeature_IsNull_ForFeaturesTheEngineDoesNotOfferYet(Feature feature)
    {
        EngineFeatureCatalog.ForAppFeature(feature).Should().BeNull();
    }

    [Theory]
    [InlineData("8,12,16,24,32", new[] { 8, 12, 16, 24, 32 })]
    [InlineData("8GB, 16GB, 24+", new[] { 8, 16, 24 })]
    [InlineData("", new int[0])]
    [InlineData(null, new int[0])]
    public void ParseVramProfiles_ReadsTheCatalogFormat(string? raw, int[] expected)
    {
        EngineFeatureCatalog.ParseVramProfiles(raw).Should().Equal(expected);
    }
```

Add `using DiffusionNexus.Domain.Enums;`. Keep the existing `SuggestVramTier` theory and fact unchanged apart from the class rename.

In `EngineWorkloadsViewModelTests.cs` replace `EngineWorkloadCatalog.Krea2Turbo` with `EngineFeatureCatalog.Krea2Turbo` and `allowedConfigurationIds: EngineWorkloadCatalog.WorkloadIds` with `allowedConfigurationIds: [EngineFeatureCatalog.Krea2Turbo]`.

In `CatalogSeedTests.cs` replace `.Concat(EngineWorkloadCatalog.WorkloadIds)` with `.Concat(EngineFeatureCatalog.AllWorkloadIds)`, `ids.Should().Contain(EngineWorkloadCatalog.Krea2Turbo);` with `ids.Should().Contain(EngineFeatureCatalog.Krea2Turbo);`, and `HaveCountGreaterThan(EngineWorkloadCatalog.WorkloadIds.Count)` with `HaveCountGreaterThan(EngineFeatureCatalog.AllWorkloadIds.Count - 1)` (Inpainting-Qwen 2512 is now both a FeatureRegistry id and an Engine row, so the union grows by one less). This theory is what proves every Engine row's workload exists in the embedded catalog seed.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EngineFeatureCatalogTests|FullyQualifiedName~EngineWorkloadsViewModelTests|FullyQualifiedName~CatalogSeedTests"`
Expected: compile error — `EngineFeatureCatalog` does not exist.

- [ ] **Step 3: Write the catalog**

`DiffusionNexus.UI/Services/Engine/EngineFeatureCatalog.cs`:

```csharp
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>One row of the Engine's Features dialog.</summary>
public enum EngineFeature
{
    /// <summary>Image Editor Inpaint and non-Vision Outpaint (identical model set).</summary>
    InpaintOutpaint,

    /// <summary>Diffusion Canvas text-to-image with Krea 2 Turbo.</summary>
    Canvas
}

/// <summary>What a Features row shows and which catalog workloads it installs.</summary>
public sealed record EngineFeatureDefinition(
    EngineFeature Feature,
    string DisplayName,
    string Description,
    IReadOnlyList<Guid> WorkloadIds);

/// <summary>
/// The app features the Diffusion Nexus Engine can be equipped with, each mapped to the catalog
/// workloads that carry its node packs and models. Rows map to whole workloads so the catalog stays
/// the single source of what a feature needs; nothing here lists model files. A row only appears once
/// it has been verified against the Engine: Outpaint Vision arrives with #607, Batch Upscale with #608.
/// </summary>
public static class EngineFeatureCatalog
{
    /// <summary>"Inpainting-Qwen 2512": ComfyUI-GGUF + the five Qwen-Image 2512 inpaint models.</summary>
    public static readonly Guid InpaintingQwen2512 = Guid.Parse("4C486765-A4C1-4E94-ACC2-BBAC0E405B6A");

    /// <summary>Krea 2 Turbo — the first Engine workload, and the Engine's torch source.</summary>
    public static readonly Guid Krea2Turbo = Guid.Parse("E79C079A-2FD7-4FE7-8086-23731092555D");

    /// <summary>Rows in display order.</summary>
    public static IReadOnlyList<EngineFeatureDefinition> All { get; } =
    [
        new(EngineFeature.InpaintOutpaint,
            "Inpaint & Outpaint",
            "Image Editor · Qwen-Image 2512 with the InstantX inpaint ControlNet and the Lightning LoRA",
            [InpaintingQwen2512]),
        new(EngineFeature.Canvas,
            "Canvas · Krea 2 Turbo",
            "Text to image in the Diffusion Canvas",
            [Krea2Turbo]),
    ];

    /// <summary>Every workload id any row installs, without duplicates.</summary>
    public static IReadOnlyList<Guid> AllWorkloadIds { get; } =
        All.SelectMany(r => r.WorkloadIds).Distinct().ToList();

    public static EngineFeatureDefinition Get(EngineFeature feature) =>
        All.Single(r => r.Feature == feature);

    /// <summary>The row that equips the Engine for an app feature, or null when the Engine does not offer it yet.</summary>
    public static EngineFeature? ForAppFeature(Feature feature) => feature switch
    {
        Feature.Inpainting or Feature.Outpaint => EngineFeature.InpaintOutpaint,
        _ => null
    };

    /// <summary>
    /// Picks the default VRAM tier: the largest configured tier that fits in the detected VRAM,
    /// falling back to the smallest tier when VRAM is unknown or below every tier (a too-small
    /// quantization still runs). Returns 0 when the workload declares no tiers — the workload
    /// installer reads 0 as "no VRAM filtering".
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

    /// <summary>Parses the catalog's comma-separated VRAM profiles ("8,16,24,24+" or "8GB") into GB values.</summary>
    public static int[] ParseVramProfiles(string? vramProfiles)
    {
        if (string.IsNullOrWhiteSpace(vramProfiles))
            return [];

        return vramProfiles
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p.Replace("GB", "").Replace("+", ""), out var val) ? val : 0)
            .Where(v => v > 0)
            .ToArray();
    }
}
```

`git rm DiffusionNexus.UI/Services/Engine/EngineWorkloadCatalog.cs`.

- [ ] **Step 4: Update the two UI call sites**

`WorkloadsViewModel.cs:192`: `return EngineFeatureCatalog.SuggestVramTier(snapshot.VramTotalMB, configuredVramProfiles);`. Delete the private `ParseVramProfiles` method at the end of the class and change its one call (`ConfiguredVramProfiles = ParseVramProfiles(config.Vram.VramProfiles)`) to `ConfiguredVramProfiles = EngineFeatureCatalog.ParseVramProfiles(config.Vram.VramProfiles)`.

`InstallerManagerViewModel.cs:784`: `Services.Engine.EngineFeatureCatalog.AllWorkloadIds);` (Task 6 replaces this branch entirely).

- [ ] **Step 5: Run the tests to verify they pass**

Same command as Step 2. Expected: all PASS.

- [ ] **Step 6: Commit and push**

```powershell
git add -A DiffusionNexus.UI/Services/Engine DiffusionNexus.UI/ViewModels DiffusionNexus.Tests
git commit -m "feat(engine): #606 EngineFeatureCatalog maps app features to Engine workloads" -m "Replaces EngineWorkloadCatalog. Rows: Inpaint & Outpaint (Inpainting-Qwen 2512) and Canvas (Krea 2 Turbo)." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 4: `IComfyUiClientProvider`

**Files:**
- Create: `DiffusionNexus.Domain/Services/IComfyUiClientProvider.cs`
- Create: `DiffusionNexus.UI/Services/Diffusion/ComfyUiClientProvider.cs`
- Modify: `DiffusionNexus.UI/App.axaml.cs:766-771` (singleton URL) + register the provider
- Test: `DiffusionNexus.Tests/Engine/ComfyUiClientProviderTests.cs` (new)

**Interfaces:**
- Consumes: `IManagedComfyUiEngine`, `IEngineRootResolver` (Task 2); `ComfyUiServerMode` (Task 1); `ManagedEngineLocator.LooksInstalled(string?)` (existing static).
- Produces (namespace `DiffusionNexus.Domain.Services`):

```csharp
public interface IComfyUiClientProvider
{
    Task<ComfyUiClientLease> AcquireAsync(IProgress<string>? progress = null, CancellationToken ct = default);
}
public sealed class ComfyUiClientLease : IDisposable
{
    public ComfyUiClientLease(IComfyUIWrapperService client, ComfyUiServerMode mode, string baseUrl, bool ownsClient);
    public IComfyUIWrapperService Client { get; }
    public ComfyUiServerMode Mode { get; }
    public string BaseUrl { get; }
}
public sealed class ComfyUiUnavailableException : Exception { public ComfyUiUnavailableException(string message); }
```

- UI: `ComfyUiClientProvider(Func<CancellationToken, Task<AppSettings>> readSettings, IEngineRootResolver rootResolver, IManagedComfyUiEngine engine, IUnifiedLogger? unifiedLogger = null, Func<string, IComfyUIWrapperService>? clientFactory = null, Func<string?, bool>? looksInstalled = null)`.
  `readSettings` is a delegate, not `IAppSettingsService`: that service is transient over a scoped DbContext, and a singleton holding one instance would run two reads on the same context when the Outpaint panel checks Outpaint and Outpaint Vision in parallel. DI builds the delegate with a fresh scope per call.

- [ ] **Step 1: Write the failing provider tests**

`DiffusionNexus.Tests/Engine/ComfyUiClientProviderTests.cs`:

```csharp
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services.Diffusion;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Engine;

public class ComfyUiClientProviderTests
{
    private AppSettings _current = new() { Id = 1 };
    private readonly Mock<IEngineRootResolver> _root = new();
    private readonly Mock<IManagedComfyUiEngine> _engine = new();
    private readonly List<string> _clientUrls = [];
    private readonly List<Mock<IComfyUIWrapperService>> _clients = [];
    private bool _looksInstalled = true;

    private void Mode(ComfyUiServerMode mode, string url = "http://127.0.0.1:8188/") =>
        _current = new AppSettings { Id = 1, ComfyUiServerMode = mode, ComfyUiServerUrl = url };

    private ComfyUiClientProvider Sut() => new(
        _ => Task.FromResult(_current), _root.Object, _engine.Object, unifiedLogger: null,
        clientFactory: url =>
        {
            _clientUrls.Add(url);
            var client = new Mock<IComfyUIWrapperService>();
            _clients.Add(client);
            return client.Object;
        },
        looksInstalled: _ => _looksInstalled);

    [Fact]
    public async Task CustomUrl_ReturnsClientOnTheSettingsUrl_AndNeverTouchesTheEngine()
    {
        Mode(ComfyUiServerMode.CustomUrl, "http://192.168.1.20:8188/");

        using var lease = await Sut().AcquireAsync();

        lease.Mode.Should().Be(ComfyUiServerMode.CustomUrl);
        lease.BaseUrl.Should().Be("http://192.168.1.20:8188/");
        _clientUrls.Should().Equal("http://192.168.1.20:8188/");
        _engine.Verify(e => e.EnsureRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _root.Verify(r => r.ResolveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Engine_StartsTheEngine_ReportsProgress_AndReturnsClientOnItsPort()
    {
        Mode(ComfyUiServerMode.Engine);
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(@"C:\Engine\ComfyUI");
        _engine.Setup(e => e.EnsureRunningAsync(@"C:\Engine\ComfyUI", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStartResult(true, "http://127.0.0.1:51234", null));
        var reported = new List<string>();

        using var lease = await Sut().AcquireAsync(new SyncProgress(reported.Add));

        lease.Mode.Should().Be(ComfyUiServerMode.Engine);
        lease.BaseUrl.Should().Be("http://127.0.0.1:51234");
        _clientUrls.Should().Equal("http://127.0.0.1:51234");
        reported.Should().Contain("Starting Diffusion Nexus Engine…");
        _engine.Verify(e => e.EnsureRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Engine_NotInstalled_Throws_WithoutStartingAnything()
    {
        Mode(ComfyUiServerMode.Engine);
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _looksInstalled = false;

        var act = () => Sut().AcquireAsync();

        await act.Should().ThrowAsync<ComfyUiUnavailableException>()
            .WithMessage("Diffusion Nexus Engine is not installed*");
        _engine.Verify(e => e.EnsureRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Engine_StartFailure_Throws_WithTheEnginesReason()
    {
        Mode(ComfyUiServerMode.Engine);
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(@"C:\Engine\ComfyUI");
        _engine.Setup(e => e.EnsureRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStartResult(false, null, "The engine process exited on its own during startup (exit code 1)."));

        var act = () => Sut().AcquireAsync();

        await act.Should().ThrowAsync<ComfyUiUnavailableException>()
            .WithMessage("Diffusion Nexus Engine failed to start: The engine process exited on its own during startup (exit code 1).");
        _clientUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task ModeIsReadOnEveryCall_SoASettingsChangeAppliesToTheNextGenerate()
    {
        Mode(ComfyUiServerMode.CustomUrl);
        var sut = Sut();
        (await sut.AcquireAsync()).Dispose();

        Mode(ComfyUiServerMode.Engine);
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(@"C:\Engine\ComfyUI");
        _engine.Setup(e => e.EnsureRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStartResult(true, "http://127.0.0.1:51234", null));
        using var second = await sut.AcquireAsync();

        second.Mode.Should().Be(ComfyUiServerMode.Engine);
    }

    [Fact]
    public void Lease_DisposesTheClientOnlyWhenItOwnsIt()
    {
        var owned = new Mock<IComfyUIWrapperService>();
        var borrowed = new Mock<IComfyUIWrapperService>();

        new ComfyUiClientLease(owned.Object, ComfyUiServerMode.Engine, "http://x", ownsClient: true).Dispose();
        new ComfyUiClientLease(borrowed.Object, ComfyUiServerMode.CustomUrl, "http://y", ownsClient: false).Dispose();

        owned.Verify(c => c.Dispose(), Times.Once);
        borrowed.Verify(c => c.Dispose(), Times.Never);
    }

    /// <summary>Synchronous IProgress — Progress&lt;T&gt; posts to a sync context and races the assertion.</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ComfyUiClientProviderTests"`
Expected: compile error — `ComfyUiClientProvider` / `IComfyUiClientProvider` do not exist.

- [ ] **Step 3: Write the contract**

`DiffusionNexus.Domain/Services/IComfyUiClientProvider.cs`:

```csharp
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Hands out a ComfyUI client for the server chosen in Settings → ComfyUI Server. The one place that
/// knows whether a job goes to the Diffusion Nexus Engine or to the user's own ComfyUI.
/// </summary>
public interface IComfyUiClientProvider
{
    /// <summary>
    /// Returns a client for the active mode. In Engine mode this starts the Engine when it is not
    /// running (reporting <c>"Starting Diffusion Nexus Engine…"</c> on <paramref name="progress"/>),
    /// which can take up to about two minutes on a cold start.
    /// </summary>
    /// <exception cref="ComfyUiUnavailableException">The Engine is not installed or failed to start.</exception>
    Task<ComfyUiClientLease> AcquireAsync(IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// A client for one operation. Dispose it when the operation ends; the Engine process itself keeps
/// running until the app exits.
/// </summary>
public sealed class ComfyUiClientLease : IDisposable
{
    private readonly bool _ownsClient;

    public ComfyUiClientLease(IComfyUIWrapperService client, ComfyUiServerMode mode, string baseUrl, bool ownsClient)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        Client = client;
        Mode = mode;
        BaseUrl = baseUrl;
        _ownsClient = ownsClient;
    }

    public IComfyUIWrapperService Client { get; }
    public ComfyUiServerMode Mode { get; }
    public string BaseUrl { get; }

    public void Dispose()
    {
        if (_ownsClient)
            Client.Dispose();
    }
}

/// <summary>The selected ComfyUI cannot be used right now. The message is written for the user.</summary>
public sealed class ComfyUiUnavailableException : Exception
{
    public ComfyUiUnavailableException(string message) : base(message) { }
}
```

- [ ] **Step 4: Write the provider**

`DiffusionNexus.UI/Services/Diffusion/ComfyUiClientProvider.cs`:

```csharp
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Services.Engine;
using Serilog;

namespace DiffusionNexus.UI.Services.Diffusion;

/// <inheritdoc cref="IComfyUiClientProvider"/>
public sealed class ComfyUiClientProvider : IComfyUiClientProvider
{
    private const string EngineSource = "Diffusion Nexus Engine";
    private const string ComfySource = "ComfyUI";
    private static readonly ILogger Logger = Log.ForContext<ComfyUiClientProvider>();

    private readonly Func<CancellationToken, Task<AppSettings>> _readSettings;
    private readonly IEngineRootResolver _rootResolver;
    private readonly IManagedComfyUiEngine _engine;
    private readonly IUnifiedLogger? _unifiedLogger;
    private readonly Func<string, IComfyUIWrapperService> _clientFactory;
    private readonly Func<string?, bool> _looksInstalled;

    /// <param name="readSettings">Reads the current settings; DI gives each call its own scope.</param>
    /// <param name="clientFactory">Test seam; defaults to <c>new ComfyUIWrapperService(url)</c>.</param>
    /// <param name="looksInstalled">Test seam; defaults to <see cref="ManagedEngineLocator.LooksInstalled"/>.</param>
    public ComfyUiClientProvider(
        Func<CancellationToken, Task<AppSettings>> readSettings,
        IEngineRootResolver rootResolver,
        IManagedComfyUiEngine engine,
        IUnifiedLogger? unifiedLogger = null,
        Func<string, IComfyUIWrapperService>? clientFactory = null,
        Func<string?, bool>? looksInstalled = null)
    {
        ArgumentNullException.ThrowIfNull(readSettings);
        ArgumentNullException.ThrowIfNull(rootResolver);
        ArgumentNullException.ThrowIfNull(engine);
        _readSettings = readSettings;
        _rootResolver = rootResolver;
        _engine = engine;
        _unifiedLogger = unifiedLogger;
        _clientFactory = clientFactory ?? (url => new ComfyUIWrapperService(url));
        _looksInstalled = looksInstalled ?? ManagedEngineLocator.LooksInstalled;
    }

    public async Task<ComfyUiClientLease> AcquireAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // Read on every call: the user may have switched the dropdown since the last generate.
        var settings = await _readSettings(ct);

        if (settings.ComfyUiServerMode == ComfyUiServerMode.CustomUrl)
        {
            var url = settings.ComfyUiServerUrl;
            Info(LogCategory.Configuration, ComfySource, $"Using your own ComfyUI at {url}.");
            return new ComfyUiClientLease(_clientFactory(url), ComfyUiServerMode.CustomUrl, url, ownsClient: true);
        }

        var root = await _rootResolver.ResolveAsync(ct);
        if (!_looksInstalled(root))
        {
            const string notInstalled =
                "Diffusion Nexus Engine is not installed. Install it in the Installation Manager.";
            Warn(EngineSource, notInstalled);
            throw new ComfyUiUnavailableException(notInstalled);
        }

        if (_engine.BaseUrl is null)
        {
            progress?.Report("Starting Diffusion Nexus Engine…");
            Info(LogCategory.InstanceManagement, EngineSource, "Engine not running; starting it for this generate.");
        }

        var started = await _engine.EnsureRunningAsync(root!, ct);
        if (!started.IsRunning || started.BaseUrl is null)
        {
            var reason = $"Diffusion Nexus Engine failed to start: {started.FailureReason ?? "unknown reason"}";
            Warn(EngineSource, reason);
            throw new ComfyUiUnavailableException(reason);
        }

        Info(LogCategory.InstanceManagement, EngineSource, $"Using the Engine at {started.BaseUrl}.");
        return new ComfyUiClientLease(_clientFactory(started.BaseUrl), ComfyUiServerMode.Engine, started.BaseUrl, ownsClient: true);
    }

    private void Info(LogCategory category, string source, string message)
    {
        Logger.Information("{Source}: {Message}", source, message);
        _unifiedLogger?.Info(category, source, message);
    }

    private void Warn(string source, string message)
    {
        Logger.Warning("{Source}: {Message}", source, message);
        _unifiedLogger?.Warn(LogCategory.InstanceManagement, source, message);
    }
}
```

Note: the "not installed" message must start with `Diffusion Nexus Engine is not installed` (the test uses a trailing wildcard).

- [ ] **Step 5: Register it and fix the singleton URL**

In `App.axaml.cs`, replace the `IComfyUIWrapperService` singleton registration (lines ~766-771) with:

```csharp
        // ComfyUI client for the features not yet on IComfyUiClientProvider (Batch Upscale,
        // ComfyUI captioning, the ComfyUI readiness backend). Built from the Settings URL — it used
        // to ignore it and always talk to 8188. A URL change reaches these after a restart; #608
        // moves Batch Upscale onto the provider.
        services.AddSingleton<IComfyUIWrapperService>(sp =>
        {
            using var scope = sp.CreateScope();
            var url = scope.ServiceProvider.GetRequiredService<IAppSettingsService>()
                .GetSettingsAsync().GetAwaiter().GetResult().ComfyUiServerUrl;
            return new ComfyUIWrapperService(string.IsNullOrWhiteSpace(url) ? "http://127.0.0.1:8188" : url);
        });

        // The ComfyUI that Inpaint and Outpaint run on: the Engine or the user's own, per Settings.
        // Settings are read through a fresh scope per call: IAppSettingsService is transient over a
        // scoped DbContext, and parallel readiness checks must not share one context.
        services.AddSingleton<IComfyUiClientProvider>(sp =>
        {
            var scopes = sp.GetRequiredService<IServiceScopeFactory>();
            return new Services.Diffusion.ComfyUiClientProvider(
                async ct =>
                {
                    using var scope = scopes.CreateScope();
                    return await scope.ServiceProvider.GetRequiredService<IAppSettingsService>().GetSettingsAsync(ct);
                },
                sp.GetRequiredService<Services.Engine.IEngineRootResolver>(),
                sp.GetRequiredService<Services.Engine.IManagedComfyUiEngine>(),
                sp.GetService<Domain.Services.UnifiedLogging.IUnifiedLogger>());
        });
```

(`ComfyUIWrapperService.DefaultBaseUrl` is private, hence the literal.)

- [ ] **Step 6: Run the tests and build**

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ComfyUiClientProviderTests"` then `dotnet build E:\Repos\DiffusionNexus\DiffusionNexus.sln -c Debug`.
Expected: PASS; build succeeds.

- [ ] **Step 7: Commit and push**

```powershell
git add DiffusionNexus.Domain DiffusionNexus.UI DiffusionNexus.Tests
git commit -m "feat(engine): #606 IComfyUiClientProvider picks the Engine or the user's ComfyUI" -m "Starts the Engine on demand and hands out a per-operation client. The shared ComfyUI singleton now honours the Settings URL instead of always using 8188." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 5: Engine readiness backend and the mode-aware router

**Files:**
- Modify: `DiffusionNexus.Domain/Enums/BackendKind.cs`
- Modify: `DiffusionNexus.Domain/Services/FeatureBackendRouter.cs`, `IFeatureBackendRouter.cs` (doc only)
- Create: `DiffusionNexus.UI/Services/Engine/EngineFeatureBackend.cs`
- Modify: `DiffusionNexus.UI/App.axaml.cs:800-801` (router + new backend registration)
- Test: `DiffusionNexus.Tests/Engine/EngineFeatureBackendTests.cs` (new), `DiffusionNexus.Tests/Domain/FeatureBackendRouterModeTests.cs` (new; put it next to any existing router tests — search `FeatureBackendRouter` under `DiffusionNexus.Tests` and use that folder)

**Interfaces:**
- Consumes: `IEngineRootResolver` (Task 2), `EngineFeatureCatalog` (Task 3), `ComfyUiServerMode` (Task 1), `ICatalog.GetWorkloadAsync(Guid, CancellationToken)` (SDK, returns `InstallationConfiguration?`), `IConfigurationCheckerService.CheckConfigurationAsync(config, root, options, ct)`.
- Produces:
  - `BackendKind.Engine`.
  - `FeatureBackendRouter(IEnumerable<IFeatureBackend> backends, IReadOnlyDictionary<Feature, BackendKind>? routing = null, Func<ComfyUiServerMode>? serverMode = null)`; `public static readonly IReadOnlySet<Feature> ServerModeFeatures` = { Inpainting, Outpaint, OutpaintVision }.
  - `EngineFeatureBackend(IEngineRootResolver rootResolver, ICatalog catalog, IConfigurationCheckerService checker, IUnifiedLogger? unifiedLogger = null, Func<string?, bool>? looksInstalled = null) : IFeatureBackend` with `Kind = BackendKind.Engine`, `DisplayName = "Diffusion Nexus Engine"`.

- [ ] **Step 1: Write the failing router tests**

```csharp
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Domain;

public class FeatureBackendRouterModeTests
{
    private static IFeatureBackend Backend(BackendKind kind)
    {
        var b = new Mock<IFeatureBackend>();
        b.SetupGet(x => x.Kind).Returns(kind);
        return b.Object;
    }

    private readonly IFeatureBackend _comfy = Backend(BackendKind.ComfyUI);
    private readonly IFeatureBackend _engine = Backend(BackendKind.Engine);

    [Theory]
    [InlineData(Feature.Inpainting)]
    [InlineData(Feature.Outpaint)]
    [InlineData(Feature.OutpaintVision)]
    public void GovernedFeatures_FollowTheServerMode(Feature feature)
    {
        var mode = ComfyUiServerMode.Engine;
        var router = new FeatureBackendRouter([_comfy, _engine], serverMode: () => mode);

        router.Resolve(feature).Should().BeSameAs(_engine);

        mode = ComfyUiServerMode.CustomUrl;
        router.Resolve(feature).Should().BeSameAs(_comfy, "the mode is read on every call");
    }

    [Theory]
    [InlineData(Feature.Captioning)]
    [InlineData(Feature.BatchUpscale)]
    [InlineData(Feature.BatchUpscaleVision)]
    public void OtherFeatures_StayOnComfyUi_InEngineMode(Feature feature)
    {
        var router = new FeatureBackendRouter([_comfy, _engine], serverMode: () => ComfyUiServerMode.Engine);

        router.Resolve(feature).Should().BeSameAs(_comfy);
    }

    [Fact]
    public void WithoutAModeSource_TheStaticRoutingStillApplies()
    {
        new FeatureBackendRouter([_comfy, _engine]).Resolve(Feature.Inpainting).Should().BeSameAs(_comfy);
    }
}
```

- [ ] **Step 2: Write the failing Engine backend tests**

`DiffusionNexus.Tests/Engine/EngineFeatureBackendTests.cs`:

```csharp
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Engine;

public class EngineFeatureBackendTests
{
    private const string Root = @"C:\Engine\ComfyUI";
    private readonly Mock<IEngineRootResolver> _root = new();
    private readonly Mock<ICatalog> _catalog = new();
    private readonly Mock<IConfigurationCheckerService> _checker = new();
    private bool _looksInstalled = true;

    public EngineFeatureBackendTests()
    {
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Root);
        _catalog.Setup(c => c.GetWorkloadAsync(EngineFeatureCatalog.InpaintingQwen2512, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InstallationConfiguration { Id = EngineFeatureCatalog.InpaintingQwen2512, Name = "Inpainting-Qwen 2512" });
    }

    private EngineFeatureBackend Sut() =>
        new(_root.Object, _catalog.Object, _checker.Object, unifiedLogger: null, looksInstalled: _ => _looksInstalled);

    private void CheckReturns(params (string Name, bool Installed, bool IsNode)[] items) =>
        _checker.Setup(c => c.CheckConfigurationAsync(It.IsAny<InstallationConfiguration>(), Root,
                It.IsAny<ConfigurationCheckOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConfigurationCheckResult
            {
                OverallStatus = ConfigurationStatus.Partial,
                CustomNodesStatus = ConfigurationStatus.Partial,
                ModelsStatus = ConfigurationStatus.Partial,
                InstallationType = ComfyUIInstallationType.Manual,
                CustomNodeResults = items.Where(i => i.IsNode).Select(i => new CustomNodeCheckResult
                {
                    Id = Guid.NewGuid(), Name = i.Name, Url = "https://example", IsInstalled = i.Installed, ExpectedPath = "x"
                }).ToList(),
                ModelResults = items.Where(i => !i.IsNode).Select(i => new ModelCheckResult
                {
                    Id = Guid.NewGuid(), Name = i.Name, IsInstalled = i.Installed, SearchedPaths = []
                }).ToList()
            });

    [Fact]
    public async Task NotInstalled_ReportsOneRequirement_AndNeverChecksFiles()
    {
        _looksInstalled = false;

        var result = await Sut().CheckFeatureAsync(Feature.Inpainting);

        result.Backend.Should().Be(BackendKind.Engine);
        result.ActiveBackendName.Should().Be("Diffusion Nexus Engine");
        result.IsReady.Should().BeFalse();
        result.MissingRequirements.Should().Equal("Diffusion Nexus Engine is not installed");
        _checker.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingNodesAndModels_AreListed_ScopedToTheEngineRoot()
    {
        CheckReturns(("ComfyUI-GGUF", false, true), ("qwen_image_vae", true, false), ("Qwen-Image-2512-GGUF", false, false));

        var result = await Sut().CheckFeatureAsync(Feature.Outpaint);

        result.IsReady.Should().BeFalse();
        result.IsBackendOnline.Should().BeTrue("an installed Engine counts as reachable; Generate starts it");
        result.MissingRequirements.Should().BeEquivalentTo(
            "Custom node missing on the Engine: ComfyUI-GGUF",
            "Model missing on the Engine: Qwen-Image-2512-GGUF");
    }

    [Fact]
    public async Task EverythingPresent_IsReady()
    {
        CheckReturns(("ComfyUI-GGUF", true, true), ("qwen_image_vae", true, false));

        var result = await Sut().CheckFeatureAsync(Feature.Inpainting);

        result.IsReady.Should().BeTrue();
        result.MissingRequirements.Should().BeEmpty();
    }

    [Fact]
    public async Task OutpaintVision_IsNotOfferedOnTheEngineYet()
    {
        var result = await Sut().CheckFeatureAsync(Feature.OutpaintVision);

        result.IsReady.Should().BeFalse();
        result.MissingRequirements.Should().Equal("Outpaint Vision is not available on the Diffusion Nexus Engine yet");
        _checker.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = () => Sut().CheckFeatureAsync(Feature.Inpainting, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~FeatureBackendRouterModeTests|FullyQualifiedName~EngineFeatureBackendTests"`
Expected: compile errors — `BackendKind.Engine`, `serverMode` parameter, `EngineFeatureBackend` missing.

- [ ] **Step 4: Add `BackendKind.Engine`**

Append to `BackendKind`:

```csharp
    /// <summary>
    /// The feature runs on the app-owned embedded ComfyUI (Diffusion Nexus Engine). Readiness is the
    /// feature's catalog workloads checked against the Engine's own folder only.
    /// </summary>
    Engine
```

- [ ] **Step 5: Make the router mode-aware**

In `FeatureBackendRouter.cs` add the field, the set and the constructor parameter, and change `Resolve`:

```csharp
    /// <summary>
    /// Features whose backend the Settings → ComfyUI Server dropdown decides (#606). OutpaintVision is
    /// included because the Outpaint panel runs both workflows through the same client.
    /// </summary>
    public static readonly IReadOnlySet<Feature> ServerModeFeatures =
        new HashSet<Feature> { Feature.Inpainting, Feature.Outpaint, Feature.OutpaintVision };

    private readonly Func<ComfyUiServerMode>? _serverMode;

    public FeatureBackendRouter(
        IEnumerable<IFeatureBackend> backends,
        IReadOnlyDictionary<Feature, BackendKind>? routing = null,
        Func<ComfyUiServerMode>? serverMode = null)
    {
        // ... existing body unchanged ...
        _serverMode = serverMode;
    }

    /// <inheritdoc />
    public IFeatureBackend? Resolve(Feature feature)
    {
        if (_serverMode is not null && ServerModeFeatures.Contains(feature))
        {
            var kind = _serverMode() == ComfyUiServerMode.Engine ? BackendKind.Engine : BackendKind.ComfyUI;
            return _backendsByKind.GetValueOrDefault(kind);
        }

        if (!_routing.TryGetValue(feature, out var staticKind))
            return null;

        return _backendsByKind.GetValueOrDefault(staticKind);
    }
```

Update the class summary to say governed features follow `serverMode`, everything else the static map. Add one sentence to `IFeatureBackendRouter.Resolve`'s doc: "For the features in `FeatureBackendRouter.ServerModeFeatures` the answer follows Settings → ComfyUI Server and may change between calls."

- [ ] **Step 6: Write `EngineFeatureBackend`**

`DiffusionNexus.UI/Services/Engine/EngineFeatureBackend.cs`:

```csharp
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using Serilog;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// Readiness for features that run on the Diffusion Nexus Engine: is the Engine installed, and does
/// its own folder hold every node pack and model the feature's catalog workloads declare? Unlike the
/// ComfyUI backend it never looks at other ComfyUI installs on the machine, and it never starts the
/// Engine — Generate does that.
/// </summary>
public sealed class EngineFeatureBackend : IFeatureBackend
{
    private const string LogSource = "Diffusion Nexus Engine";
    private static readonly ILogger Logger = Log.ForContext<EngineFeatureBackend>();

    private readonly IEngineRootResolver _rootResolver;
    private readonly ICatalog _catalog;
    private readonly IConfigurationCheckerService _checker;
    private readonly IUnifiedLogger? _unifiedLogger;
    private readonly Func<string?, bool> _looksInstalled;

    public EngineFeatureBackend(
        IEngineRootResolver rootResolver,
        ICatalog catalog,
        IConfigurationCheckerService checker,
        IUnifiedLogger? unifiedLogger = null,
        Func<string?, bool>? looksInstalled = null)
    {
        ArgumentNullException.ThrowIfNull(rootResolver);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(checker);
        _rootResolver = rootResolver;
        _catalog = catalog;
        _checker = checker;
        _unifiedLogger = unifiedLogger;
        _looksInstalled = looksInstalled ?? ManagedEngineLocator.LooksInstalled;
    }

    public BackendKind Kind => BackendKind.Engine;

    public string DisplayName => "Diffusion Nexus Engine";

    public async Task<FeatureReadinessResult> CheckFeatureAsync(Feature feature, CancellationToken ct = default)
    {
        var row = EngineFeatureCatalog.ForAppFeature(feature);
        if (row is null)
        {
            var label = feature == Feature.OutpaintVision ? "Outpaint Vision" : feature.ToString();
            return NotReady(feature, isOnline: true, [$"{label} is not available on the Diffusion Nexus Engine yet"]);
        }

        var root = await _rootResolver.ResolveAsync(ct);
        if (!_looksInstalled(root))
        {
            Emit($"{feature}: Engine not installed.");
            return NotReady(feature, isOnline: false, ["Diffusion Nexus Engine is not installed"]);
        }

        var missing = new List<string>();
        foreach (var workloadId in EngineFeatureCatalog.Get(row.Value).WorkloadIds)
        {
            var configuration = await _catalog.GetWorkloadAsync(workloadId, ct);
            if (configuration is null)
            {
                missing.Add($"Workload {workloadId} is missing from the catalog. Update the catalog and try again.");
                continue;
            }

            var check = await _checker.CheckConfigurationAsync(configuration, root!, options: null, ct);
            missing.AddRange(check.CustomNodeResults.Where(n => !n.IsInstalled)
                .Select(n => $"Custom node missing on the Engine: {n.Name}"));
            missing.AddRange(check.ModelResults.Where(m => !m.IsInstalled)
                .Select(m => $"Model missing on the Engine: {m.Name}"));
        }

        Emit(missing.Count == 0
            ? $"{feature}: ready on the Engine ({root})."
            : $"{feature}: {missing.Count} item(s) missing on the Engine ({root}).");

        return new FeatureReadinessResult
        {
            Feature = feature,
            Backend = BackendKind.Engine,
            IsBackendOnline = true,
            IsReady = missing.Count == 0,
            ActiveBackendName = DisplayName,
            MissingRequirements = missing,
            Warnings = [],
            Endpoint = root
        };
    }

    private FeatureReadinessResult NotReady(Feature feature, bool isOnline, IReadOnlyList<string> missing) => new()
    {
        Feature = feature,
        Backend = BackendKind.Engine,
        IsBackendOnline = isOnline,
        IsReady = false,
        ActiveBackendName = DisplayName,
        MissingRequirements = missing,
        Warnings = []
    };

    private void Emit(string message)
    {
        Logger.Information("Engine readiness: {Message}", message);
        _unifiedLogger?.Info(LogCategory.Configuration, LogSource, message);
    }
}
```

(`ModelCheckResult.IsInstalled` is already true for placeholders, so placeholders never block.)

- [ ] **Step 7: Register the backend and the mode source**

In `App.axaml.cs`, add after the `LocalInferenceFeatureBackend` registration (line ~798):

```csharp
        services.AddSingleton<IFeatureBackend>(sp =>
            new Services.Engine.EngineFeatureBackend(
                sp.GetRequiredService<Services.Engine.IEngineRootResolver>(),
                sp.GetRequiredService<ICatalog>(),
                sp.GetRequiredService<IConfigurationCheckerService>(),
                sp.GetService<Domain.Services.UnifiedLogging.IUnifiedLogger>()));
```

Replace the router registration with:

```csharp
        services.AddSingleton<IFeatureBackendRouter>(sp =>
        {
            var scopes = sp.GetRequiredService<IServiceScopeFactory>();
            return new FeatureBackendRouter(
                sp.GetServices<IFeatureBackend>(),
                serverMode: () =>
                {
                    // Fresh scope per call: the Outpaint panel resolves Outpaint and OutpaintVision in
                    // parallel, and two reads on one DbContext throw.
                    using var scope = scopes.CreateScope();
                    return scope.ServiceProvider.GetRequiredService<IAppSettingsService>()
                        .GetSettingsAsync().GetAwaiter().GetResult().ComfyUiServerMode;
                });
        });
```

`Resolve` is synchronous today; `GetSettingsAsync` uses `ConfigureAwait(false)` throughout, so blocking on it does not deadlock the UI thread, and it is a small single-row query. Do not cache the value: a cached mode would miss a switch in Settings, and a cache refreshed from the `SettingsSaved` event would race the readiness re-check that the same event triggers.

- [ ] **Step 8: Run the tests and build**

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~FeatureBackendRouter|FullyQualifiedName~EngineFeatureBackendTests|FullyQualifiedName~FeatureReadiness"` and `dotnet build E:\Repos\DiffusionNexus\DiffusionNexus.sln -c Debug`.
Expected: PASS; build succeeds.

- [ ] **Step 9: Commit and push**

```powershell
git add DiffusionNexus.Domain DiffusionNexus.UI DiffusionNexus.Tests
git commit -m "feat(engine): #606 Engine readiness backend; router follows the server mode" -m "Inpainting, Outpaint and OutpaintVision resolve to the Engine or ComfyUI per Settings. The Engine backend checks only the Engine's folder and never starts it." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 6: Engine Features dialog

**Files:**
- Create: `DiffusionNexus.UI/ViewModels/EngineFeatureRowViewModel.cs`
- Create: `DiffusionNexus.UI/ViewModels/EngineFeaturesViewModel.cs`
- Create: `DiffusionNexus.UI/Views/Dialogs/EngineFeaturesDialog.axaml` + `.axaml.cs`
- Modify: `DiffusionNexus.UI/Services/DatasetEventAggregator.cs` (new event)
- Modify: `DiffusionNexus.UI/ViewModels/InstallerManagerViewModel.cs` (`IsEngineTileVisible` default, Engine branch of `OnWorkloadsRequestedAsync`, new `OpenEngineFeaturesAsync`, dialog presenter seam)
- Modify: `DiffusionNexus.UI/Views/InstallerManagerView.axaml:386` ("Workloads" → "Features" on the Engine button only)
- Modify: `DiffusionNexus.UI/App.axaml.cs:1252-1264` (remove Canvas coupling) and the navigation block (~1385) for the new event
- Test: `DiffusionNexus.Tests/Engine/EngineFeaturesViewModelTests.cs` (new); update `DiffusionNexus.Tests/InstallerManager/EngineWorkloadsRequestTests.cs`

**Interfaces:**
- Consumes: `EngineFeatureCatalog`, `EngineFeature` (Task 3); `ICatalog`, `IConfigurationCheckerService`, `IWorkloadInstallService.InstallSelectedAsync(...)`, `IResourceMonitorService.GetSnapshotAsync(ct)` (existing).
- Produces:
  - `enum EngineFeatureStatus { Checking, Installed, Partial, NotInstalled, Installing, Error }`
  - `EngineFeatureRowViewModel` with `Definition`, `DisplayName`, `Description`, `IsSelected`, `IsSelectable`, `Status`, `StatusText`, `NeedsText`.
  - `EngineFeaturesViewModel(ICatalog catalog, IConfigurationCheckerService checker, IWorkloadInstallService installer, string engineRoot, IResourceMonitorService? resourceMonitor = null, IUnifiedLogger? unifiedLogger = null, EngineFeature? preselect = null, Func<string, long?>? freeSpaceProbe = null)` with `Rows`, `IsLoading`, `IsInstalling`, `ProgressText`, `FooterText`, `DidInstall`, `LoadCommand`, `InstallSelectedCommand`, `CancelInstallCommand`.
  - `NavigateToEngineFeaturesEventArgs { EngineFeature? Preselect }`; `IDatasetEventAggregator.NavigateToEngineFeaturesRequested` + `PublishNavigateToEngineFeatures(...)`.
  - `InstallerManagerViewModel.OpenEngineFeaturesAsync(EngineFeature? preselect)`; `internal Func<EngineFeaturesViewModel, Task>? EngineFeaturesDialogPresenter`.

- [ ] **Step 1: Write the failing view-model tests**

`DiffusionNexus.Tests/Engine/EngineFeaturesViewModelTests.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Engine;

public class EngineFeaturesViewModelTests
{
    private const string Root = @"C:\Engine\ComfyUI";
    private readonly Mock<ICatalog> _catalog = new();
    private readonly Mock<IConfigurationCheckerService> _checker = new();
    private readonly Mock<IWorkloadInstallService> _installer = new();
    private readonly Dictionary<Guid, ConfigurationCheckResult> _state = new();

    public EngineFeaturesViewModelTests()
    {
        foreach (var id in EngineFeatureCatalog.AllWorkloadIds)
        {
            var config = new InstallationConfiguration { Id = id, Name = id.ToString() };
            config.Vram.VramProfiles = "8,16,24";
            _catalog.Setup(c => c.GetWorkloadAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(config);
        }

        _checker.Setup(c => c.CheckConfigurationAsync(It.IsAny<InstallationConfiguration>(), Root,
                It.IsAny<ConfigurationCheckOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InstallationConfiguration c, string _, ConfigurationCheckOptions? _, CancellationToken _) => _state[c.Id]);
    }

    private static ConfigurationCheckResult Result(int nodesMissing, int nodesPresent, int modelsMissing, int modelsPresent) => new()
    {
        OverallStatus = ConfigurationStatus.Partial,
        CustomNodesStatus = ConfigurationStatus.Partial,
        ModelsStatus = ConfigurationStatus.Partial,
        InstallationType = ComfyUIInstallationType.Manual,
        CustomNodeResults = Enumerable.Range(0, nodesMissing + nodesPresent).Select(i => new CustomNodeCheckResult
        {
            Id = Guid.NewGuid(), Name = $"node{i}", Url = "u", IsInstalled = i >= nodesMissing, ExpectedPath = "p"
        }).ToList(),
        ModelResults = Enumerable.Range(0, modelsMissing + modelsPresent).Select(i => new ModelCheckResult
        {
            Id = Guid.NewGuid(), Name = $"model{i}", IsInstalled = i >= modelsMissing, SearchedPaths = []
        }).ToList()
    };

    private EngineFeaturesViewModel Sut(EngineFeature? preselect = null) =>
        new(_catalog.Object, _checker.Object, _installer.Object, Root, preselect: preselect,
            freeSpaceProbe: _ => 412L * 1024 * 1024 * 1024);

    private EngineFeatureRowViewModel Row(EngineFeaturesViewModel vm, EngineFeature f) =>
        vm.Rows.Single(r => r.Definition.Feature == f);

    [Fact]
    public async Task Load_DerivesStatusPerRow()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 3, 2);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut();

        await vm.LoadCommand.ExecuteAsync(null);

        Row(vm, EngineFeature.InpaintOutpaint).Status.Should().Be(EngineFeatureStatus.Partial);
        Row(vm, EngineFeature.InpaintOutpaint).StatusText.Should().Be("Partial · 2 of 5 models");
        Row(vm, EngineFeature.InpaintOutpaint).NeedsText.Should().Be("1 node pack · 5 models");
        Row(vm, EngineFeature.Canvas).Status.Should().Be(EngineFeatureStatus.Installed);
        Row(vm, EngineFeature.Canvas).IsSelected.Should().BeTrue("installed rows are ticked");
        Row(vm, EngineFeature.Canvas).IsSelectable.Should().BeFalse("and locked");
    }

    [Fact]
    public async Task NothingPresent_IsNotInstalled()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 5, 0);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut();

        await vm.LoadCommand.ExecuteAsync(null);

        Row(vm, EngineFeature.InpaintOutpaint).Status.Should().Be(EngineFeatureStatus.NotInstalled);
        Row(vm, EngineFeature.InpaintOutpaint).StatusText.Should().Be("Not installed");
    }

    [Fact]
    public async Task Preselect_TicksThatRow_AndTheFooterCountsIt()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 5, 0);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut(EngineFeature.InpaintOutpaint);

        await vm.LoadCommand.ExecuteAsync(null);

        Row(vm, EngineFeature.InpaintOutpaint).IsSelected.Should().BeTrue();
        vm.FooterText.Should().Be("Selected: 1 feature · 412 GB free on C:\\");
        vm.InstallSelectedCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Install_PassesOnlyTheMissingItems_FromAFreshCheck_AndSkipsInstalledRows()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 3, 2);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut(EngineFeature.InpaintOutpaint);
        await vm.LoadCommand.ExecuteAsync(null);

        // Between load and install, one model arrived (e.g. installed by another surface).
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 2, 3);

        IReadOnlyList<CustomNodeCheckResult>? nodes = null;
        IReadOnlyList<ModelCheckResult>? models = null;
        _installer.Setup(i => i.InstallSelectedAsync(It.IsAny<InstallationConfiguration>(), Root,
                It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
                It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
                It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()))
            .Callback((InstallationConfiguration _, string _, IReadOnlyList<CustomNodeCheckResult> n, IReadOnlyList<ModelCheckResult> m,
                int _, IProgress<WorkloadInstallProgress>? _, IProgress<DownloadProgress>? _, Func<CancellationToken>? _, CancellationToken _) =>
            { nodes = n; models = m; })
            .ReturnsAsync("done");

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        _installer.Verify(i => i.InstallSelectedAsync(
            It.Is<InstallationConfiguration>(c => c.Id == EngineFeatureCatalog.InpaintingQwen2512), Root,
            It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
            It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
            It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()), Times.Once);
        _installer.Verify(i => i.InstallSelectedAsync(
            It.Is<InstallationConfiguration>(c => c.Id == EngineFeatureCatalog.Krea2Turbo), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
            It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
            It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()), Times.Never, "installed rows are not reinstalled");
        nodes.Should().HaveCount(1).And.OnlyContain(n => !n.IsInstalled);
        models.Should().HaveCount(2, "the model that arrived after load must not be downloaded again")
            .And.OnlyContain(m => !m.IsInstalled);
        vm.DidInstall.Should().BeTrue();
    }

    [Fact]
    public async Task Install_RechecksEveryRowAfterwards()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 5, 0);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut(EngineFeature.InpaintOutpaint);
        await vm.LoadCommand.ExecuteAsync(null);
        _installer.Setup(i => i.InstallSelectedAsync(It.IsAny<InstallationConfiguration>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
                It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
                It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(0, 1, 0, 5))
            .ReturnsAsync("done");

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Row(vm, EngineFeature.InpaintOutpaint).Status.Should().Be(EngineFeatureStatus.Installed);
        vm.IsInstalling.Should().BeFalse();
    }

    [Fact]
    public async Task InstallFailure_MarksTheRowAsError_AndEndsTheInstall()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 5, 0);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut(EngineFeature.InpaintOutpaint);
        await vm.LoadCommand.ExecuteAsync(null);
        _installer.Setup(i => i.InstallSelectedAsync(It.IsAny<InstallationConfiguration>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
                It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
                It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("HF returned 503"));

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        vm.IsInstalling.Should().BeFalse();
        vm.ProgressText.Should().Contain("HF returned 503");
        Row(vm, EngineFeature.InpaintOutpaint).Status.Should().Be(EngineFeatureStatus.NotInstalled,
            "the re-check after a failed install shows what is really on disk");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EngineFeaturesViewModelTests"`
Expected: compile error — `EngineFeaturesViewModel` does not exist.

- [ ] **Step 3: Write the row view model**

`DiffusionNexus.UI/ViewModels/EngineFeatureRowViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using DiffusionNexus.UI.Services.Engine;

namespace DiffusionNexus.UI.ViewModels;

public enum EngineFeatureStatus { Checking, Installed, Partial, NotInstalled, Installing, Error }

/// <summary>One row of the Engine Features dialog.</summary>
public sealed partial class EngineFeatureRowViewModel : ObservableObject
{
    public EngineFeatureRowViewModel(EngineFeatureDefinition definition)
    {
        Definition = definition;
    }

    public EngineFeatureDefinition Definition { get; }
    public string DisplayName => Definition.DisplayName;
    public string Description => Definition.Description;

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectable))]
    private EngineFeatureStatus _status = EngineFeatureStatus.Checking;

    [ObservableProperty] private string _statusText = "Checking…";
    [ObservableProperty] private string _needsText = string.Empty;

    /// <summary>Installed rows stay ticked and locked; rows mid-check or mid-install are locked too.</summary>
    public bool IsSelectable => Status is EngineFeatureStatus.Partial or EngineFeatureStatus.NotInstalled or EngineFeatureStatus.Error;

    /// <summary>Latest check per workload, kept for the install step.</summary>
    internal List<(InstallationConfiguration Config, ConfigurationCheckResult Result)> Checks { get; } = [];

    /// <summary>Derives status, status text and the Needs column from <see cref="Checks"/>.</summary>
    internal void ApplyChecks()
    {
        var nodes = Checks.SelectMany(c => c.Result.CustomNodeResults).ToList();
        var models = Checks.SelectMany(c => c.Result.ModelResults).ToList();
        var missing = nodes.Count(n => !n.IsInstalled) + models.Count(m => !m.IsInstalled);
        var present = nodes.Count(n => n.IsInstalled) + models.Count(m => m.IsInstalled);

        NeedsText = $"{nodes.Count} node pack{(nodes.Count == 1 ? "" : "s")} · {models.Count} model{(models.Count == 1 ? "" : "s")}";

        if (missing == 0)
        {
            Status = EngineFeatureStatus.Installed;
            StatusText = "Installed";
            IsSelected = true;
        }
        else if (present == 0)
        {
            Status = EngineFeatureStatus.NotInstalled;
            StatusText = "Not installed";
        }
        else
        {
            Status = EngineFeatureStatus.Partial;
            StatusText = $"Partial · {models.Count(m => m.IsInstalled)} of {models.Count} models";
        }
    }
}
```

- [ ] **Step 4: Write the dialog view model**

`DiffusionNexus.UI/ViewModels/EngineFeaturesViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.Engine;
using Serilog;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// The Engine tile's Features dialog: tick app features, press Install selected, and the node packs
/// and models their catalog workloads declare are installed into the Engine — only what is missing.
/// </summary>
public sealed partial class EngineFeaturesViewModel : ViewModelBase
{
    private const string LogSource = "Diffusion Nexus Engine";
    private static readonly ILogger Logger = Log.ForContext<EngineFeaturesViewModel>();

    private readonly ICatalog _catalog;
    private readonly IConfigurationCheckerService _checker;
    private readonly IWorkloadInstallService _installer;
    private readonly string _engineRoot;
    private readonly IResourceMonitorService? _resourceMonitor;
    private readonly IUnifiedLogger? _unifiedLogger;
    private readonly EngineFeature? _preselect;
    private readonly Func<string, long?> _freeSpaceProbe;
    private CancellationTokenSource? _installCts;

    public EngineFeaturesViewModel(
        ICatalog catalog,
        IConfigurationCheckerService checker,
        IWorkloadInstallService installer,
        string engineRoot,
        IResourceMonitorService? resourceMonitor = null,
        IUnifiedLogger? unifiedLogger = null,
        EngineFeature? preselect = null,
        Func<string, long?>? freeSpaceProbe = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        _catalog = catalog;
        _checker = checker;
        _installer = installer;
        _engineRoot = engineRoot;
        _resourceMonitor = resourceMonitor;
        _unifiedLogger = unifiedLogger;
        _preselect = preselect;
        _freeSpaceProbe = freeSpaceProbe ?? ProbeFreeSpace;

        foreach (var definition in EngineFeatureCatalog.All)
        {
            var row = new EngineFeatureRowViewModel(definition);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(EngineFeatureRowViewModel.IsSelected) or nameof(EngineFeatureRowViewModel.Status))
                {
                    UpdateFooter();
                    InstallSelectedCommand.NotifyCanExecuteChanged();
                }
            };
            Rows.Add(row);
        }
    }

    public ObservableCollection<EngineFeatureRowViewModel> Rows { get; } = [];

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    private bool _isInstalling;

    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private string _footerText = string.Empty;

    /// <summary>True once an install ran, so the caller can re-sync model paths and refresh readiness.</summary>
    public bool DidInstall { get; private set; }

    private IEnumerable<EngineFeatureRowViewModel> RowsToInstall =>
        Rows.Where(r => r.IsSelected && r.IsSelectable);

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            await CheckAllAsync(CancellationToken.None);
            if (_preselect is { } pre)
            {
                var row = Rows.First(r => r.Definition.Feature == pre);
                if (row.IsSelectable) row.IsSelected = true;
            }
        }
        finally
        {
            IsLoading = false;
            UpdateFooter();
        }
    }

    private bool CanInstallSelected() => !IsInstalling && RowsToInstall.Any();

    [RelayCommand(CanExecute = nameof(CanInstallSelected))]
    private async Task InstallSelectedAsync()
    {
        IsInstalling = true;
        _installCts = new CancellationTokenSource();
        var ct = _installCts.Token;
        var rows = RowsToInstall.ToList();
        Info($"Installing {string.Join(", ", rows.Select(r => r.DisplayName))} into {_engineRoot}.");

        try
        {
            foreach (var row in rows)
            {
                row.Status = EngineFeatureStatus.Installing;
                row.StatusText = "Installing…";

                foreach (var workloadId in row.Definition.WorkloadIds)
                {
                    var config = await _catalog.GetWorkloadAsync(workloadId, ct)
                        ?? throw new InvalidOperationException($"Workload {workloadId} is missing from the catalog.");

                    // Fresh check right before installing: a file shared with an earlier workload in
                    // this run, or delivered since the dialog opened, must not be downloaded again.
                    var check = await _checker.CheckConfigurationAsync(config, _engineRoot, options: null, ct);
                    var nodes = check.CustomNodeResults.Where(n => !n.IsInstalled).ToList();
                    var models = check.ModelResults.Where(m => !m.IsInstalled).ToList();
                    if (nodes.Count == 0 && models.Count == 0)
                        continue;

                    var vramGb = await SuggestVramAsync(config.Vram.VramProfiles, ct);
                    ProgressText = $"{row.DisplayName}: installing {nodes.Count} node pack(s) and {models.Count} model(s)…";
                    Info(ProgressText);

                    var summary = await _installer.InstallSelectedAsync(
                        config, _engineRoot, nodes, models, vramGb,
                        new Progress<WorkloadInstallProgress>(p =>
                        {
                            ProgressText = $"{row.DisplayName}: {p.ItemName} — {p.Message}";
                            if (p.IsFailed) _unifiedLogger?.Warn(LogCategory.Installation, LogSource, ProgressText);
                        }),
                        new Progress<DownloadProgress>(d =>
                        {
                            if (d.IsActive && !d.IsComplete)
                                ProgressText = $"{row.DisplayName}: downloading {d.FileName} {d.DownloadedSizeText} / {d.TotalSizeText} {d.SpeedText}";
                        }),
                        skipDownloadTokenProvider: null,
                        ct);

                    DidInstall = true;
                    Info($"{row.DisplayName}: {summary}");
                }
            }

            ProgressText = "Done.";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Install cancelled.";
            Info(ProgressText);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Engine feature install failed");
            _unifiedLogger?.Error(LogCategory.Installation, LogSource, "Feature install failed", ex);
            ProgressText = $"Install failed: {ex.Message}";
        }
        finally
        {
            await CheckAllAsync(CancellationToken.None);
            _installCts.Dispose();
            _installCts = null;
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private void CancelInstall() => _installCts?.Cancel();

    private async Task CheckAllAsync(CancellationToken ct)
    {
        foreach (var row in Rows)
        {
            row.Checks.Clear();
            try
            {
                foreach (var workloadId in row.Definition.WorkloadIds)
                {
                    var config = await _catalog.GetWorkloadAsync(workloadId, ct);
                    if (config is null)
                        throw new InvalidOperationException($"Workload {workloadId} is missing from the catalog.");
                    row.Checks.Add((config, await _checker.CheckConfigurationAsync(config, _engineRoot, options: null, ct)));
                }

                row.ApplyChecks();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warning(ex, "Engine feature check failed for {Row}", row.DisplayName);
                row.Status = EngineFeatureStatus.Error;
                row.StatusText = "Check failed";
            }
        }
    }

    private async Task<int> SuggestVramAsync(string? vramProfiles, CancellationToken ct)
    {
        if (_resourceMonitor is null) return 0;
        var snapshot = await _resourceMonitor.GetSnapshotAsync(ct);
        return EngineFeatureCatalog.SuggestVramTier(snapshot.VramTotalMB, EngineFeatureCatalog.ParseVramProfiles(vramProfiles));
    }

    private void UpdateFooter()
    {
        var count = RowsToInstall.Count();
        var drive = Path.GetPathRoot(_engineRoot) ?? _engineRoot;
        var free = _freeSpaceProbe(_engineRoot);
        var freeText = free is { } bytes ? $" · {bytes / (1024L * 1024 * 1024)} GB free on {drive}" : string.Empty;
        FooterText = $"Selected: {count} feature{(count == 1 ? "" : "s")}{freeText}";
    }

    private static long? ProbeFreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; }
        catch { return null; }
    }

    private void Info(string message)
    {
        Logger.Information("Engine features: {Message}", message);
        _unifiedLogger?.Info(LogCategory.Installation, LogSource, message);
    }
}
```

Check `DownloadProgress` member names against `WorkloadDetailsDialog.axaml.cs:318-350` (`IsActive`, `IsComplete`, `FileName`, `DownloadedSizeText`, `TotalSizeText`, `SpeedText`) and `IResourceMonitorService.GetSnapshotAsync` (takes a `CancellationToken`; returns `ResourceSnapshot` with `VramTotalMB`). Adjust only if a name differs.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EngineFeaturesViewModelTests"`
Expected: PASS. (`Progress<T>` callbacks may arrive after the assertion; the tests do not assert on progress text except after a failure, which is set synchronously.)

- [ ] **Step 6: Write the dialog view**

`DiffusionNexus.UI/Views/Dialogs/EngineFeaturesDialog.axaml` (follows `WorkloadsDialog.axaml`'s window chrome and button styles):

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:DiffusionNexus.UI.ViewModels"
        x:Class="DiffusionNexus.UI.Views.Dialogs.EngineFeaturesDialog"
        x:DataType="vm:EngineFeaturesViewModel"
        Title="Diffusion Nexus Engine · Features"
        Width="860" Height="520"
        WindowStartupLocation="CenterOwner"
        Background="#1E1E1E">

    <Window.Styles>
        <Style Selector="Button.close-btn">
            <Setter Property="Background" Value="#333333"/>
            <Setter Property="Foreground" Value="White"/>
            <Setter Property="CornerRadius" Value="6"/>
            <Setter Property="Padding" Value="16,8"/>
        </Style>
        <Style Selector="Button.primary-btn">
            <Setter Property="Background" Value="#5E35B1"/>
            <Setter Property="Foreground" Value="White"/>
            <Setter Property="CornerRadius" Value="6"/>
            <Setter Property="Padding" Value="16,8"/>
        </Style>
    </Window.Styles>

    <Grid Margin="16" RowDefinitions="Auto,Auto,*,Auto,Auto">
        <TextBlock Grid.Row="0" Text="Diffusion Nexus Engine · Features" FontSize="18" FontWeight="Bold" Foreground="White"/>
        <TextBlock Grid.Row="1" Margin="0,4,0,12" FontSize="12" Opacity="0.7" TextWrapping="Wrap"
                   Text="Choose what the Engine should be able to do. The required node packs and models are installed into the Engine only. Shared files are downloaded once."/>

        <ScrollViewer Grid.Row="2">
            <ItemsControl ItemsSource="{Binding Rows}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate x:DataType="vm:EngineFeatureRowViewModel">
                        <Border BorderBrush="#2A2A2A" BorderThickness="0,0,0,1" Padding="4,10">
                            <Grid ColumnDefinitions="32,*,220,170">
                                <CheckBox Grid.Column="0" VerticalAlignment="Top"
                                          IsChecked="{Binding IsSelected, Mode=TwoWay}"
                                          IsEnabled="{Binding IsSelectable}"/>
                                <StackPanel Grid.Column="1" Spacing="2">
                                    <TextBlock Text="{Binding DisplayName}" FontWeight="SemiBold" Foreground="#EEE"/>
                                    <TextBlock Text="{Binding Description}" FontSize="11" Opacity="0.65" TextWrapping="Wrap"/>
                                </StackPanel>
                                <TextBlock Grid.Column="2" Text="{Binding NeedsText}" FontSize="11" Opacity="0.65"/>
                                <TextBlock Grid.Column="3" Text="{Binding StatusText}" FontWeight="SemiBold" Foreground="#CCC"/>
                            </Grid>
                        </Border>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </ScrollViewer>

        <StackPanel Grid.Row="3" Spacing="4" Margin="0,8,0,0" IsVisible="{Binding ProgressText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}">
            <ProgressBar IsIndeterminate="True" Height="4" IsVisible="{Binding IsInstalling}"/>
            <TextBlock Text="{Binding ProgressText}" FontSize="12" Opacity="0.8" TextWrapping="Wrap"/>
        </StackPanel>

        <Grid Grid.Row="4" ColumnDefinitions="*,Auto" Margin="0,12,0,0">
            <TextBlock Grid.Column="0" Text="{Binding FooterText}" FontSize="12" Opacity="0.7" VerticalAlignment="Center"/>
            <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="8">
                <Button Classes="close-btn" Content="Cancel" Command="{Binding CancelInstallCommand}" IsVisible="{Binding IsInstalling}"/>
                <Button Classes="close-btn" Content="Close" Click="OnClose" IsEnabled="{Binding !IsInstalling}"/>
                <Button Classes="primary-btn" Content="Install selected" Command="{Binding InstallSelectedCommand}"/>
            </StackPanel>
        </Grid>
    </Grid>
</Window>
```

`EngineFeaturesDialog.axaml.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>The Engine tile's Features dialog. All logic lives in <see cref="ViewModels.EngineFeaturesViewModel"/>.</summary>
public partial class EngineFeaturesDialog : Window
{
    public EngineFeaturesDialog()
    {
        AvaloniaXamlLoader.Load(this);
        // Closing mid-install would orphan the download; the Close button is disabled meanwhile,
        // and the window's own close box is refused the same way.
        Closing += (_, e) =>
        {
            if (DataContext is ViewModels.EngineFeaturesViewModel { IsInstalling: true })
                e.Cancel = true;
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
```

- [ ] **Step 7: Add the navigation event**

In `DatasetEventAggregator.cs`, next to `NavigateToSettingsEventArgs`:

```csharp
/// <summary>
/// Raised to open the Diffusion Nexus Engine's Features dialog, optionally with one row pre-ticked
/// (the editor's "Install Inpaint &amp; Outpaint" link).
/// </summary>
public sealed class NavigateToEngineFeaturesEventArgs : DatasetEventArgs
{
    public Services.Engine.EngineFeature? Preselect { get; init; }
}
```

In `IDatasetEventAggregator`, next to the Settings members:

```csharp
    event EventHandler<NavigateToEngineFeaturesEventArgs>? NavigateToEngineFeaturesRequested;
    void PublishNavigateToEngineFeatures(NavigateToEngineFeaturesEventArgs args);
```

In `DatasetEventAggregator`, next to the Settings implementations:

```csharp
    /// <inheritdoc/>
    public event EventHandler<NavigateToEngineFeaturesEventArgs>? NavigateToEngineFeaturesRequested;

    /// <inheritdoc/>
    public void PublishNavigateToEngineFeatures(NavigateToEngineFeaturesEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        RaiseEvent(NavigateToEngineFeaturesRequested, args);
    }
```

(Adjust the `Services.Engine.` prefix to the file's namespace: the file is in `DiffusionNexus.UI.Services`, so `Engine.EngineFeature?` also works.)

- [ ] **Step 8: Write the failing installer-manager tests**

In `EngineWorkloadsRequestTests.cs`, change both expected messages from `"Install the engine first — workloads are installed into it."` to `"Install the engine first — features are installed into it."`. Then add:

```csharp
    [Fact]
    public async Task EngineInstalled_OpensTheFeaturesDialog_ScopedToTheEngineRoot()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(root, "main.py"), "");
        try
        {
            var packages = new List<InstallerPackage>
            {
                new() { Id = 1, Name = "Diffusion Nexus Engine", InstallationPath = root, Type = InstallerType.ComfyUI, IsAppManaged = true }
            };
            var vm = EngineTestHarness.CreateInstallerManagerViewModel(packages: packages);
            EngineFeaturesViewModel? shown = null;
            vm.EngineFeaturesDialogPresenter = features => { shown = features; return Task.CompletedTask; };
            await vm.LoadInstallationsCommand.ExecuteAsync(null);

            await vm.InstallerCards.Single(c => c.IsEngine).ShowWorkloadsCommand.ExecuteAsync(null);

            shown.Should().NotBeNull();
            shown!.Rows.Select(r => r.DisplayName).Should().Equal("Inpaint & Outpaint", "Canvas · Krea 2 Turbo");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EngineTile_IsVisibleByDefault_WithoutTheCanvasSwitch()
    {
        var vm = EngineTestHarness.CreateInstallerManagerViewModel(packages: []);
        vm.IsEngineTileVisible.Should().BeTrue();
    }
```

Add `using DiffusionNexus.UI.ViewModels;`. Look at `EngineTestHarness.CreateInstallerManagerViewModel` for its optional parameters; if `catalogMock` defaults to a strict or null catalog, pass `catalogMock: new Mock<ICatalog>()` so `LoadCommand` inside the presenter path does not throw. If `ManagedEngineLocator.LooksInstalled` needs more than `main.py` (it does not today: directory + `main.py`), create what it needs.

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~DiffusionNexus.Tests.InstallerManager"`
Expected: FAIL — `EngineFeaturesDialogPresenter` does not exist; message differs; visibility default false.

- [ ] **Step 9: Wire the Installer Manager**

In `InstallerManagerViewModel.cs`:

1. `IsEngineTileVisible` field: `private bool _isEngineTileVisible = Services.Diffusion.DiffusionFeatureFlags.UseLocalDiffusionBackend;` and update its doc comment to say the tile is shown whenever the local-diffusion flag is on (no longer tied to the Canvas switch).
2. Add the presenter seam and the public opener:

```csharp
    /// <summary>
    /// Test seam: shows the Features dialog. Null in production, where a real
    /// <see cref="Views.Dialogs.EngineFeaturesDialog"/> is opened over the main window.
    /// </summary>
    internal Func<EngineFeaturesViewModel, Task>? EngineFeaturesDialogPresenter { get; set; }

    /// <summary>
    /// Opens the Engine's Features dialog, optionally with <paramref name="preselect"/> ticked. Called
    /// from the Engine tile and from the editor's "Install Inpaint &amp; Outpaint" link.
    /// </summary>
    public async Task OpenEngineFeaturesAsync(Services.Engine.EngineFeature? preselect = null)
    {
        var card = InstallerCards.FirstOrDefault(c => c.IsEngine);
        if (card is null || !card.IsEngineInstalled || string.IsNullOrWhiteSpace(card.InstallationPath))
        {
            await _dialogService.ShowMessageAsync("Diffusion Nexus Engine",
                "Install the engine first — features are installed into it.");
            return;
        }

        // The disk check behind this dialog resolves its search paths from the engine's own
        // extra_model_paths.yaml, so sync it before checking and again after installing.
        if (_engineModelPaths is not null)
            await _engineModelPaths.SyncAsync(card.InstallationPath);

        try
        {
            var vm = new EngineFeaturesViewModel(
                _catalog, _checkerService, _installService, card.InstallationPath,
                _resourceMonitor, _unifiedLogger, preselect);
            await vm.LoadCommand.ExecuteAsync(null);

            if (EngineFeaturesDialogPresenter is not null)
            {
                await EngineFeaturesDialogPresenter(vm);
            }
            else
            {
                var dialog = new Views.Dialogs.EngineFeaturesDialog { DataContext = vm };
                var parentWindow = (Avalonia.Application.Current?.ApplicationLifetime
                    as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (parentWindow is not null)
                    await dialog.ShowDialog(parentWindow);
            }

            if (vm.DidInstall && _engineModelPaths is not null)
                await _engineModelPaths.SyncAsync(card.InstallationPath);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to open the Engine Features dialog for {Path}", card.InstallationPath);
            await _dialogService.ShowMessageAsync("Error", $"Failed to load Engine features: {ex.Message}");
        }
    }
```

3. Replace the whole `if (card.IsEngine) { ... }` block in `OnWorkloadsRequestedAsync` with:

```csharp
        if (card.IsEngine)
        {
            await OpenEngineFeaturesAsync();
            return;
        }
```

In `InstallerManagerView.axaml`, change the Engine button's label at line ~386 (the one inside the button whose `IsVisible="{Binding ShowEngineWorkloadsButton}"`) from `Text="Workloads"` to `Text="Features"`. Leave the ordinary card's "Workloads" (line ~366) untouched.

In `App.axaml.cs`:
- Delete the block at ~1252-1264 that sets `installerManagerVm.IsEngineTileVisible` from `mainViewModel.IsDiffusionCanvasEnabled` and its `PropertyChanged` relay (and the comment above it).
- Hoist `installerManagerModule` so the navigation block can see it: declare `ModuleItem? installerManagerModule = null;` next to `InstallerManagerViewModel? installerManagerVm = null;` (~line 1088) and assign instead of declaring at ~1106.
- In the navigation block, after the `NavigateToSettingsRequested` handler, add:

```csharp
            eventAggregator.NavigateToEngineFeaturesRequested += (_, e) =>
            {
                if (installerManagerModule is not null)
                    mainViewModel.NavigateToModuleCommand.Execute(installerManagerModule);
                _ = installerManagerVm.OpenEngineFeaturesAsync(e.Preselect);
            };
```

- [ ] **Step 10: Run the tests and build**

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~DiffusionNexus.Tests.InstallerManager|FullyQualifiedName~DiffusionNexus.Tests.Engine"` and `dotnet build E:\Repos\DiffusionNexus\DiffusionNexus.sln -c Debug`.
Expected: PASS; build succeeds (compiled bindings validate the new XAML at build time).

- [ ] **Step 11: Commit and push**

```powershell
git add DiffusionNexus.UI DiffusionNexus.Tests
git commit -m "feat(engine): #606 Engine Features dialog replaces Workloads on the Engine tile" -m "Per-feature checklist with one Install selected button; installs only what a fresh check reports missing. The Engine tile no longer depends on the Canvas switch." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 7: Readiness panel — "Running on" / "Not installed on the Engine"

**Files:**
- Modify: `DiffusionNexus.UI/Services/DatasetEventAggregator.cs` (`NavigateToSettingsEventArgs.Section`, new `SettingsSection` enum)
- Modify: `DiffusionNexus.UI/ViewModels/FeatureReadinessViewModel.cs`
- Modify: `DiffusionNexus.UI/Views/Controls/FeatureReadinessPanel.axaml`
- Modify: `DiffusionNexus.UI/ViewModels/SettingsViewModel.cs` (`IsComfyUiServerExpanded`)
- Modify: `DiffusionNexus.UI/Views/SettingsView.axaml:635` + `SettingsView.axaml.cs` (expand + bring into view)
- Modify: `DiffusionNexus.UI/App.axaml.cs` (Settings handler honours the section)
- Test: `DiffusionNexus.Tests/ViewModels/FeatureReadinessViewModelBackendLineTests.cs` (new)

**Interfaces:**
- Consumes: `BackendKind.Engine`, `FeatureBackendRouter.ServerModeFeatures` (Task 5), `EngineFeatureCatalog.ForAppFeature/Get` (Task 3), `PublishNavigateToEngineFeatures` (Task 6).
- Produces:
  - `enum SettingsSection { ComfyUiServer }` (namespace `DiffusionNexus.UI.Services`); `NavigateToSettingsEventArgs.Section` (`SettingsSection?`).
  - `FeatureReadinessViewModel(IFeatureReadinessService? readinessService, Feature feature, IDatasetEventAggregator? eventAggregator = null)` with `IsEngineBackend`, `ShowBackendLine`, `ShowChangeLink`, `ShowInstallOnEngine`, `InstallOnEngineLabel`, `ChangeBackendCommand`, `InstallOnEngineCommand`.
  - `SettingsViewModel.IsComfyUiServerExpanded` (`[ObservableProperty] bool`).

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

public class FeatureReadinessViewModelBackendLineTests
{
    private readonly Mock<IFeatureReadinessService> _service = new();
    private readonly Mock<IDatasetEventAggregator> _events = new();

    private void Returns(Feature feature, BackendKind kind, string name, params string[] missing) =>
        _service.Setup(s => s.CheckAsync(feature, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FeatureReadinessResult
            {
                Feature = feature, Backend = kind, ActiveBackendName = name,
                IsBackendOnline = true, IsReady = missing.Length == 0,
                MissingRequirements = missing, Warnings = []
            });

    [Fact]
    public async Task ReadyOnTheEngine_ShowsRunningOn_WithTheChangeLink()
    {
        Returns(Feature.Inpainting, BackendKind.Engine, "Diffusion Nexus Engine");
        var vm = new FeatureReadinessViewModel(_service.Object, Feature.Inpainting, _events.Object);

        await vm.CheckReadinessAsync();

        vm.ShowBackendLine.Should().BeTrue();
        vm.ActiveBackendName.Should().Be("Diffusion Nexus Engine");
        vm.ShowChangeLink.Should().BeTrue();
        vm.ShowInstallOnEngine.Should().BeFalse();
    }

    [Fact]
    public async Task MissingOnTheEngine_OffersTheInstallLink_ForThatFeaturesRow()
    {
        Returns(Feature.Outpaint, BackendKind.Engine, "Diffusion Nexus Engine", "Model missing on the Engine: qwen_image_vae");
        var vm = new FeatureReadinessViewModel(_service.Object, Feature.Outpaint, _events.Object);

        await vm.CheckReadinessAsync();

        vm.ShowInstallOnEngine.Should().BeTrue();
        vm.InstallOnEngineLabel.Should().Be("Install Inpaint & Outpaint");

        vm.InstallOnEngineCommand.Execute(null);
        _events.Verify(e => e.PublishNavigateToEngineFeatures(
            It.Is<NavigateToEngineFeaturesEventArgs>(a => a.Preselect == EngineFeature.InpaintOutpaint)), Times.Once);
    }

    [Fact]
    public async Task OutpaintVisionOnTheEngine_HasNoInstallLink_BecauseNoRowOffersItYet()
    {
        Returns(Feature.OutpaintVision, BackendKind.Engine, "Diffusion Nexus Engine",
            "Outpaint Vision is not available on the Diffusion Nexus Engine yet");
        var vm = new FeatureReadinessViewModel(_service.Object, Feature.OutpaintVision, _events.Object);

        await vm.CheckReadinessAsync();

        vm.ShowInstallOnEngine.Should().BeFalse();
        vm.ShowChangeLink.Should().BeTrue("the user can still switch to their own ComfyUI");
    }

    [Fact]
    public void ChangeLink_OpensSettingsAtTheComfyUiServerSection()
    {
        var vm = new FeatureReadinessViewModel(_service.Object, Feature.Inpainting, _events.Object);

        vm.ChangeBackendCommand.Execute(null);

        _events.Verify(e => e.PublishNavigateToSettings(
            It.Is<NavigateToSettingsEventArgs>(a => a.Section == SettingsSection.ComfyUiServer)), Times.Once);
    }

    [Theory]
    [InlineData(Feature.Captioning)]
    [InlineData(Feature.BatchUpscale)]
    public async Task FeaturesTheDropdownDoesNotGovern_ShowTheirBackend_WithoutAChangeLink(Feature feature)
    {
        Returns(feature, BackendKind.ComfyUI, "ComfyUI");
        var vm = new FeatureReadinessViewModel(_service.Object, feature, _events.Object);

        await vm.CheckReadinessAsync();

        vm.ShowBackendLine.Should().BeTrue();
        vm.ShowChangeLink.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~FeatureReadinessViewModelBackendLineTests"`
Expected: compile errors — new members missing.

- [ ] **Step 3: Section hint on the Settings event**

In `DatasetEventAggregator.cs` replace the empty `NavigateToSettingsEventArgs` with:

```csharp
/// <summary>A part of the Settings page a navigation request can open directly.</summary>
public enum SettingsSection
{
    /// <summary>The "ComfyUI Server" expander (server dropdown + URL).</summary>
    ComfyUiServer
}

/// <summary>
/// Event raised when navigation to the Settings page is requested.
/// </summary>
public sealed class NavigateToSettingsEventArgs : DatasetEventArgs
{
    /// <summary>When set, Settings expands this section and scrolls it into view.</summary>
    public SettingsSection? Section { get; init; }
}
```

- [ ] **Step 4: Extend `FeatureReadinessViewModel`**

Add `using DiffusionNexus.UI.Services;` and `using DiffusionNexus.UI.Services.Engine;`. Change the constructor and add members:

```csharp
    private readonly IDatasetEventAggregator? _eventAggregator;
    private BackendKind? _activeBackendKind;

    public FeatureReadinessViewModel(
        IFeatureReadinessService? readinessService,
        Feature feature,
        IDatasetEventAggregator? eventAggregator = null)
    {
        _readinessService = readinessService;
        _feature = feature;
        _eventAggregator = eventAggregator;

        CheckReadinessCommand = new AsyncRelayCommand(CheckReadinessAsync);
        ChangeBackendCommand = new RelayCommand(() =>
            _eventAggregator?.PublishNavigateToSettings(new NavigateToSettingsEventArgs { Section = SettingsSection.ComfyUiServer }));
        InstallOnEngineCommand = new RelayCommand(() =>
        {
            if (EngineFeatureCatalog.ForAppFeature(_feature) is { } row)
                _eventAggregator?.PublishNavigateToEngineFeatures(new NavigateToEngineFeaturesEventArgs { Preselect = row });
        });

        FeatureDisplayName = readinessService?.GetRequirements(feature)?.DisplayName ?? feature.ToString();
    }

    /// <summary>True when the last check was answered by the Diffusion Nexus Engine.</summary>
    public bool IsEngineBackend => _activeBackendKind == BackendKind.Engine;

    /// <summary>Show the "Running on …" / "Not installed on the Engine" line once a check has answered.</summary>
    public bool ShowBackendLine => HasChecked && !IsChecking && !string.IsNullOrEmpty(ActiveBackendName);

    /// <summary>
    /// The "change" link only appears for features the Settings dropdown actually governs; elsewhere it
    /// would promise a switch that does nothing.
    /// </summary>
    public bool ShowChangeLink => FeatureBackendRouter.ServerModeFeatures.Contains(_feature);

    /// <summary>The Engine answered, something is missing, and an Engine row can install it.</summary>
    public bool ShowInstallOnEngine =>
        ShowBackendLine && IsEngineBackend && HasMissingRequirements && EngineFeatureCatalog.ForAppFeature(_feature) is not null;

    /// <summary>"Install Inpaint &amp; Outpaint" — the row's display name.</summary>
    public string? InstallOnEngineLabel =>
        EngineFeatureCatalog.ForAppFeature(_feature) is { } row ? $"Install {EngineFeatureCatalog.Get(row).DisplayName}" : null;

    /// <summary>Opens Settings at the ComfyUI Server section.</summary>
    public IRelayCommand ChangeBackendCommand { get; }

    /// <summary>Opens the Engine's Features dialog with this feature's row ticked.</summary>
    public IRelayCommand InstallOnEngineCommand { get; }
```

Raise change notifications for the derived properties. In `CheckReadinessAsync`, set `_activeBackendKind = result.Backend;` right after `ActiveBackendName = result.ActiveBackendName;`, and at the very end of the `finally` block (after `HasChecked = true;`) add:

```csharp
            OnPropertyChanged(nameof(IsEngineBackend));
            OnPropertyChanged(nameof(ShowBackendLine));
            OnPropertyChanged(nameof(ShowInstallOnEngine));
```

Also add `OnPropertyChanged(nameof(ShowBackendLine));` inside the `IsChecking` setter when the value changes, so the line hides while re-checking (change the setter to `{ if (SetProperty(ref _isChecking, value)) OnPropertyChanged(nameof(ShowBackendLine)); }`).

- [ ] **Step 5: Run the tests to verify they pass**

Same command as Step 2. Expected: PASS.

- [ ] **Step 6: Panel markup**

In `FeatureReadinessPanel.axaml`, directly after the status-row `Grid` (before the "Missing requirements detail" `Border`), add:

```xml
    <!-- Which backend answered, with the way to change it or to install what is missing on the Engine -->
    <Panel IsVisible="{Binding ShowBackendLine}">
      <StackPanel Orientation="Horizontal" Spacing="4" IsVisible="{Binding !ShowInstallOnEngine}">
        <TextBlock Text="Running on" FontSize="11" Opacity="0.7" VerticalAlignment="Center"/>
        <TextBlock Text="{Binding ActiveBackendName}" FontSize="11" FontWeight="SemiBold" VerticalAlignment="Center"/>
        <TextBlock Text="·" FontSize="11" Opacity="0.7" VerticalAlignment="Center" IsVisible="{Binding ShowChangeLink}"/>
        <Button Content="change" Command="{Binding ChangeBackendCommand}" IsVisible="{Binding ShowChangeLink}"
                Background="Transparent" BorderThickness="0" Padding="0" FontSize="11" Foreground="#8AB4F8"
                Cursor="Hand" ToolTip.Tip="Choose which ComfyUI runs this tool (Settings → ComfyUI Server)"/>
      </StackPanel>
      <StackPanel Orientation="Horizontal" Spacing="4" IsVisible="{Binding ShowInstallOnEngine}">
        <TextBlock Text="Not installed on the Engine" FontSize="11" FontWeight="SemiBold" Foreground="#FFAB91" VerticalAlignment="Center"/>
        <TextBlock Text="·" FontSize="11" Opacity="0.7" VerticalAlignment="Center"/>
        <Button Content="{Binding InstallOnEngineLabel}" Command="{Binding InstallOnEngineCommand}"
                Background="Transparent" BorderThickness="0" Padding="0" FontSize="11" Foreground="#8AB4F8"
                Cursor="Hand" ToolTip.Tip="Open the Engine's Features dialog with this feature ticked"/>
      </StackPanel>
    </Panel>
```

- [ ] **Step 7: Settings opens at the section**

`SettingsViewModel.cs`, next to `_isComfyUiServerOnline`:

```csharp
    /// <summary>Bound to the ComfyUI Server expander; set by navigation from the editor's "change" link.</summary>
    [ObservableProperty]
    private bool _isComfyUiServerExpanded;
```

`SettingsView.axaml:635`: give the expander a name and bind it:

```xml
        <Expander x:Name="ComfyUiServerExpander" Header="ComfyUI Server"
                  IsExpanded="{Binding IsComfyUiServerExpanded, Mode=TwoWay}" HorizontalAlignment="Stretch">
```

`SettingsView.axaml.cs`: when the view model raises `IsComfyUiServerExpanded` = true, bring the expander into view. Add to the constructor (after `InitializeComponent()` / the existing init):

```csharp
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.SettingsViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ViewModels.SettingsViewModel.IsComfyUiServerExpanded)
                        && vm.IsComfyUiServerExpanded)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(
                            () => this.FindControl<Avalonia.Controls.Expander>("ComfyUiServerExpander")?.BringIntoView(),
                            Avalonia.Threading.DispatcherPriority.Loaded);
                    }
                };
            }
        };
```

`App.axaml.cs`, Settings navigation handler:

```csharp
            eventAggregator.NavigateToSettingsRequested += (_, e) =>
            {
                mainViewModel.NavigateToModuleCommand.Execute(settingsModule);
                if (e.Section == SettingsSection.ComfyUiServer)
                {
                    // Reset first so a second "change" click re-triggers the scroll.
                    settingsVm.IsComfyUiServerExpanded = false;
                    settingsVm.IsComfyUiServerExpanded = true;
                }
            };
```

- [ ] **Step 8: Build and run the readiness tests**

Run: `dotnet build E:\Repos\DiffusionNexus\DiffusionNexus.sln -c Debug` then `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~FeatureReadiness|FullyQualifiedName~Settings"`.
Expected: build succeeds; PASS.

- [ ] **Step 9: Commit and push**

```powershell
git add DiffusionNexus.UI DiffusionNexus.Tests
git commit -m "feat(editor): #606 readiness panel shows the active backend with change / install links" -m "'Running on Diffusion Nexus Engine · change' opens Settings at ComfyUI Server; 'Not installed on the Engine · Install Inpaint & Outpaint' opens the Engine Features dialog with that row ticked." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 8: Settings — Server dropdown

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/SettingsViewModel.cs` (property, load at ~361, save at ~678, change handler, Engine status, test-connection gating, new constructor parameters)
- Modify: `DiffusionNexus.UI/Views/SettingsView.axaml:634-683`
- Modify: `DiffusionNexus.UI/App.axaml.cs:889` (pass the new dependencies)
- Test: `DiffusionNexus.Tests/ViewModels/SettingsViewModelServerModeTests.cs` (new)

**Interfaces:**
- Consumes: `ComfyUiServerMode` (Task 1), `IEngineRootResolver` (Task 2), `IManagedComfyUiEngine` (Task 2), `PublishNavigateToEngineFeatures` (Task 6).
- Produces: `SettingsViewModel.ComfyUiServerMode`, `ServerModeOptions`, `SelectedServerModeOption`, `IsCustomUrlMode`, `EngineStatusText`, `OpenEngineFeaturesCommand`; constructor gains trailing optional `IEngineRootResolver? engineRootResolver = null, IManagedComfyUiEngine? engine = null, Func<string?, bool>? looksInstalled = null`.

- [ ] **Step 1: Write the failing tests**

Model the construction on `SettingsViewModelSyncSettingsTests.cs:31` (copy its settings-service mock setup and `LoadSettingsAsync` call; it shows how `GetSettingsAsync` is mocked and how the VM is loaded). Then:

```csharp
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

public class SettingsViewModelServerModeTests
{
    private readonly Mock<IAppSettingsService> _settings = new();
    private readonly Mock<IEngineRootResolver> _root = new();
    private readonly Mock<IManagedComfyUiEngine> _engine = new();
    private readonly Mock<IDatasetEventAggregator> _events = new();

    private async Task<SettingsViewModel> LoadedAsync(ComfyUiServerMode mode, string? engineRoot = null)
    {
        _settings.Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings { Id = 1, ComfyUiServerMode = mode });
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(engineRoot);

        var vm = new SettingsViewModel(_settings.Object, new Mock<ISecureStorage>().Object,
            eventAggregator: _events.Object, engineRootResolver: _root.Object, engine: _engine.Object,
            looksInstalled: r => r is not null);
        await vm.LoadCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task Load_ReflectsTheStoredMode_AndDoesNotMarkChanges()
    {
        var vm = await LoadedAsync(ComfyUiServerMode.CustomUrl);

        vm.ComfyUiServerMode.Should().Be(ComfyUiServerMode.CustomUrl);
        vm.SelectedServerModeOption!.Mode.Should().Be(ComfyUiServerMode.CustomUrl);
        vm.IsCustomUrlMode.Should().BeTrue();
        vm.HasChanges.Should().BeFalse();
    }

    [Fact]
    public async Task PickingTheEngine_DisablesTheUrlRow_AndMarksChanges()
    {
        var vm = await LoadedAsync(ComfyUiServerMode.CustomUrl);

        vm.SelectedServerModeOption = vm.ServerModeOptions.Single(o => o.Mode == ComfyUiServerMode.Engine);

        vm.ComfyUiServerMode.Should().Be(ComfyUiServerMode.Engine);
        vm.IsCustomUrlMode.Should().BeFalse();
        vm.HasChanges.Should().BeTrue();
        vm.ServerModeOptions.Select(o => o.DisplayName).Should().Equal("Diffusion Nexus Engine", "Custom URL");
    }

    [Theory]
    [InlineData(null, false, "Not installed — install it in the Installation Manager")]
    [InlineData(@"C:\Engine\ComfyUI", false, "Installed · not running (starts on first use)")]
    [InlineData(@"C:\Engine\ComfyUI", true, "Installed · running")]
    public async Task EngineStatus_DescribesInstallAndRunState(string? root, bool running, string expected)
    {
        _engine.SetupGet(e => e.BaseUrl).Returns(running ? "http://127.0.0.1:51234" : null);

        var vm = await LoadedAsync(ComfyUiServerMode.Engine, root);

        vm.EngineStatusText.Should().Be(expected);
    }

    [Fact]
    public async Task OpenEngineFeatures_PublishesTheNavigation()
    {
        var vm = await LoadedAsync(ComfyUiServerMode.Engine);

        vm.OpenEngineFeaturesCommand.Execute(null);

        _events.Verify(e => e.PublishNavigateToEngineFeatures(It.IsAny<NavigateToEngineFeaturesEventArgs>()), Times.Once);
    }
}
```

The "installed" cases resolve a fake root, which `ManagedEngineLocator.LooksInstalled` would reject; the `looksInstalled` seam (same as the provider's) keeps the test off the disk. `LoadCommand` is the generated command for `SettingsViewModel.LoadAsync` (line 350), the same call `SettingsViewModelSyncSettingsTests` uses. If its load also needs other service calls mocked (folders, LoRA sources), copy those setups from `SettingsViewModelSyncSettingsTests`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelServerModeTests"`
Expected: compile errors.

- [ ] **Step 3: Implement the view-model side**

In `SettingsViewModel.cs`:

```csharp
    /// <summary>One entry of the ComfyUI Server dropdown.</summary>
    public sealed record ServerModeOption(ComfyUiServerMode Mode, string DisplayName);

    /// <summary>Dropdown entries, in display order.</summary>
    public IReadOnlyList<ServerModeOption> ServerModeOptions { get; } =
    [
        new(ComfyUiServerMode.Engine, "Diffusion Nexus Engine"),
        new(ComfyUiServerMode.CustomUrl, "Custom URL"),
    ];

    /// <summary>Which ComfyUI runs Inpaint and Outpaint.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomUrlMode))]
    private ComfyUiServerMode _comfyUiServerMode = ComfyUiServerMode.Engine;

    /// <summary>The URL row and Test Connection are only active for a custom URL.</summary>
    public bool IsCustomUrlMode => ComfyUiServerMode == ComfyUiServerMode.CustomUrl;

    /// <summary>Bound to the dropdown.</summary>
    public ServerModeOption? SelectedServerModeOption
    {
        get => ServerModeOptions.FirstOrDefault(o => o.Mode == ComfyUiServerMode);
        set
        {
            if (value is not null && value.Mode != ComfyUiServerMode)
            {
                ComfyUiServerMode = value.Mode;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>One line under the Engine option: installed / not installed / running.</summary>
    [ObservableProperty]
    private string _engineStatusText = string.Empty;

    partial void OnComfyUiServerModeChanged(ComfyUiServerMode value) => HasChanges = true;

    [RelayCommand]
    private void OpenEngineFeatures() =>
        _eventAggregator?.PublishNavigateToEngineFeatures(new NavigateToEngineFeaturesEventArgs());

    private async Task RefreshEngineStatusAsync()
    {
        if (_engineRootResolver is null)
        {
            EngineStatusText = string.Empty;
            return;
        }

        var root = await _engineRootResolver.ResolveAsync();
        EngineStatusText = !_looksInstalled(root)
            ? "Not installed — install it in the Installation Manager"
            : _engine?.BaseUrl is not null
                ? "Installed · running"
                : "Installed · not running (starts on first use)";
    }
```

Constructor: append `IEngineRootResolver? engineRootResolver = null, IManagedComfyUiEngine? engine = null, Func<string?, bool>? looksInstalled = null` and store them (`_looksInstalled = looksInstalled ?? ManagedEngineLocator.LooksInstalled;`). Add `using DiffusionNexus.UI.Services.Engine;` and `using DiffusionNexus.Domain.Enums;`.

Load (~line 361): next to `ComfyUiServerUrl = settings.ComfyUiServerUrl;` add `ComfyUiServerMode = settings.ComfyUiServerMode;` and `OnPropertyChanged(nameof(SelectedServerModeOption));`, then at the end of the load method — after the existing code that resets `HasChanges = false` — add `await RefreshEngineStatusAsync();`. Verify that the existing load resets `HasChanges` after mapping (it must, or every load of every other field would already mark changes); place the mode assignment before that reset.

Save (~line 678): next to `ComfyUiServerUrl = ComfyUiServerUrl,` add `ComfyUiServerMode = ComfyUiServerMode,`.

Test Connection: make the command's CanExecute depend on the mode — add `CanExecute = nameof(IsCustomUrlMode)` to its `[RelayCommand]` attribute and `[NotifyCanExecuteChangedFor(nameof(TestComfyUiConnectionCommand))]` to `_comfyUiServerMode`.

`App.axaml.cs:889`: append to the `SettingsViewModel` factory call the arguments `engineRootResolver: sp.GetService<Services.Engine.IEngineRootResolver>(), engine: sp.GetService<Services.Engine.IManagedComfyUiEngine>()` (use named arguments, since the existing call is positional).

- [ ] **Step 4: Run the tests to verify they pass**

Same command as Step 2. Expected: PASS.

- [ ] **Step 5: Settings markup**

Replace the inner `StackPanel` of the ComfyUI Server expander (`SettingsView.axaml:637-679`, everything inside `<Border Padding="16">`) with:

```xml
            <StackPanel Spacing="12">

              <TextBlock Text="Which ComfyUI runs Inpaint and Outpaint in the Image Editor."
                         FontSize="12" Opacity="0.7"/>

              <!-- Server -->
              <StackPanel Spacing="4">
                <TextBlock Text="Server" FontWeight="SemiBold"/>
                <ComboBox ItemsSource="{Binding ServerModeOptions}"
                          SelectedItem="{Binding SelectedServerModeOption, Mode=TwoWay}"
                          HorizontalAlignment="Stretch">
                  <ComboBox.ItemTemplate>
                    <DataTemplate x:DataType="vm:SettingsViewModel+ServerModeOption">
                      <TextBlock Text="{Binding DisplayName}"/>
                    </DataTemplate>
                  </ComboBox.ItemTemplate>
                </ComboBox>

                <!-- Engine selected -->
                <StackPanel Spacing="6" IsVisible="{Binding !IsCustomUrlMode}">
                  <TextBlock Text="The app installs and starts this ComfyUI itself. Choose what it can do in the Installation Manager → Diffusion Nexus Engine → Features."
                             FontSize="12" Opacity="0.7" TextWrapping="Wrap"/>
                  <StackPanel Orientation="Horizontal" Spacing="12">
                    <TextBlock Text="{Binding EngineStatusText}" FontSize="12" VerticalAlignment="Center"/>
                    <Button Content="Open Engine Features" Command="{Binding OpenEngineFeaturesCommand}"/>
                  </StackPanel>
                </StackPanel>

                <!-- Custom URL selected -->
                <TextBlock Text="A ComfyUI you run yourself. You install the required custom nodes and models."
                           FontSize="12" Opacity="0.7" TextWrapping="Wrap"
                           IsVisible="{Binding IsCustomUrlMode}"/>
              </StackPanel>

              <!-- Server URL -->
              <StackPanel Spacing="4" IsEnabled="{Binding IsCustomUrlMode}">
                <TextBlock Text="Server URL" FontWeight="SemiBold"/>
                <Grid ColumnDefinitions="Auto,*,Auto" RowDefinitions="Auto">
                  <Panel Grid.Column="0" Width="12" Height="12" VerticalAlignment="Center" Margin="0,0,8,0">
                    <Ellipse Width="12" Height="12" Fill="#2D7D46" IsVisible="{Binding IsComfyUiServerOnline}"/>
                    <Ellipse Width="12" Height="12" Fill="#FF6B6B" IsVisible="{Binding !IsComfyUiServerOnline}"/>
                  </Panel>
                  <TextBox Grid.Column="1" Text="{Binding ComfyUiServerUrl, Mode=TwoWay}" Watermark="http://127.0.0.1:8188/"/>
                  <Button Grid.Column="2" Content="Test Connection" Command="{Binding TestComfyUiConnectionCommand}"
                          IsEnabled="{Binding !IsTestingComfyUiConnection}" Margin="8,0,0,0"/>
                </Grid>
                <StackPanel Orientation="Horizontal" Spacing="8" IsVisible="{Binding IsTestingComfyUiConnection}">
                  <ProgressBar IsIndeterminate="True" Width="100" Height="4"/>
                  <TextBlock Text="Testing connection..." FontSize="12" Opacity="0.7"/>
                </StackPanel>
              </StackPanel>

            </StackPanel>
```

Check the `vm:` xmlns prefix at the top of `SettingsView.axaml`; if the file uses a different prefix for `DiffusionNexus.UI.ViewModels`, use it in the `DataTemplate`'s `x:DataType`. The "Open Installation Manager" button from the spec mockup is labelled "Open Engine Features" here because it opens the dialog directly via the Task 6 event, which navigates to the Installation Manager first.

- [ ] **Step 6: Build and run the Settings tests**

Run: `dotnet build E:\Repos\DiffusionNexus\DiffusionNexus.sln -c Debug` then `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~Settings"`.
Expected: build succeeds; PASS.

- [ ] **Step 7: Commit and push**

```powershell
git add DiffusionNexus.UI DiffusionNexus.Tests
git commit -m "feat(settings): #606 ComfyUI Server dropdown — Diffusion Nexus Engine or Custom URL" -m "The URL row is active only for Custom URL; the Engine option shows install/run state and opens the Engine Features dialog." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 9: Inpaint and Outpaint run through the provider

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/InpaintingViewModel.cs`
- Modify: `DiffusionNexus.UI/ViewModels/OutpaintingViewModel.cs`
- Modify: `DiffusionNexus.UI/ViewModels/ImageEditorViewModel.cs:418-441`
- Modify: `DiffusionNexus.UI/ViewModels/Tabs/ImageEditTabViewModel.cs:50, 292, 303, 310`
- Modify: `DiffusionNexus.UI/ViewModels/LoraDatasetHelperViewModel.cs:227, 266`
- Modify: `DiffusionNexus.UI/App.axaml.cs:1006-1028`
- Modify: `DiffusionNexus.Tests/ViewModels/InpaintingViewModelGGUFResolutionTests.cs`
- Test: `DiffusionNexus.Tests/ViewModels/EditorEngineGenerateTests.cs` (new)

**Interfaces:**
- Consumes: `IComfyUiClientProvider`, `ComfyUiClientLease`, `ComfyUiUnavailableException` (Task 4); `FeatureReadinessViewModel(service, feature, eventAggregator)` (Task 7); `IDatasetEventAggregator.SettingsSaved` (existing).
- Produces:
  - `InpaintingViewModel(Func<bool> hasImage, Action<string> deactivateOtherTools, IComfyUiClientProvider? comfyUiClientProvider, IDatasetEventAggregator? eventAggregator, IFeatureReadinessService? readinessService = null, IUnifiedLogger? unifiedLogger = null)`.
  - `OutpaintingViewModel(Func<bool> hasImage, Func<int> getImageWidth, Func<int> getImageHeight, Action<string> deactivateOtherTools, IComfyUiClientProvider? comfyUiClientProvider = null, IFeatureReadinessService? readinessService = null, IUnifiedLogger? unifiedLogger = null, IDatasetEventAggregator? eventAggregator = null)`.
  - `ImageEditorViewModel(..., IComfyUiClientProvider? comfyUiClientProvider = null, ...)` replacing `IComfyUIWrapperService? comfyUiService` in the same position; same for `ImageEditTabViewModel` (4th parameter).
  - `LoraDatasetHelperViewModel(..., ICivitaiBaseModelCatalog? baseModelCatalog = null, IComfyUiClientProvider? comfyUiClientProvider = null)` — new trailing parameter; the existing `comfyUiService` stays for Batch Upscale.

- [ ] **Step 1: Port the existing GGUF tests to the provider**

In `InpaintingViewModelGGUFResolutionTests.cs`, replace the constructor's `comfyUiService: _comfyMock.Object,` with:

```csharp
            comfyUiClientProvider: Provider(_comfyMock.Object),
```

and add to the class:

```csharp
    internal static IComfyUiClientProvider Provider(IComfyUIWrapperService client,
        ComfyUiServerMode mode = ComfyUiServerMode.CustomUrl)
    {
        var provider = new Mock<IComfyUiClientProvider>();
        provider.Setup(p => p.AcquireAsync(It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ComfyUiClientLease(client, mode, "http://test", ownsClient: false));
        return provider.Object;
    }
```

Add `using DiffusionNexus.Domain.Enums;`.

- [ ] **Step 2: Write the failing generate tests**

`DiffusionNexus.Tests/ViewModels/EditorEngineGenerateTests.cs`:

```csharp
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

public class EditorEngineGenerateTests
{
    private static string TempImage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dn-test-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        return path;
    }

    private static Mock<IComfyUiClientProvider> Unavailable(string message)
    {
        var provider = new Mock<IComfyUiClientProvider>();
        provider.Setup(p => p.AcquireAsync(It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ComfyUiUnavailableException(message));
        return provider;
    }

    [Fact]
    public async Task Inpaint_EngineFailsToStart_ShowsTheReason_AndIsNotLeftBusy()
    {
        var messages = new List<string?>();
        var vm = new InpaintingViewModel(() => true, _ => { },
            Unavailable("Diffusion Nexus Engine failed to start: exit code 1").Object, eventAggregator: null);
        vm.StatusMessageChanged += (_, m) => messages.Add(m);

        await vm.ProcessInpaintAsync(TempImage());

        vm.HasError.Should().BeTrue();
        vm.IsBusy.Should().BeFalse();
        vm.ProgressDisplayText.Should().Be("Diffusion Nexus Engine failed to start: exit code 1");
        messages.Should().Contain("Diffusion Nexus Engine failed to start: exit code 1");
    }

    [Fact]
    public async Task Outpaint_EngineNotInstalled_ShowsTheReason_AndIsNotLeftBusy()
    {
        var vm = new OutpaintingViewModel(() => true, () => 512, () => 512, _ => { },
            Unavailable("Diffusion Nexus Engine is not installed. Install it in the Installation Manager.").Object);

        await vm.ProcessOutpaintAsync(TempImage(), useVision: false, 64, 0, 64, 0);

        vm.HasError.Should().BeTrue();
        vm.IsBusy.Should().BeFalse();
        vm.ProgressDisplayText.Should().StartWith("Diffusion Nexus Engine is not installed");
    }

    [Fact]
    public async Task Inpaint_FailureAfterTheEngineStarted_UsesTheEngineWording()
    {
        var client = new Mock<IComfyUIWrapperService>();
        client.Setup(c => c.UploadImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var vm = new InpaintingViewModel(() => true, _ => { },
            InpaintingViewModelGGUFResolutionTests.Provider(client.Object, ComfyUiServerMode.Engine), eventAggregator: null);

        await vm.ProcessInpaintAsync(TempImage());

        vm.ProgressDisplayText.Should().Be("Generation failed – is the Diffusion Nexus Engine running?");
    }

    [Fact]
    public async Task SwitchingTheServerInSettings_WhileThePanelIsOpen_RechecksReadiness()
    {
        var events = new Mock<IDatasetEventAggregator>();
        var readiness = new Mock<IFeatureReadinessService>();
        readiness.Setup(r => r.CheckAsync(Feature.Inpainting, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FeatureReadinessResult
            {
                Feature = Feature.Inpainting, Backend = BackendKind.Engine, ActiveBackendName = "Diffusion Nexus Engine",
                IsBackendOnline = true, IsReady = true, MissingRequirements = [], Warnings = []
            });
        var vm = new InpaintingViewModel(() => true, _ => { }, comfyUiClientProvider: null, events.Object, readiness.Object);
        vm.IsPanelOpen = true;
        await Task.Delay(50); // the open-panel check is fire-and-forget

        events.Raise(e => e.SettingsSaved += null, events.Object, new SettingsSavedEventArgs());
        await Task.Delay(50);

        readiness.Verify(r => r.CheckAsync(Feature.Inpainting, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SettingsSaved_WithThePanelClosed_DoesNotCheck()
    {
        var events = new Mock<IDatasetEventAggregator>();
        var readiness = new Mock<IFeatureReadinessService>();
        _ = new OutpaintingViewModel(() => true, () => 512, () => 512, _ => { },
            readinessService: readiness.Object, eventAggregator: events.Object);

        events.Raise(e => e.SettingsSaved += null, events.Object, new SettingsSavedEventArgs());
        await Task.Delay(50);

        readiness.Verify(r => r.CheckAsync(It.IsAny<Feature>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
```

If `InpaintingViewModel.IsBusy` / `OutpaintingViewModel.IsBusy` / `HasError` / `ProgressDisplayText` are not public getters, they are bound from XAML, so they are; confirm by name. `ProcessOutpaintAsync`'s parameter order is `(imagePath, useVision, extendLeft, extendTop, extendRight, extendBottom)`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EditorEngineGenerateTests|FullyQualifiedName~InpaintingViewModelGGUFResolutionTests"`
Expected: compile errors — constructor parameter types differ.

- [ ] **Step 4: Move `InpaintingViewModel` onto the provider**

1. Field: replace `private readonly IComfyUIWrapperService? _comfyUiService;` with
   `private readonly IComfyUiClientProvider? _clientProvider;` and add `private readonly IUnifiedLogger? _unifiedLogger;` plus `private const string LogSource = "Inpaint";` (add `using DiffusionNexus.Domain.Services.UnifiedLogging;`).
2. Constructor signature per **Interfaces**; assign `_clientProvider = comfyUiClientProvider; _unifiedLogger = unifiedLogger;`. Build the readiness view model with the aggregator: `Readiness = new FeatureReadinessViewModel(readinessService, Feature.Inpainting, eventAggregator);`. At the end of the constructor subscribe:

```csharp
        // The Settings dropdown decides which ComfyUI this tool runs on. Re-check when it is saved so
        // the "Running on …" line follows a switch without reopening the tool.
        if (_eventAggregator is not null)
        {
            _eventAggregator.SettingsSaved += (_, _) =>
            {
                if (IsPanelOpen) _ = RunReadinessCheckAsync();
            };
        }
```

3. In `ExecuteGenerateAsync` and `ExecuteGenerateAndCompareAsync`, replace each `if (_comfyUiService is null)` guard with `if (_clientProvider is null)` (same message).
4. Rewrite `ProcessInpaintAsync`'s guard, try, catches and finally. The guard becomes `if (_clientProvider is null) { ... }` with the same message. The body becomes:

```csharp
        ComfyUiClientLease? lease = null;
        try
        {
            Emit("Generate requested.");
            lease = await _clientProvider.AcquireAsync(new Progress<string>(msg => Status = msg));
            var comfy = lease.Client;
            Emit($"Running on {(lease.Mode == ComfyUiServerMode.Engine ? "the Diffusion Nexus Engine" : "your own ComfyUI")} at {lease.BaseUrl}.");

            Status = "Uploading image to ComfyUI...";
            var uploadedFilename = await comfy.UploadImageAsync(maskedImagePath);
            Emit($"Image uploaded as {uploadedFilename}.");

            Status = "Checking available models...";
            var resolvedUnetName = await ResolveQwenImageGGUFModelAsync(comfy);
            // ... unchanged from here, except every `_comfyUiService.` becomes `comfy.` ...
```

   After `QueueWorkflowAsync` returns add `Emit($"Prompt queued ({promptId}).");`; after `DownloadImageAsync` add `Emit("Result received.");`. Replace the catch/finally section with:

```csharp
        catch (ComfyUiUnavailableException ex)
        {
            HasError = true;
            ProgressDisplayText = ex.Message;
            StatusMessageChanged?.Invoke(this, ex.Message);
            _unifiedLogger?.Warn(LogCategory.General, LogSource, ex.Message);
        }
        catch (OperationCanceledException)
        {
            StatusMessageChanged?.Invoke(this, "Inpainting was cancelled.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Inpainting failed");
            _unifiedLogger?.Error(LogCategory.General, LogSource, "Inpainting failed", ex);
            HasError = true;
            ProgressDisplayText = lease?.Mode == ComfyUiServerMode.Engine
                ? "Generation failed – is the Diffusion Nexus Engine running?"
                : "Generation failed – is ComfyUI running?";
            StatusMessageChanged?.Invoke(this, $"Inpainting failed: {ex.Message}");
        }
        finally
        {
            lease?.Dispose();
            OnFinished();
        }
```

   Add the helper:

```csharp
    private void Emit(string message)
    {
        Logger.Information("Inpaint: {Message}", message);
        _unifiedLogger?.Info(LogCategory.General, LogSource, message);
    }
```

5. `ResolveQwenImageGGUFModelAsync` takes the client: change its signature to `private async Task<string?> ResolveQwenImageGGUFModelAsync(IComfyUIWrapperService comfy)`, delete its `if (_comfyUiService is null) return null;` guard, and use `comfy.GetNodeInputOptionsAsync(...)`.
6. Check `OnFinished()` still resets everything `IsBusy` depends on; the early returns inside the try (no GGUF model, workflow missing) already call `OnFinished()` and then `return`, which now also runs `finally` — `OnFinished()` twice is harmless (it only clears flags), but remove the inner `OnFinished();` calls before those `return`s so it runs exactly once.

- [ ] **Step 5: Move `OutpaintingViewModel` onto the provider**

Same pattern:

1. Field `_comfyUiService` → `private readonly IComfyUiClientProvider? _clientProvider;`; add `private readonly IDatasetEventAggregator? _eventAggregator;`.
2. Constructor per **Interfaces**; both readiness view models get the aggregator: `new FeatureReadinessViewModel(readinessService, Feature.Outpaint, eventAggregator)` and `new FeatureReadinessViewModel(readinessService, Feature.OutpaintVision, eventAggregator)`. Subscribe at the end of the constructor:

```csharp
        if (_eventAggregator is not null)
        {
            _eventAggregator.SettingsSaved += (_, _) =>
            {
                if (IsPanelOpen) _ = RunReadinessChecksAsync();
            };
        }
```

3. `ExecuteGenerateAsync`'s guard (line ~612): `if (_clientProvider is null)`.
4. `ProcessOutpaintAsync`: guard → `_clientProvider`; acquire the lease at the top of the try exactly as in Step 4 (log through the existing `EmitInfo`, source `"Outpaint"`); every `_comfyUiService.` → `comfy.`; `ResolveQwenImageGGUFModelAsync(comfy)` with the same signature change as Step 4.5; add the `ComfyUiUnavailableException` catch before `OperationCanceledException`, the mode-aware failure text in the generic catch, and `lease?.Dispose();` before `OnFinished();` in `finally`. Remove inner `OnFinished();` calls before early `return`s inside the try, as in Step 4.6. Emit "Prompt queued (id)" and "Result received." like Inpaint.

- [ ] **Step 6: Thread the provider through the constructor chain**

- `ImageEditorViewModel.cs:421`: `IComfyUIWrapperService? comfyUiService = null,` → `IComfyUiClientProvider? comfyUiClientProvider = null,`. Lines 440-441:

```csharp
        Inpainting = new InpaintingViewModel(() => HasImage, DeactivateOtherTools, comfyUiClientProvider, eventAggregator, readinessService, unifiedLogger);
        Outpainting = new OutpaintingViewModel(() => HasImage, () => ImageWidth, () => ImageHeight, DeactivateOtherTools, comfyUiClientProvider, readinessService, unifiedLogger, eventAggregator);
```

- `ImageEditTabViewModel.cs`: field `_comfyUiService` → `private readonly IComfyUiClientProvider? _comfyUiClientProvider;`; 4th constructor parameter `IComfyUIWrapperService? comfyUiService = null` → `IComfyUiClientProvider? comfyUiClientProvider = null`; assignment and the `new ImageEditorViewModel(...)` call pass `_comfyUiClientProvider` in the same position.
- `LoraDatasetHelperViewModel.cs`: append `IComfyUiClientProvider? comfyUiClientProvider = null` as the last constructor parameter (after `baseModelCatalog`), add a `<param>` doc line, and in the `new ImageEditTabViewModel(...)` call at ~266 pass `comfyUiClientProvider` instead of `comfyUiService`. `BatchUpscaleTabViewModel` at ~269 keeps `comfyUiService`.
- `App.axaml.cs:1028`: after `sp.GetService<Civitai.ICivitaiBaseModelCatalog>()` add `, sp.GetService<IComfyUiClientProvider>()`.

Then search the whole solution for remaining callers: `Grep pattern "new (InpaintingViewModel|OutpaintingViewModel|ImageEditorViewModel|ImageEditTabViewModel)\(" path E:\Repos\DiffusionNexus`. Test call sites use named arguments (`eventAggregator:`, `thumbnailOrchestrator:`), so only ones naming `comfyUiService:` need changing.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~EditorEngineGenerateTests|FullyQualifiedName~InpaintingViewModelGGUFResolutionTests|FullyQualifiedName~ImageEdit"`
Expected: PASS.

- [ ] **Step 8: Full test run**

Run: `dotnet test E:\Repos\DiffusionNexus\DiffusionNexus.Tests\DiffusionNexus.Tests.csproj`
Expected: everything passes except the known pre-existing failures listed in Global Constraints. Record the totals for the PR description.

- [ ] **Step 9: Commit and push**

```powershell
git add DiffusionNexus.UI DiffusionNexus.Tests
git commit -m "feat(editor): #606 Inpaint and Outpaint run on the ComfyUI chosen in Settings" -m "Each generate acquires a client from IComfyUiClientProvider (starting the Engine on demand), logs its steps to the Unified Console, and re-checks readiness when Settings are saved. Workflow JSON and node mutation unchanged." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 10: Manual verification, follow-up issue, PR

**Files:** none (verification and GitHub only), plus `C:\Users\Little God\.claude\projects\e--Repos-DiffusionNexus-Installer-SDK\memory\editor-on-engine-milestone.md` (status line).

- [ ] **Step 1: Confirm the BenQ is connected and get its rectangle**

```powershell
Get-CimInstance -Namespace root\wmi WmiMonitorID | % { $_.InstanceName }   # expect DISPLAY\BNQ7F05\...
Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Screen]::AllScreens | ft DeviceName,Primary,Bounds
```

Use the non-primary 16:9 screen left of `X = 0`. If `BNQ7F05` is missing, stop and ask the owner before using the LG.

- [ ] **Step 2: Launch the app and move its window onto the BenQ**

Run the UI project (`dotnet run --project E:\Repos\DiffusionNexus\DiffusionNexus.UI\DiffusionNexus.UI.csproj -c Debug`), then move the main window with `SetWindowPos` into the BenQ rectangle reported by the same process. Use `SetThreadDpiAwarenessContext(-4)` before `PrintWindow` captures (see memory note on screenshot DPI).

- [ ] **Step 3: Walk the spec's manual checklist and capture each screen**

1. Settings → ComfyUI Server shows the Server dropdown. On this dev machine (an upgraded database) it must read **Custom URL** — that is the upgrade rule working. Switch to Diffusion Nexus Engine: URL row disables, the Engine status line appears. Save.
2. Installation Manager shows the Engine tile with the Canvas switch **off**. If the Engine is not installed, install it (long; torch download).
3. Engine tile → **Features**: rows "Inpaint & Outpaint" and "Canvas · Krea 2 Turbo" with status and Needs. Tick Inpaint & Outpaint → Install selected. Progress text updates; the window refuses to close mid-install; both rows are re-checked at the end.
4. Image Editor → Inpaint: panel reads "Ready · Running on Diffusion Nexus Engine · change". Generate: status shows "Starting Diffusion Nexus Engine…", then progress, then the result lands on the canvas. Repeat for Outpaint (Generate). Generate (Vision) is greyed with "Outpaint Vision is not available on the Diffusion Nexus Engine yet".
5. Unified Console shows: Engine start, install steps, "Generate requested", "Prompt queued (…)", "Result received." for both tools.
6. Click "change" → Settings opens with ComfyUI Server expanded and in view. Switch to Custom URL pointing at a stopped `http://127.0.0.1:8188/`, Save, go back: the open Inpaint panel re-checks and reads "Server offline · Running on ComfyUI · change".
7. With the Engine selected and the Qwen models removed (or on a second machine), the panel reads "Not installed on the Engine · Install Inpaint & Outpaint"; the link opens Features with that row ticked.

Record pass/fail per item with the screenshot path. Any failure: fix on this branch (new commit + push), re-run the affected items.

- [ ] **Step 4: File the follow-up issue**

```powershell
gh auth switch --user Little-God1983
gh issue create --repo Little-God1983/DiffusionNexus --label enhancement --milestone "Editor on the Diffusion Nexus Engine" --title "Engine: Start/Stop in the console tile row do nothing" --body "The Diffusion Nexus Engine appears in the install tile row above the Unified Console with a Start button. Its InstallerPackage row has ExecutablePath = null, so LaunchInstanceCoreAsync returns silently and Start does nothing. Start should call ManagedComfyUiEngine.EnsureRunningAsync and Stop should call StopAsync. Found while building #606."
```

- [ ] **Step 5: Open the PR**

```powershell
gh pr create --repo Little-God1983/DiffusionNexus --base develop --head feature/editor-inpaint-outpaint-on-engine --title "Engine: Image Editor Inpaint + Outpaint run on the Diffusion Nexus Engine (#606)" --body-file <scratchpad>\pr-606.md
```

The body (write it to a scratchpad file first) covers: what the user sees (Settings dropdown, Engine Features dialog, readiness line), the upgrade rule (existing settings → Custom URL), the singleton URL fix and its restart caveat, test totals from Task 9 Step 8, the manual checklist results with screenshots, the follow-up issue link, and `Closes #606`. End it with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Do **not** merge; the owner merges.

- [ ] **Step 6: Update the project memory**

In `editor-on-engine-milestone.md`, replace the "awaiting the user's spec review" status with "#606 implemented, PR #<n> open; manual checklist results inside the PR; not merged". Update the matching line in `MEMORY.md`.

---

## Self-Review Notes

- **Spec coverage:** §4.1 → Task 1 + Task 8; §4.2 → Task 4 (+ Task 9 consumers); §4.3 → Task 5 + Task 7; §4.4 → Task 3 + Task 6; §4.5 → Task 9; §4.6 → Task 6 (Engine Features event) + Task 7 (Settings section); §4.7 → Task 10 Step 4 (Start button issue; the rest stays out); §5 table rows → Tasks 5, 7, 9; §6 tests → Tasks 1-9, manual list → Task 10.
- **Deviations from the spec, all recorded back into the spec on 2026-10-07:** upgrade rule (existing row → CustomUrl); `OutpaintVision` governed by the mode with an honest "not available yet"; the change link only on governed features; no download-size estimate (catalog has no sizes); `IsEngineTileVisible` kept as a property defaulting to the flag.
- **Type names used across tasks:** `ComfyUiServerMode`, `IComfyUiClientProvider`, `ComfyUiClientLease`, `ComfyUiUnavailableException`, `IManagedComfyUiEngine`, `IEngineRootResolver`, `EngineFeatureCatalog`, `EngineFeature`, `EngineFeatureDefinition`, `EngineFeatureBackend`, `EngineFeaturesViewModel`, `EngineFeatureRowViewModel`, `EngineFeatureStatus`, `NavigateToEngineFeaturesEventArgs`, `SettingsSection`, `FeatureBackendRouter.ServerModeFeatures` — each defined once, in the task listed in its **Produces** block.
