# Settings → Catalog (own channel, automatic updates, per-app notices) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Issue #618. Diffusion Nexus gets its own catalog channel and "apply automatically" setting (Settings → Catalog), announces only changes to its own workloads, and stops applying catalog updates silently.

**Architecture:** A singleton `CatalogUpdateCoordinator` (DiffusionNexus.UI/Services/Catalog) owns check / apply / switch / startup and is the one writer of `CatalogOptions.Channel`. It filters every check with `CatalogAudience.DiffusionNexus` (SDK 2.3.0), reads and writes the two new `AppSettings` columns through `ICatalogPreferenceStore` (a fresh DI scope per call), and holds an apply while `IInstallActivity` reports a running install. `CatalogSettingsViewModel` renders the coordinator in a new Settings expander. The startup path (`CatalogStartup`) resolves the channel first, reports, then runs the coordinator's startup check; `CatalogStartupUpdater` and the copied `CatalogChannelResolver` go.

**Tech Stack:** .NET 10, Avalonia 11 + CommunityToolkit.Mvvm, EF Core 10 (SQLite), xUnit + FluentAssertions 7 + Moq, SDK packages `DiffusionNexus.Installer.SDK.*` 2.3.0.

**Spec:** SDK repo `E:\Repos\DiffusionNexus.Installer.SDK\docs\superpowers\specs\2026-10-10-catalog-per-app-distribution-design.md` — §6 (this app), §3 (folders), §4 (audience, startup table), §5 (shared rules). Installer counterpart, already reviewed five rounds: `E:\Repos\DiffusionNexus.Installer\DiffusionNexus.Installer.Core\Updates\CatalogUpdateCoordinator.cs` (PR Into-The-Latent/DiffusionNexus.Installer#54). Its review lessons are built into Tasks 5–6 and listed under Review Focus.

## Global Constraints

- One issue (#618), one PR, branch `feature/catalog-settings-per-app` → `develop`. Commit and push after each task. The pre-push hook accepts `feature/…` only.
- SDK pin `2.3.0` for Models, Services, Shared, Catalog in `DiffusionNexus.UI/DiffusionNexus.UI.csproj`. No `--prerelease`.
- Audience: `CatalogAudience.DiffusionNexus` (counts drafts; the app lists them).
- Storage: `AppSettings.CatalogChannel` (`string?`, max 16, null = never chosen) and `AppSettings.ApplyCatalogUpdatesAutomatically` (`bool`, default false). Saved only through their own setters, **never** through `SaveSettingsAsync` (a Settings save carries a snapshot that must not overwrite them).
- First start with the columns: the channel copies the installer's saved channel once (`IUserSettingsRepository.GetByUserNameAsync(Environment.UserName)`, read-only — never `GetOrCreate…`, never a write to `user_settings.json`); nothing saved there → Stable. After that the app never reads `user_settings.json`.
- `DIFFUSIONNEXUS_CATALOG_CHANNEL` wins for a run (`CatalogChannelResolver.Resolve`), radios disabled with a hint.
- Logging: `IUnifiedLogger`, `LogCategory.General`, source `"Catalog"` for every coordinator step. The startup report keeps source `"Installer SDK"`.
- Copy (exact): startup notice `Catalog update available – see Settings → Catalog`; outcome `Catalog update available – v{N}`, `Up to date.`, `Catalog v{N} has no changes for Diffusion Nexus.`, `Catalog updated to v{N}.`, `Catalog updated to v{N} automatically.`, `Catalog v{N} applied automatically; no changes for Diffusion Nexus.`; hold `It can be applied once {name} has finished.`
- Migrations: `dotnet ef migrations add AddCatalogUpdateSettings --project DiffusionNexus.DataAccess --startup-project DiffusionNexus.DataAccess --context DiffusionNexusCoreDbContext --output-dir Migrations/Core`. Back up the DB with `publish.ps1` first (copilot-instructions).
- Tests: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj`. Baseline is recorded in Task 1, step 1.
- Windows only; no cross-platform branches.

## Review Focus

Failure modes the spec implies, most of them found the hard way in installer PR #54. Each has its test in the owning task.

1. **A release that changes only the installer's workloads** must not be announced (no list, no Apply), yet the startup apply with the toggle on still lands it (full snapshots, shared folder). → Task 5, `A_release_with_only_installer_changes_is_not_announced`, `…is_still_applied_automatically`.
2. **The page must show what the SDK found in the target folder after a switch, never the cross-channel preview** (an Apply that applies nothing, forever). Landed nothing → up to date; failed with nothing landed → the preview stays and Apply is the retry; landed part → a fresh check. → Task 6, `A_switch_whose_apply_lands_nothing_is_up_to_date_and_offers_nothing`, `A_failed_switch_apply_offers_apply_as_the_retry`.
3. **A folder seeded from the embedded (Stable) catalog after a switch to Preview** whose release differs only in installer workloads must still be offered — otherwise nothing ever completes the switch. → Task 5, `A_release_that_replaces_content_from_another_channel_is_offered`.
4. **A switch returns once the preference is saved**; a held switch (install running) checks the followed folder in the background under Checking. → Task 6, `A_switch_held_by_an_install_checks_the_followed_folder_in_the_background`.
5. **A failed automatic apply leaves Apply as the retry**, and an empty state (folder not seeded yet) never replaces the last known one. → Task 5, `A_failed_automatic_apply_can_be_retried`; Task 6, `A_switch_into_a_missing_folder_keeps_the_last_known_state`.

---

### Task 1: Pin SDK 2.3.0 and use the SDK's channel resolver

**Files:**
- Modify: `DiffusionNexus.UI/DiffusionNexus.UI.csproj:95-98`
- Delete: `DiffusionNexus.UI/Services/Catalog/CatalogChannelResolver.cs`
- Delete: `DiffusionNexus.Tests/Catalog/CatalogChannelResolverTests.cs`
- Modify (usings only): `DiffusionNexus.UI/App.axaml.cs`, `DiffusionNexus.UI/Services/Catalog/CatalogStartup.cs`, `DiffusionNexus.UI/Services/Catalog/CatalogStartupUpdater.cs`, `DiffusionNexus.Tests/Catalog/CatalogStartupTests.cs`, `DiffusionNexus.Tests/Catalog/CatalogStartupUpdaterTests.cs`

**Interfaces:**
- Produces: the SDK's `DiffusionNexus.Installer.SDK.Catalog.Updates.{CatalogChannelResolver, CatalogChannelSource, CatalogAudience, CatalogChangeRows, ChannelSwitchWarning}` available to later tasks.

- [ ] **Step 1: Baseline**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q 2>&1 | tail -3`
Expected: `Failed: 0`. Write the Passed count into the ledger as the baseline.

- [ ] **Step 2: Bump the pin and see the ambiguity**

In `DiffusionNexus.UI.csproj` change the four `Version="2.1.0"` (lines 95–98) to `Version="2.3.0"`.

Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -v q 2>&1 | grep -o "error CS0104: '[A-Za-z]*'" | sort | uniq -c`
Expected: CS0104 for `CatalogChannelResolver` and/or `CatalogChannelSource` (both namespaces are imported).

- [ ] **Step 3: Delete the copy and its tests**

```bash
git rm -q DiffusionNexus.UI/Services/Catalog/CatalogChannelResolver.cs DiffusionNexus.Tests/Catalog/CatalogChannelResolverTests.cs
```

The SDK's types live in `DiffusionNexus.Installer.SDK.Catalog.Updates`. `CatalogStartup.cs`, `CatalogStartupUpdater.cs` and both test files already import that namespace; in `App.axaml.cs` add `using DiffusionNexus.Installer.SDK.Catalog.Updates;` if the build reports `CatalogChannelResolver` missing (line ~516 uses `CatalogChannelResolver.EnvironmentVariable`).

- [ ] **Step 4: Build and test**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q 2>&1 | tail -3`
Expected: `Failed: 0`; Passed = baseline − the number of cases `CatalogChannelResolverTests` held (count them before the delete with `grep -c "InlineData\|\[Fact\]"`). `CatalogRegistrationTests` and `CatalogSeedTests` set `InstalledCatalogPath`, so they stay on the SDK's single-folder mode and pass unchanged.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore(catalog): #618 pin SDK 2.3.0; CatalogChannelResolver comes from the SDK (SDK #74)"
git push -u origin feature/catalog-settings-per-app
```

---

### Task 2: The two catalog settings in `AppSettings`

**Files:**
- Modify: `DiffusionNexus.Domain/Entities/AppSettings.cs` (after `ComfyUiServerMode`, ~line 235)
- Create: `DiffusionNexus.Domain/Models/CatalogUpdateSettings.cs`
- Modify: `DiffusionNexus.DataAccess/Configurations/AppSettingsConfiguration.cs:25`
- Create (generated): `DiffusionNexus.DataAccess/Migrations/Core/<timestamp>_AddCatalogUpdateSettings.cs` + `.Designer.cs`; Modify: `DiffusionNexusCoreDbContextModelSnapshot.cs`
- Modify: `DiffusionNexus.DataAccess/Recovery/DatabaseRecoveryService.cs:283-298`
- Modify: `DiffusionNexus.DataAccess/Repositories/Interfaces/IAppSettingsRepository.cs`, `DiffusionNexus.DataAccess/Repositories/AppSettingsRepository.cs`
- Modify: `DiffusionNexus.Domain/Services/IAppSettingsService.cs`, `DiffusionNexus.Service/Services/AppSettingsService.cs`
- Test: `DiffusionNexus.Tests/DataAccess/CoreDbMigrationTests.cs`, Create: `DiffusionNexus.Tests/Service/AppSettingsServiceCatalogTests.cs`

**Interfaces:**
- Produces: `record CatalogUpdateSettings(string? Channel, bool ApplyAutomatically)` (namespace `DiffusionNexus.Domain.Models`); `IAppSettingsService.GetCatalogUpdateSettingsAsync(CancellationToken)` → `Task<CatalogUpdateSettings>` (read-only; no row → `(null, false)`); `SetCatalogChannelAsync(string channel, CancellationToken)`; `SetApplyCatalogUpdatesAutomaticallyAsync(bool on, CancellationToken)` (both create the row if missing).

- [ ] **Step 1: Back up the database**

Run `pwsh ./publish.ps1` far enough to make its DB backup (copilot-instructions), or copy `%LOCALAPPDATA%\DiffusionNexus\Data\Diffusion_Nexus-core.db` aside.

- [ ] **Step 2: Failing service tests**

Create `DiffusionNexus.Tests/Service/AppSettingsServiceCatalogTests.cs`:

```csharp
using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Service;

/// <summary>#618: the app's own catalog channel and "apply automatically", saved only through their own setters.</summary>
public sealed class AppSettingsServiceCatalogTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public AppSettingsServiceCatalogTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDataAccessLayer(o => o.UseSqlite(_connection));
        services.AddSingleton(new Mock<ISecureStorage>().Object);
        services.AddTransient<IAppSettingsService, AppSettingsService>();
        _provider = services.BuildServiceProvider();
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private IAppSettingsService Service(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IAppSettingsService>();

    [Fact]
    public async Task Without_a_settings_row_nothing_was_chosen_and_nothing_is_created()
    {
        using var scope = _provider.CreateScope();

        var settings = await Service(scope).GetCatalogUpdateSettingsAsync();

        settings.Should().Be(new Domain.Models.CatalogUpdateSettings(null, false));
        (await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AppSettings.GetSettingsAsync()).Should().BeNull("a read creates nothing");
    }

    [Fact]
    public async Task The_channel_and_the_toggle_round_trip_and_create_the_row_when_missing()
    {
        using (var scope = _provider.CreateScope())
        {
            await Service(scope).SetCatalogChannelAsync("Preview");
            await Service(scope).SetApplyCatalogUpdatesAutomaticallyAsync(true);
        }

        using (var scope = _provider.CreateScope())
        {
            (await Service(scope).GetCatalogUpdateSettingsAsync()).Should().Be(new Domain.Models.CatalogUpdateSettings("Preview", true));
        }
    }

    [Fact]
    public async Task A_settings_save_never_overwrites_the_catalog_settings()
    {
        AppSettings snapshot;
        using (var scope = _provider.CreateScope())
        {
            snapshot = await Service(scope).GetSettingsAsync();   // the Settings page's snapshot, taken before the catalog changes
        }
        using (var scope = _provider.CreateScope())
        {
            await Service(scope).SetCatalogChannelAsync("Preview");
            await Service(scope).SetApplyCatalogUpdatesAutomaticallyAsync(true);
        }

        using (var scope = _provider.CreateScope())
        {
            await Service(scope).SaveSettingsAsync(snapshot);
        }

        using (var scope = _provider.CreateScope())
        {
            (await Service(scope).GetCatalogUpdateSettingsAsync()).Should().Be(new Domain.Models.CatalogUpdateSettings("Preview", true));
        }
    }
}
```

Add to `CoreDbMigrationTests.cs` (after the ComfyUiServerMode test; same `insertRow` pattern):

```csharp
    /// <summary>
    /// #618: an upgrading database gets no catalog channel (the app copies the installer's once) and
    /// automatic updates off.
    /// </summary>
    [Fact]
    public void AddCatalogUpdateSettings_ExistingRowHasNoChannelAndAutomaticUpdatesOff()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var dbPath = Path.Combine(tempDir.FullName, "catalog-settings-test.db");
        const string insertRow =
            "INSERT INTO [AppSettings] (Id, ShowNsfw, GenerateVideoThumbnails, ShowVideoPreview, " +
            "UseForgeStylePrompts, MergeLoraSources, DeleteEmptySourceFolders, BackupDatasetImagesEnabled, " +
            "BackupDatabaseEnabled, AutoBackupIntervalDays, AutoBackupIntervalHours, MaxBackups, " +
            "ComfyUiServerUrl, UpdatedAt) VALUES (1, 0, 1, 0, 1, 0, 0, 0, 1, 1, 0, 10, " +
            "'http://127.0.0.1:8188/', datetime('now'))";
        try
        {
            var options = new DbContextOptionsBuilder<DiffusionNexusCoreDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=False")
                .Options;

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                var migrator = ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
                migrator.Migrate("20261008172424_AddComfyUiServerMode");
                ctx.Database.ExecuteSqlRaw(insertRow);
                ctx.Database.Migrate();
            }

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                var row = ctx.AppSettings.Single();
                row.CatalogChannel.Should().BeNull();
                row.ApplyCatalogUpdatesAutomatically.Should().BeFalse();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            tempDir.Delete(recursive: true);
        }
    }
```

- [ ] **Step 3: Run them**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q --filter "FullyQualifiedName~AppSettingsServiceCatalogTests|FullyQualifiedName~AddCatalogUpdateSettings" 2>&1 | grep -E "error CS|Passed!|Failed!" | head -5`
Expected: compile errors (`GetCatalogUpdateSettingsAsync`, `CatalogChannel` missing).

- [ ] **Step 4: Entity, model, configuration**

`AppSettings.cs`, after `ComfyUiServerMode`:

```csharp
    /// <summary>
    /// The catalog channel this app follows ("Stable" or "Preview"), #618. Null until chosen: the first
    /// start with this column copies the installer's saved channel once. Saved only by
    /// <c>IAppSettingsService.SetCatalogChannelAsync</c>, never by a Settings save.
    /// </summary>
    public string? CatalogChannel { get; set; }

    /// <summary>
    /// Apply a catalog update the startup check finds without asking (#618). Off by default. Saved only by
    /// <c>IAppSettingsService.SetApplyCatalogUpdatesAutomaticallyAsync</c>, never by a Settings save.
    /// </summary>
    public bool ApplyCatalogUpdatesAutomatically { get; set; }
```

Create `DiffusionNexus.Domain/Models/CatalogUpdateSettings.cs`:

```csharp
namespace DiffusionNexus.Domain.Models;

/// <summary>The app's catalog channel (null = never chosen) and its "apply automatically" setting (#618).</summary>
public sealed record CatalogUpdateSettings(string? Channel, bool ApplyAutomatically);
```

`AppSettingsConfiguration.cs`, after the `ComfyUiServerMode` property block:

```csharp
        entity.Property(e => e.CatalogChannel).HasMaxLength(16);
        entity.Property(e => e.ApplyCatalogUpdatesAutomatically).HasDefaultValue(false);
```

- [ ] **Step 5: Migration**

Run: `dotnet ef migrations add AddCatalogUpdateSettings --project DiffusionNexus.DataAccess --startup-project DiffusionNexus.DataAccess --context DiffusionNexusCoreDbContext --output-dir Migrations/Core`
Expected: a migration whose `Up` adds `CatalogChannel` (TEXT, maxLength 16, nullable) and `ApplyCatalogUpdatesAutomatically` (INTEGER NOT NULL DEFAULT 0 / `defaultValue: false`). No data update is needed.

Then: `dotnet ef migrations has-pending-model-changes --project DiffusionNexus.DataAccess --startup-project DiffusionNexus.DataAccess --context DiffusionNexusCoreDbContext`
Expected: no pending changes (CI runs this).

- [ ] **Step 6: Column repair**

In `DatabaseRecoveryService.cs`'s `requiredColumns` add:

```csharp
                { "CatalogChannel", "ALTER TABLE AppSettings ADD COLUMN CatalogChannel TEXT" },
                { "ApplyCatalogUpdatesAutomatically", "ALTER TABLE AppSettings ADD COLUMN ApplyCatalogUpdatesAutomatically INTEGER NOT NULL DEFAULT 0" },
```

- [ ] **Step 7: Repository and service**

`IAppSettingsRepository.cs`:

```csharp
    /// <summary>The catalog channel and toggle (#618), read-only. Null without a settings row.</summary>
    Task<Domain.Models.CatalogUpdateSettings?> GetCatalogUpdateSettingsAsync(CancellationToken cancellationToken = default);
```

`AppSettingsRepository.cs`, next to `GetComfyUiServerConnectionAsync`:

```csharp
    /// <inheritdoc />
    public async Task<Domain.Models.CatalogUpdateSettings?> GetCatalogUpdateSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await DbSet
            .AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new Domain.Models.CatalogUpdateSettings(s.CatalogChannel, s.ApplyCatalogUpdatesAutomatically))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
```

`IAppSettingsService.cs`:

```csharp
    /// <summary>
    /// The catalog channel (null = never chosen) and "apply catalog updates automatically" (#618).
    /// Read-only: nothing is created, seeded or tracked; without a settings row it answers (null, false).
    /// </summary>
    Task<Models.CatalogUpdateSettings> GetCatalogUpdateSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the catalog channel the app follows (#618). Creates the settings row if missing.</summary>
    Task SetCatalogChannelAsync(string channel, CancellationToken cancellationToken = default);

    /// <summary>Saves "apply catalog updates automatically" (#618). Creates the settings row if missing.</summary>
    Task SetApplyCatalogUpdatesAutomaticallyAsync(bool on, CancellationToken cancellationToken = default);
```

`AppSettingsService.cs`, after `GetComfyUiServerConnectionAsync`:

```csharp
    /// <inheritdoc />
    public async Task<Domain.Models.CatalogUpdateSettings> GetCatalogUpdateSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _unitOfWork.AppSettings
            .GetCatalogUpdateSettingsAsync(cancellationToken)
            .ConfigureAwait(false);

        // No row yet: nothing was chosen.
        return settings ?? new Domain.Models.CatalogUpdateSettings(null, false);
    }

    /// <inheritdoc />
    public async Task SetCatalogChannelAsync(string channel, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        var settings = await TrackedSettingsAsync(cancellationToken).ConfigureAwait(false);
        settings.CatalogChannel = channel.Trim();
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetApplyCatalogUpdatesAutomaticallyAsync(bool on, CancellationToken cancellationToken = default)
    {
        var settings = await TrackedSettingsAsync(cancellationToken).ConfigureAwait(false);
        settings.ApplyCatalogUpdatesAutomatically = on;
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The settings row, tracked, created when missing. A fresh read: see GetSettingsAsync on the identity map.</summary>
    private async Task<AppSettings> TrackedSettingsAsync(CancellationToken cancellationToken)
    {
        _unitOfWork.ClearChangeTracker();
        return await _unitOfWork.AppSettings
            .GetSettingsWithIncludesAsync(cancellationToken)
            .ConfigureAwait(false);
    }
```

Do **not** add either field to `SaveSettingsAsync`'s whitelist; add this comment next to the `ComfyUiServerMode` line there:

```csharp
        // CatalogChannel and ApplyCatalogUpdatesAutomatically are absent on purpose (#618): the catalog
        // section saves them at once through their own setters, and this snapshot would put back old values.
```

- [ ] **Step 8: Run the tests**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q --filter "FullyQualifiedName~AppSettingsServiceCatalogTests|FullyQualifiedName~CoreDbMigrationTests" 2>&1 | tail -3`
Expected: all pass.

- [ ] **Step 9: Whole suite, commit**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q 2>&1 | tail -3` — Expected: `Failed: 0`.

```bash
git add -A
git commit -m "feat(settings): #618 AppSettings.CatalogChannel and ApplyCatalogUpdatesAutomatically, saved only through their own setters"
git push
```

---

### Task 3: `ICatalogPreferenceStore` — the channel copied from the installer once

**Files:**
- Create: `DiffusionNexus.UI/Services/Catalog/CatalogPreferenceStore.cs`
- Test: Create `DiffusionNexus.Tests/Catalog/CatalogPreferenceStoreTests.cs`

**Interfaces:**
- Consumes: Task 2's `IAppSettingsService` members; SDK `IUserSettingsRepository.GetByUserNameAsync(string, CancellationToken)`, `CatalogChannelResolver.TryParse(string?, out CatalogChannel)`.
- Produces:
  ```csharp
  public sealed record CatalogPreferences(string? Channel, bool ApplyAutomatically);
  public interface ICatalogPreferenceStore
  {
      Task<CatalogPreferences> LoadAsync(CancellationToken ct = default);
      Task SaveChannelAsync(CatalogChannel channel, CancellationToken ct = default);
      Task SaveApplyAutomaticallyAsync(bool on, CancellationToken ct = default);
  }
  public sealed class CatalogPreferenceStore(IServiceScopeFactory scopes, IUserSettingsRepository installerSettings, IUnifiedLogger? logger = null) : ICatalogPreferenceStore
  ```
  `LoadAsync` throws on database I/O failures (the coordinator decides); an unreadable installer file is "nothing saved".

- [ ] **Step 1: Failing tests**

Create `DiffusionNexus.Tests/Catalog/CatalogPreferenceStoreTests.cs`:

```csharp
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Infrastructure.Services;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Services.Settings;
using DiffusionNexus.UI.Services.Catalog;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Catalog;

public sealed class CatalogPreferenceStoreTests
{
    private readonly Mock<IAppSettingsService> _settings = new();
    private readonly Mock<IUserSettingsRepository> _installer = new();
    private readonly UnifiedLogger _log = new();
    private CatalogUpdateSettings _stored = new(null, false);

    public CatalogPreferenceStoreTests()
    {
        _settings.Setup(s => s.GetCatalogUpdateSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _stored);
        _settings.Setup(s => s.SetCatalogChannelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .Callback((string c, CancellationToken _) => _stored = _stored with { Channel = c }).Returns(Task.CompletedTask);
        _settings.Setup(s => s.SetApplyCatalogUpdatesAutomaticallyAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                 .Callback((bool on, CancellationToken _) => _stored = _stored with { ApplyAutomatically = on }).Returns(Task.CompletedTask);
    }

    private CatalogPreferenceStore Store()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_settings.Object);
        return new CatalogPreferenceStore(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _installer.Object, _log);
    }

    private void InstallerSaved(string? channel) =>
        _installer.Setup(r => r.GetByUserNameAsync(Environment.UserName, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(channel is null ? null : new UserSettings { UserName = Environment.UserName, CatalogChannel = channel });

    [Fact]
    public async Task The_first_start_copies_the_installers_channel_once_and_saves_it()
    {
        InstallerSaved("Preview");

        var first = await Store().LoadAsync();

        first.Channel.Should().Be("Preview");
        _settings.Verify(s => s.SetCatalogChannelAsync("Preview", It.IsAny<CancellationToken>()), Times.Once);
        _log.GetEntries().Should().Contain(e => e.Source == "Catalog" && e.Message.Contains("installer's Preview channel"));

        InstallerSaved("Stable");   // the installer changes its mind later
        (await Store().LoadAsync()).Channel.Should().Be("Preview", "the copy happens once; the apps choose independently after that");
        _settings.Verify(s => s.SetCatalogChannelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nonsense")]
    public async Task Nothing_usable_saved_by_the_installer_starts_on_Stable(string? installerChannel)
    {
        InstallerSaved(installerChannel);

        (await Store().LoadAsync()).Channel.Should().Be("Stable");
        _settings.Verify(s => s.SetCatalogChannelAsync("Stable", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_unreadable_installer_file_starts_on_Stable_and_never_writes_it()
    {
        _installer.Setup(r => r.GetByUserNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("locked"));

        (await Store().LoadAsync()).Channel.Should().Be("Stable");
        _installer.Verify(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()), Times.Never);
        _installer.Verify(r => r.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_chosen_channel_never_reads_the_installers_file()
    {
        _stored = new CatalogUpdateSettings("Stable", true);

        var loaded = await Store().LoadAsync();

        loaded.Should().Be(new CatalogPreferences("Stable", true));
        _installer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Saves_go_through_their_own_setters()
    {
        var store = Store();

        await store.SaveChannelAsync(CatalogChannel.Preview);
        await store.SaveApplyAutomaticallyAsync(true);

        _stored.Should().Be(new CatalogUpdateSettings("Preview", true));
    }
}
```

- [ ] **Step 2: Run them**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q --filter "FullyQualifiedName~CatalogPreferenceStoreTests" 2>&1 | grep -E "error CS|Passed!|Failed!" | head -3`
Expected: compile error (`CatalogPreferenceStore` missing).

- [ ] **Step 3: Implement**

Create `DiffusionNexus.UI/Services/Catalog/CatalogPreferenceStore.cs`:

```csharp
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Services.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.Services.Catalog;

/// <summary>The app's catalog channel (null only when nothing could be stored) and "apply automatically".</summary>
public sealed record CatalogPreferences(string? Channel, bool ApplyAutomatically);

/// <summary>Where the app keeps its catalog channel and "apply automatically" (#618): two AppSettings columns.</summary>
public interface ICatalogPreferenceStore
{
    /// <summary>
    /// The stored preferences. On the first start with the columns the channel takes the installer's
    /// saved channel once (Stable when it has none) and stores it, so a Preview user stays on Preview;
    /// after that the two apps choose independently. Database failures propagate.
    /// </summary>
    Task<CatalogPreferences> LoadAsync(CancellationToken ct = default);

    Task SaveChannelAsync(CatalogChannel channel, CancellationToken ct = default);

    Task SaveApplyAutomaticallyAsync(bool on, CancellationToken ct = default);
}

/// <summary>
/// A fresh DI scope per call: <see cref="IAppSettingsService"/> is transient over a scoped unit of work,
/// and this store is held by a singleton (same rule as App.axaml.cs's other settings readers).
/// </summary>
public sealed class CatalogPreferenceStore(IServiceScopeFactory scopes, IUserSettingsRepository installerSettings, IUnifiedLogger? logger = null)
    : ICatalogPreferenceStore
{
    private const string LogSource = "Catalog";

    public async Task<CatalogPreferences> LoadAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        var stored = await settings.GetCatalogUpdateSettingsAsync(ct).ConfigureAwait(false);
        if (stored.Channel is not null) return new CatalogPreferences(stored.Channel, stored.ApplyAutomatically);

        var copied = await InstallerChannelAsync(ct).ConfigureAwait(false);
        await settings.SetCatalogChannelAsync(copied.ToString(), ct).ConfigureAwait(false);
        return new CatalogPreferences(copied.ToString(), stored.ApplyAutomatically);
    }

    public async Task SaveChannelAsync(CatalogChannel channel, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAppSettingsService>().SetCatalogChannelAsync(channel.ToString(), ct).ConfigureAwait(false);
    }

    public async Task SaveApplyAutomaticallyAsync(bool on, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAppSettingsService>().SetApplyCatalogUpdatesAutomaticallyAsync(on, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The installer's saved channel, read-only (GetOrCreateForCurrentUserAsync would write the installer's
    /// file). Unreadable or nothing usable: Stable.
    /// </summary>
    private async Task<CatalogChannel> InstallerChannelAsync(CancellationToken ct)
    {
        string? saved;
        try
        {
            saved = (await installerSettings.GetByUserNameAsync(Environment.UserName, ct).ConfigureAwait(false))?.CatalogChannel;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.Warn(LogCategory.General, LogSource, "The installer's settings could not be read; starting on the Stable catalog channel.", ex.Message);
            return CatalogChannel.Stable;
        }

        if (CatalogChannelResolver.TryParse(saved, out var channel))
        {
            logger?.Info(LogCategory.General, LogSource, $"First start with the catalog setting: following the installer's {channel} channel from now on.",
                "Change it in Settings → Catalog; the installer keeps its own choice.");
            return channel;
        }

        logger?.Info(LogCategory.General, LogSource, "First start with the catalog setting: the installer saved no channel; following Stable.");
        return CatalogChannel.Stable;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q --filter "FullyQualifiedName~CatalogPreferenceStoreTests" 2>&1 | tail -3`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(catalog): #618 CatalogPreferenceStore — the channel copied from the installer once, then the app's own"
git push
```

---

### Task 4: `IInstallActivity` — which install is running

**Files:**
- Create: `DiffusionNexus.UI/Services/InstallActivity.cs`
- Modify: `DiffusionNexus.UI/ViewModels/EngineFeaturesViewModel.cs` (ctor + `InstallSelectedAsync`)
- Modify: `DiffusionNexus.UI/ViewModels/WorkloadsViewModel.cs` (ctor + `InstallItemsAsync`)
- Modify: `DiffusionNexus.UI/ViewModels/InstallerManagerViewModel.cs` (ctor, :811, :890, engine install :1056/:1220, package update :1301/:1374)
- Modify: `DiffusionNexus.UI/App.axaml.cs` (register singleton, pass to `InstallerManagerViewModel` at :1052)
- Test: Create `DiffusionNexus.Tests/Services/InstallActivityTests.cs`; Modify `DiffusionNexus.Tests/Engine/EngineFeaturesViewModelTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public interface IInstallActivity
  {
      string? Running { get; }          // the newest running install's name, null when idle
      event Action? Changed;            // raised on every Begin and end
      IDisposable Begin(string name);   // dispose ends it; safe to dispose twice
  }
  public sealed class InstallActivity : IInstallActivity
  ```
- `EngineFeaturesViewModel(..., IInstallActivity? installActivity = null)` (last optional param), `WorkloadsViewModel(..., IInstallActivity? installActivity = null)`, `InstallerManagerViewModel(..., IInstallActivity? installActivity = null)`.

- [ ] **Step 1: Failing tests**

Create `DiffusionNexus.Tests/Services/InstallActivityTests.cs`:

```csharp
using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Services;

public sealed class InstallActivityTests
{
    [Fact]
    public void Running_names_the_newest_install_until_every_install_ended()
    {
        var activity = new InstallActivity();
        var changes = 0;
        activity.Changed += () => changes++;

        var engine = activity.Begin("Diffusion Nexus Engine");
        var workload = activity.Begin("Krea-2-Turbo");
        activity.Running.Should().Be("Krea-2-Turbo");

        workload.Dispose();
        activity.Running.Should().Be("Diffusion Nexus Engine");
        engine.Dispose();
        activity.Running.Should().BeNull();
        changes.Should().Be(4);
    }

    [Fact]
    public void Ending_an_install_twice_counts_once()
    {
        var activity = new InstallActivity();
        var a = activity.Begin("A");
        var b = activity.Begin("B");

        a.Dispose();
        a.Dispose();

        activity.Running.Should().Be("B");
    }
}
```

In `EngineFeaturesViewModelTests.cs` extend `Sut` (line ~55) with the activity, then add the test (setup copied from the file's existing install test at ~:160):

```csharp
    private EngineFeaturesViewModel Sut(EngineFeature? preselect = null, IInstallActivity? activity = null) =>
        new(_catalog.Object, _checker.Object, _installer.Object, Root, preselect: preselect,
            freeSpaceProbe: _ => 412L * 1024 * 1024 * 1024, installActivity: activity);

    // #618: the catalog apply waits while an Engine Features install runs.
    [Fact]
    public async Task An_install_is_reported_as_running_until_it_ends()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 2, 3);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var activity = new InstallActivity();
        string? seen = null;
        _installer.Setup(i => i.InstallSelectedAsync(It.IsAny<InstallationConfiguration>(), Root,
                It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
                It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
                It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => seen = activity.Running)
            .ReturnsAsync("done");
        var vm = Sut(EngineFeature.InpaintOutpaint, activity);
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        seen.Should().Be("Diffusion Nexus Engine features");
        activity.Running.Should().BeNull();
    }
```

(`using DiffusionNexus.UI.Services;` for `InstallActivity` / `IInstallActivity`. The preselected Inpaint/Outpaint row is selected by the preselect, as in the existing install test; its missing node and models make `RowsToInstall` non-empty.)

- [ ] **Step 2: Run them**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q --filter "FullyQualifiedName~InstallActivityTests|FullyQualifiedName~An_install_is_reported_as_running" 2>&1 | grep -E "error CS|Passed!|Failed!" | head -3`
Expected: compile error (`InstallActivity` missing).

- [ ] **Step 3: Implement the tracker**

Create `DiffusionNexus.UI/Services/InstallActivity.cs`:

```csharp
namespace DiffusionNexus.UI.Services;

/// <summary>
/// Which install is running right now, app-wide: Engine Features, the base Engine install, a workload
/// install, a package update. The catalog apply waits while one runs (#618): it never swaps the catalog
/// under a running plan.
/// </summary>
public interface IInstallActivity
{
    /// <summary>The newest running install's name; null when none runs.</summary>
    string? Running { get; }

    /// <summary>Raised after every Begin and every end, on the caller's thread.</summary>
    event Action? Changed;

    /// <summary>Marks <paramref name="name"/> as running until the returned handle is disposed (twice is fine).</summary>
    IDisposable Begin(string name);
}

public sealed class InstallActivity : IInstallActivity
{
    private readonly Lock _gate = new();
    private readonly List<Entry> _running = [];

    public string? Running { get { lock (_gate) return _running.Count == 0 ? null : _running[^1].Name; } }

    public event Action? Changed;

    public IDisposable Begin(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var entry = new Entry(this, name);
        lock (_gate) _running.Add(entry);
        Changed?.Invoke();
        return entry;
    }

    private void End(Entry entry)
    {
        bool removed;
        lock (_gate) removed = _running.Remove(entry);
        if (removed) Changed?.Invoke();
    }

    private sealed class Entry(InstallActivity owner, string name) : IDisposable
    {
        public string Name { get; } = name;
        public void Dispose() => owner.End(this);
    }
}
```

- [ ] **Step 4: Hook the four install paths**

`EngineFeaturesViewModel`: add ctor param `IInstallActivity? installActivity = null` (last), field `_installActivity`. In `InstallSelectedAsync`, right after `IsInstalling = true;`:

```csharp
        using var running = _installActivity?.Begin("Diffusion Nexus Engine features");
```

(The `using` ends with the method, after the existing `finally` sets `IsInstalling = false`.)

`WorkloadsViewModel`: add ctor param `IInstallActivity? installActivity = null` (last), field `_installActivity`. First line of `InstallItemsAsync`:

```csharp
        using var running = _installActivity?.Begin(config.Name);
```

`InstallerManagerViewModel`: add ctor param `IInstallActivity? installActivity = null` (last), field `_installActivity`.
- :811 `new EngineFeaturesViewModel(_catalog, _checkerService, _installService, card.InstallationPath, _resourceMonitor, _unifiedLogger, preselect, installActivity: _installActivity)`.
- :890 `new WorkloadsViewModel(_catalog, _checkerService, _installService, comfyUiRoot, resourceMonitor: _resourceMonitor, installActivity: _installActivity)`.
- Base Engine install: next to `_isEngineInstallInFlight = true;` (:1057) add `var engineRunning = _installActivity?.Begin("Diffusion Nexus Engine");` and in the outer `finally` next to `_isEngineInstallInFlight = false;` add `engineRunning?.Dispose();`.
- Package update: next to `card.IsUpdating = true;` (:1301) add `var updateRunning = _installActivity?.Begin(card.Name);` (use the card's display-name property — the same one the update log lines use), and next to `card.IsUpdating = false;` (:1374) add `updateRunning?.Dispose();`. Both lines sit in the same method; if `IsUpdating = false` is in a `finally`, the dispose goes there too.

`App.axaml.cs`: register `services.AddSingleton<IInstallActivity, InstallActivity>();` next to the other UI services, and pass `installActivity: sp.GetService<IInstallActivity>()` in the `InstallerManagerViewModel` factory (:1052).

- [ ] **Step 5: Run the tests, whole suite, commit**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q 2>&1 | tail -3` — Expected: `Failed: 0`.

```bash
git add -A
git commit -m "feat(installs): #618 IInstallActivity — Engine Features, Engine, workload and package installs say they are running"
git push
```

---

### Task 5: `CatalogUpdateCoordinator` — channel, check, apply, startup, automatic apply

**Files:**
- Create: `DiffusionNexus.UI/Services/Catalog/CatalogUpdateCoordinator.cs`
- Create: `DiffusionNexus.UI/Services/Catalog/CatalogUpdateOffer.cs`
- Test: Create `DiffusionNexus.Tests/Catalog/FakeCatalogUpdateService.cs`, `DiffusionNexus.Tests/Catalog/CatalogChecks.cs`, `DiffusionNexus.Tests/Catalog/CatalogUpdateCoordinatorTests.cs`

**Interfaces:**
- Consumes: Task 3 `ICatalogPreferenceStore`, `CatalogPreferences`; Task 4 `IInstallActivity`; SDK `ICatalogUpdateService`, `CatalogUpdateCheck.For`, `HasChanges`, `CatalogAudience.DiffusionNexus`, `CatalogChannelResolver`, `CatalogOptions.{Channel, UsesSharedFolders, InstalledCatalogPath, ReadFolderFor, LocalOverridePath}`.
- Produces (Task 6 adds the switch members to the same interface/class; Task 7 and 8 consume all of it):
  ```csharp
  public enum CatalogUpdatePhase { Idle, Checking, Checked, Applying, Applied }
  public sealed record CatalogChannelSwitch(CatalogUpdateCheck Preview, ChannelSwitchWarning Warning) { public CatalogChannel Target => Preview.Channel; }
  public interface ICatalogUpdateCoordinator
  {
      CatalogChannel Channel { get; }
      CatalogChannelSource ChannelSource { get; }
      string? OverridePath { get; }
      CatalogUpdatePhase Phase { get; }
      CatalogUpdateCheck? LastCheck { get; }          // always filtered for the app
      DateTimeOffset? LastCheckedAt { get; }
      LocalCatalogState? Installed { get; }
      CatalogDownloadProgress? Progress { get; }
      CatalogApplyResult? LastApply { get; }
      bool LastApplyFailed { get; }
      bool LastApplyAutomatic { get; }
      bool AutoApply { get; }
      bool UpdateAvailable { get; }
      bool CanApply { get; }
      string? ApplyBlockedReason { get; }
      CatalogChannelSwitch? PendingSwitch { get; }    // Task 6
      event Action? Changed;
      Task<CatalogChannel> ResolveChannelAsync(CancellationToken ct = default);
      Task CheckAsync(CancellationToken ct = default);
      Task ApplyAsync(CancellationToken ct = default);
      Task CheckAtStartupAsync(CancellationToken ct = default);
      Task<bool> SetAutoApplyAsync(bool on, CancellationToken ct = default);
      Task SwitchChannelAsync(CatalogChannel target, CancellationToken ct = default);   // Task 6
      Task ConfirmSwitchAsync(CancellationToken ct = default);                         // Task 6
      void KeepChannel();                                                              // Task 6
  }
  public static class CatalogUpdateOffer
  {
      public static bool ReleaseAvailable(CatalogUpdateCheck? check, CatalogUpdatePhase phase, bool pendingSwitch, bool lastApplyFailed);
      public static bool Offered(CatalogUpdateCheck? check, CatalogUpdatePhase phase, bool pendingSwitch, bool lastApplyFailed, bool contentFromAnotherChannel);
  }
  public sealed class CatalogUpdateCoordinator(ICatalogUpdateService updates, ICatalog catalog, CatalogOptions options,
      ICatalogPreferenceStore preferences, IInstallActivity installs, Func<string?> readEnvironment, IUnifiedLogger? logger = null)
  ```
  In this task the three switch members exist and throw `NotImplementedException`; Task 6 replaces them.

- [ ] **Step 1: Test helpers**

Create `DiffusionNexus.Tests/Catalog/CatalogChecks.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Models.Enums;

namespace DiffusionNexus.Tests.Catalog;

/// <summary>Hand-built SDK check results. The SDK's own tests cover CheckAsync; these describe its output.</summary>
internal static class CatalogChecks
{
    public static CatalogManifest Remote(int version, CatalogChannel channel) => new()
    {
        CatalogVersion = version,
        Channel = channel,
        Commit = "0123456",
        GeneratedAt = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero),
        Archive = new CatalogArchiveInfo("catalog.zip", new string('0', 64), 1024),
    };

    public static CatalogUpdateCheck Available(int version = 7, CatalogChannel channel = CatalogChannel.Stable,
        IReadOnlyList<WorkloadChange>? workloads = null, IReadOnlyList<WorkflowChange>? workflows = null) =>
        new(CatalogUpdateOutcome.UpdatesAvailable, channel, Remote(version, channel), new LocalCatalogState(),
            workloads ?? [WorkloadUpdated("Upscaling-Z-Image-Turbo", "V1.6", "V1.7")], workflows ?? [], null);

    public static CatalogUpdateCheck Outcome(CatalogUpdateOutcome outcome, CatalogChannel channel = CatalogChannel.Stable, string? error = null) =>
        new(outcome, channel, null, new LocalCatalogState(), [], [], error);

    public static WorkloadChange WorkloadUpdated(string name, string from, string to) => new(Guid.NewGuid(), name, ChangeKind.Updated, from, to);
    public static WorkloadChange WorkloadAdded(string name, string version) => new(Guid.NewGuid(), name, ChangeKind.Added, null, version);
    public static WorkloadChange WorkloadRemoved(string name, string version) => new(Guid.NewGuid(), name, ChangeKind.Removed, version, null);

    /// <summary>Every workload the check mentions marked as <paramref name="target"/>'s; workflows owned by all of them. What a 2.3.0 manifest carries.</summary>
    public static CatalogUpdateCheck Targeting(this CatalogUpdateCheck check, WorkloadTargetType target, bool released = true)
    {
        IReadOnlyList<Guid> owners = check.Workloads.Select(w => w.Id).ToList();
        return check with
        {
            Index = new CatalogAudienceIndex(
                check.Workloads.ToDictionary(w => w.Id, _ => new WorkloadAudienceInfo(target, released)),
                check.Workflows.ToDictionary(w => w.Id, _ => owners)),
        };
    }

    public static LocalCatalogState State(int version, CatalogChannel? channel, SectionOrigin origin = SectionOrigin.Remote) => new()
    {
        Workloads = new SectionState(version, "abc", DateTimeOffset.UtcNow) { Channel = channel, Origin = origin },
        Workflows = new SectionState(version, "abc", DateTimeOffset.UtcNow) { Channel = channel, Origin = origin },
    };
}
```

Create `DiffusionNexus.Tests/Catalog/FakeCatalogUpdateService.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Tests.Catalog;

/// <summary>Scriptable stand-in for the SDK's update service. HoldCheck / HoldApply keep a call in flight until the test releases it.</summary>
internal sealed class FakeCatalogUpdateService : ICatalogUpdateService
{
    public Func<CatalogUpdateCheck> NextCheck { get; set; } = () => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate);
    public Func<CatalogChannel, CatalogUpdateCheck>? NextPreview { get; set; }
    public Func<CatalogApplyResult> NextApply { get; set; } = () => new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null);
    public TaskCompletionSource? HoldCheck { get; set; }
    public TaskCompletionSource? HoldApply { get; set; }
    public Exception? ApplyFailure { get; set; }
    public Action? OnApply { get; set; }

    public int CheckCalls { get; private set; }
    public List<(CatalogChannel From, CatalogChannel To)> PreviewedSwitches { get; } = [];
    public int ApplyCalls { get; private set; }
    public CatalogUpdateCheck? AppliedCheck { get; private set; }
    public CatalogSections? AppliedSections { get; private set; }

    public async Task<CatalogUpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        CheckCalls++;
        if (HoldCheck is not null) await HoldCheck.Task;
        return NextCheck();
    }

    public Task<CatalogUpdateCheck> CheckAsync(CatalogChannel channel, CancellationToken ct = default) =>
        PreviewSwitchAsync(CatalogChannel.Stable, channel, ct);

    public async Task<CatalogUpdateCheck> PreviewSwitchAsync(CatalogChannel from, CatalogChannel to, CancellationToken ct = default)
    {
        PreviewedSwitches.Add((from, to));
        if (HoldCheck is not null) await HoldCheck.Task;
        return (NextPreview?.Invoke(to) ?? NextCheck() with { Channel = to }) with { IsSwitchPreview = true, FollowedChannel = from };
    }

    public async Task<CatalogApplyResult> ApplyAsync(CatalogUpdateCheck check, CatalogSections sections,
        IProgress<CatalogDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        ApplyCalls++;
        AppliedCheck = check;
        AppliedSections = sections;
        OnApply?.Invoke();
        if (HoldApply is not null) await HoldApply.Task.WaitAsync(ct);
        if (ApplyFailure is not null) throw ApplyFailure;
        return NextApply();
    }
}
```

- [ ] **Step 2: Failing coordinator tests**

Create `DiffusionNexus.Tests/Catalog/CatalogUpdateCoordinatorTests.cs`:

```csharp
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Infrastructure.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Catalog;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Catalog;

public sealed partial class CatalogUpdateCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dn-catalog-coordinator-{Guid.NewGuid():N}");
    private readonly FakeCatalogUpdateService _service = new();
    private readonly Mock<ICatalog> _catalog = new();
    private readonly InstallActivity _installs = new();
    private readonly UnifiedLogger _log = new();
    private readonly FakePreferences _preferences = new();
    private readonly CatalogOptions _options;
    private string? _environment;

    public CatalogUpdateCoordinatorTests()
    {
        Directory.CreateDirectory(_dir);
        // SDK 2.3.0 shared folders, what the app runs with: one folder per channel under a shared root.
        _options = new CatalogOptions { SharedRoot = Path.Combine(_dir, "shared"), Channel = CatalogChannel.Stable };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private CatalogUpdateCoordinator Create() =>
        new(_service, _catalog.Object, _options, _preferences, _installs, () => _environment, _log);

    private void WriteState(CatalogChannel folder, int version, CatalogChannel? recorded = null, SectionOrigin origin = SectionOrigin.Remote) =>
        CatalogChecks.State(version, recorded ?? folder, origin).Save(_options.WriteFolderFor(folder, 1));

    private static void Settle(CatalogUpdateCoordinator coordinator) =>
        SpinWait.SpinUntil(() => coordinator.Phase is not (CatalogUpdatePhase.Applying or CatalogUpdatePhase.Checking), 10_000).Should().BeTrue();

    private IEnumerable<string> Logged => _log.GetEntries().Where(e => e.Source == "Catalog").Select(e => e.Message);

    internal sealed class FakePreferences : ICatalogPreferenceStore
    {
        public CatalogPreferences Stored { get; set; } = new("Stable", false);
        public Exception? LoadFailure { get; set; }
        public Exception? SaveFailure { get; set; }
        public List<CatalogChannel> SavedChannels { get; } = [];

        public Task<CatalogPreferences> LoadAsync(CancellationToken ct = default) =>
            LoadFailure is not null ? Task.FromException<CatalogPreferences>(LoadFailure) : Task.FromResult(Stored);

        public Task SaveChannelAsync(CatalogChannel channel, CancellationToken ct = default)
        {
            if (SaveFailure is not null) return Task.FromException(SaveFailure);
            SavedChannels.Add(channel);
            Stored = Stored with { Channel = channel.ToString() };
            return Task.CompletedTask;
        }

        public Task SaveApplyAutomaticallyAsync(bool on, CancellationToken ct = default)
        {
            if (SaveFailure is not null) return Task.FromException(SaveFailure);
            Stored = Stored with { ApplyAutomatically = on };
            return Task.CompletedTask;
        }
    }

    // ----- channel -----

    [Fact]
    public async Task The_stored_channel_is_followed_and_written_into_the_sdk_options()
    {
        _preferences.Stored = new CatalogPreferences("Preview", false);
        using var coordinator = Create();

        (await coordinator.ResolveChannelAsync()).Should().Be(CatalogChannel.Preview);

        _options.Channel.Should().Be(CatalogChannel.Preview);
        coordinator.ChannelSource.Should().Be(CatalogChannelSource.Setting);
        Logged.Should().Contain("Following the Preview catalog channel (Setting)");
    }

    [Fact]
    public async Task The_environment_variable_wins_for_this_run()
    {
        _preferences.Stored = new CatalogPreferences("Stable", false);
        _environment = "preview";
        using var coordinator = Create();

        await coordinator.ResolveChannelAsync();

        coordinator.Channel.Should().Be(CatalogChannel.Preview);
        coordinator.ChannelSource.Should().Be(CatalogChannelSource.Environment);
    }

    [Fact]
    public async Task Unreadable_preferences_follow_Stable_apply_nothing_and_are_read_again_next_time()
    {
        _preferences.LoadFailure = new IOException("database locked");
        using var coordinator = Create();

        await coordinator.ResolveChannelAsync();
        coordinator.Channel.Should().Be(CatalogChannel.Stable);
        coordinator.AutoApply.Should().BeFalse();

        _preferences.LoadFailure = null;
        _preferences.Stored = new CatalogPreferences("Preview", true);
        await coordinator.ResolveChannelAsync();
        coordinator.Channel.Should().Be(CatalogChannel.Preview, "a failed read never latches");
        coordinator.AutoApply.Should().BeTrue();
    }

    // ----- check: per-app notices (spec §4) -----

    [Fact]
    public async Task A_release_with_only_installer_changes_is_not_announced()
    {
        _service.NextCheck = () => CatalogChecks.Available(7, CatalogChannel.Stable, [CatalogChecks.WorkloadAdded("Qwen-Image-Edit", "V1.0")])
            .Targeting(WorkloadTargetType.Installer);
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.LastCheck!.Outcome.Should().Be(CatalogUpdateOutcome.UpdatesAvailable, "the folder is behind; releases are full snapshots");
        coordinator.LastCheck.HasChanges.Should().BeFalse();
        coordinator.LastCheck.Workloads.Should().BeEmpty();
        coordinator.UpdateAvailable.Should().BeFalse();
        coordinator.CanApply.Should().BeFalse();
        coordinator.LastCheckedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_release_for_the_app_is_announced_with_only_its_rows()
    {
        var app = CatalogChecks.WorkloadUpdated("Upscaling-Z-Image-Turbo", "V1.6", "V1.7");
        var installer = CatalogChecks.WorkloadAdded("Qwen-Image-Edit", "V1.0");
        _service.NextCheck = () => CatalogChecks.Available(7, CatalogChannel.Stable, [app, installer]) with
        {
            Index = new CatalogAudienceIndex(new Dictionary<Guid, WorkloadAudienceInfo>
            {
                [app.Id] = new(WorkloadTargetType.DiffusionNexusCore, true),
                [installer.Id] = new(WorkloadTargetType.Installer, true),
            }, new Dictionary<Guid, IReadOnlyList<Guid>>()),
        };
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.UpdateAvailable.Should().BeTrue();
        coordinator.LastCheck!.Workloads.Should().ContainSingle().Which.Name.Should().Be("Upscaling-Z-Image-Turbo");
    }

    [Fact]
    public async Task A_check_without_targets_counts_for_the_app()
    {
        _service.NextCheck = () => CatalogChecks.Available(6);   // a manifest packed before 2.3.0: Index empty
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.UpdateAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_release_that_replaces_content_from_another_channel_is_offered()
    {
        // Following Preview; the Preview folder holds the embedded Stable seed (a switch that never landed).
        _preferences.Stored = new CatalogPreferences("Preview", false);
        WriteState(CatalogChannel.Preview, 5, recorded: CatalogChannel.Stable, origin: SectionOrigin.Embedded);
        _service.NextCheck = () => CatalogChecks.Available(6, CatalogChannel.Preview, [CatalogChecks.WorkloadAdded("Qwen-Image-Edit", "V1.0")])
            .Targeting(WorkloadTargetType.Installer);
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.LastCheck!.HasChanges.Should().BeFalse();
        coordinator.UpdateAvailable.Should().BeTrue("applying it is the only thing that replaces the other channel's content");
    }

    [Fact]
    public async Task A_check_that_throws_is_a_filtered_failed_check()
    {
        _service.NextCheck = () => throw new InvalidOperationException("boom");
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.LastCheck!.Outcome.Should().Be(CatalogUpdateOutcome.Failed);
        coordinator.LastCheck.Audience.Should().Be(CatalogAudience.DiffusionNexus);
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
    }

    // ----- apply -----

    [Fact]
    public async Task Apply_sends_the_whole_release_and_ends_applied()
    {
        _service.NextCheck = () => CatalogChecks.Available(7).Targeting(WorkloadTargetType.DiffusionNexusCore);
        _service.OnApply = () => WriteState(CatalogChannel.Stable, 7);
        using var coordinator = Create();
        await coordinator.CheckAsync();

        await coordinator.ApplyAsync();
        Settle(coordinator);

        _service.AppliedSections.Should().Be(CatalogSections.All);
        _service.AppliedCheck!.Unfiltered.Should().NotBeNull("the SDK sees the whole release");
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Applied);
        coordinator.Installed!.HighestCatalogVersion.Should().Be(7);
        Logged.Should().Contain(m => m.StartsWith("Updated: Upscaling-Z-Image-Turbo"));
    }

    [Fact]
    public async Task Apply_waits_while_an_install_runs_and_says_which()
    {
        _service.NextCheck = () => CatalogChecks.Available(7);
        using var coordinator = Create();
        await coordinator.CheckAsync();
        var running = _installs.Begin("Krea-2-Turbo");
        var changed = 0;
        coordinator.Changed += () => changed++;

        await coordinator.ApplyAsync();

        _service.ApplyCalls.Should().Be(0, "never swap the catalog under a running plan");
        coordinator.CanApply.Should().BeFalse();
        coordinator.ApplyBlockedReason.Should().Be("It can be applied once Krea-2-Turbo has finished.");

        running.Dispose();
        changed.Should().BeGreaterThan(0, "the page re-renders when the install ends");
        coordinator.CanApply.Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_apply_keeps_the_error_and_apply_is_the_retry()
    {
        _service.NextCheck = () => CatalogChecks.Available(7);
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.None, CatalogSections.All, "The download stalled.");
        using var coordinator = Create();
        await coordinator.CheckAsync();

        await coordinator.ApplyAsync();
        Settle(coordinator);

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
        coordinator.LastApplyFailed.Should().BeTrue();
        coordinator.CanApply.Should().BeTrue();
    }

    [Fact]
    public async Task An_apply_that_throws_reports_the_error_and_checks_again_what_is_left()
    {
        _service.NextCheck = () => CatalogChecks.Available(7);
        using var coordinator = Create();
        await coordinator.CheckAsync();
        _service.ApplyFailure = new InvalidOperationException("unexpected");
        _service.NextCheck = () => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate);   // both sections had landed before the throw

        await coordinator.ApplyAsync();
        Settle(coordinator);

        coordinator.LastApply!.Error.Should().Be("unexpected");
        coordinator.LastCheck!.Outcome.Should().Be(CatalogUpdateOutcome.UpToDate);
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
        _catalog.Verify(c => c.Invalidate(), Times.Once);
    }

    // ----- startup (spec §6 table) -----

    [Fact]
    public async Task Startup_with_the_toggle_off_logs_one_line_and_applies_nothing()
    {
        _service.NextCheck = () => CatalogChecks.Available(7).Targeting(WorkloadTargetType.DiffusionNexusCore);
        using var coordinator = Create();

        await coordinator.CheckAtStartupAsync();

        _service.ApplyCalls.Should().Be(0);
        Logged.Count(m => m == "Catalog update available – see Settings → Catalog").Should().Be(1);
    }

    [Fact]
    public async Task Startup_with_the_toggle_on_applies_silently_and_logs_the_changes()
    {
        _preferences.Stored = new CatalogPreferences("Stable", true);
        _service.NextCheck = () => CatalogChecks.Available(7).Targeting(WorkloadTargetType.DiffusionNexusCore);
        _service.OnApply = () => WriteState(CatalogChannel.Stable, 7);
        using var coordinator = Create();

        await coordinator.CheckAtStartupAsync();
        Settle(coordinator);

        _service.ApplyCalls.Should().Be(1);
        coordinator.LastApplyAutomatic.Should().BeTrue();
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Applied);
        Logged.Should().Contain("Applying the catalog update automatically (setting on)");
        Logged.Should().Contain(m => m.StartsWith("Updated: Upscaling-Z-Image-Turbo"));
    }

    [Fact]
    public async Task A_release_with_only_installer_changes_is_still_applied_automatically()
    {
        _preferences.Stored = new CatalogPreferences("Stable", true);
        _service.NextCheck = () => CatalogChecks.Available(7, CatalogChannel.Stable, [CatalogChecks.WorkloadAdded("Qwen-Image-Edit", "V1.0")])
            .Targeting(WorkloadTargetType.Installer);
        _service.OnApply = () => WriteState(CatalogChannel.Stable, 7);
        using var coordinator = Create();

        await coordinator.CheckAtStartupAsync();
        Settle(coordinator);

        _service.ApplyCalls.Should().Be(1, "full snapshots: the folder both apps read stays current");
        Logged.Should().NotContain("Catalog update available – see Settings → Catalog");
    }

    [Fact]
    public async Task Startup_with_the_toggle_off_and_only_installer_changes_does_nothing_visible()
    {
        _service.NextCheck = () => CatalogChecks.Available(7, CatalogChannel.Stable, [CatalogChecks.WorkloadAdded("Qwen-Image-Edit", "V1.0")])
            .Targeting(WorkloadTargetType.Installer);
        using var coordinator = Create();

        await coordinator.CheckAtStartupAsync();

        _service.ApplyCalls.Should().Be(0);
        Logged.Should().NotContain("Catalog update available – see Settings → Catalog");
    }

    [Fact]
    public async Task A_failed_automatic_apply_can_be_retried()
    {
        _preferences.Stored = new CatalogPreferences("Stable", true);
        _service.NextCheck = () => CatalogChecks.Available(7, CatalogChannel.Stable, [CatalogChecks.WorkloadAdded("Qwen-Image-Edit", "V1.0")])
            .Targeting(WorkloadTargetType.Installer);
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.None, CatalogSections.All, "The download stalled.");
        using var coordinator = Create();

        await coordinator.CheckAtStartupAsync();
        Settle(coordinator);

        coordinator.LastApplyFailed.Should().BeTrue();
        coordinator.CanApply.Should().BeTrue("Apply is the retry, whoever the changes were for");
    }

    // ----- the setting -----

    [Fact]
    public async Task The_toggle_is_saved_and_refused_while_an_apply_runs()
    {
        _service.NextCheck = () => CatalogChecks.Available(7);
        _service.HoldApply = new TaskCompletionSource();
        using var coordinator = Create();
        await coordinator.CheckAsync();
        _ = coordinator.ApplyAsync();

        (await coordinator.SetAutoApplyAsync(true)).Should().BeFalse();
        _preferences.Stored.ApplyAutomatically.Should().BeFalse();

        _service.HoldApply.SetResult();
        Settle(coordinator);
        (await coordinator.SetAutoApplyAsync(true)).Should().BeTrue();
        _preferences.Stored.ApplyAutomatically.Should().BeTrue();
        coordinator.AutoApply.Should().BeTrue();
    }

    [Fact]
    public async Task A_throwing_changed_handler_never_breaks_a_check()
    {
        using var coordinator = Create();
        coordinator.Changed += () => throw new InvalidOperationException("disposed view");

        await coordinator.CheckAsync();

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
    }
}
```

- [ ] **Step 3: Run them**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q --filter "FullyQualifiedName~CatalogUpdateCoordinatorTests" 2>&1 | grep -E "error CS|Passed!|Failed!" | head -3`
Expected: compile error (`CatalogUpdateCoordinator` missing).

- [ ] **Step 4: The offer rule**

Create `DiffusionNexus.UI/Services/Catalog/CatalogUpdateOffer.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.UI.Services.Catalog;

/// <summary>
/// What Settings → Catalog offers, as pure functions of the coordinator's state -- shared by the coordinator
/// and the page's test stub so the page tests see the real rule (installer PR #54 lesson).
/// </summary>
public static class CatalogUpdateOffer
{
    /// <summary>
    /// The followed channel's folder is behind its release, whoever the changes are for. A switch preview
    /// whose apply failed counts too, whatever it said: the SDK found the target folder needs the release
    /// (it tried to fill it), and applying the preview again is the retry. Not while a switch waits for an
    /// answer (the check describes the channel being left), nor once applied.
    /// </summary>
    public static bool ReleaseAvailable(CatalogUpdateCheck? check, CatalogUpdatePhase phase, bool pendingSwitch, bool lastApplyFailed) =>
        !pendingSwitch && phase != CatalogUpdatePhase.Applied && check is not null
        && (check.Outcome == CatalogUpdateOutcome.UpdatesAvailable || (check.IsSwitchPreview && lastApplyFailed));

    /// <summary>
    /// Announced and offered: the release changes something this app shows; or a failed apply is retried
    /// whoever the changes were for; or the installed content is another channel's (an embedded seed after a
    /// switch) and only this release replaces it.
    /// </summary>
    public static bool Offered(CatalogUpdateCheck? check, CatalogUpdatePhase phase, bool pendingSwitch, bool lastApplyFailed, bool contentFromAnotherChannel) =>
        ReleaseAvailable(check, phase, pendingSwitch, lastApplyFailed)
        && (check!.HasChanges || lastApplyFailed || contentFromAnotherChannel);
}
```

- [ ] **Step 5: The coordinator**

Create `DiffusionNexus.UI/Services/Catalog/CatalogUpdateCoordinator.cs`:

```csharp
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.UI.Services.Catalog;

/// <summary>Idle → Checking → Checked → Applying → Applied. A failed or cancelled apply returns to Checked.</summary>
public enum CatalogUpdatePhase { Idle, Checking, Checked, Applying, Applied }

/// <summary>A switch the user has been warned about and not answered yet.</summary>
/// <param name="Preview">The check of the target channel, filtered for this app; Switch applies exactly this.</param>
public sealed record CatalogChannelSwitch(CatalogUpdateCheck Preview, ChannelSwitchWarning Warning)
{
    public CatalogChannel Target => Preview.Channel;
}

/// <summary>
/// The catalog-update state Settings → Catalog renders (#618). One process-wide instance; the view model
/// subscribes to <see cref="Changed"/> and marshals to the UI thread itself.
/// </summary>
public interface ICatalogUpdateCoordinator
{
    /// <summary>The channel this app follows: environment → its own saved setting → Stable.</summary>
    CatalogChannel Channel { get; }
    CatalogChannelSource ChannelSource { get; }
    /// <summary>The SDK's local override folder, for the OverrideActive message. Null when none is configured.</summary>
    string? OverridePath { get; }
    CatalogUpdatePhase Phase { get; }
    /// <summary>Always filtered for Diffusion Nexus (its Audience is set). After a switch: see SwitchChannelAsync.</summary>
    CatalogUpdateCheck? LastCheck { get; }
    DateTimeOffset? LastCheckedAt { get; }
    /// <summary>catalog-state.json of the folder the app reads, as of the last check, apply or switch.</summary>
    LocalCatalogState? Installed { get; }
    /// <summary>Non-null only while <see cref="Phase"/> is Applying and bytes flow.</summary>
    CatalogDownloadProgress? Progress { get; }
    CatalogApplyResult? LastApply { get; }
    /// <summary>The apply in <see cref="LastApply"/> failed or landed only part of the release.</summary>
    bool LastApplyFailed { get; }
    /// <summary>The apply in <see cref="LastApply"/> (or running) was started by the startup check.</summary>
    bool LastApplyAutomatic { get; }
    /// <summary>"Apply catalog updates automatically". False until the preferences are read, and while they cannot be.</summary>
    bool AutoApply { get; }
    /// <summary>The release is announced and offered (<see cref="CatalogUpdateOffer.Offered"/>).</summary>
    bool UpdateAvailable { get; }
    bool CanApply { get; }
    /// <summary>Why Apply is withheld although an update is available (an install is running). Null otherwise.</summary>
    string? ApplyBlockedReason { get; }
    /// <summary>A switch waiting for Switch or Keep. While set, checks, applies and other switches are refused.</summary>
    CatalogChannelSwitch? PendingSwitch { get; }

    /// <summary>Raised after every state change, on any thread. Handlers that throw are logged and skipped.</summary>
    event Action? Changed;

    /// <summary>Resolves <see cref="Channel"/> without a network check and writes it into CatalogOptions.Channel. Never throws.</summary>
    Task<CatalogChannel> ResolveChannelAsync(CancellationToken ct = default);
    /// <summary>Never throws. A running check is joined; while an apply runs it is a no-op that returns at once.</summary>
    Task CheckAsync(CancellationToken ct = default);
    /// <summary>Never throws. A no-op unless <see cref="CanApply"/>. Returns once the download has started.</summary>
    Task ApplyAsync(CancellationToken ct = default);
    /// <summary>The startup check (spec §6). Never throws.</summary>
    Task CheckAtStartupAsync(CancellationToken ct = default);
    /// <summary>Saves the toggle. False, with nothing saved, while a check, an apply or a switch runs. Save errors propagate.</summary>
    Task<bool> SetAutoApplyAsync(bool on, CancellationToken ct = default);
    /// <summary>
    /// Previews <paramref name="target"/>. A diff that removes or changes Diffusion Nexus workloads waits in
    /// <see cref="PendingSwitch"/>; otherwise the switch goes ahead as <see cref="ConfirmSwitchAsync"/> would.
    /// Returns once the choice is saved; a download runs on. Save errors propagate (then nothing changed).
    /// </summary>
    Task SwitchChannelAsync(CatalogChannel target, CancellationToken ct = default);
    /// <summary>"Switch": saves the channel, then lets the SDK fill the target folder. A no-op without a pending switch.</summary>
    Task ConfirmSwitchAsync(CancellationToken ct = default);
    /// <summary>"Keep": drops the pending switch. Saves nothing.</summary>
    void KeepChannel();
}

/// <summary>
/// Owns the check / apply / switch lifecycle around the SDK's <see cref="ICatalogUpdateService"/> and is the
/// one writer of <see cref="CatalogOptions.Channel"/>. Every check is filtered for Diffusion Nexus
/// (<see cref="CatalogAudience.DiffusionNexus"/>); releases are always applied whole. Every step logs to the
/// Unified Console (source "Catalog"). Mirrors the installer's coordinator (Into-The-Latent/DiffusionNexus.Installer#54).
/// </summary>
public sealed class CatalogUpdateCoordinator : ICatalogUpdateCoordinator, IDisposable
{
    public const string LogSource = "Catalog";

    private readonly ICatalogUpdateService _updates;
    private readonly ICatalog _catalog;
    private readonly CatalogOptions _options;
    private readonly ICatalogPreferenceStore _preferences;
    private readonly IInstallActivity _installs;
    private readonly Func<string?> _readEnvironment;
    private readonly IUnifiedLogger? _logger;
    private readonly CatalogAudience _audience = CatalogAudience.DiffusionNexus;

    private readonly Lock _gate = new();
    private Task? _inFlight;
    private bool _channelResolved;
    private bool _autoApplyResolved;
    private bool _switching;
    private bool _savingAutoApply;
    private bool _applyAutomatic;
    private bool _installRunning;

    public CatalogUpdateCoordinator(
        ICatalogUpdateService updates,
        ICatalog catalog,
        CatalogOptions options,
        ICatalogPreferenceStore preferences,
        IInstallActivity installs,
        Func<string?> readEnvironment,
        IUnifiedLogger? logger = null)
    {
        _updates = updates;
        _catalog = catalog;
        _options = options;
        _preferences = preferences;
        _installs = installs;
        _readEnvironment = readEnvironment;
        _logger = logger;
        _installRunning = installs.Running is not null;
        _installs.Changed += OnInstallsChanged;
    }

    public CatalogChannel Channel { get; private set; } = CatalogChannel.Stable;
    public CatalogChannelSource ChannelSource { get; private set; } = CatalogChannelSource.Default;
    public string? OverridePath => _options.LocalOverridePath;
    public CatalogUpdatePhase Phase { get; private set; } = CatalogUpdatePhase.Idle;
    public CatalogUpdateCheck? LastCheck { get; private set; }
    public DateTimeOffset? LastCheckedAt { get; private set; }
    public LocalCatalogState? Installed { get; private set; }
    public CatalogDownloadProgress? Progress { get; private set; }
    public CatalogApplyResult? LastApply { get; private set; }
    public bool AutoApply { get; private set; }
    public CatalogChannelSwitch? PendingSwitch { get; private set; }

    public bool LastApplyFailed { get { lock (_gate) return LastApply is { } r && (r.Error is not null || r.Failed != CatalogSections.None); } }

    public bool LastApplyAutomatic { get { lock (_gate) return _applyAutomatic && (LastApply is not null || Phase is CatalogUpdatePhase.Applying or CatalogUpdatePhase.Applied); } }

    public bool UpdateAvailable => CatalogUpdateOffer.Offered(LastCheck, Phase, PendingSwitch is not null, LastApplyFailed, ContentFromAnotherChannel);

    private bool ReleaseAvailable => CatalogUpdateOffer.ReleaseAvailable(LastCheck, Phase, PendingSwitch is not null, LastApplyFailed);

    /// <summary>The installed content records a channel other than the one followed (an embedded seed after a switch).</summary>
    private bool ContentFromAnotherChannel => Installed is { } s && (s.Workloads?.Channel ?? s.Workflows?.Channel) is { } recorded && recorded != Channel;

    public bool CanApply => UpdateAvailable && Phase == CatalogUpdatePhase.Checked && _installs.Running is null;

    public string? ApplyBlockedReason => UpdateAvailable && _installs.Running is { } name ? $"It can be applied once {name} has finished." : null;

    public event Action? Changed;

    // ----- channel -----

    public async Task<CatalogChannel> ResolveChannelAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureChannelAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warn(LogCategory.General, LogSource, $"The catalog channel could not be resolved; following {Channel}.", ex.Message);
        }
        Raise();
        lock (_gate) return Channel;
    }

    /// <summary>Reads the channel and the toggle once; a failed read latches nothing and is retried next time.</summary>
    private async Task EnsureChannelAsync(CancellationToken ct)
    {
        lock (_gate) { if (_channelResolved && _autoApplyResolved) return; }

        CatalogPreferences? stored = null;
        try
        {
            stored = await _preferences.LoadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warn(LogCategory.General, LogSource, "The catalog setting could not be read; read again on the next check. Nothing is applied automatically until it can be.", ex.Message);
        }

        lock (_gate)
        {
            if (!_autoApplyResolved)
            {
                AutoApply = stored?.ApplyAutomatically ?? false;
                if (stored is not null) _autoApplyResolved = true;
            }
            // A switch finished while the read above was awaited: its answer is newer.
            if (_channelResolved) return;
            ApplyResolution(_readEnvironment(), stored?.Channel);
            if (stored is not null) _channelResolved = true;
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void ApplyResolution(string? environmentValue, string? savedValue)
    {
        var (channel, source) = CatalogChannelResolver.Resolve(environmentValue, savedValue);
        Channel = channel;
        ChannelSource = source;
        _options.Channel = channel;
        _logger?.Info(LogCategory.General, LogSource, $"Following the {channel} catalog channel ({source})");
    }

    // ----- check -----

    public Task CheckAsync(CancellationToken ct = default)
    {
        Task inFlight;
        lock (_gate)
        {
            if (_switching || PendingSwitch is not null)
            {
                _logger?.Info(LogCategory.General, LogSource, "Catalog check refused: a channel switch is in progress");
                return Task.CompletedTask;
            }
            if (_inFlight is { IsCompleted: false })
            {
                if (Phase != CatalogUpdatePhase.Applying) return _inFlight;
                _logger?.Info(LogCategory.General, LogSource, "Catalog check refused: an apply is in progress");
                return Task.CompletedTask;
            }
            Phase = CatalogUpdatePhase.Checking;
            Progress = null;
            LastApply = null;
            _applyAutomatic = false;
            inFlight = _inFlight = Task.Run(() => CheckCoreAsync(ct), CancellationToken.None);
        }
        Raise();
        return inFlight;
    }

    private async Task CheckCoreAsync(CancellationToken ct)
    {
        try
        {
            await EnsureChannelAsync(ct).ConfigureAwait(false);
            _logger?.Info(LogCategory.General, LogSource, $"Catalog update check started on {Channel}");
            var check = (await _updates.CheckAsync(ct).ConfigureAwait(false)).For(_audience);
            var installed = LocalCatalogState.Load(_options.InstalledCatalogPath);
            lock (_gate)
            {
                LastCheck = check;
                LastCheckedAt = DateTimeOffset.Now;
                // An empty state (a channel folder that does not exist yet) describes nothing; the last known one stays.
                if (installed.RecordsAnything() || Installed is null) Installed = installed;
                Phase = CatalogUpdatePhase.Checked;
            }
            _logger?.Info(LogCategory.General, LogSource,
                $"Catalog update check: {check.Outcome} (remote v{check.Remote?.CatalogVersion}, installed v{installed.HighestCatalogVersion}, " +
                $"{check.Workloads.Count} workload / {check.Workflows.Count} workflow changes for Diffusion Nexus, " +
                $"{check.Unfiltered!.Workloads.Count + check.Unfiltered.Workflows.Count} in the release)",
                check.Error);
        }
        catch (Exception ex)
        {
            // The SDK's CheckAsync never throws; this is the last line of defence for a task nobody awaits.
            _logger?.Error(LogCategory.General, LogSource, "Catalog update check failed unexpectedly", ex);
            lock (_gate)
            {
                LastCheck = FailedCheck(Channel, Installed, ex.Message);
                LastCheckedAt = DateTimeOffset.Now;
                Phase = CatalogUpdatePhase.Checked;
            }
        }
        Raise();
    }

    /// <summary>A check that never ran, filtered like every other so <see cref="LastCheck"/> always carries the audience.</summary>
    private CatalogUpdateCheck FailedCheck(CatalogChannel channel, LocalCatalogState? installed, string error) =>
        new CatalogUpdateCheck(CatalogUpdateOutcome.Failed, channel, null, installed ?? new LocalCatalogState(), [], [], error).For(_audience);

    // ----- apply -----

    public Task ApplyAsync(CancellationToken ct = default) => StartApply(automatic: false, ct);

    public async Task CheckAtStartupAsync(CancellationToken ct = default)
    {
        await CheckAsync(ct).ConfigureAwait(false);

        bool offered, release, auto;
        lock (_gate) (offered, release, auto) = (UpdateAvailable, ReleaseAvailable, AutoApply);

        if (auto && release)
        {
            // Not awaited and not under the caller's token: returns once the download has started.
            _ = StartApply(automatic: true, CancellationToken.None);
            return;
        }
        if (offered) _logger?.Info(LogCategory.General, LogSource, "Catalog update available – see Settings → Catalog");
        else if (release) _logger?.Info(LogCategory.General, LogSource, "The catalog release changes nothing Diffusion Nexus shows; it lands with the next apply.");
    }

    private Task StartApply(bool automatic, CancellationToken ct)
    {
        CatalogUpdateCheck check;
        Task inFlight;
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false })
            {
                _logger?.Info(LogCategory.General, LogSource, $"Catalog apply refused: an apply or check is already running (automatic {automatic})");
                return Task.CompletedTask;
            }
            // A click applies what the page offered. The startup apply takes the release whether or not it
            // changes anything Diffusion Nexus shows: full snapshots, and the folder is shared with the installer.
            var canStart = automatic
                ? ReleaseAvailable && Phase == CatalogUpdatePhase.Checked && _installs.Running is null
                : CanApply;
            if (_switching || !canStart)
            {
                _logger?.Info(LogCategory.General, LogSource,
                    $"Catalog apply refused: phase {Phase}, offered {UpdateAvailable}, install running {_installs.Running ?? "none"}, switching {_switching}, automatic {automatic}");
                return Task.CompletedTask;
            }
            if (automatic) _logger?.Info(LogCategory.General, LogSource, "Applying the catalog update automatically (setting on)");
            check = LastCheck!;
            Phase = CatalogUpdatePhase.Applying;
            LastApply = null;
            _applyAutomatic = automatic;
            Progress = null;
            inFlight = _inFlight = Task.Run(() => ApplyCoreAsync(check, ct), CancellationToken.None);
        }
        Raise();
        return inFlight;
    }

    private async Task ApplyCoreAsync(CatalogUpdateCheck check, CancellationToken ct)
    {
        _logger?.Info(LogCategory.General, LogSource, $"Catalog apply started: v{check.Remote?.CatalogVersion} from {check.Channel}");
        try
        {
            var result = await _updates.ApplyAsync(check, CatalogSections.All, new ProgressRelay(this), ct).ConfigureAwait(false);
            var installed = LocalCatalogState.Load(_options.InstalledCatalogPath);
            var succeeded = result.Failed == CatalogSections.None && result.Error is null;
            var landedSomething = result.Applied != CatalogSections.None;
            var fresh = await AfterSwitchAsync(check, result.Applied, succeeded, installed).ConfigureAwait(false);

            lock (_gate)
            {
                LastApply = result;
                if (installed.RecordsAnything() || Installed is null) Installed = installed;
                Progress = null;
                if (fresh is not null) LastCheck = fresh;
                // A switch whose target folder was already current applies nothing: nothing was "updated".
                Phase = succeeded && landedSomething ? CatalogUpdatePhase.Applied : CatalogUpdatePhase.Checked;
            }
            if (landedSomething) LogChanges(check);
            if (succeeded)
                _logger?.Info(LogCategory.General, LogSource, landedSomething
                    ? $"Catalog updated to v{installed.HighestCatalogVersion} ({check.Channel})."
                    : $"The {check.Channel} catalog folder was already current.");
            else
                _logger?.Warn(LogCategory.General, LogSource, $"Catalog update incomplete: {result.Failed} not applied (applied: {result.Applied}).", result.Error);
        }
        catch (OperationCanceledException)
        {
            _logger?.Info(LogCategory.General, LogSource, "Catalog apply cancelled; nothing was changed");
            lock (_gate)
            {
                LastApply = null;
                Progress = null;
                Phase = CatalogUpdatePhase.Checked;
            }
        }
        catch (Exception ex)
        {
            // The SDK catches only I/O errors per section; anything else escapes, possibly after a section
            // landed, and then its catalog cache was not invalidated either. A fresh check says what is left.
            _logger?.Error(LogCategory.General, LogSource, "Catalog apply failed unexpectedly", ex);
            TryInvalidate();
            var installed = LocalCatalogState.Load(_options.InstalledCatalogPath);
            var fresh = await CheckFollowedAsync().ConfigureAwait(false);
            lock (_gate)
            {
                LastApply = new CatalogApplyResult(CatalogSections.None, CatalogSections.All, ex.Message);
                if (installed.RecordsAnything() || Installed is null) Installed = installed;
                LastCheck = fresh;
                LastCheckedAt = DateTimeOffset.Now;
                Progress = null;
                Phase = CatalogUpdatePhase.Checked;
            }
        }
        Raise();
    }

    /// <summary>
    /// What <see cref="LastCheck"/> becomes after the apply of a switch preview, or null to keep it. The preview
    /// compared the target's release with the folder being left; the SDK's apply checked the target folder
    /// itself. Landed everything: the preview stands. Landed nothing without an error: the SDK's verdict that the
    /// folder is current, recorded as up to date. Failed with nothing landed: the preview stands -- applying it
    /// again is the retry. Landed part: a check of the folder now followed tells what is left. (Task 6 uses it.)
    /// </summary>
    private async Task<CatalogUpdateCheck?> AfterSwitchAsync(CatalogUpdateCheck check, CatalogSections applied, bool succeeded, LocalCatalogState installed)
    {
        if (!check.IsSwitchPreview || applied == CatalogSections.All) return null;
        if (succeeded && applied == CatalogSections.None)
            return new CatalogUpdateCheck(CatalogUpdateOutcome.UpToDate, check.Channel, check.Remote, installed, [], [], null).For(_audience);
        if (applied == CatalogSections.None) return null;
        return await CheckFollowedAsync().ConfigureAwait(false);
    }

    /// <summary>A check of the folder now followed. Never throws; a check that throws is a Failed check.</summary>
    private async Task<CatalogUpdateCheck> CheckFollowedAsync()
    {
        try
        {
            var fresh = (await _updates.CheckAsync(CancellationToken.None).ConfigureAwait(false)).For(_audience);
            _logger?.Info(LogCategory.General, LogSource, $"Catalog check of the followed folder: {fresh.Outcome} (remote v{fresh.Remote?.CatalogVersion})", fresh.Error);
            return fresh;
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.General, LogSource, "Catalog check of the followed folder failed unexpectedly", ex);
            CatalogChannel channel;
            LocalCatalogState? installed;
            lock (_gate) (channel, installed) = (Channel, Installed);
            return FailedCheck(channel, installed, ex.Message);
        }
    }

    private void LogChanges(CatalogUpdateCheck check)
    {
        foreach (var row in CatalogChangeRows.Build(check))
            _logger?.Info(LogCategory.General, LogSource, $"{row.Change}: {row.Name} ({row.VersionText})");
    }

    private void TryInvalidate()
    {
        try { _catalog.Invalidate(); }
        catch (Exception ex) { _logger?.Warn(LogCategory.General, LogSource, "The catalog cache could not be refreshed after a failed apply.", ex.Message); }
    }

    // ----- the setting -----

    public async Task<bool> SetAutoApplyAsync(bool on, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_switching || _savingAutoApply || PendingSwitch is not null || Phase is CatalogUpdatePhase.Checking or CatalogUpdatePhase.Applying)
            {
                _logger?.Info(LogCategory.General, LogSource, $"Apply catalog updates automatically: change to {(on ? "on" : "off")} refused while {Phase}");
                return false;
            }
            _savingAutoApply = true;
        }
        try
        {
            await _preferences.SaveApplyAutomaticallyAsync(on, ct).ConfigureAwait(false);
            lock (_gate)
            {
                AutoApply = on;
                _autoApplyResolved = true;
            }
            _logger?.Info(LogCategory.General, LogSource, $"Apply catalog updates automatically: saved {(on ? "on" : "off")}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warn(LogCategory.General, LogSource, $"Apply catalog updates automatically: {(on ? "on" : "off")} could not be saved; nothing was changed", ex.Message);
            throw;
        }
        finally
        {
            lock (_gate) _savingAutoApply = false;
            Raise();
        }
    }

    // ----- switch (Task 6) -----

    public Task SwitchChannelAsync(CatalogChannel target, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ConfirmSwitchAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public void KeepChannel() => throw new NotImplementedException();

    // ----- plumbing -----

    private void OnInstallsChanged()
    {
        var running = _installs.Running is not null;
        bool flipped;
        lock (_gate)
        {
            flipped = running != _installRunning;
            _installRunning = running;
        }
        if (flipped) Raise();
    }

    /// <summary>Each subscriber on its own: a throwing handler (a view gone away) must not fault a background task.</summary>
    private void Raise()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { _logger?.Warn(LogCategory.General, LogSource, "A catalog page handler threw.", ex.Message); }
        }
    }

    /// <summary>Raises only when the shown percent moves: the SDK reports every ~80 KB.</summary>
    private sealed class ProgressRelay(CatalogUpdateCoordinator owner) : IProgress<CatalogDownloadProgress>
    {
        private long? _lastShown;

        public void Report(CatalogDownloadProgress value)
        {
            var shown = value.TotalBytes is > 0 ? (long)Math.Round(100.0 * value.BytesReceived / value.TotalBytes.Value) : (long)Math.Round(value.BytesReceived / 104_857.6);
            bool moved;
            lock (owner._gate)
            {
                owner.Progress = value;
                moved = shown != _lastShown;
                _lastShown = shown;
            }
            if (moved) owner.Raise();
        }
    }

    public void Dispose() => _installs.Changed -= OnInstallsChanged;
}

public static class LocalCatalogStateExtensions
{
    /// <summary>A section was recorded. False for the empty state a folder that does not exist yet reads as.</summary>
    public static bool RecordsAnything(this LocalCatalogState state) => state.Workloads is not null || state.Workflows is not null;
}
```

Note `CheckAtStartupAsync` logs a wait when the startup apply is refused because an install runs — `StartApply` already logs the refusal, so nothing extra is needed.

- [ ] **Step 6: Run the coordinator tests**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q --filter "FullyQualifiedName~CatalogUpdateCoordinatorTests" 2>&1 | tail -3`
Expected: all pass. If `A_release_that_replaces_content_from_another_channel_is_offered` fails because `Installed` is null, check that `CheckCoreAsync` loads from `_options.InstalledCatalogPath` after `EnsureChannelAsync` set the channel (shared folders: the path then names the Preview folder).

- [ ] **Step 7: Whole suite, commit**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q 2>&1 | tail -3` — Expected: `Failed: 0`.

```bash
git add -A
git commit -m "feat(catalog): #618 CatalogUpdateCoordinator — the app's own channel, checks filtered for Diffusion Nexus, apply held during installs, startup per spec"
git push
```

---

### Task 6: Switching channels

**Files:**
- Modify: `DiffusionNexus.UI/Services/Catalog/CatalogUpdateCoordinator.cs` (replace the three `NotImplementedException` members)
- Test: `DiffusionNexus.Tests/Catalog/CatalogUpdateCoordinatorTests.cs` (new region, partial class file `CatalogUpdateCoordinatorSwitchTests.cs` in the same folder)

**Interfaces:**
- Consumes: Task 5's class internals (`_gate`, `_inFlight`, `ApplyResolution`, `ApplyCoreAsync`, `CheckFollowedAsync`, `FailedCheck`, `Raise`, `RecordsAnything`); SDK `ICatalogUpdateService.PreviewSwitchAsync(from, to, ct)`, `ChannelSwitchWarning.For(check)`, `ICatalog.State`.
- Produces: working `SwitchChannelAsync`, `ConfirmSwitchAsync`, `KeepChannel`, `PendingSwitch`.

Rules (spec §6 + installer PR #54):
- The preview is `PreviewSwitchAsync(followed, target).For(DiffusionNexus)`; a warning (removed or changed Diffusion Nexus workloads) waits in `PendingSwitch`.
- Switch: save the channel first (a failed save changes nothing and propagates), then follow it. With shared folders the preview is handed to `ApplyAsync` when it says UpdatesAvailable **or** UpToDate (the SDK checks the target folder itself and fills it when missing or behind); without shared folders only UpdatesAvailable is applied.
- The call returns once the choice is saved; an apply runs on (`_inFlight`). While an install runs the apply is held: the folder now followed is checked in the background under Checking.
- `Installed` is read from the target folder's state file at once when it records anything; otherwise from `ICatalog.State` (which seeds the folder) in the background; an empty state never replaces a known one.
- After the switch's apply: `AfterSwitchAsync` (Task 5) decides `LastCheck`.

- [ ] **Step 1: Failing tests**

Create `DiffusionNexus.Tests/Catalog/CatalogUpdateCoordinatorSwitchTests.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.UI.Services.Catalog;
using FluentAssertions;

namespace DiffusionNexus.Tests.Catalog;

public sealed partial class CatalogUpdateCoordinatorTests
{
    private static CatalogUpdateCheck PreviewRemovesAppWorkload(CatalogChannel channel) =>
        CatalogChecks.Available(6, channel, [CatalogChecks.WorkloadRemoved("Upscaling-Z-Image-Turbo Vision", "V1.0")])
            .Targeting(WorkloadTargetType.DiffusionNexusCore);

    [Fact]
    public async Task A_switch_that_removes_an_app_workload_waits_for_an_answer_and_saves_nothing()
    {
        _service.NextPreview = PreviewRemovesAppWorkload;
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);

        coordinator.PendingSwitch!.Warning.Removed.Should().Equal("Upscaling-Z-Image-Turbo Vision");
        _service.PreviewedSwitches.Should().Equal((CatalogChannel.Stable, CatalogChannel.Preview));
        _preferences.SavedChannels.Should().BeEmpty();
        coordinator.Channel.Should().Be(CatalogChannel.Stable);
        coordinator.UpdateAvailable.Should().BeFalse("the last check describes the channel being left");

        coordinator.KeepChannel();
        coordinator.PendingSwitch.Should().BeNull();
        _preferences.SavedChannels.Should().BeEmpty();
    }

    [Fact]
    public async Task A_switch_that_only_changes_installer_workloads_goes_ahead_without_asking()
    {
        _service.NextPreview = channel => CatalogChecks.Available(6, channel, [CatalogChecks.WorkloadRemoved("Qwen-Image-Edit", "V1.0")])
            .Targeting(WorkloadTargetType.Installer);
        _service.OnApply = () => WriteState(CatalogChannel.Preview, 6);
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        Settle(coordinator);

        coordinator.PendingSwitch.Should().BeNull();
        _preferences.SavedChannels.Should().Equal(CatalogChannel.Preview);
        _options.Channel.Should().Be(CatalogChannel.Preview);
        _service.ApplyCalls.Should().Be(1);
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Applied);
    }

    [Fact]
    public async Task Confirming_saves_the_channel_before_the_download_and_applies_the_preview()
    {
        _service.NextPreview = PreviewRemovesAppWorkload;
        var savedBeforeApply = false;
        _service.OnApply = () => { savedBeforeApply = _preferences.SavedChannels.Count == 1; WriteState(CatalogChannel.Preview, 6); };
        using var coordinator = Create();
        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        var preview = coordinator.PendingSwitch!.Preview;

        await coordinator.ConfirmSwitchAsync();
        Settle(coordinator);

        savedBeforeApply.Should().BeTrue("a failed download must not lose the choice");
        _service.AppliedCheck.Should().BeSameAs(preview);
        coordinator.Installed!.HighestCatalogVersion.Should().Be(6);
        coordinator.LastCheck!.Remote!.CatalogVersion.Should().Be(6, "landed everything: the preview names the release");
    }

    [Fact]
    public async Task A_switch_whose_apply_lands_nothing_is_up_to_date_and_offers_nothing()
    {
        WriteState(CatalogChannel.Preview, 6);
        _service.NextPreview = channel => CatalogChecks.Available(6, channel, [CatalogChecks.WorkloadAdded("Ernie", "V1.0")])
            .Targeting(WorkloadTargetType.DiffusionNexusCore);   // additions only: no warning
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.None, CatalogSections.None, null);
        _service.NextCheck = () => throw new InvalidOperationException("no second fetch: the SDK just checked that folder");
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        Settle(coordinator);

        coordinator.LastCheck!.Outcome.Should().Be(CatalogUpdateOutcome.UpToDate);
        coordinator.UpdateAvailable.Should().BeFalse("otherwise Apply is offered and applies nothing, forever");
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
    }

    [Fact]
    public async Task A_same_content_switch_still_lets_the_sdk_fill_the_target_folder()
    {
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate, channel) with { Remote = CatalogChecks.Remote(6, channel) };
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null);
        _service.OnApply = () => WriteState(CatalogChannel.Preview, 6);
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        Settle(coordinator);

        _service.ApplyCalls.Should().Be(1);
        _service.AppliedCheck!.IsSwitchPreview.Should().BeTrue();
        coordinator.Installed!.HighestCatalogVersion.Should().Be(6);
    }

    [Fact]
    public async Task A_failed_switch_apply_offers_apply_as_the_retry()
    {
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate, channel) with { Remote = CatalogChecks.Remote(6, channel) };
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.None, CatalogSections.All, "The download stalled.");
        _service.NextCheck = () => throw new InvalidOperationException("no check expected");
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        Settle(coordinator);

        coordinator.LastApplyFailed.Should().BeTrue();
        coordinator.UpdateAvailable.Should().BeTrue("the SDK found the folder needs the release");
        coordinator.CanApply.Should().BeTrue();

        _service.NextApply = () => new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null);
        _service.OnApply = () => WriteState(CatalogChannel.Preview, 6);
        await coordinator.ApplyAsync();
        Settle(coordinator);
        _service.AppliedCheck!.IsSwitchPreview.Should().BeTrue("the retry lets the SDK fill the target folder again");
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Applied);
    }

    [Fact]
    public async Task A_switch_held_by_an_install_checks_the_followed_folder_in_the_background()
    {
        var running = _installs.Begin("Krea-2-Turbo");
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate, channel);
        var hold = new TaskCompletionSource();
        _service.NextCheck = () => CatalogChecks.Available(6, CatalogChannel.Preview).Targeting(WorkloadTargetType.DiffusionNexusCore);
        using var coordinator = Create();
        await coordinator.ResolveChannelAsync();
        _service.HoldCheck = hold;

        var switching = coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        // The preview is held too; release it, then hold the background check.
        hold.SetResult();
        await switching.WaitAsync(TimeSpan.FromSeconds(5));

        _service.ApplyCalls.Should().Be(0, "never swap the catalog under a running plan");
        Settle(coordinator);
        coordinator.LastCheck!.Outcome.Should().Be(CatalogUpdateOutcome.UpdatesAvailable, "a check of the folder now followed, not the preview");
        coordinator.ApplyBlockedReason.Should().Be("It can be applied once Krea-2-Turbo has finished.");

        running.Dispose();
        coordinator.CanApply.Should().BeTrue();
    }

    [Fact]
    public async Task A_switch_into_a_missing_folder_keeps_the_last_known_state()
    {
        WriteState(CatalogChannel.Stable, 5);
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.Failed, channel, "No such host is known.");
        using var coordinator = Create();
        await coordinator.CheckAsync();
        coordinator.Installed!.HighestCatalogVersion.Should().Be(5);

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        SpinWait.SpinUntil(() => coordinator.Phase == CatalogUpdatePhase.Checked, 10_000);

        coordinator.Installed!.HighestCatalogVersion.Should().Be(5, "an empty Preview folder is no description of what the app will read");
        _preferences.SavedChannels.Should().Equal(CatalogChannel.Preview, "a failed preview still saves the choice");
    }

    [Fact]
    public async Task A_switch_into_a_missing_folder_describes_the_seed_the_catalog_reads()
    {
        _catalog.SetupGet(c => c.State).Returns(CatalogChecks.State(5, CatalogChannel.Stable, SectionOrigin.Embedded));
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.Failed, channel, "offline");
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);

        SpinWait.SpinUntil(() => coordinator.Installed?.HighestCatalogVersion == 5, 10_000).Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_save_changes_nothing_and_propagates()
    {
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate, channel);
        _preferences.SaveFailure = new IOException("database locked");
        using var coordinator = Create();

        var act = () => coordinator.SwitchChannelAsync(CatalogChannel.Preview);

        await act.Should().ThrowAsync<IOException>();
        coordinator.Channel.Should().Be(CatalogChannel.Stable);
        _options.Channel.Should().Be(CatalogChannel.Stable);
        _service.ApplyCalls.Should().Be(0);
    }

    [Fact]
    public async Task Under_the_environment_variable_a_switch_only_saves_the_preference()
    {
        _environment = "Stable";
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);

        _preferences.SavedChannels.Should().Equal(CatalogChannel.Preview);
        coordinator.Channel.Should().Be(CatalogChannel.Stable, "the environment decides this run");
        _service.PreviewedSwitches.Should().BeEmpty();
    }

    [Fact]
    public async Task Checks_and_other_switches_are_refused_while_a_switch_waits_for_an_answer()
    {
        _service.NextPreview = PreviewRemovesAppWorkload;
        using var coordinator = Create();
        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);

        await coordinator.CheckAsync();
        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);

        _service.CheckCalls.Should().Be(0);
        _service.PreviewedSwitches.Should().HaveCount(1);
        (await coordinator.SetAutoApplyAsync(true)).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run them**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q --filter "FullyQualifiedName~CatalogUpdateCoordinatorTests" 2>&1 | grep -E "\[FAIL\]|Passed!|Failed!" | head -15`
Expected: the new tests FAIL (`NotImplementedException`); Task 5's tests still pass.

- [ ] **Step 3: Implement the switch**

Replace the three `NotImplementedException` members with:

```csharp
    // ----- switch -----

    public async Task SwitchChannelAsync(CatalogChannel target, CancellationToken ct = default)
    {
        CatalogUpdatePhase phaseBefore;
        lock (_gate)
        {
            if (_switching || _savingAutoApply || PendingSwitch is not null || Phase is CatalogUpdatePhase.Checking or CatalogUpdatePhase.Applying)
            {
                _logger?.Info(LogCategory.General, LogSource, $"Catalog channel switch to {target} refused while {Phase}{(PendingSwitch is null ? "" : ", a switch waits for an answer")}");
                return;
            }
            // Claimed with the guard so a check starting during the awaits below backs off.
            _switching = true;
            phaseBefore = Phase;
        }

        try
        {
            await EnsureChannelAsync(ct).ConfigureAwait(false);
            CatalogChannel current;
            CatalogChannelSource source;
            lock (_gate) (current, source) = (Channel, ChannelSource);

            if (source == CatalogChannelSource.Environment)
            {
                // The environment decides what this run follows; the preference is saved for runs without it.
                await _preferences.SaveChannelAsync(target, ct).ConfigureAwait(false);
                _logger?.Info(LogCategory.General, LogSource, $"Catalog channel {target} saved; DIFFUSIONNEXUS_CATALOG_CHANNEL keeps {current} for this run");
                return;
            }
            if (current == target) return;

            lock (_gate)
            {
                Phase = CatalogUpdatePhase.Checking;
                Progress = null;
            }
            Raise();

            var preview = await PreviewAsync(current, target, ct).ConfigureAwait(false);
            lock (_gate) Phase = phaseBefore;

            if (ChannelSwitchWarning.For(preview) is { } warning)
            {
                lock (_gate) PendingSwitch = new CatalogChannelSwitch(preview, warning);
                _logger?.Info(LogCategory.General, LogSource,
                    $"Catalog channel switch to {target} waits for an answer: v{warning.Version} removes {warning.Removed.Count} and {warning.ChangesText ?? "changes nothing"}");
                return;
            }

            await CommitSwitchAsync(preview, phaseBefore, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _switching = false;
                // The preview's Checking ends here; a held switch's background check keeps its own.
                if (Phase == CatalogUpdatePhase.Checking && _inFlight is not { IsCompleted: false }) Phase = phaseBefore;
            }
            Raise();
        }
    }

    public async Task ConfirmSwitchAsync(CancellationToken ct = default)
    {
        CatalogChannelSwitch pending;
        CatalogUpdatePhase phaseBefore;
        lock (_gate)
        {
            if (PendingSwitch is null || _switching)
            {
                _logger?.Info(LogCategory.General, LogSource, "Catalog channel switch confirmed with no switch waiting; nothing to do");
                return;
            }
            pending = PendingSwitch;
            PendingSwitch = null;
            _switching = true;
            phaseBefore = Phase;
        }

        _logger?.Info(LogCategory.General, LogSource, $"Catalog channel switch to {pending.Target} confirmed");
        try
        {
            await CommitSwitchAsync(pending.Preview, phaseBefore, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _switching = false;
            Raise();
        }
    }

    public void KeepChannel()
    {
        lock (_gate)
        {
            if (PendingSwitch is null) return;
            _logger?.Info(LogCategory.General, LogSource, $"Catalog channel switch to {PendingSwitch.Target} declined; nothing was saved");
            PendingSwitch = null;
        }
        Raise();
    }

    /// <summary>The switch as the SDK diffs it, filtered for Diffusion Nexus. Changes nothing. Never throws.</summary>
    private async Task<CatalogUpdateCheck> PreviewAsync(CatalogChannel from, CatalogChannel target, CancellationToken ct)
    {
        _logger?.Info(LogCategory.General, LogSource, $"Catalog channel switch: previewing {target}");
        try
        {
            var check = (await _updates.PreviewSwitchAsync(from, target, ct).ConfigureAwait(false)).For(_audience);
            _logger?.Info(LogCategory.General, LogSource,
                $"Catalog channel preview: {check.Outcome} (remote v{check.Remote?.CatalogVersion}, {check.Workloads.Count} workload / {check.Workflows.Count} workflow changes for Diffusion Nexus)",
                check.Error);
            return check;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Error(LogCategory.General, LogSource, "Catalog channel preview failed unexpectedly", ex);
            return FailedCheck(target, Installed, ex.Message);
        }
    }

    /// <summary>
    /// Saves the channel, follows it, and starts what the folder now followed needs; returns once the choice is
    /// saved. The caller holds <see cref="_switching"/>. Only a failed save throws, and then nothing changed.
    /// </summary>
    private async Task CommitSwitchAsync(CatalogUpdateCheck preview, CatalogUpdatePhase phaseBefore, CancellationToken ct)
    {
        var target = preview.Channel;
        try
        {
            await _preferences.SaveChannelAsync(target, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warn(LogCategory.General, LogSource, $"Catalog channel {target} could not be saved; nothing was changed", ex.Message);
            lock (_gate) Phase = phaseBefore;
            throw;
        }

        // With shared folders an up-to-date preview is handed to the SDK as well: it compared against the folder
        // being left, and the SDK's apply checks the target folder itself (fills it when missing or behind,
        // applies nothing when current). With one folder the preview compared against that very folder.
        var appliable = preview.Outcome == CatalogUpdateOutcome.UpdatesAvailable
            || (preview.Outcome == CatalogUpdateOutcome.UpToDate && _options.UsesSharedFolders);
        // The target folder's state file, outside the lock (the shared-folder read enumerates format folders).
        var known = LocalCatalogState.Load(_options.ReadFolderFor(target));
        var applying = false;
        var held = false;
        lock (_gate)
        {
            ApplyResolution(_readEnvironment(), target.ToString());
            _channelResolved = true;
            if (known.RecordsAnything()) Installed = known;
            LastApply = null;
            _applyAutomatic = false;
            Progress = null;
            if (Channel != target)
            {
                // The environment variable appeared since the preview: it decides.
                LastCheck = null;
                Phase = CatalogUpdatePhase.Idle;
            }
            else
            {
                LastCheck = preview;
                LastCheckedAt = DateTimeOffset.Now;
                if (appliable && _installs.Running is null)
                {
                    // Not under the caller's token: the switch has returned by the time the download runs.
                    Phase = CatalogUpdatePhase.Applying;
                    _inFlight = Task.Run(() => ApplyCoreAsync(preview, CancellationToken.None), CancellationToken.None);
                    applying = true;
                }
                else
                {
                    held = appliable;
                    Phase = held ? CatalogUpdatePhase.Checking : CatalogUpdatePhase.Checked;
                }
            }
            if (!applying) _inFlight = Task.Run(() => AfterSwitchNotAppliedAsync(held), CancellationToken.None);
        }
        _logger?.Info(LogCategory.General, LogSource, $"Catalog channel switched to {target}: " +
            (applying ? (preview.Outcome == CatalogUpdateOutcome.UpToDate ? "checking its folder" : $"applying v{preview.Remote?.CatalogVersion}")
            : held ? "the apply waits for the running install" : preview.Outcome.ToString()));
    }

    /// <summary>
    /// After a switch that started no apply, in the background: the state of the catalog the app reads now
    /// (reading it seeds a channel folder that does not exist yet); held by a running install, a check of the
    /// folder now followed, so the Apply after the install lands what that folder needs. Never throws.
    /// </summary>
    private async Task AfterSwitchNotAppliedAsync(bool held)
    {
        LocalCatalogState? state = null;
        try
        {
            // ICatalog.State blocks on the load and seeds a missing channel folder; off the caller's thread.
            state = await Task.Run(() => _catalog.State).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warn(LogCategory.General, LogSource, "The catalog could not be read after the switch; reading its state file instead.", ex.Message);
        }
        state ??= LocalCatalogState.Load(_options.InstalledCatalogPath);
        lock (_gate)
        {
            if (state.RecordsAnything() || Installed is null) Installed = state;
        }

        if (held)
        {
            var fresh = await CheckFollowedAsync().ConfigureAwait(false);
            lock (_gate)
            {
                LastCheck = fresh;
                LastCheckedAt = DateTimeOffset.Now;
                Phase = CatalogUpdatePhase.Checked;
            }
        }
        Raise();
    }
```

- [ ] **Step 4: Run the coordinator tests three times**

Run: `for i in 1 2 3; do dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q --filter "FullyQualifiedName~CatalogUpdateCoordinatorTests" 2>&1 | tail -1; done`
Expected: all pass, three times (timing-sensitive code; `Settle` waits up to 10 s).

- [ ] **Step 5: Whole suite, commit**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q 2>&1 | tail -3` — Expected: `Failed: 0`.

```bash
git add -A
git commit -m "feat(catalog): #618 switching channels — warning for Diffusion Nexus workloads, the SDK fills the target folder, Apply is the retry, held switches check in the background"
git push
```

---

### Task 7: Startup wiring; `CatalogStartupUpdater` goes

**Files:**
- Modify: `DiffusionNexus.UI/Services/Catalog/CatalogStartup.cs`
- Delete: `DiffusionNexus.UI/Services/Catalog/CatalogStartupUpdater.cs`, `DiffusionNexus.Tests/Catalog/CatalogStartupUpdaterTests.cs`
- Modify: `DiffusionNexus.UI/Services/Catalog/InstallerSdkServiceCollectionExtensions.cs:29-65`
- Modify: `DiffusionNexus.UI/App.axaml.cs:505-526`
- Modify: `DiffusionNexus.Tests/Catalog/CatalogStartupTests.cs`, `DiffusionNexus.Tests/Catalog/CatalogRegistrationTests.cs`
- Modify: `.github/copilot-instructions.md` (the DB-vs-catalog paragraph)

**Interfaces:**
- Consumes: Tasks 3–6 (`ICatalogPreferenceStore`, `IInstallActivity`, `ICatalogUpdateCoordinator`).
- Produces: `CatalogStartup.RunAsync(ICatalog catalog, ICatalogUpdateCoordinator coordinator, IActivityLogService? activityLog, CancellationToken ct = default)`; DI singletons `ICatalogPreferenceStore`, `ICatalogUpdateCoordinator` (as `CatalogUpdateCoordinator`).

- [ ] **Step 1: Rewrite the startup tests**

In `CatalogStartupTests.cs`:
- delete the five channel tests (`Follows_the_channel_the_installer_saved`, `Environment_channel_wins_over_the_saved_one`, `Nothing_saved_falls_back_to_Stable_as_the_default`, `Never_writes_the_user_settings`, `A_failing_settings_read_falls_back_to_the_default_with_a_warning`) — Tasks 3 and 5 cover them now;
- delete `Run_passes_a_Default_source_through_so_the_fallback_guard_holds` (the guard is gone: each channel has its own folder);
- remove the `_settings` mock and the `ResolveChannelAsync` helper;
- replace the `Run` helper and the two remaining Run tests with:

```csharp
    private readonly Mock<ICatalogUpdateCoordinator> _coordinator = new();

    private Task Run() => CatalogStartup.RunAsync(_catalog.Object, _coordinator.Object, _log);

    [Fact]
    public async Task Run_resolves_the_channel_before_the_catalog_is_read_then_checks()
    {
        var order = new List<string>();
        _coordinator.Setup(c => c.ResolveChannelAsync(It.IsAny<CancellationToken>()))
                    .Callback(() => order.Add("resolve")).ReturnsAsync(CatalogChannel.Preview);
        _catalog.SetupGet(c => c.Source).Callback(() => order.Add("read")).Returns(new CatalogSource(CatalogSourceKind.Installed, @"C:\x", false));
        _coordinator.Setup(c => c.CheckAtStartupAsync(It.IsAny<CancellationToken>()))
                    .Callback(() => order.Add("check")).Returns(Task.CompletedTask);

        await Run();

        order.Should().StartWith(["resolve", "read"]).And.EndWith("check");
    }

    [Fact]
    public async Task Run_still_checks_for_updates_when_the_catalog_report_fails()
    {
        _catalog.SetupGet(c => c.Source).Throws(new IOException("locked"));

        await Run();

        _coordinator.Verify(c => c.CheckAtStartupAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
```

(Use the file's existing way of building a `CatalogSource` if its constructor differs — the report tests in the same file already create one.) The report tests call `CatalogStartup.Report(catalog, channel, source, log)` unchanged; their `CatalogChannelSource` now comes from the SDK.

In `CatalogRegistrationTests.cs` add:

```csharp
    [Fact]
    public void The_coordinator_and_its_preference_store_resolve_as_singletons()
    {
        using var provider = BuildProvider();   // the file's existing provider builder for AddInstallerSdkCatalog
        var coordinator = provider.GetRequiredService<ICatalogUpdateCoordinator>();
        coordinator.Should().BeSameAs(provider.GetRequiredService<ICatalogUpdateCoordinator>());
        provider.GetRequiredService<ICatalogPreferenceStore>().Should().NotBeNull();
    }
```

The registration needs `IInstallActivity`, `IServiceScopeFactory`, and (optionally) `IUnifiedLogger`; register `services.AddSingleton<IInstallActivity, InstallActivity>()` in the test's provider setup next to the existing `AddInstallerSdkCatalog` call.

- [ ] **Step 2: Run them**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q --filter "FullyQualifiedName~CatalogStartupTests|FullyQualifiedName~CatalogRegistrationTests" 2>&1 | grep -E "error CS|Passed!|Failed!" | head -3`
Expected: compile errors (`RunAsync` signature, missing registrations).

- [ ] **Step 3: Rewire**

`CatalogStartup.cs`: replace `RunAsync` and delete `ResolveChannelAsync`:

```csharp
    /// <summary>
    /// The catalog's startup work in one background task: resolve the channel this app follows (before anything
    /// reads the catalog, so the first read takes that channel's folder), report the catalog to the Unified
    /// Console, then the coordinator's startup check. Never throws.
    /// </summary>
    public static async Task RunAsync(ICatalog catalog, ICatalogUpdateCoordinator coordinator, IActivityLogService? activityLog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(coordinator);

        var channel = await coordinator.ResolveChannelAsync(ct).ConfigureAwait(false);
        Report(catalog, channel, coordinator.ChannelSource, activityLog);
        await coordinator.CheckAtStartupAsync(ct).ConfigureAwait(false);
    }
```

Update the class summary: "pick the channel this app follows (Settings → Catalog)" instead of "the channel the installer saved"; drop the `IUserSettingsRepository` using if unused.

`InstallerSdkServiceCollectionExtensions.cs`: replace the `CatalogStartupUpdater` registration with:

```csharp
        // The app's own catalog channel and "apply automatically" (#618). The installer's user_settings.json is
        // read once, on the first start with the columns, to copy its channel.
        services.AddSingleton<ICatalogPreferenceStore>(sp => new CatalogPreferenceStore(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IUserSettingsRepository>(),
            sp.GetService<IUnifiedLogger>()));
        services.AddSingleton<CatalogUpdateCoordinator>(sp => new CatalogUpdateCoordinator(
            sp.GetRequiredService<ICatalogUpdateService>(),
            sp.GetRequiredService<ICatalog>(),
            sp.GetRequiredService<CatalogOptions>(),
            sp.GetRequiredService<ICatalogPreferenceStore>(),
            sp.GetRequiredService<IInstallActivity>(),
            () => Environment.GetEnvironmentVariable(CatalogChannelResolver.EnvironmentVariable),
            sp.GetService<IUnifiedLogger>()));
        services.AddSingleton<ICatalogUpdateCoordinator>(sp => sp.GetRequiredService<CatalogUpdateCoordinator>());
```

and rewrite the comment above `AddDiffusionNexusCatalog` to:

```csharp
    // SDK 2.3.0: installed catalogs live in %LocalAppData%\ITL-Shared\format-N\catalog-stable|preview\, shared
    // with the installer; the old %LocalAppData%\DiffusionNexus\catalog is copied over once by the SDK.
    // CatalogUpdateCoordinator is the one writer of CatalogOptions.Channel.
```

`App.axaml.cs` `StartCatalogInBackground`:

```csharp
                await CatalogStartup.RunAsync(
                    Services!.GetRequiredService<ICatalog>(),
                    Services!.GetRequiredService<ICatalogUpdateCoordinator>(),
                    Services!.GetService<IActivityLogService>());
```

Delete `CatalogStartupUpdater.cs` and its tests:

```bash
git rm -q DiffusionNexus.UI/Services/Catalog/CatalogStartupUpdater.cs DiffusionNexus.Tests/Catalog/CatalogStartupUpdaterTests.cs
```

`.github/copilot-instructions.md`: replace the sentence "The app follows the catalog channel the installer saved and never writes it." and the catalog path with:

"Installed catalogs live in `%LocalAppData%\ITL-Shared\format-N\catalog-stable|preview\`, shared with the 3.x installer (SDK 2.3.0). The app has its own channel and "apply automatically" (Settings → Catalog, `AppSettings.CatalogChannel` / `ApplyCatalogUpdatesAutomatically`); on the first start it copied the installer's channel once. It never writes the installer's `user_settings.json`."

- [ ] **Step 4: Run the tests, whole suite**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q 2>&1 | tail -3`
Expected: `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(catalog): #618 startup resolves the app's channel first, then reports and runs the coordinator's startup check; CatalogStartupUpdater (silent auto-apply) removed"
git push
```

---

### Task 8: Settings → Catalog

**Files:**
- Create: `DiffusionNexus.UI/ViewModels/CatalogSettingsViewModel.cs`
- Modify: `DiffusionNexus.UI/ViewModels/SettingsViewModel.cs` (ctor param, `Catalog` property, dispose)
- Modify: `DiffusionNexus.UI/Views/SettingsView.axaml` (new expander after "ComfyUI Server", before the LoRA Sort comment at :715)
- Modify: `DiffusionNexus.UI/App.axaml.cs:966-976` (pass the coordinator)
- Test: Create `DiffusionNexus.Tests/ViewModels/CatalogSettingsViewModelTests.cs`, `DiffusionNexus.Tests/Catalog/StubCatalogUpdateCoordinator.cs`

**Interfaces:**
- Consumes: Task 5/6 `ICatalogUpdateCoordinator`, `CatalogUpdateOffer`, `CatalogUpdatePhase`, `CatalogChannelSwitch`, `LocalCatalogStateExtensions.RecordsAnything`; `IUiScheduler`.
- Produces: `CatalogSettingsViewModel(ICatalogUpdateCoordinator coordinator, IUiScheduler ui)` with the bindable members listed in Step 3; `SettingsViewModel.Catalog` (`CatalogSettingsViewModel?`); `SettingsViewModel` ctor param `ICatalogUpdateCoordinator? catalogUpdates = null` (last).

- [ ] **Step 1: Test stub**

Create `DiffusionNexus.Tests/Catalog/StubCatalogUpdateCoordinator.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.UI.Services.Catalog;

namespace DiffusionNexus.Tests.Catalog;

/// <summary>Settable coordinator for view-model tests; the offer rule is the real one (CatalogUpdateOffer).</summary>
internal sealed class StubCatalogUpdateCoordinator : ICatalogUpdateCoordinator
{
    public CatalogChannel Channel { get; set; } = CatalogChannel.Stable;
    public CatalogChannelSource ChannelSource { get; set; } = CatalogChannelSource.Setting;
    public string? OverridePath { get; set; }
    public CatalogUpdatePhase Phase { get; set; } = CatalogUpdatePhase.Idle;
    public CatalogUpdateCheck? LastCheck { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public LocalCatalogState? Installed { get; set; }
    public CatalogDownloadProgress? Progress { get; set; }
    public CatalogApplyResult? LastApply { get; set; }
    public bool LastApplyAutomatic { get; set; }
    public bool AutoApply { get; set; }
    public string? InstallRunning { get; set; }
    public CatalogChannelSwitch? PendingSwitch { get; set; }

    public bool LastApplyFailed => LastApply is { } r && (r.Error is not null || r.Failed != CatalogSections.None);
    public bool UpdateAvailable => CatalogUpdateOffer.Offered(LastCheck, Phase, PendingSwitch is not null, LastApplyFailed,
        Installed is { } s && (s.Workloads?.Channel ?? s.Workflows?.Channel) is { } c && c != Channel);
    public bool CanApply => UpdateAvailable && Phase == CatalogUpdatePhase.Checked && InstallRunning is null;
    public string? ApplyBlockedReason => UpdateAvailable && InstallRunning is { } n ? $"It can be applied once {n} has finished." : null;

    public event Action? Changed;
    public void RaiseChanged() => Changed?.Invoke();

    public int Checks { get; private set; }
    public int Applies { get; private set; }
    public List<CatalogChannel> Switches { get; } = [];
    public int Confirms { get; private set; }
    public int Keeps { get; private set; }
    public List<bool> AutoApplySet { get; } = [];
    public bool RefuseAutoApply { get; set; }
    public CatalogChannelSwitch? PendingOnSwitch { get; set; }

    public Task<CatalogChannel> ResolveChannelAsync(CancellationToken ct = default) => Task.FromResult(Channel);
    public Task CheckAsync(CancellationToken ct = default) { Checks++; return Task.CompletedTask; }
    public Task ApplyAsync(CancellationToken ct = default) { Applies++; return Task.CompletedTask; }
    public Task CheckAtStartupAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> SetAutoApplyAsync(bool on, CancellationToken ct = default)
    {
        AutoApplySet.Add(on);
        if (RefuseAutoApply) return Task.FromResult(false);
        AutoApply = on;
        return Task.FromResult(true);
    }

    public Task SwitchChannelAsync(CatalogChannel target, CancellationToken ct = default)
    {
        Switches.Add(target);
        if (PendingOnSwitch is not null) PendingSwitch = PendingOnSwitch;
        else Channel = target;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task ConfirmSwitchAsync(CancellationToken ct = default)
    {
        Confirms++;
        if (PendingSwitch is { } p) { Channel = p.Target; PendingSwitch = null; }
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public void KeepChannel() { Keeps++; PendingSwitch = null; Changed?.Invoke(); }
}
```

- [ ] **Step 2: Failing view-model tests**

Create `DiffusionNexus.Tests/ViewModels/CatalogSettingsViewModelTests.cs`:

```csharp
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.Tests.Catalog;
using DiffusionNexus.Tests.Helpers;
using DiffusionNexus.UI.Services.Catalog;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

public sealed class CatalogSettingsViewModelTests
{
    private readonly StubCatalogUpdateCoordinator _catalog = new();

    private CatalogSettingsViewModel Vm() => new(_catalog, new ImmediateUiScheduler());

    private void Checked(CatalogUpdateCheck check)
    {
        _catalog.LastCheck = check.For(CatalogAudience.DiffusionNexus);
        _catalog.Phase = CatalogUpdatePhase.Checked;
    }

    [Fact]
    public void Shows_the_followed_channel_and_the_installed_version()
    {
        _catalog.Channel = CatalogChannel.Preview;
        _catalog.Installed = CatalogChecks.State(6, CatalogChannel.Preview);

        var vm = Vm();

        vm.IsPreview.Should().BeTrue();
        vm.IsStable.Should().BeFalse();
        vm.StatusText.Should().Be("Catalog v6 · Preview");
        vm.OutcomeText.Should().Be("Not checked yet.");
    }

    [Fact]
    public void Content_from_another_channel_is_named_in_the_status()
    {
        _catalog.Channel = CatalogChannel.Preview;
        _catalog.Installed = CatalogChecks.State(5, CatalogChannel.Stable, SectionOrigin.Embedded);

        Vm().StatusText.Should().Be("Catalog v5 · Preview (content from Stable)");
    }

    [Fact]
    public void An_update_for_the_app_lists_its_changes_and_offers_apply()
    {
        Checked(CatalogChecks.Available(7).Targeting(WorkloadTargetType.DiffusionNexusCore));
        var vm = Vm();

        vm.OutcomeText.Should().Be("Catalog update available – v7");
        vm.ChangeGroups.Should().ContainSingle().Which.Heading.Should().Be("Updated");
        vm.ChangeGroups[0].Rows.Should().ContainSingle().Which.Should().Be("Workload · Upscaling-Z-Image-Turbo · V1.6 → V1.7");
        vm.ShowApply.Should().BeTrue();
        vm.ApplyCommand.CanExecute(null).Should().BeTrue();

        vm.ApplyCommand.Execute(null);
        _catalog.Applies.Should().Be(1);
    }

    [Fact]
    public void A_release_with_only_installer_changes_says_so_and_offers_nothing()
    {
        Checked(CatalogChecks.Available(7, CatalogChannel.Stable, [CatalogChecks.WorkloadAdded("Qwen-Image-Edit", "V1.0")]).Targeting(WorkloadTargetType.Installer));
        var vm = Vm();

        vm.OutcomeText.Should().Be("Catalog v7 has no changes for Diffusion Nexus.");
        vm.ChangeGroups.Should().BeEmpty();
        vm.ShowApply.Should().BeFalse();
    }

    [Fact]
    public void An_install_running_holds_apply_and_says_which()
    {
        Checked(CatalogChecks.Available(7));
        _catalog.InstallRunning = "Krea-2-Turbo";
        var vm = Vm();

        vm.ShowApply.Should().BeFalse();
        vm.ApplyBlockedReason.Should().Be("It can be applied once Krea-2-Turbo has finished.");
    }

    [Theory]
    [InlineData(null, 7L, "Applying the catalog…")]
    [InlineData(1024L * 1024, 2L * 1024 * 1024, "Downloading… 50%")]
    public void Progress_is_shown_while_any_apply_runs(long? received, long? total, string expected)
    {
        _catalog.LastCheck = CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate).For(CatalogAudience.DiffusionNexus);
        _catalog.Phase = CatalogUpdatePhase.Applying;
        _catalog.Progress = received is null ? null : new CatalogDownloadProgress(received.Value, total);

        Vm().ProgressText.Should().Be(expected);
    }

    [Fact]
    public void A_failed_apply_shows_the_error_even_when_nothing_is_offered()
    {
        _catalog.LastCheck = CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate).For(CatalogAudience.DiffusionNexus);
        _catalog.Phase = CatalogUpdatePhase.Checked;
        _catalog.LastApply = new CatalogApplyResult(CatalogSections.None, CatalogSections.All, "The download stalled.");

        Vm().ErrorText.Should().Be("The catalog update failed: The download stalled.");
    }

    [Fact]
    public void A_silent_apply_with_no_app_changes_says_so_once_it_landed()
    {
        Checked(CatalogChecks.Available(7, CatalogChannel.Stable, [CatalogChecks.WorkloadAdded("Qwen-Image-Edit", "V1.0")]).Targeting(WorkloadTargetType.Installer));
        _catalog.Phase = CatalogUpdatePhase.Applied;
        _catalog.LastApplyAutomatic = true;
        _catalog.Installed = CatalogChecks.State(7, CatalogChannel.Stable);

        Vm().OutcomeText.Should().Be("Catalog v7 applied automatically; no changes for Diffusion Nexus.");
    }

    [Fact]
    public void Picking_the_other_channel_switches_and_a_warning_shows_switch_and_keep()
    {
        var preview = CatalogChecks.Available(6, CatalogChannel.Preview, [CatalogChecks.WorkloadRemoved("Upscaling-Z-Image-Turbo Vision", "V1.0")])
            .Targeting(WorkloadTargetType.DiffusionNexusCore).For(CatalogAudience.DiffusionNexus);
        _catalog.PendingOnSwitch = new CatalogChannelSwitch(preview, ChannelSwitchWarning.For(preview)!);
        var vm = Vm();

        vm.IsPreview = true;

        _catalog.Switches.Should().Equal(CatalogChannel.Preview);
        vm.ShowWarning.Should().BeTrue();
        vm.WarningText.Should().Be("Preview is at v6. Switching removes Upscaling-Z-Image-Turbo Vision. Software you have already installed is not affected.");
        vm.SwitchButtonText.Should().Be("Switch to Preview");
        vm.KeepButtonText.Should().Be("Keep Stable");
        vm.IsPreview.Should().BeTrue("the dot shows the channel being decided");

        vm.KeepChannelCommand.Execute(null);
        vm.ShowWarning.Should().BeFalse();
        vm.IsStable.Should().BeTrue();
    }

    [Fact]
    public void Under_the_environment_variable_the_radios_are_disabled_with_a_hint()
    {
        _catalog.ChannelSource = CatalogChannelSource.Environment;

        var vm = Vm();

        vm.CanChooseChannel.Should().BeFalse();
        vm.ChannelHint.Should().Be("Set by DIFFUSIONNEXUS_CATALOG_CHANNEL for this run.");
    }

    [Fact]
    public void A_refused_toggle_snaps_back()
    {
        _catalog.RefuseAutoApply = true;
        var vm = Vm();

        vm.AutoApply = true;

        _catalog.AutoApplySet.Should().Equal(true);
        vm.AutoApply.Should().BeFalse();
    }

    [Fact]
    public void Coordinator_changes_refresh_the_page()
    {
        var vm = Vm();
        Checked(CatalogChecks.Available(7));

        _catalog.RaiseChanged();

        vm.OutcomeText.Should().Be("Catalog update available – v7");
    }
}
```

- [ ] **Step 3: Run them**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q --filter "FullyQualifiedName~CatalogSettingsViewModelTests" 2>&1 | grep -E "error CS|Passed!|Failed!" | head -3`
Expected: compile error (`CatalogSettingsViewModel` missing).

- [ ] **Step 4: The view model**

Create `DiffusionNexus.UI/ViewModels/CatalogSettingsViewModel.cs`:

```csharp
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Catalog;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>One "Added" / "Updated" / "Removed" block of the change list.</summary>
public sealed record CatalogChangeGroup(string Heading, IReadOnlyList<string> Rows);

/// <summary>
/// Settings → Catalog (#618): renders <see cref="ICatalogUpdateCoordinator"/>, mirroring the installer's
/// update page. Everything is recomputed from the coordinator on each Changed, on the UI thread.
/// </summary>
public sealed partial class CatalogSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ICatalogUpdateCoordinator _catalog;
    private readonly IUiScheduler _ui;
    private bool _refreshing;

    public CatalogSettingsViewModel(ICatalogUpdateCoordinator catalog, IUiScheduler ui)
    {
        _catalog = catalog;
        _ui = ui;
        _catalog.Changed += OnChanged;
        Refresh();
    }

    [ObservableProperty] private bool _isStable;
    [ObservableProperty] private bool _isPreview;
    [ObservableProperty] private bool _canChooseChannel;
    [ObservableProperty] private string? _channelHint;
    [ObservableProperty] private bool _autoApply;
    [ObservableProperty] private bool _canToggleAutoApply;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _outcomeText = string.Empty;
    [ObservableProperty] private string? _lastCheckedText;
    [ObservableProperty] private IReadOnlyList<CatalogChangeGroup> _changeGroups = [];
    [ObservableProperty] private bool _showApply;
    [ObservableProperty] private string? _applyBlockedReason;
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _showWarning;
    [ObservableProperty] private string? _warningText;
    [ObservableProperty] private string _switchButtonText = string.Empty;
    [ObservableProperty] private string _keepButtonText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    partial void OnIsStableChanged(bool value) { if (value && !_refreshing) _ = SwitchAsync(CatalogChannel.Stable); }
    partial void OnIsPreviewChanged(bool value) { if (value && !_refreshing) _ = SwitchAsync(CatalogChannel.Preview); }
    partial void OnAutoApplyChanged(bool value) { if (!_refreshing) _ = SetAutoApplyAsync(value); }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => _catalog.CheckAsync();
    private bool CanCheck() => !IsBusy && !ShowWarning;

    [RelayCommand(CanExecute = nameof(CanApplyNow))]
    private Task ApplyAsync() => _catalog.ApplyAsync();
    private bool CanApplyNow() => _catalog.CanApply;

    [RelayCommand]
    private Task ConfirmSwitchAsync() => _catalog.ConfirmSwitchAsync();

    [RelayCommand]
    private void KeepChannel() => _catalog.KeepChannel();

    private async Task SwitchAsync(CatalogChannel target)
    {
        try
        {
            await _catalog.SwitchChannelAsync(target);
        }
        catch (Exception ex)
        {
            ErrorText = $"The channel could not be saved: {ex.Message}";
        }
        Refresh();   // a refused switch changes nothing in the coordinator; put the dot back
    }

    private async Task SetAutoApplyAsync(bool on)
    {
        try
        {
            if (!await _catalog.SetAutoApplyAsync(on)) Refresh();   // refused: snap back
        }
        catch (Exception ex)
        {
            ErrorText = $"The setting could not be saved: {ex.Message}";
            Refresh();
        }
    }

    private void OnChanged() => _ui.Post(Refresh);

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            var shown = _catalog.PendingSwitch?.Target ?? _catalog.Channel;
            IsStable = shown == CatalogChannel.Stable;
            IsPreview = shown == CatalogChannel.Preview;
            var busy = _catalog.Phase is CatalogUpdatePhase.Checking or CatalogUpdatePhase.Applying;
            IsBusy = busy;
            var pinned = _catalog.ChannelSource == CatalogChannelSource.Environment;
            CanChooseChannel = !pinned && !busy && _catalog.PendingSwitch is null;
            ChannelHint = pinned ? "Set by DIFFUSIONNEXUS_CATALOG_CHANNEL for this run." : null;
            AutoApply = _catalog.AutoApply;
            CanToggleAutoApply = !busy && _catalog.PendingSwitch is null;

            StatusText = Status(_catalog);
            OutcomeText = Outcome(_catalog);
            LastCheckedText = _catalog.LastCheckedAt is { } at ? "Last checked " + at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : null;

            var showList = _catalog.LastCheck is { } check && _catalog.Phase != CatalogUpdatePhase.Checking
                && (_catalog.UpdateAvailable || (_catalog.Phase == CatalogUpdatePhase.Applied && _catalog.LastApplyAutomatic));
            ChangeGroups = showList ? Groups(_catalog.LastCheck!) : [];
            ShowApply = _catalog.UpdateAvailable && _catalog.Phase != CatalogUpdatePhase.Applying && _catalog.ApplyBlockedReason is null;
            ApplyBlockedReason = _catalog.Phase != CatalogUpdatePhase.Applying ? _catalog.ApplyBlockedReason : null;
            ProgressText = _catalog.Phase == CatalogUpdatePhase.Applying ? Progress(_catalog.Progress) : null;
            if (_catalog.Phase != CatalogUpdatePhase.Applying && _catalog.LastApplyFailed && _catalog.LastApply is { } failed)
                ErrorText = $"The catalog update failed: {failed.Error?.TrimEnd('.')}" + (failed.Error is null ? "." : string.Empty);
            else if (busy || _catalog.LastApply is null)
                ErrorText = null;

            if (_catalog.PendingSwitch is { } pending)
            {
                ShowWarning = true;
                WarningText = Warning(pending.Warning);
                SwitchButtonText = $"Switch to {pending.Target}";
                KeepButtonText = $"Keep {_catalog.Channel}";
            }
            else
            {
                ShowWarning = false;
                WarningText = null;
            }
        }
        finally
        {
            _refreshing = false;
        }
        CheckCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private static string Status(ICatalogUpdateCoordinator c)
    {
        if (c.Installed is not { } s || !s.RecordsAnything()) return $"No catalog installed yet · {c.Channel}";
        var text = $"Catalog v{s.HighestCatalogVersion} · {c.Channel}";
        return (s.Workloads?.Channel ?? s.Workflows?.Channel) is { } recorded && recorded != c.Channel ? $"{text} (content from {recorded})" : text;
    }

    private static string Outcome(ICatalogUpdateCoordinator c)
    {
        if (c.PendingSwitch is { } pending) return $"Switching to {pending.Target} waits for your answer below.";
        if (c.Phase == CatalogUpdatePhase.Checking) return "Checking the catalog…";
        if (c.Phase == CatalogUpdatePhase.Applied)
        {
            var landed = c.Installed is { } s && s.RecordsAnything() ? s.HighestCatalogVersion : c.LastCheck?.Remote?.CatalogVersion;
            if (!c.LastApplyAutomatic) return $"Catalog updated to v{landed}.";
            return c.LastCheck is { HasChanges: false }
                ? $"Catalog v{landed} applied automatically; no changes for Diffusion Nexus."
                : $"Catalog updated to v{landed} automatically.";
        }
        return c.LastCheck switch
        {
            null => "Not checked yet.",
            { IsSwitchPreview: true } k when c.UpdateAvailable => $"Catalog update available – v{k.Remote?.CatalogVersion}",
            { Outcome: CatalogUpdateOutcome.UpToDate } => "Up to date.",
            { Outcome: CatalogUpdateOutcome.UpdatesAvailable } k when !c.UpdateAvailable && c.Phase != CatalogUpdatePhase.Applying
                => $"Catalog v{k.Remote?.CatalogVersion} has no changes for Diffusion Nexus.",
            { Outcome: CatalogUpdateOutcome.UpdatesAvailable } k => $"Catalog update available – v{k.Remote?.CatalogVersion}",
            { Outcome: CatalogUpdateOutcome.RequiresNewerSoftware } => "This catalog update needs a newer version of Diffusion Nexus.",
            { Outcome: CatalogUpdateOutcome.OverrideActive } => $"Update check skipped: a local catalog override is active at {c.OverridePath}.",
            { Outcome: CatalogUpdateOutcome.Failed } k => $"The catalog check failed: {k.Error}",
            _ => string.Empty,
        };
    }

    private static string Progress(CatalogDownloadProgress? p) => p switch
    {
        null => "Applying the catalog…",
        { TotalBytes: > 0 } t => $"Downloading… {(100.0 * t.BytesReceived / t.TotalBytes.Value).ToString("F0", CultureInfo.InvariantCulture)}%",
        var t => $"Downloading… {(t.BytesReceived / 1_048_576.0).ToString("F1", CultureInfo.InvariantCulture)} MB",
    };

    private static string Warning(ChannelSwitchWarning w)
    {
        var parts = new List<string>();
        if (w.Removed.Count > 0) parts.Add("removes " + Join(w.Removed));
        if (w.ChangesText is { } changes) parts.Add(changes);
        return $"{w.Target} is at v{w.Version}. Switching {string.Join(" and ", parts)}. Software you have already installed is not affected.";
    }

    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    private static IReadOnlyList<CatalogChangeGroup> Groups(CatalogUpdateCheck check)
    {
        var rows = CatalogChangeRows.Build(check);
        return new[] { ChangeKind.Added, ChangeKind.Updated, ChangeKind.Removed }
            .Select(kind => new CatalogChangeGroup(kind.ToString(),
                rows.Where(r => r.Change == kind).Select(r => string.IsNullOrEmpty(r.VersionText) ? $"{r.Kind} · {r.Name}" : $"{r.Kind} · {r.Name} · {r.VersionText}").ToList()))
            .Where(g => g.Rows.Count > 0)
            .ToList();
    }

    public void Dispose() => _catalog.Changed -= OnChanged;
}
```

- [ ] **Step 5: Run the view-model tests**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q --filter "FullyQualifiedName~CatalogSettingsViewModelTests" 2>&1 | tail -3`
Expected: all pass. If the change-row assertion fails on the exact text, read `CatalogChangeRows.Build`'s `VersionText` for an update (`"V1.6 → V1.7"`) and adjust only the separator in `Groups`, not the test's meaning.

- [ ] **Step 6: Wire it into Settings**

`SettingsViewModel.cs`: add ctor param `ICatalogUpdateCoordinator? catalogUpdates = null` (last). In the body:

```csharp
        if (catalogUpdates is not null) Catalog = new CatalogSettingsViewModel(catalogUpdates, uiScheduler ?? new AvaloniaUiScheduler());
```

and the property:

```csharp
    /// <summary>Settings → Catalog (#618). Null in the designer and in tests that do not pass a coordinator.</summary>
    public CatalogSettingsViewModel? Catalog { get; }
```

(`uiScheduler` is the existing optional ctor param; `AvaloniaUiScheduler` is the production `IUiScheduler`.) If `SettingsViewModel` implements `IDisposable`, dispose `Catalog` there; otherwise no change — the Settings VM lives as long as the app.

`App.axaml.cs:966-976`: add `catalogUpdates: sp.GetService<ICatalogUpdateCoordinator>()` to the `SettingsViewModel` factory.

`SettingsView.axaml`: insert after the ComfyUI Server `</Expander>` (line 713):

```xml
        <!-- Catalog (#618) -->
        <Expander Header="Catalog" HorizontalAlignment="Stretch" IsVisible="{Binding Catalog, Converter={x:Static ObjectConverters.IsNotNull}}">
          <Border Padding="16" DataContext="{Binding Catalog}" x:DataType="vm:CatalogSettingsViewModel">
            <StackPanel Spacing="12">
              <TextBlock Text="Workloads and workflows come from the Diffusion Nexus catalog, shared with the installer. Preview gets new catalog content before everyone else."
                         FontSize="12" Opacity="0.7" TextWrapping="Wrap"/>

              <!-- Channel -->
              <StackPanel Spacing="4">
                <TextBlock Text="Channel" FontWeight="SemiBold"/>
                <StackPanel Orientation="Horizontal" Spacing="16">
                  <RadioButton GroupName="CatalogChannel" Content="Stable" IsChecked="{Binding IsStable, Mode=TwoWay}" IsEnabled="{Binding CanChooseChannel}"/>
                  <RadioButton GroupName="CatalogChannel" Content="Preview" IsChecked="{Binding IsPreview, Mode=TwoWay}" IsEnabled="{Binding CanChooseChannel}"/>
                </StackPanel>
                <TextBlock Text="{Binding ChannelHint}" FontSize="12" Opacity="0.7" IsVisible="{Binding ChannelHint, Converter={x:Static ObjectConverters.IsNotNull}}"/>
              </StackPanel>

              <!-- Switch warning -->
              <Border IsVisible="{Binding ShowWarning}" Background="#33D29922" BorderBrush="#D29922" BorderThickness="1" CornerRadius="4" Padding="12">
                <StackPanel Spacing="8">
                  <TextBlock Text="{Binding WarningText}" TextWrapping="Wrap"/>
                  <StackPanel Orientation="Horizontal" Spacing="8">
                    <Button Content="{Binding SwitchButtonText}" Command="{Binding ConfirmSwitchCommand}" Classes="accent"/>
                    <Button Content="{Binding KeepButtonText}" Command="{Binding KeepChannelCommand}"/>
                  </StackPanel>
                </StackPanel>
              </Border>

              <!-- Automatic updates -->
              <StackPanel Spacing="4">
                <ToggleSwitch IsChecked="{Binding AutoApply, Mode=TwoWay}" IsEnabled="{Binding CanToggleAutoApply}"
                              OnContent="Apply catalog updates automatically" OffContent="Apply catalog updates automatically"/>
                <TextBlock Text="When Diffusion Nexus starts and finds a catalog update, it applies it without asking. Never while an install is running; then the update waits here."
                           FontSize="12" Opacity="0.7" TextWrapping="Wrap"/>
              </StackPanel>

              <!-- Status -->
              <StackPanel Spacing="4">
                <TextBlock Text="{Binding StatusText}" FontWeight="SemiBold"/>
                <TextBlock Text="{Binding OutcomeText}" TextWrapping="Wrap"/>
                <TextBlock Text="{Binding LastCheckedText}" FontSize="12" Opacity="0.7" IsVisible="{Binding LastCheckedText, Converter={x:Static ObjectConverters.IsNotNull}}"/>
              </StackPanel>

              <!-- Change list -->
              <ItemsControl ItemsSource="{Binding ChangeGroups}">
                <ItemsControl.ItemTemplate>
                  <DataTemplate x:DataType="vm:CatalogChangeGroup">
                    <StackPanel Spacing="2" Margin="0,4,0,0">
                      <TextBlock Text="{Binding Heading}" FontWeight="SemiBold" FontSize="12"/>
                      <ItemsControl ItemsSource="{Binding Rows}">
                        <ItemsControl.ItemTemplate>
                          <DataTemplate x:DataType="x:String">
                            <TextBlock Text="{Binding}" FontSize="12" TextWrapping="Wrap"/>
                          </DataTemplate>
                        </ItemsControl.ItemTemplate>
                      </ItemsControl>
                    </StackPanel>
                  </DataTemplate>
                </ItemsControl.ItemTemplate>
              </ItemsControl>

              <TextBlock Text="{Binding ProgressText}" IsVisible="{Binding ProgressText, Converter={x:Static ObjectConverters.IsNotNull}}"/>
              <TextBlock Text="{Binding ApplyBlockedReason}" FontSize="12" Opacity="0.7" TextWrapping="Wrap"
                         IsVisible="{Binding ApplyBlockedReason, Converter={x:Static ObjectConverters.IsNotNull}}"/>
              <TextBlock Text="{Binding ErrorText}" Foreground="#FF6B6B" TextWrapping="Wrap"
                         IsVisible="{Binding ErrorText, Converter={x:Static ObjectConverters.IsNotNull}}"/>

              <StackPanel Orientation="Horizontal" Spacing="8">
                <Button Content="Check for updates" Command="{Binding CheckCommand}"/>
                <Button Content="Apply catalog update" Command="{Binding ApplyCommand}" Classes="accent" IsVisible="{Binding ShowApply}"/>
              </StackPanel>
            </StackPanel>
          </Border>
        </Expander>
```

If `vm:` does not map the namespace `DiffusionNexus.UI.ViewModels` in this view, use the prefix the view already declares for `SettingsViewModel`.

- [ ] **Step 7: Build, whole suite, commit**

Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -v q 2>&1 | grep -E " error |Build succeeded" | head -5` — Expected: `Build succeeded` (compiled bindings check every binding path).
Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q 2>&1 | tail -3` — Expected: `Failed: 0`.

```bash
git add -A
git commit -m "feat(settings): #618 Settings → Catalog — channel with switch warning, check, change list, Apply, apply automatically"
git push
```

---

## After the tasks

1. Whole-branch review (fresh reviewer, most capable model) against this plan, the spec §6 and the Review Focus; fix Critical/Important test-first.
2. Open the PR to `develop` (closes #618), then the owner's `/code-review` rounds: 3, plus 2 more while bugs remain, each with a fixed/skipped table on the PR.
3. **Smoke test on the BenQ** (as issue #618 describes): installer channel Preview + no catalog columns → Preview shown, `ITL-Shared\format-1\catalog-preview\` read, console line "First start with the catalog setting: following the installer's Preview channel"; Check → list + Apply; switch to Stable (warning if Diffusion Nexus workloads go) → `catalog-stable\` read; toggle on, restart with an update pending → applied silently with console lines; a workload install running → "It can be applied once … has finished." Back up `%LocalAppData%\DiffusionNexus\Data` and `ITL-Shared` first, restore after.
