# Metadata Sync Overhaul — Plan A: Schema, Sync State & LibrarySyncService (WP1 + WP2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the tile-driven, absence-based metadata sync phases in `LoraViewerViewModel` with a DB-driven `LibrarySyncService` that records *attempt outcomes* in a new `ModelSyncStates` table, so a re-run on an unchanged library is a no-op — without any network traffic, data loss or overwrite for existing users on upgrade.

**Architecture:** (1) Additive schema: `ModelSyncState` 1:1 with `Model`, two nullable columns on `ModelImage`, one data migration (hash uppercase), plus an automatic pre-migration `VACUUM INTO` backup in `DatabaseRecoveryService`. (2) `SyncStateDeriver` creates state rows for legacy models from existing data (pure function, no HTTP). (3) `LibrarySyncService` = `PlanAsync` (DB queries + in-memory retry policy → counts) and `ExecuteAsync` (ordered `ISyncStep`s: DiscoverFiles, IdentifyModel, FetchTags, FetchImages; each stamps state). The two metadata "appliers" currently buried in the ViewModel (Civitai response → DB, sidecar JSON → DB) move to the Service layer unchanged in behavior. (4) The ViewModel's `DownloadMissingMetadataAsync` and per-tile `DownloadMetadataForTileAsync` call the service with `SyncScope.Library` / `SyncScope.Models(id)`; phases 1/1b/2/3/4 are deleted. Thumbnails (WP3), the header/heuristic identity chain (WP4), the unified download path (WP5) and the plan dialog (WP6) are later plans on the same spec; this plan leaves explicit seams for them (`ISyncStep` registry, `SyncStepKind.Thumbnails` reserved, `IdentifyModelStep` fallback order).

**Tech Stack:** .NET 10, EF Core 10 (SQLite), CommunityToolkit.Mvvm, xUnit 2.9 + FluentAssertions 8 + Moq 4.20, Serilog (Service) / `IUnifiedLogger` (Unified Console).

**Spec:** `docs/superpowers/specs/2026-08-21-metadata-sync-overhaul-design.md` (committed copy of issue #521 with decisions D1–D6 resolved).

## Global Constraints

- Repo: `e:\Repos\DiffusionNexus`, branch `feature/metadata-sync-overhaul` (off `origin/develop`). Never commit to `develop`/`main`. Commit trailer: `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- Tests: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release` — **never** solution-level. Filter with `--filter "FullyQualifiedName~<Namespace>"`. Known unrelated flake: `GenerationGalleryViewModelTests.TagCloudSearchText_FiltersTheVisibleChips`.
- The user's running app may lock `DiffusionNexus.UI\bin\Release`; if a Release *build* fails only with MSB3021/MSB3027 copy errors, build `-c Debug` to verify compilation and say so.
- Repo rule (`.github/copilot-instructions.md`): **before modifying entity classes, `IEntityTypeConfiguration`s or migrations, run `publish.ps1`** (Task 1 Step 1 does this once for the whole plan). When refactoring, mark replaced members `[Obsolete("...")]` before deleting them in the wiring task.
- Windows-first; where a path/filesystem rule is Win32-specific, add `// TODO: Linux Implementation for Task X` and keep the code open for extension.
- Schema safety (spec §3): additive only; nullable columns; no rewrite of existing columns except the approved `UPDATE ModelFiles SET HashSHA256 = upper(HashSHA256)`; no network on upgrade; never overwrite existing data.
- Retry policy (D1): `NotIdentified` / `NotOnCivitai` re-check after 30 days; `Error` after 1 day, max 3 attempts; hard failures never (Force only).
- Existing name clash: `DiffusionNexus.Domain.Services.SyncProgress` already exists (file sync). New types use the `LibrarySync*` prefix.
- Logging: Unified Console via optional `IUnifiedLogger?` ctor param (nullable field, `sp.GetService<IUnifiedLogger>()` in DI), category `LogCategory.Network` for Civitai calls and `LogCategory.FileSystem` for disk work, source `"LibrarySync"`. One `Warn` line per failed item (no stack trace); `Debug` for per-item success; `Info` at step start/end with counts.
- EF Core SQLite cannot translate `DateTimeOffset` comparisons — **all staleness/retry math happens in C#** on projected rows, never in `Where`.
- `DateTimeOffset` columns are stored as TEXT by the provider (no converters), consistent with `Model.LastSyncedAt`.

---

## File Structure

| File | Responsibility |
|---|---|
| `DiffusionNexus.Domain/Enums/SyncOutcome.cs` | **Create.** Outcome of the identity step. |
| `DiffusionNexus.Domain/Entities/ModelSyncState.cs` | **Create.** 1:1 attempt-state row per `Model`. |
| `DiffusionNexus.Domain/Entities/ThumbnailFailureReason.cs` | **Create.** String constants for `ModelImage.ThumbnailFailure`. |
| `DiffusionNexus.Domain/Entities/Model.cs` | **Modify.** `SyncState` navigation. |
| `DiffusionNexus.Domain/Entities/ModelImage.cs` | **Modify.** `ThumbnailAttemptedAt`, `ThumbnailFailure`. |
| `DiffusionNexus.DataAccess/Configurations/ModelSyncStateConfiguration.cs` | **Create.** Table/keys/conversions. |
| `DiffusionNexus.DataAccess/Configurations/ModelConfiguration.cs`, `ModelImageConfiguration.cs` | **Modify.** Navigation + new column lengths. |
| `DiffusionNexus.DataAccess/Data/DiffusionNexusCoreDbContext.cs` | **Modify.** `DbSet<ModelSyncState>`. |
| `DiffusionNexus.DataAccess/Migrations/Core/<ts>_AddModelSyncStateAndThumbnailAttempts.cs` (+Designer, +Snapshot) | **Create (generated).** Additive migration + hash uppercase SQL. |
| `DiffusionNexus.DataAccess/Recovery/PreMigrationBackup.cs` | **Create.** `VACUUM INTO` before `Migrate()`, retention 3. |
| `DiffusionNexus.DataAccess/Recovery/DatabaseRecoveryService.cs` | **Modify.** Call backup; self-heal `ModelImages` columns + `ModelSyncStates` table. |
| `DiffusionNexus.DataAccess/Repositories/Interfaces/ISyncStateRepository.cs`, `Repositories/SyncStateRepository.cs` | **Create.** State rows + the three candidate selection queries. |
| `DiffusionNexus.DataAccess/UnitOfWork/IUnitOfWork.cs`, `UnitOfWork.cs` | **Modify.** `SyncStates` property. |
| `DiffusionNexus.Domain/Services/Sync/*.cs` | **Create.** Contracts: `SyncScope`, `SyncStepKind`, `SyncOptions`, `SyncRetryPolicy`, `SyncPlan`, `SyncReport`, `LibrarySyncProgress`, `ILibrarySyncService`, candidate records. |
| `DiffusionNexus.Service/Services/Sync/SyncStateDeriver.cs` | **Create.** Pure derivation (spec S3). |
| `DiffusionNexus.Service/Services/Sync/SyncStateInitializer.cs` | **Create.** Creates missing rows in batches. |
| `DiffusionNexus.Service/Services/Sync/CivitaiMetadataApplier.cs` | **Create (moved from VM).** Civitai response → DB graph. |
| `DiffusionNexus.Service/Services/Sync/SidecarMetadataApplier.cs` | **Create (moved from VM).** `.civitai.info`/`.json` → DB graph, plus local preview thumbnail. |
| `DiffusionNexus.Service/Services/Sync/Steps/ISyncStep.cs`, `DiscoverFilesStep.cs`, `IdentifyModelStep.cs`, `FetchTagsStep.cs`, `FetchImagesStep.cs` | **Create.** One step per file. |
| `DiffusionNexus.Service/Services/Sync/LibrarySyncService.cs` | **Create.** Plan/Execute orchestration. |
| `DiffusionNexus.Service/Services/Sync/SyncServiceCollectionExtensions.cs` | **Create.** `AddLibrarySync()`. |
| `DiffusionNexus.UI/App.axaml.cs` | **Modify.** Register. |
| `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs` | **Modify.** Call service; delete phases 1/1b/2/3/4 + appliers. |
| `DiffusionNexus.UI/Doc/LoraViewer.md` | **Modify.** Document the new sync. |
| `DiffusionNexus.Tests/Sync/**` | **Create.** Tests per task (`namespace DiffusionNexus.Tests.Sync...`). |
| `DiffusionNexus.Tests/DataAccess/CoreDbMigrationTests.cs`, `DataAccess/Recovery/*` | **Modify/Create.** Migration + backup tests. |

Test harness used by every DB test in this plan (copy into each test class; there is deliberately no shared helper in this repo):

```csharp
// in-memory SQLite through the public DI surface (repositories are internal)
private readonly SqliteConnection _connection = new("DataSource=:memory:");
private readonly ServiceProvider _sp;
public XTests()
{
    _connection.Open();
    var services = new ServiceCollection();
    services.AddDataAccessLayer(o => o.UseSqlite(_connection));
    _sp = services.BuildServiceProvider();
    using var scope = _sp.CreateScope();
    scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>().Database.EnsureCreated();
}
private IUnitOfWork NewUow() => _sp.CreateScope().ServiceProvider.GetRequiredService<IUnitOfWork>(); // one scope per call; dispose via uow
public void Dispose() { _sp.Dispose(); _connection.Dispose(); }
```
(`EnsureCreated` builds the schema from the current model, so new entities are present without running migrations.)

---

### Task 1: Domain types (entity, enum, columns)

**Files:**
- Create: `DiffusionNexus.Domain/Enums/SyncOutcome.cs`
- Create: `DiffusionNexus.Domain/Entities/ModelSyncState.cs`
- Create: `DiffusionNexus.Domain/Entities/ThumbnailFailureReason.cs`
- Modify: `DiffusionNexus.Domain/Entities/Model.cs:97` (after `Tags`)
- Modify: `DiffusionNexus.Domain/Entities/ModelImage.cs:75` (after `ThumbnailHeight`)
- Test: `DiffusionNexus.Tests/Sync/Domain/ModelSyncStateTests.cs`

**Interfaces:**
- Produces: `enum SyncOutcome { None, Matched, Sidecar, Header, Heuristic, NotIdentified, Error }`; `class ModelSyncState { int ModelId; Model? Model; DateTimeOffset? MetadataCheckedAt; SyncOutcome MetadataOutcome; int MetadataAttempts; string? LastError; DateTimeOffset? TagsCheckedAt; DateTimeOffset? ImagesCheckedAt; string? SidecarSignature; DateTimeOffset? HeaderCheckedAt; DateTimeOffset UpdatedAt; }`; `ModelImage.ThumbnailAttemptedAt : DateTimeOffset?`, `ModelImage.ThumbnailFailure : string?`; `static class ThumbnailFailureReason { Http404, HttpError, NotDecodable, Corrupt, LocalFileMissing, VideoNoPoster, UnsupportedScheme; IsHardFailure(string?) }`.

- [ ] **Step 1: Repo-rule safety backup (once for the whole plan)**

Run from `e:\Repos\DiffusionNexus` in PowerShell:
```powershell
.\publish.ps1 -SkipDatabasePrompt -IncludeDatabase -NoZip -NoBump
```
Expected: `publish\` refreshed with the app + a copy of `Diffusion_Nexus-core.db`. Then an explicit DB safety copy (read-only VACUUM, app may be running):
```powershell
$src = "$env:LOCALAPPDATA\DiffusionNexus\Data\Diffusion_Nexus-core.db"
$dst = "$env:LOCALAPPDATA\DiffusionNexus\Data\Diffusion_Nexus-core.pre-metadata-sync-$(Get-Date -Format yyyyMMdd-HHmmss).db"
python -c "import sqlite3,sys; c=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True); c.execute(\"VACUUM INTO '\"+sys.argv[2].replace(\"'\",\"''\")+\"'\"); print('ok')" $src $dst
```
Expected: `ok` and the new file exists. If `publish.ps1` fails for an unrelated reason (locked files), record the failure in the task report; the VACUUM copy is the backup that matters.

- [ ] **Step 2: Write the failing test**

`DiffusionNexus.Tests/Sync/Domain/ModelSyncStateTests.cs`:
```csharp
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Domain;

public class ModelSyncStateTests
{
    [Fact]
    public void NewStateDefaultsToNoneWithZeroAttempts()
    {
        var state = new ModelSyncState { ModelId = 7 };

        state.MetadataOutcome.Should().Be(SyncOutcome.None);
        state.MetadataAttempts.Should().Be(0);
        state.MetadataCheckedAt.Should().BeNull();
        state.TagsCheckedAt.Should().BeNull();
        state.ImagesCheckedAt.Should().BeNull();
        state.HeaderCheckedAt.Should().BeNull();
        state.SidecarSignature.Should().BeNull();
        state.LastError.Should().BeNull();
    }

    [Theory]
    [InlineData(ThumbnailFailureReason.Http404, true)]
    [InlineData(ThumbnailFailureReason.NotDecodable, true)]
    [InlineData(ThumbnailFailureReason.LocalFileMissing, true)]
    [InlineData(ThumbnailFailureReason.UnsupportedScheme, true)]
    [InlineData(ThumbnailFailureReason.HttpError, false)]
    [InlineData(ThumbnailFailureReason.Corrupt, false)]
    [InlineData(ThumbnailFailureReason.VideoNoPoster, false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void HardFailuresAreNeverAutoRetried(string? reason, bool expectedHard)
    {
        ThumbnailFailureReason.IsHardFailure(reason).Should().Be(expectedHard);
    }

    [Fact]
    public void ModelImageCarriesAttemptColumns()
    {
        var image = new ModelImage { Url = "https://x/y.jpeg" };
        image.ThumbnailAttemptedAt.Should().BeNull();
        image.ThumbnailFailure.Should().BeNull();
    }
}
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~DiffusionNexus.Tests.Sync.Domain"`
Expected: build error — `ModelSyncState`, `SyncOutcome`, `ThumbnailFailureReason` do not exist.

- [ ] **Step 4: Implement**

`DiffusionNexus.Domain/Enums/SyncOutcome.cs`:
```csharp
namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// How the identity step last resolved a model (base model / Civitai linkage).
/// Persisted as a string (see <c>ModelSyncStateConfiguration</c>) — append new members, never reorder.
/// </summary>
public enum SyncOutcome
{
    /// <summary>Never attempted.</summary>
    None = 0,
    /// <summary>Matched to a Civitai version by file hash.</summary>
    Matched,
    /// <summary>Not on Civitai; metadata came from a .civitai.info / .json sidecar.</summary>
    Sidecar,
    /// <summary>Not on Civitai, no sidecar; base model read from the safetensors header (WP4).</summary>
    Header,
    /// <summary>Base model guessed from the file name (WP4). Shown to the user as "guessed".</summary>
    Heuristic,
    /// <summary>Every source was tried and none identified the model. Re-checked after the retry window.</summary>
    NotIdentified,
    /// <summary>The attempt failed (network, disk, parse). Re-checked after the short retry window, bounded by attempts.</summary>
    Error
}
```

`DiffusionNexus.Domain/Entities/ModelSyncState.cs`:
```csharp
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// Persisted record of what the library sync has already <i>tried</i> for one <see cref="Model"/>,
/// so "checked and genuinely empty" is distinguishable from "never checked". One row per model
/// (PK = FK). A model without a row is a legacy row whose state is derived from existing data
/// on first plan (<c>SyncStateDeriver</c>) — never by calling the network.
/// </summary>
public class ModelSyncState
{
    /// <summary>Primary key and foreign key to <see cref="Model"/>.</summary>
    public int ModelId { get; set; }

    public Model? Model { get; set; }

    /// <summary>Last identity attempt (hash lookup + fallback chain).</summary>
    public DateTimeOffset? MetadataCheckedAt { get; set; }

    public SyncOutcome MetadataOutcome { get; set; } = SyncOutcome.None;

    /// <summary>Consecutive failed identity attempts; reset to 0 on any non-error outcome.</summary>
    public int MetadataAttempts { get; set; }

    /// <summary>One-line description of the last failure. Never a stack trace.</summary>
    public string? LastError { get; set; }

    /// <summary>Tags were fetched for the model's Civitai id — stamped even when the result was empty.</summary>
    public DateTimeOffset? TagsCheckedAt { get; set; }

    /// <summary>Image records were fetched for the model's versions — stamped even when the result was empty.</summary>
    public DateTimeOffset? ImagesCheckedAt { get; set; }

    /// <summary>
    /// <c>{fullPath}|{lastWriteUtcTicks}|{length}</c> of the sidecar last parsed, so an unchanged sidecar is not
    /// re-parsed on every run and a changed one is.
    /// </summary>
    public string? SidecarSignature { get; set; }

    /// <summary>The safetensors header was read (WP4).</summary>
    public DateTimeOffset? HeaderCheckedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

`DiffusionNexus.Domain/Entities/ThumbnailFailureReason.cs`:
```csharp
namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// Values for <see cref="ModelImage.ThumbnailFailure"/>. Strings (not an enum) so a future reason
/// needs no migration. Hard failures are never retried automatically — only by an explicit Force.
/// </summary>
public static class ThumbnailFailureReason
{
    public const string Http404 = "Http404";
    public const string HttpError = "HttpError";
    public const string NotDecodable = "NotDecodable";
    /// <summary>An existing BLOB failed to decode; it was nulled and will be re-fetched once.</summary>
    public const string Corrupt = "Corrupt";
    public const string LocalFileMissing = "LocalFileMissing";
    public const string VideoNoPoster = "VideoNoPoster";
    /// <summary>URL scheme the thumbnail pipeline cannot fetch (anything but http/https/file).</summary>
    public const string UnsupportedScheme = "UnsupportedScheme";

    public static bool IsHardFailure(string? reason) => reason is Http404 or NotDecodable or LocalFileMissing or UnsupportedScheme;
}
```

`Model.cs` — after line 97 (`public ICollection<ModelTag> Tags ...`):
```csharp
    /// <summary>Library-sync attempt state (1:1, optional — null for legacy rows until derived).</summary>
    public ModelSyncState? SyncState { get; set; }
```

`ModelImage.cs` — after `ThumbnailHeight` (line 75):
```csharp
    /// <summary>When the thumbnail pipeline last tried to produce <see cref="ThumbnailData"/> for this image.</summary>
    public DateTimeOffset? ThumbnailAttemptedAt { get; set; }

    /// <summary>Why the last attempt failed — one of <see cref="ThumbnailFailureReason"/>; null after success.</summary>
    public string? ThumbnailFailure { get; set; }
```

- [ ] **Step 5: Run tests**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~DiffusionNexus.Tests.Sync.Domain"`
Expected: 11 passed.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.Domain DiffusionNexus.Tests/Sync
git commit -m "feat(sync): ModelSyncState entity, SyncOutcome, thumbnail attempt columns (#521 WP1)"
```

---

### Task 2: EF configuration, migration, self-heal

**Files:**
- Create: `DiffusionNexus.DataAccess/Configurations/ModelSyncStateConfiguration.cs`
- Modify: `DiffusionNexus.DataAccess/Configurations/ModelConfiguration.cs:36-39` (relationships block)
- Modify: `DiffusionNexus.DataAccess/Configurations/ModelImageConfiguration.cs:24-26` (thumbnail block)
- Modify: `DiffusionNexus.DataAccess/Data/DiffusionNexusCoreDbContext.cs` (`#region DbSets`)
- Create (generated): `DiffusionNexus.DataAccess/Migrations/Core/<ts>_AddModelSyncStateAndThumbnailAttempts.cs` + `.Designer.cs`, regenerated `DiffusionNexusCoreDbContextModelSnapshot.cs`
- Modify: `DiffusionNexus.DataAccess/Recovery/DatabaseRecoveryService.cs:289-339` (`RepairModelsTableColumns` sibling)
- Test: `DiffusionNexus.Tests/DataAccess/CoreDbMigrationTests.cs` (extend), `DiffusionNexus.Tests/Sync/DataAccess/SyncSchemaMigrationTests.cs`

**Interfaces:**
- Consumes: Task 1 types.
- Produces: table `ModelSyncStates` (PK `ModelId`, FK → `Models.Id` cascade, `MetadataOutcome TEXT(20)`, `LastError TEXT(500)`, `SidecarSignature TEXT(1100)`), columns `ModelImages.ThumbnailAttemptedAt TEXT NULL`, `ModelImages.ThumbnailFailure TEXT(30) NULL`; `ModelFiles.HashSHA256` uppercased; `DbSet<ModelSyncState> ModelSyncStates`.

- [ ] **Step 1: Write the failing tests**

`DiffusionNexus.Tests/Sync/DataAccess/SyncSchemaMigrationTests.cs` — file-based, runs the real migration chain, asserts (a) additive-only, (b) hash uppercase, (c) pre-existing rows survive:
```csharp
using DiffusionNexus.DataAccess.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DiffusionNexus.Tests.Sync.DataAccess;

public sealed class SyncSchemaMigrationTests : IDisposable
{
    private const string PreviousMigration = "20260816161430_AddInstallerPackageIsAppManaged";
    private const string NewMigration = "AddModelSyncStateAndThumbnailAttempts";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dnx-syncschema-");
    private string DbPath => Path.Combine(_dir.FullName, "core.db");

    private DiffusionNexusCoreDbContext NewContext() => new(
        new DbContextOptionsBuilder<DiffusionNexusCoreDbContext>()
            .UseSqlite($"Data Source={DbPath};Pooling=False").Options);

    private static Dictionary<string, List<string>> Columns(DiffusionNexusCoreDbContext ctx, params string[] tables)
    {
        var result = new Dictionary<string, List<string>>();
        var conn = ctx.Database.GetDbConnection();
        conn.Open();
        foreach (var t in tables)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info('{t}');";
            using var r = cmd.ExecuteReader();
            var cols = new List<string>();
            while (r.Read()) cols.Add($"{r["name"]}:{r["type"]}:{r["notnull"]}");
            result[t] = cols;
        }
        conn.Close();
        return result;
    }

    private void MigrateTo(string target)
    {
        using var ctx = NewContext();
        ctx.GetService<IMigrator>().Migrate(target);
    }

    [Fact]
    public void MigrationIsAdditiveAndUppercasesHashes()
    {
        MigrateTo(PreviousMigration);

        // seed a legacy row set with a lowercase hash
        using (var ctx = NewContext())
        {
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO Models (Id, Name, Type, Mode, Source, IsNsfw, IsPoi, AllowNoCredit, AllowCommercialUse, AllowDerivatives, AllowDifferentLicense, CreatedAt, IsUserEdited, TotalVersionCount) " +
                "VALUES (1, 'legacy', 'LORA', 'Available', 'LocalFile', 0, 0, 0, 'None', 0, 0, '2026-01-01T00:00:00+00:00', 0, 0);");
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO ModelVersions (Id, ModelId, Name, BaseModel, EarlyAccessDays, CreatedAt, IsUserEdited, DownloadCount, RatingCount, Rating, ThumbsUpCount, ThumbsDownCount) " +
                "VALUES (1, 1, 'v1', 'Unknown', 0, '2026-01-01T00:00:00+00:00', 0, 0, 0, 0, 0, 0);");
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO ModelFiles (Id, ModelVersionId, FileName, SizeKB, FileType, IsPrimary, Format, Precision, SizeType, PickleScanResult, VirusScanResult, HashSHA256, LocalPath, IsLocalFileValid) " +
                "VALUES (1, 1, 'a.safetensors', 1, 'Model', 1, 'SafeTensor', 'Unknown', 'Unknown', 'Pending', 'Pending', 'abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789', 'C:\\l\\a.safetensors', 1);");
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO ModelImages (Id, ModelVersionId, Url, IsNsfw, NsfwLevel, Width, Height, SortOrder, IsLocalCacheValid, LikeCount, HeartCount, CommentCount) " +
                "VALUES (1, 1, 'https://x/y.jpeg', 0, 'None', 0, 0, 0, 0, 0, 0, 0);");
        }

        Dictionary<string, List<string>> before;
        using (var ctx = NewContext()) before = Columns(ctx, "Models", "ModelVersions", "ModelFiles", "ModelImages");

        using (var ctx = NewContext()) ctx.Database.Migrate();

        using (var ctx = NewContext())
        {
            var after = Columns(ctx, "Models", "ModelVersions", "ModelFiles", "ModelImages");
            foreach (var (table, cols) in before)
                after[table].Should().StartWith(cols, $"existing columns of {table} must be untouched (additive-only)");
            after["ModelImages"].Should().Contain("ThumbnailAttemptedAt:TEXT:0").And.Contain("ThumbnailFailure:TEXT:0");

            var syncStateCols = Columns(ctx, "ModelSyncStates")["ModelSyncStates"];
            syncStateCols.Should().Contain("ModelId:INTEGER:1").And.Contain("MetadataOutcome:TEXT:1");

            var hash = ctx.Set<DiffusionNexus.Domain.Entities.ModelFile>().Single().HashSHA256;
            hash.Should().Be("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789");

            ctx.Set<DiffusionNexus.Domain.Entities.ModelImage>().Single().Url.Should().Be("https://x/y.jpeg", "rows survive");
            ctx.Set<DiffusionNexus.Domain.Entities.ModelSyncState>().Should().BeEmpty("the migration never invents state rows");
            ctx.Database.GetAppliedMigrations().Should().Contain(m => m.EndsWith(NewMigration));
        }
    }

    [Fact]
    public void MigrationIsIdempotentOnRerun()
    {
        using (var ctx = NewContext()) ctx.Database.Migrate();
        using (var ctx = NewContext()) ctx.Database.Migrate();
        using var check = NewContext();
        check.Database.GetPendingMigrations().Should().BeEmpty();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { _dir.Delete(recursive: true); } catch { }
    }
}
```
If the seed `INSERT`s fail with "table X has no column named Y" / "NOT NULL constraint failed", fix the INSERT to match the **previous** migration's schema (read `DiffusionNexusCoreDbContextModelSnapshot.cs` at `PreviousMigration`), not the migration.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~SyncSchemaMigrationTests"`
Expected: FAIL — `ModelSyncStates` table does not exist / `HashSHA256` still lowercase.

- [ ] **Step 3: Configuration + DbSet**

`DiffusionNexus.DataAccess/Configurations/ModelSyncStateConfiguration.cs`:
```csharp
using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class ModelSyncStateConfiguration : IEntityTypeConfiguration<ModelSyncState>
{
    public void Configure(EntityTypeBuilder<ModelSyncState> entity)
    {
        entity.ToTable("ModelSyncStates");
        // PK == FK: exactly one state row per model; deleting the model deletes its state.
        entity.HasKey(e => e.ModelId);

        entity.Property(e => e.MetadataOutcome).HasConversion<string>().HasMaxLength(20);
        entity.Property(e => e.LastError).HasMaxLength(500);
        entity.Property(e => e.SidecarSignature).HasMaxLength(1100);

        entity.HasOne(e => e.Model)
            .WithOne(m => m.SyncState)
            .HasForeignKey<ModelSyncState>(e => e.ModelId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```
`ModelImageConfiguration.cs` — after `entity.Property(e => e.ThumbnailMimeType).HasMaxLength(50);`:
```csharp
        entity.Property(e => e.ThumbnailFailure).HasMaxLength(30);
```
`DiffusionNexusCoreDbContext.cs` — inside `#region DbSets`, next to `Models`:
```csharp
    public DbSet<ModelSyncState> ModelSyncStates => Set<ModelSyncState>();
```
(`ModelConfiguration` needs no change — the relationship is configured from the dependent side.)

- [ ] **Step 4: Generate the migration, then add the data statement**

```powershell
cd e:\Repos\DiffusionNexus\DiffusionNexus.DataAccess
dotnet ef migrations add AddModelSyncStateAndThumbnailAttempts --context DiffusionNexusCoreDbContext --output-dir Migrations/Core
```
Expected: three files created/updated under `Migrations/Core`. Open the new `<ts>_AddModelSyncStateAndThumbnailAttempts.cs` and verify `Up` contains exactly: `AddColumn ThumbnailAttemptedAt (TEXT, nullable: true)`, `AddColumn ThumbnailFailure (TEXT, maxLength 30, nullable: true)`, `CreateTable ModelSyncStates` (PK `ModelId`, FK cascade), and nothing else. If it contains anything touching other tables, the model snapshot was stale — stop and report. Then append to the end of `Up`:
```csharp
            // D2 (issue #521): downloads stored SHA256 uppercase, the viewer's sync stored it lowercase.
            // Normalize once so SQL equality works without ToLower() scans. Idempotent; covered by the
            // pre-migration backup taken by DatabaseRecoveryService.
            migrationBuilder.Sql("UPDATE ModelFiles SET HashSHA256 = upper(HashSHA256) WHERE HashSHA256 IS NOT NULL AND HashSHA256 <> upper(HashSHA256);");
```
`Down` stays as generated (hash casing is not reverted — lossless either way).

- [ ] **Step 5: Self-heal entries**

In `DatabaseRecoveryService.cs`, directly after the call `RepairModelsTableColumns(dbContext, connection);` (inside `CheckAndRepairSchema`), add `RepairModelImagesTableColumns(dbContext, connection);` and `EnsureModelSyncStatesTable(dbContext);`. Add the two private methods next to `RepairModelsTableColumns` (same shape as that method — read it first):
```csharp
    /// <summary>Mirror of <see cref="RepairModelsTableColumns"/> for ModelImages. Add entries whenever a migration adds nullable columns there.</summary>
    private void RepairModelImagesTableColumns(DiffusionNexusCoreDbContext dbContext, System.Data.Common.DbConnection connection)
    {
        var existing = ReadColumnNames(connection, "ModelImages");
        var required = new Dictionary<string, string>
        {
            { "ThumbnailAttemptedAt", "ALTER TABLE ModelImages ADD COLUMN ThumbnailAttemptedAt TEXT" },
            { "ThumbnailFailure", "ALTER TABLE ModelImages ADD COLUMN ThumbnailFailure TEXT" },
        };
        foreach (var col in required)
        {
            if (existing.Contains(col.Key)) continue;
            _log.Warning($"CheckAndRepairSchema: Missing ModelImages.'{col.Key}'. Attempting to add...");
            try { dbContext.Database.ExecuteSqlRaw(col.Value); }
            catch (Exception ex) { _log.Error(ex, $"CheckAndRepairSchema: Failed to add ModelImages.'{col.Key}'"); }
        }
    }

    private void EnsureModelSyncStatesTable(DiffusionNexusCoreDbContext dbContext)
    {
        try
        {
            dbContext.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS ModelSyncStates (" +
                "ModelId INTEGER NOT NULL CONSTRAINT PK_ModelSyncStates PRIMARY KEY, " +
                "MetadataCheckedAt TEXT NULL, MetadataOutcome TEXT NOT NULL, MetadataAttempts INTEGER NOT NULL, " +
                "LastError TEXT NULL, TagsCheckedAt TEXT NULL, ImagesCheckedAt TEXT NULL, SidecarSignature TEXT NULL, " +
                "HeaderCheckedAt TEXT NULL, UpdatedAt TEXT NOT NULL, " +
                "CONSTRAINT FK_ModelSyncStates_Models_ModelId FOREIGN KEY (ModelId) REFERENCES Models (Id) ON DELETE CASCADE);");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "CheckAndRepairSchema: Failed to ensure ModelSyncStates table");
        }
    }
```
If `RepairModelsTableColumns` reads columns inline rather than through a `ReadColumnNames(connection, table)` helper, extract that helper (private static, returns `HashSet<string>(OrdinalIgnoreCase)` from `PRAGMA table_info`) and use it in both methods. The `CREATE TABLE` text must match the generated migration's column types exactly — copy them from the migration file.

- [ ] **Step 6: Run tests**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~SyncSchemaMigrationTests|FullyQualifiedName~CoreDbMigrationTests|FullyQualifiedName~DatabaseRecoveryServiceTests"`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add DiffusionNexus.DataAccess DiffusionNexus.Tests/Sync/DataAccess
git commit -m "feat(sync): ModelSyncStates table + thumbnail attempt columns migration, hash uppercase, self-heal (#521 WP1)"
```

---

### Task 3: Automatic pre-migration backup (spec S2)

**Files:**
- Create: `DiffusionNexus.DataAccess/Recovery/PreMigrationBackup.cs`
- Modify: `DiffusionNexus.DataAccess/Recovery/DatabaseRecoveryService.cs:65-76` (between `GetPendingMigrations()` and `Migrate()`)
- Test: `DiffusionNexus.Tests/DataAccess/Recovery/PreMigrationBackupTests.cs`

**Interfaces:**
- Produces: `internal static class PreMigrationBackup { static string? TryCreate(string databaseFilePath, string firstPendingMigration, IDatabaseRecoveryLogger log, int keep = 3); static string BuildBackupPath(string databaseFilePath, string migrationName, DateTimeOffset now); }` — returns the backup path or null (never throws).

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.DataAccess.Recovery;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace DiffusionNexus.Tests.DataAccess.Recovery;

public sealed class PreMigrationBackupTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dnx-premig-");
    private string DbPath => Path.Combine(_dir.FullName, "Diffusion_Nexus-core.db");

    private void CreateDbWithRow()
    {
        using var c = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE T (X INTEGER); INSERT INTO T VALUES (42);";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void BuildBackupPathUsesMigrationNameAndTimestampNextToTheDatabase()
    {
        var path = PreMigrationBackup.BuildBackupPath(@"C:\d\Diffusion_Nexus-core.db", "20260821120000_AddModelSyncState",
            new DateTimeOffset(2026, 8, 21, 13, 5, 9, TimeSpan.Zero));
        path.Should().Be(@"C:\d\Diffusion_Nexus-core.pre-AddModelSyncState-20260821-130509.db");
    }

    [Fact]
    public void TryCreateWritesAReadableCopy()
    {
        CreateDbWithRow();
        var backup = PreMigrationBackup.TryCreate(DbPath, "20260821120000_AddModelSyncState", NullDatabaseRecoveryLogger.Instance);
        backup.Should().NotBeNull();
        File.Exists(backup).Should().BeTrue();
        using var c = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT X FROM T";
        cmd.ExecuteScalar().Should().Be(42L);
    }

    [Fact]
    public void TryCreateKeepsOnlyTheNewestN()
    {
        CreateDbWithRow();
        for (var i = 0; i < 5; i++)
        {
            PreMigrationBackup.TryCreate(DbPath, $"2026082112000{i}_M{i}", NullDatabaseRecoveryLogger.Instance, keep: 3);
            Thread.Sleep(1100); // timestamp resolution is seconds
        }
        Directory.GetFiles(_dir.FullName, "Diffusion_Nexus-core.pre-*.db").Should().HaveCount(3);
    }

    [Fact]
    public void TryCreateReturnsNullWhenTheDatabaseDoesNotExistYet()
    {
        PreMigrationBackup.TryCreate(DbPath, "x", NullDatabaseRecoveryLogger.Instance).Should().BeNull();
        Directory.GetFiles(_dir.FullName).Should().BeEmpty();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { _dir.Delete(recursive: true); } catch { }
    }
}
```
`NullDatabaseRecoveryLogger` is `internal`? Check `DiffusionNexus.DataAccess/Recovery/NullDatabaseRecoveryLogger.cs`; DataAccess has no `InternalsVisibleTo`. If it is internal, add `DiffusionNexus.DataAccess/Properties/AssemblyInfo.cs` with `[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DiffusionNexus.Tests")]` (mirrors the Service/UI/Civitai projects) — this also makes `PreMigrationBackup` reachable.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~PreMigrationBackupTests"`
Expected: build error — `PreMigrationBackup` does not exist.

- [ ] **Step 3: Implement**

`DiffusionNexus.DataAccess/Recovery/PreMigrationBackup.cs`:
```csharp
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DiffusionNexus.DataAccess.Recovery;

/// <summary>
/// Takes a consistent copy of the core database immediately before pending EF migrations are applied.
/// Lives in DataAccess (not the Service-layer <c>DatabaseBackupService</c>) because
/// <see cref="DatabaseRecoveryService"/> must not depend upward on settings. <c>VACUUM INTO</c> is
/// safe against a live WAL database and produces a compact, standalone file.
/// </summary>
internal static class PreMigrationBackup
{
    private const string Marker = ".pre-";

    public static string BuildBackupPath(string databaseFilePath, string migrationName, DateTimeOffset now)
    {
        var dir = Path.GetDirectoryName(databaseFilePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(databaseFilePath);
        // "20260821120000_AddModelSyncState" -> "AddModelSyncState"
        var underscore = migrationName.IndexOf('_');
        var shortName = underscore >= 0 && underscore < migrationName.Length - 1 ? migrationName[(underscore + 1)..] : migrationName;
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(dir, $"{stem}{Marker}{shortName}-{stamp}.db");
    }

    /// <returns>The backup path, or null when nothing was backed up (no database yet, or the copy failed). Never throws.</returns>
    public static string? TryCreate(string databaseFilePath, string firstPendingMigration, IDatabaseRecoveryLogger log, int keep = 3)
    {
        try
        {
            if (!File.Exists(databaseFilePath))
            {
                log.Information("PreMigrationBackup: no existing database — nothing to back up");
                return null;
            }

            var backupPath = BuildBackupPath(databaseFilePath, firstPendingMigration, DateTimeOffset.Now);
            using (var connection = new SqliteConnection($"Data Source={databaseFilePath};Mode=ReadOnly;Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                // VACUUM INTO takes a string literal, not a bound parameter; escape embedded quotes.
                command.CommandText = $"VACUUM INTO '{backupPath.Replace("'", "''")}'";
                command.ExecuteNonQuery();
            }
            log.Information($"PreMigrationBackup: wrote {backupPath}");

            Prune(databaseFilePath, keep, log);
            return backupPath;
        }
        catch (Exception ex)
        {
            log.Error(ex, "PreMigrationBackup: backup failed — continuing with migration");
            return null;
        }
    }

    private static void Prune(string databaseFilePath, int keep, IDatabaseRecoveryLogger log)
    {
        if (keep <= 0) return;
        var dir = Path.GetDirectoryName(databaseFilePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(databaseFilePath);
        var stale = Directory.GetFiles(dir, $"{stem}{Marker}*.db")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(keep);
        foreach (var file in stale)
        {
            try { file.Delete(); log.Information($"PreMigrationBackup: pruned {file.Name}"); }
            catch (Exception ex) { log.Warning($"PreMigrationBackup: could not prune {file.Name}: {ex.Message}"); }
        }
    }
}
```
Check `IDatabaseRecoveryLogger` member names (`Information`, `Warning`, `Error(Exception, string)`) in `DiffusionNexus.DataAccess/Recovery/IDatabaseRecoveryLogger.cs` and adapt the calls if they differ.

In `DatabaseRecoveryService.InitializeAndRepair`, inside `if (pendingMigrations.Count > 0)` **before** `dbContext.Database.Migrate();`:
```csharp
                // Spec #521 S2: a consistent copy next to the DB before any schema change.
                var dbFile = TryGetDatabaseFilePath(dbContext);
                if (dbFile is not null)
                    PreMigrationBackup.TryCreate(dbFile, pendingMigrations[0], _log);
```
and the helper (private static) — the connection string is `Data Source=<path>[;...]`:
```csharp
    private static string? TryGetDatabaseFilePath(DiffusionNexusCoreDbContext dbContext)
    {
        try
        {
            var cs = dbContext.Database.GetConnectionString();
            if (string.IsNullOrWhiteSpace(cs)) return null;
            var path = new SqliteConnectionStringBuilder(cs).DataSource;
            return string.IsNullOrWhiteSpace(path) || path == ":memory:" ? null : path;
        }
        catch { return null; }
    }
```

- [ ] **Step 4: Run tests**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~PreMigrationBackupTests|FullyQualifiedName~DatabaseRecoveryServiceTests"`
Expected: pass. Also add one assertion to the existing `DatabaseRecoveryServiceTests` (file-based): after `InitializeAndRepair` on a DB created at an older migration, a `*.pre-*.db` file exists next to it; on a fresh (non-existent) DB, none does.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.DataAccess DiffusionNexus.Tests/DataAccess/Recovery
git commit -m "feat(db): automatic VACUUM INTO backup before pending migrations, keep newest 3 (#521 S2)"
```

---

### Task 4: Sync contracts (Domain)

**Files:**
- Create: `DiffusionNexus.Domain/Services/Sync/SyncScope.cs`, `SyncStepKind.cs`, `SyncOptions.cs`, `SyncRetryPolicy.cs`, `SyncPlan.cs`, `SyncReport.cs`, `LibrarySyncProgress.cs`, `ILibrarySyncService.cs`, `SyncCandidates.cs`
- Test: `DiffusionNexus.Tests/Sync/Domain/SyncRetryPolicyTests.cs`

**Interfaces (produced — later tasks use these names exactly):**
```csharp
namespace DiffusionNexus.Domain.Services.Sync;

public enum SyncScopeKind { Library, SourceFolder, Models }
public sealed record SyncScope(SyncScopeKind Kind, string? SourceFolder = null, IReadOnlyList<int>? ModelIds = null)
{
    public static SyncScope Library { get; } = new(SyncScopeKind.Library);
    public static SyncScope ForFolder(string folder) => new(SyncScopeKind.SourceFolder, SourceFolder: folder);
    public static SyncScope ForModels(params int[] ids) => new(SyncScopeKind.Models, ModelIds: ids);
}

public enum SyncStepKind { DiscoverFiles, IdentifyModel, FetchTags, FetchImages, Thumbnails }   // Thumbnails implemented in Plan B

public sealed record SyncRetryPolicy(TimeSpan NotIdentifiedRetryAfter, TimeSpan ErrorRetryAfter, int MaxErrorAttempts)
{
    public static SyncRetryPolicy Default { get; } = new(TimeSpan.FromDays(30), TimeSpan.FromDays(1), 3);
    /// <summary>Whether an identity attempt is due given the stored outcome.</summary>
    public bool IsIdentifyDue(SyncOutcome outcome, DateTimeOffset? checkedAt, int attempts, DateTimeOffset now, bool force);
    /// <summary>Whether a "fetch once" step (tags/images) is due.</summary>
    public bool IsFetchDue(DateTimeOffset? checkedAt, bool force);
}

public sealed record SyncOptions(
    IReadOnlySet<SyncStepKind> Steps,          // which steps to run
    bool ForceIdentify = false,                // re-run identity even when NotIdentified/Matched-by-fallback
    bool ForceTags = false,
    bool ForceImages = false,
    bool ForceThumbnails = false,
    SyncRetryPolicy? RetryPolicy = null)
{
    public static SyncOptions All { get; } = new(new HashSet<SyncStepKind>(Enum.GetValues<SyncStepKind>()));
    public SyncRetryPolicy Policy => RetryPolicy ?? SyncRetryPolicy.Default;
}

public sealed record SyncPlanStep(SyncStepKind Kind, int Count, TimeSpan EstimatedDuration, string Description);
public sealed record SyncPlan(SyncScope Scope, SyncOptions Options, IReadOnlyList<SyncPlanStep> Steps, DateTimeOffset PlannedAt)
{
    public bool HasWork => Steps.Any(s => s.Count > 0 || s.Kind == SyncStepKind.DiscoverFiles);
    public TimeSpan EstimatedDuration => TimeSpan.FromTicks(Steps.Sum(s => s.EstimatedDuration.Ticks));
}

public sealed record SyncFailure(SyncStepKind Step, int ModelId, string Name, string Reason);
public sealed record SyncStepReport(SyncStepKind Kind, int Planned, int Processed, int Succeeded, int Skipped, int Failed);
public sealed record SyncReport(SyncPlan Plan, IReadOnlyList<SyncStepReport> Steps, IReadOnlyList<SyncFailure> Failures, bool Cancelled, TimeSpan Elapsed, int NewFilesDiscovered)
{
    public string Summary { get; }   // "Discovered 2 · Identified 1/3 · Tags 68/68 · Images 0 · (cancelled)" — built in ctor; each step as "{Name} {Succeeded}/{Planned}", steps with Planned==0 omitted except Discover
}

public sealed record LibrarySyncProgress(SyncStepKind Step, int Index, int Total, string? CurrentItem);

public interface ILibrarySyncService
{
    Task<SyncPlan> PlanAsync(SyncScope scope, SyncOptions options, CancellationToken ct = default);
    Task<SyncReport> ExecuteAsync(SyncPlan plan, IProgress<LibrarySyncProgress>? progress = null, CancellationToken ct = default);
    /// <summary>True while an ExecuteAsync is running anywhere in the process (single-flight).</summary>
    bool IsRunning { get; }
}

// Candidate projections returned by ISyncStateRepository (Task 5). Kept in Domain so Service never sees EF.
public sealed record IdentifyCandidate(int ModelId, int VersionId, int FileId, string Name, string LocalPath, string? Sha256,
    string? BaseModelRaw, SyncOutcome Outcome, DateTimeOffset? CheckedAt, int Attempts, string? SidecarSignature);
public sealed record TagCandidate(int ModelId, int CivitaiModelId, string Name, DateTimeOffset? TagsCheckedAt);
public sealed record ImageCandidate(int ModelId, int VersionId, int CivitaiVersionId, string Name, DateTimeOffset? ImagesCheckedAt);
```

- [ ] **Step 1: Write the failing tests**

`DiffusionNexus.Tests/Sync/Domain/SyncRetryPolicyTests.cs`:
```csharp
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.Sync;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Domain;

public class SyncRetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly SyncRetryPolicy _p = SyncRetryPolicy.Default;

    [Fact] public void NeverCheckedIsDue() => _p.IsIdentifyDue(SyncOutcome.None, null, 0, Now, force: false).Should().BeTrue();
    [Fact] public void MatchedIsNeverDueWithoutForce() => _p.IsIdentifyDue(SyncOutcome.Matched, Now.AddYears(-5), 0, Now, false).Should().BeFalse();
    [Fact] public void MatchedIsDueWithForce() => _p.IsIdentifyDue(SyncOutcome.Matched, Now.AddDays(-1), 0, Now, true).Should().BeTrue();
    [Fact] public void SidecarAndHeaderAndHeuristicAreDueAfterLongWindow()
    {
        foreach (var o in new[] { SyncOutcome.Sidecar, SyncOutcome.Header, SyncOutcome.Heuristic, SyncOutcome.NotIdentified })
        {
            _p.IsIdentifyDue(o, Now.AddDays(-29), 0, Now, false).Should().BeFalse($"{o} within 30 days");
            _p.IsIdentifyDue(o, Now.AddDays(-31), 0, Now, false).Should().BeTrue($"{o} after 30 days");
        }
    }
    [Fact] public void ErrorIsDueAfterOneDayUntilAttemptsExhausted()
    {
        _p.IsIdentifyDue(SyncOutcome.Error, Now.AddHours(-23), 1, Now, false).Should().BeFalse();
        _p.IsIdentifyDue(SyncOutcome.Error, Now.AddHours(-25), 1, Now, false).Should().BeTrue();
        _p.IsIdentifyDue(SyncOutcome.Error, Now.AddDays(-10), 3, Now, false).Should().BeFalse("3 attempts exhausted");
        _p.IsIdentifyDue(SyncOutcome.Error, Now.AddDays(-10), 3, Now, true).Should().BeTrue("force resets");
    }
    [Fact] public void FetchOnceIsDueOnlyWhenNeverCheckedOrForced()
    {
        _p.IsFetchDue(null, false).Should().BeTrue();
        _p.IsFetchDue(Now.AddYears(-3), false).Should().BeFalse("checked-and-empty is final");
        _p.IsFetchDue(Now.AddYears(-3), true).Should().BeTrue();
    }
    [Fact] public void ScopeFactoriesCarryTheirArguments()
    {
        SyncScope.Library.Kind.Should().Be(SyncScopeKind.Library);
        SyncScope.ForFolder(@"E:\Loras").SourceFolder.Should().Be(@"E:\Loras");
        SyncScope.ForModels(1, 2).ModelIds.Should().Equal(1, 2);
    }
    [Fact] public void ReportSummaryListsNonEmptySteps()
    {
        var plan = new SyncPlan(SyncScope.Library, SyncOptions.All, new[]
        {
            new SyncPlanStep(SyncStepKind.DiscoverFiles, 0, TimeSpan.Zero, "Discover new files"),
            new SyncPlanStep(SyncStepKind.IdentifyModel, 3, TimeSpan.FromSeconds(6), "Identify"),
            new SyncPlanStep(SyncStepKind.FetchTags, 0, TimeSpan.Zero, "Tags"),
        }, Now);
        var report = new SyncReport(plan, new[]
        {
            new SyncStepReport(SyncStepKind.DiscoverFiles, 0, 0, 0, 0, 0),
            new SyncStepReport(SyncStepKind.IdentifyModel, 3, 3, 1, 1, 1),
            new SyncStepReport(SyncStepKind.FetchTags, 0, 0, 0, 0, 0),
        }, Array.Empty<SyncFailure>(), Cancelled: false, TimeSpan.FromSeconds(7), NewFilesDiscovered: 2);
        report.Summary.Should().Be("Discovered 2 · Identified 1/3");
        plan.HasWork.Should().BeTrue();
        plan.EstimatedDuration.Should().Be(TimeSpan.FromSeconds(6));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~SyncRetryPolicyTests"`
Expected: build error.

- [ ] **Step 3: Implement** the contracts exactly as listed under Interfaces. `SyncRetryPolicy` bodies:
```csharp
    public bool IsIdentifyDue(SyncOutcome outcome, DateTimeOffset? checkedAt, int attempts, DateTimeOffset now, bool force)
    {
        if (force) return true;
        if (checkedAt is null || outcome == SyncOutcome.None) return true;
        return outcome switch
        {
            SyncOutcome.Matched => false,
            SyncOutcome.Error => attempts < MaxErrorAttempts && now - checkedAt.Value >= ErrorRetryAfter,
            _ => now - checkedAt.Value >= NotIdentifiedRetryAfter,   // Sidecar, Header, Heuristic, NotIdentified: a better source may appear
        };
    }

    public bool IsFetchDue(DateTimeOffset? checkedAt, bool force) => force || checkedAt is null;
```
`SyncReport.Summary` (computed in the primary-constructor body or an init-only property set by a static factory — a `record` with an explicit constructor is fine):
```csharp
    public string Summary { get; } = BuildSummary(Steps, Cancelled, NewFilesDiscovered);

    private static string BuildSummary(IReadOnlyList<SyncStepReport> steps, bool cancelled, int discovered)
    {
        var parts = new List<string> { $"Discovered {discovered}" };
        foreach (var s in steps.Where(s => s.Kind != SyncStepKind.DiscoverFiles && s.Planned > 0))
            parts.Add($"{Label(s.Kind)} {s.Succeeded}/{s.Planned}");
        if (cancelled) parts.Add("(cancelled)");
        return string.Join(" · ", parts);
    }

    public static string Label(SyncStepKind kind) => kind switch
    {
        SyncStepKind.DiscoverFiles => "Discovered",
        SyncStepKind.IdentifyModel => "Identified",
        SyncStepKind.FetchTags => "Tags",
        SyncStepKind.FetchImages => "Images",
        SyncStepKind.Thumbnails => "Thumbnails",
        _ => kind.ToString(),
    };
```
(For a positional record, declare `Summary` as a get-only property initialized from the positional parameters — C# allows `public string Summary { get; } = BuildSummary(Steps, Cancelled, NewFilesDiscovered);` inside the record body.)

- [ ] **Step 4: Run tests** — expected 9 passed.

- [ ] **Step 5: Commit**
```bash
git add DiffusionNexus.Domain/Services/Sync DiffusionNexus.Tests/Sync/Domain
git commit -m "feat(sync): library sync contracts — scope, options, retry policy, plan/report (#521 WP2)"
```

---

### Task 5: `ISyncStateRepository` + selection queries

**Files:**
- Create: `DiffusionNexus.DataAccess/Repositories/Interfaces/ISyncStateRepository.cs`, `DiffusionNexus.DataAccess/Repositories/SyncStateRepository.cs`
- Modify: `DiffusionNexus.DataAccess/UnitOfWork/IUnitOfWork.cs` (add property), `UnitOfWork.cs` (lazy field)
- Test: `DiffusionNexus.Tests/Sync/DataAccess/SyncStateRepositoryTests.cs`

**Interfaces:**
- Consumes: Task 4 candidate records.
- Produces:
```csharp
public interface ISyncStateRepository : IRepository<ModelSyncState>
{
    Task<IReadOnlyList<int>> GetModelIdsWithoutStateAsync(CancellationToken ct = default);
    Task<ModelSyncState?> GetByModelIdAsync(int modelId, CancellationToken ct = default);           // tracked
    Task<ModelSyncState> GetOrCreateAsync(int modelId, CancellationToken ct = default);             // tracked; Add()s a new row with defaults when missing
    /// <summary>LoRA-family models with a valid local file and no Civitai id, within scope. No retry filtering — the caller applies SyncRetryPolicy.</summary>
    Task<IReadOnlyList<IdentifyCandidate>> SelectIdentifyCandidatesAsync(SyncScope scope, CancellationToken ct = default);
    /// <summary>Models with a Civitai id, not user-edited, zero tags, within scope.</summary>
    Task<IReadOnlyList<TagCandidate>> SelectTagCandidatesAsync(SyncScope scope, CancellationToken ct = default);
    /// <summary>Versions with a Civitai id and zero images whose model has a Civitai id, within scope.</summary>
    Task<IReadOnlyList<ImageCandidate>> SelectImageCandidatesAsync(SyncScope scope, CancellationToken ct = default);
}
```
`IUnitOfWork.SyncStates : ISyncStateRepository`.

Scope semantics (shared private helper `ApplyScope(IQueryable<Model>, SyncScope)`): `Library` → all; `Models` → `ModelIds.Contains(m.Id)`; `SourceFolder` → models having any file whose `LocalPath.ToLower().StartsWith(prefixLower)` where `prefix = folder.TrimEnd('\\','/') + Path.DirectorySeparatorChar` (boundary-aware: `E:\Loras\` does not match `E:\Loras_backup\...`). `// TODO: Linux Implementation for Task 5: case-sensitive paths + '/' separator.` "LoRA-family" = `Type` in `LORA, LoCon, DoRA, Unknown` (same set as `ModelFileSyncService.IsLoraFamily`). "Valid local file" = `LocalPath != null && IsLocalFileValid`.

- [ ] **Step 1: Write the failing tests** (`DiffusionNexus.Tests/Sync/DataAccess/SyncStateRepositoryTests.cs`, harness from File Structure; seed through `uow.Models.AddAsync` + `SaveChangesAsync`):

```csharp
// helper used by every test here
private static Model NewLocalModel(string name, string path, int? civitaiId = null, bool userEdited = false,
    ModelType type = ModelType.LORA, bool withTag = false, int? versionCivitaiId = null, bool withImage = false)
{
    var m = new Model { Name = name, Type = type, Source = DataSource.LocalFile, CivitaiId = civitaiId, IsUserEdited = userEdited };
    var v = new ModelVersion { Name = "v1", CivitaiId = versionCivitaiId, BaseModelRaw = "???" };
    v.Files.Add(new ModelFile { FileName = Path.GetFileName(path), LocalPath = path, IsLocalFileValid = true, IsPrimary = true, HashSHA256 = "AA" });
    if (withImage) v.Images.Add(new ModelImage { Url = "https://x/y.jpeg" });
    m.Versions.Add(v);
    if (withTag) m.Tags.Add(new ModelTag { Tag = new Tag { Name = "style", NormalizedName = "style" } });
    return m;
}

[Fact] public async Task GetModelIdsWithoutStateReturnsOnlyLegacyRows()
  // seed A (no state) and B (state row via GetOrCreateAsync + SaveChanges) → result == [A.Id]
[Fact] public async Task GetOrCreateAddsDefaultRowOnce()
  // GetOrCreate twice in the same uow returns the same tracked instance; after SaveChanges, GetByModelId returns Outcome None, Attempts 0
[Fact] public async Task IdentifyCandidatesExcludeCivitaiMatchedInvalidAndNonLora()
  // seed: local LoRA (in), civitaiId=5 (out), IsLocalFileValid=false (out), Type=Checkpoint (out), Type=Unknown (in) → names
[Fact] public async Task IdentifyCandidatesCarryStateFields()
  // seed model with state Outcome=NotIdentified, CheckedAt=t, Attempts=2, SidecarSignature="sig" → candidate fields equal
[Fact] public async Task SourceFolderScopeIsBoundaryAware()
  // paths E:\Loras\a.safetensors (in), E:\Loras_backup\b.safetensors (out), e:\loras\sub\c.safetensors (in, case-insensitive) with ForFolder(@"E:\Loras")
[Fact] public async Task ModelsScopeFiltersByIds()
[Fact] public async Task TagCandidatesRequireCivitaiIdAndNoTagsAndNotUserEdited()
  // civitaiId+noTags (in), civitaiId+tag (out), civitaiId+userEdited (out), no civitaiId (out); TagsCheckedAt comes from state when present
[Fact] public async Task ImageCandidatesRequireVersionCivitaiIdAndNoImages()
  // model civitaiId=1: version civitaiId=10 no images (in); version civitaiId=11 with image (out); version civitaiId=null (out)
```
Write each test fully (no pseudo-code in the file) following the comment lines.

- [ ] **Step 2: Run to verify it fails** — `--filter "FullyQualifiedName~SyncStateRepositoryTests"`: build error (`SyncStates` not on `IUnitOfWork`).

- [ ] **Step 3: Implement** `SyncStateRepository : RepositoryBase<ModelSyncState>, ISyncStateRepository` (internal sealed, ctor passes context to base). Query shapes:

```csharp
    public async Task<IReadOnlyList<int>> GetModelIdsWithoutStateAsync(CancellationToken ct = default)
        => await Context.Models.Where(m => m.SyncState == null).Select(m => m.Id).ToListAsync(ct).ConfigureAwait(false);

    public Task<ModelSyncState?> GetByModelIdAsync(int modelId, CancellationToken ct = default)
        => DbSet.FirstOrDefaultAsync(s => s.ModelId == modelId, ct);

    public async Task<ModelSyncState> GetOrCreateAsync(int modelId, CancellationToken ct = default)
    {
        var local = Context.ChangeTracker.Entries<ModelSyncState>().FirstOrDefault(e => e.Entity.ModelId == modelId)?.Entity;
        if (local is not null) return local;
        var existing = await DbSet.FirstOrDefaultAsync(s => s.ModelId == modelId, ct).ConfigureAwait(false);
        if (existing is not null) return existing;
        var created = new ModelSyncState { ModelId = modelId, UpdatedAt = DateTimeOffset.UtcNow };
        await DbSet.AddAsync(created, ct).ConfigureAwait(false);
        return created;
    }

    private static readonly ModelType[] LoraFamily = [ModelType.LORA, ModelType.LoCon, ModelType.DoRA, ModelType.Unknown];

    private static IQueryable<Model> ApplyScope(IQueryable<Model> q, SyncScope scope)
    {
        switch (scope.Kind)
        {
            case SyncScopeKind.Models:
                var ids = scope.ModelIds ?? Array.Empty<int>();
                return q.Where(m => ids.Contains(m.Id));
            case SyncScopeKind.SourceFolder:
                // TODO: Linux Implementation for Task 5: case-sensitive comparison and '/' separator.
                var prefix = (scope.SourceFolder ?? string.Empty).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                var prefixLower = prefix.ToLowerInvariant();
                return q.Where(m => m.Versions.Any(v => v.Files.Any(f => f.LocalPath != null && f.LocalPath.ToLower().StartsWith(prefixLower))));
            default:
                return q;
        }
    }

    public async Task<IReadOnlyList<IdentifyCandidate>> SelectIdentifyCandidatesAsync(SyncScope scope, CancellationToken ct = default)
    {
        var rows = await ApplyScope(Context.Models.AsNoTracking(), scope)
            .Where(m => m.CivitaiId == null && LoraFamily.Contains(m.Type))
            .SelectMany(m => m.Versions.SelectMany(v => v.Files
                .Where(f => f.LocalPath != null && f.IsLocalFileValid)
                .Select(f => new
                {
                    m.Id, VersionId = v.Id, FileId = f.Id, m.Name, f.LocalPath, f.HashSHA256, v.BaseModelRaw,
                    Outcome = m.SyncState != null ? m.SyncState.MetadataOutcome : SyncOutcome.None,
                    CheckedAt = m.SyncState != null ? m.SyncState.MetadataCheckedAt : null,
                    Attempts = m.SyncState != null ? m.SyncState.MetadataAttempts : 0,
                    Signature = m.SyncState != null ? m.SyncState.SidecarSignature : null,
                })))
            .ToListAsync(ct).ConfigureAwait(false);

        // One candidate per model: prefer the primary file, then the first. Done in memory (tiny).
        return rows.GroupBy(r => r.Id)
            .Select(g => g.First())
            .Select(r => new IdentifyCandidate(r.Id, r.VersionId, r.FileId, r.Name, r.LocalPath!, r.HashSHA256, r.BaseModelRaw, r.Outcome, r.CheckedAt, r.Attempts, r.Signature))
            .ToList();
    }
```
(Order the inner `Files` by `IsPrimary` descending before `Select` so `g.First()` is the primary file: `.OrderByDescending(f => f.IsPrimary)`.) `SelectTagCandidatesAsync`: `Where(m => m.CivitaiId != null && !m.IsUserEdited && !m.Tags.Any())` projecting `(m.Id, m.CivitaiId!.Value, m.Name, m.SyncState != null ? m.SyncState.TagsCheckedAt : null)`. `SelectImageCandidatesAsync`: from `ApplyScope(...).Where(m => m.CivitaiId != null).SelectMany(m => m.Versions.Where(v => v.CivitaiId != null && !v.Images.Any()).Select(v => new {...}))`. If EF cannot translate a `SyncState != null ? … : …` projection, switch to a left-join style: `from m in q join s in Context.ModelSyncStates on m.Id equals s.ModelId into ss from s in ss.DefaultIfEmpty() select new { …, Outcome = s == null ? SyncOutcome.None : s.MetadataOutcome, … }`.

`IUnitOfWork`: `ISyncStateRepository SyncStates { get; }`; `UnitOfWork`: `private ISyncStateRepository? _syncStates; public ISyncStateRepository SyncStates => _syncStates ??= new SyncStateRepository(_context);`. Update every `Mock<IUnitOfWork>`/fake in `DiffusionNexus.Tests` that implements the interface explicitly (grep `: IUnitOfWork`).

- [ ] **Step 4: Run tests** — repository tests + `--filter "FullyQualifiedName~DiffusionNexus.Tests.DataAccess"` (UoW tests) pass.

- [ ] **Step 5: Commit**
```bash
git add DiffusionNexus.DataAccess DiffusionNexus.Tests
git commit -m "feat(sync): ISyncStateRepository with scope-aware candidate queries (#521 WP2)"
```

---

### Task 6: `SyncStateDeriver` + `SyncStateInitializer` (spec S3)

**Files:**
- Create: `DiffusionNexus.Service/Services/Sync/SyncStateDeriver.cs`, `DiffusionNexus.Service/Services/Sync/SyncStateInitializer.cs`
- Test: `DiffusionNexus.Tests/Sync/Service/SyncStateDeriverTests.cs`, `SyncStateInitializerTests.cs`

**Interfaces:**
- Produces: `public static class SyncStateDeriver { public static ModelSyncState Derive(Model model, DateTimeOffset now); }` and `public sealed class SyncStateInitializer(IServiceScopeFactory scopes, IUnifiedLogger? logger = null) { public Task<int> EnsureInitializedAsync(CancellationToken ct = default); }` returning the number of rows created.

Derivation rules (table — the tests are this table):

| Model facts | MetadataOutcome | MetadataCheckedAt | TagsCheckedAt | ImagesCheckedAt |
|---|---|---|---|---|
| `CivitaiId != null` | `Matched` | `LastSyncedAt ?? now` | `LastSyncedAt ?? now` if `Tags.Count > 0` else `null` | `LastSyncedAt ?? now` if any version has images else `null` |
| `CivitaiId == null`, `LastSyncedAt != null`, `Source == LocalFile`, base model not placeholder | `Sidecar` | `LastSyncedAt` | `null` | `null` |
| `CivitaiId == null`, `LastSyncedAt != null`, otherwise | `NotIdentified` | `LastSyncedAt` | `null` | `null` |
| `CivitaiId == null`, `LastSyncedAt == null` | `None` | `null` | `null` | `null` |

`MetadataAttempts = 0`, `LastError = null`, `SidecarSignature = null`, `HeaderCheckedAt = null`, `UpdatedAt = now`. Placeholder base model = `string.IsNullOrWhiteSpace(raw) || raw == "???"` on every version (use the first version with a file; no versions ⇒ placeholder). Tags for a matched-but-tagless model stay `null` on purpose: that is the "68 asked one final time, then stamped" path.

- [ ] **Step 1: Write the failing tests** — `SyncStateDeriverTests` as `[Theory]` rows covering each table line plus: `LastSyncedAt` null with `CivitaiId` set ⇒ timestamps = `now`; images on a *second* version count; `IsUserEdited` does not change the derivation. `SyncStateInitializerTests` (DB harness + `ServiceCollection` that also registers the initializer): seed 3 legacy models + 1 with state ⇒ returns 3, all 4 have rows, second call returns 0 and changes nothing (`UpdatedAt` unchanged).

- [ ] **Step 2: Run to verify fail** — `--filter "FullyQualifiedName~DiffusionNexus.Tests.Sync.Service"`.

- [ ] **Step 3: Implement**

```csharp
public static class SyncStateDeriver
{
    public static ModelSyncState Derive(Model model, DateTimeOffset now)
    {
        var stamp = model.LastSyncedAt ?? now;
        var state = new ModelSyncState { ModelId = model.Id, UpdatedAt = now };

        if (model.CivitaiId is not null)
        {
            state.MetadataOutcome = SyncOutcome.Matched;
            state.MetadataCheckedAt = stamp;
            state.TagsCheckedAt = model.Tags.Count > 0 ? stamp : null;
            state.ImagesCheckedAt = model.Versions.Any(v => v.Images.Count > 0) ? stamp : null;
            return state;
        }

        if (model.LastSyncedAt is null) return state; // None

        state.MetadataCheckedAt = model.LastSyncedAt;
        var hasRealBaseModel = model.Versions.Any(v => !IsPlaceholder(v.BaseModelRaw));
        state.MetadataOutcome = model.Source == DataSource.LocalFile && hasRealBaseModel ? SyncOutcome.Sidecar : SyncOutcome.NotIdentified;
        return state;
    }

    public static bool IsPlaceholder(string? baseModelRaw) => string.IsNullOrWhiteSpace(baseModelRaw) || baseModelRaw == "???";
}
```
`SyncStateInitializer.EnsureInitializedAsync`: fresh scope → `uow.SyncStates.GetModelIdsWithoutStateAsync` → if empty return 0 → log `Info(FileSystem, "LibrarySync", $"Initializing sync state for {n} legacy models (derived from existing data, no network)")` → for each batch of 200 ids: fresh scope, `uow.Models.GetByIdWithIncludesAsync(id)` per id (it loads Versions/Images/Tags), `uow.SyncStates.AddAsync(SyncStateDeriver.Derive(model, now))`, `SaveChangesAsync` per batch; `ct` checked per batch; return total created. Note `GetByIdWithIncludesAsync` per id is N queries — acceptable at 2.5k once; if it exceeds ~2 s in the test, add `IModelRepository.GetByIdsWithSyncFactsAsync(ids)` projecting only `(Id, CivitaiId, LastSyncedAt, Source, TagCount, HasImages, BaseModelRaws)` and derive from that projection instead — keep `Derive(Model, now)` as the tested core either way.

- [ ] **Step 4: Run tests** — pass.

- [ ] **Step 5: Commit**
```bash
git add DiffusionNexus.Service/Services/Sync DiffusionNexus.Tests/Sync/Service
git commit -m "feat(sync): derive legacy sync state from existing data, no network (#521 S3)"
```

---

### Task 7: Move `CivitaiMetadataApplier` out of the ViewModel

**Files:**
- Create: `DiffusionNexus.Service/Services/Sync/CivitaiMetadataApplier.cs`
- Modify: `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs:2078-2308` (`UpdateModelFromCivitaiAsync`, `SyncTagsFromCivitai`) — mark `[Obsolete]`, delegate
- Test: `DiffusionNexus.Tests/Sync/Service/CivitaiMetadataApplierTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed class CivitaiMetadataApplier(ICivitaiClient client, IUnifiedLogger? logger = null)
{
    /// <summary>Fetches the full model (for tags/images/versions) and writes the Civitai response into the DB graph of <paramref name="modelId"/>.
    /// Honors IsUserEdited on model and version. Saves. Returns false when the model row no longer exists.</summary>
    public Task<bool> ApplyAsync(IUnitOfWork uow, int modelId, int fileId, CivitaiModelVersion version, string? apiKey, CancellationToken ct = default);
    /// <summary>Tags only (FetchTags step): GetModelAsync → replace tags unless IsUserEdited; saves. Returns the number of tags written (0 is a valid, final answer).</summary>
    public Task<int> ApplyTagsAsync(IUnitOfWork uow, int modelId, int civitaiModelId, string? apiKey, CancellationToken ct = default);
    /// <summary>Images only (FetchImages step): GetModelVersionAsync → append images not yet present by CivitaiId; saves. Returns images added.</summary>
    public Task<int> ApplyImagesAsync(IUnitOfWork uow, int modelId, int versionId, int civitaiVersionId, string? apiKey, CancellationToken ct = default);
    public static void SyncTags(Model dbModel, IReadOnlyList<string> civitaiTags, Dictionary<string, Tag> knownTags);   // moved verbatim
}
```

- [ ] **Step 1: Write the failing tests** (DB harness; `Mock<ICivitaiClient>` returning hand-built `CivitaiModel`/`CivitaiModelVersion` records):
  - `ApplyAsync_WritesCivitaiIdsBaseModelTriggerWordsImagesHashesAndTags` — seed local model (one version, one primary file, no hashes); client `GetModelAsync(77)` returns model with one version `Id=700, BaseModel="SDXL 1.0", TrainedWords=["a","b"], Images=[one], Files=[Primary=true, Hashes.SHA256="ABC"]`, `Tags=["style","anime"]`; assert `CivitaiId=77`, `CivitaiModelPageId=77`, `Source=CivitaiApi`, `LastSyncedAt` set, version `CivitaiId=700`, `BaseModelRaw="SDXL 1.0"`, 2 trigger words, 1 image, file `HashSHA256="ABC"`, 2 tags.
  - `ApplyAsync_PreservesUserEditedNameDescriptionTagsAndTriggerWords` — model `IsUserEdited=true` + version `IsUserEdited=true` with existing name/tag/trigger word ⇒ unchanged, but `CivitaiId`/`BaseModelRaw`/images still applied.
  - `ApplyAsync_SkipsCivitaiIdAlreadyOwnedByAnotherModel` — second model already has `CivitaiId=77` ⇒ first keeps `CivitaiId=null`, `CivitaiModelPageId=77` set, no exception.
  - `ApplyTagsAsync_ReturnsZeroForTaglessModelWithoutThrowing`.
  - `ApplyImagesAsync_AppendsOnlyNewImagesByCivitaiId` — existing image CivitaiId=5; response has 5 and 6 ⇒ one added, SortOrder continues.
  - `ApplyAsync_ReturnsFalseWhenModelMissing`.

- [ ] **Step 2: Run to verify fail.**

- [ ] **Step 3: Implement** by **moving** `LoraViewerViewModel.cs:2078-2308` into the new class with these edits only: `tile.ModelEntity.Id` → `modelId`; the version lookup `v.Files.Any(f => f.Id == tile.SelectedVersion?.PrimaryFile?.Id)` → `v.Files.Any(f => f.Id == fileId)`; the `using var scope …/unitOfWork` lines removed (the `uow` parameter is used); `_civitaiClient!` → `client`; `_logger?.Warn(LogCategory.Network, "CivitaiSync", …)` → `logger?.Warn(LogCategory.Network, "LibrarySync", …)`; delete the trailing `Dispatcher.UIThread.InvokeAsync(() => tile.RefreshModelData(...))` block and the reload before it; return `true`. `ApplyTagsAsync` = `GetModelAsync` + `GetByIdWithIncludesAsync` + (`IsUserEdited` ? skip : `SyncTags`) + stamp nothing (the step stamps state) + `SaveChangesAsync` + return `dbModel.Tags.Count`. `ApplyImagesAsync` = `GetModelVersionAsync(civitaiVersionId)` + the image-append block (lines 2208-2240) against `dbModel.Versions.First(v => v.Id == versionId)` + save + return added count. In the ViewModel, replace the body of `UpdateModelFromCivitaiAsync` with a call to the applier (resolve `CivitaiMetadataApplier` via a fresh scope) followed by the existing tile refresh, and mark it `[Obsolete("Replaced by LibrarySyncService (#521); removed in Task 12")]`. Keep `SyncTagsFromCivitai` as a one-line forwarder to `CivitaiMetadataApplier.SyncTags`, also `[Obsolete]`.

- [ ] **Step 4: Run** applier tests + `--filter "FullyQualifiedName~DiffusionNexus.Tests.Viewer"` (LoraViewer tests still green) + Debug build of UI.

- [ ] **Step 5: Commit**
```bash
git add DiffusionNexus.Service/Services/Sync/CivitaiMetadataApplier.cs DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs DiffusionNexus.Tests/Sync/Service/CivitaiMetadataApplierTests.cs
git commit -m "refactor(sync): move Civitai response → DB applier into Service (#521 WP2)"
```

---

### Task 8: Move `SidecarMetadataApplier` (incl. local preview thumbnail) out of the ViewModel

**Files:**
- Create: `DiffusionNexus.Service/Services/Sync/SidecarMetadataApplier.cs`
- Modify: `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs:1394-2008` (`TryApplyLocalMetadataFallbackAsync`, `ApplyCivitaiInfoFormatAsync`, `ApplyModelLevelJsonFormatAsync`, `ApplySimpleJsonFormat`, `ApplyImagesFromJson`, `ApplyFileHashesFromJson`, `LocalPreviewExtensions`, `TryApplyLocalThumbnailAsync`)
- Test: `DiffusionNexus.Tests/Sync/Service/SidecarMetadataApplierTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed record SidecarLookup(string? SidecarPath, string Signature)   // Signature = $"{fullPath}|{lastWriteUtc.Ticks}|{length}", or "" when no sidecar
public sealed record SidecarApplyResult(bool Applied, string? SidecarPath, string Signature, bool ThumbnailApplied);
public sealed class SidecarMetadataApplier(IUnifiedLogger? logger = null)
{
    public static SidecarLookup Find(string modelFilePath);          // prefers {base}.civitai.info over {base}.json; base = file name without extension
    /// <summary>Parses the sidecar (if any) into the DB graph of modelId/versionId — same rules as the ViewModel code it replaces —
    /// then applies a local preview image next to the file as the version's thumbnail BLOB when no thumbnail exists. Saves. Never throws.</summary>
    public Task<SidecarApplyResult> ApplyAsync(IUnitOfWork uow, int modelId, string modelFilePath, CancellationToken ct = default);
}
```

- [ ] **Step 1: Write the failing tests** (DB harness + `Directory.CreateTempSubdirectory`):
  - `Find_PrefersCivitaiInfoOverJson_AndSignatureChangesWhenFileChanges`.
  - `ApplyAsync_CivitaiInfoSetsBaseModelIdsTriggerWordsAndMarksLocalFileSource` — write a minimal `.civitai.info` (`{"id":700,"modelId":77,"baseModel":"Pony","trainedWords":["x"],"model":{"name":"N","nsfw":false},"files":[{"primary":true,"hashes":{"SHA256":"ABC"}}],"images":[{"url":"https://x/y.jpeg","nsfw":false}]}`) ⇒ `BaseModelRaw="Pony"`, version `CivitaiId=700`, model `CivitaiModelPageId=77`, `Source=LocalFile`, `LastSyncedAt` set, hash `ABC`, 1 image, `Applied=true`, `Signature` non-empty.
  - `ApplyAsync_SimpleJsonSdVersionFallback` — `{"sd version":"SD1"}` ⇒ `BaseModelRaw="SD1"`.
  - `ApplyAsync_NoSidecarButLocalPreviewAppliesThumbnail` — write a 64×64 PNG (`SkiaSharp` encode in the test) as `{base}.preview.png` ⇒ `Applied=false`, `ThumbnailApplied=true`, version has one image with `Url` starting `file://` and non-empty `ThumbnailData`.
  - `ApplyAsync_NothingThereReturnsNotApplied`.

- [ ] **Step 2: Run to verify fail.**

- [ ] **Step 3: Implement** by moving lines 1394–2008 with these edits only: `tile.ModelEntity.Id` → `modelId`; `tile.SelectedVersion?.PrimaryFile?.LocalPath` → `modelFilePath`; `tile.DisplayName` → `Path.GetFileName(modelFilePath)`; remove the DI-scope lines (use the `uow` parameter); remove both `Dispatcher.UIThread.InvokeAsync` blocks (tile refresh and `tile.ThumbnailImage = new Bitmap(...)`); `_logger` → `logger` with source `"LibrarySync"`; return the record. The `ApplyCivitaiInfoFormatAsync`/`ApplyModelLevelJsonFormatAsync` helpers take `unitOfWork.Models` today — keep that parameter. Local thumbnail: only when the version has no image with `ThumbnailData` (never overwrite — spec S4). ViewModel: `TryApplyLocalMetadataFallbackAsync` body → applier call + existing tile refresh; mark all eight moved members `[Obsolete("Replaced by SidecarMetadataApplier (#521); removed in Task 12")]`.

- [ ] **Step 4: Run** new tests + Viewer tests + Debug build.

- [ ] **Step 5: Commit**
```bash
git add DiffusionNexus.Service/Services/Sync/SidecarMetadataApplier.cs DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs DiffusionNexus.Tests/Sync/Service/SidecarMetadataApplierTests.cs
git commit -m "refactor(sync): move sidecar/local-preview applier into Service (#521 WP2)"
```

---

### Task 9: `ISyncStep`, `DiscoverFilesStep`, `IdentifyModelStep`

**Files:**
- Create: `DiffusionNexus.Service/Services/Sync/Steps/ISyncStep.cs`, `DiscoverFilesStep.cs`, `IdentifyModelStep.cs`, `DiffusionNexus.Service/Services/Sync/FileHasher.cs`
- Test: `DiffusionNexus.Tests/Sync/Service/Steps/IdentifyModelStepTests.cs`, `FileHasherTests.cs`

**Interfaces:**
```csharp
public sealed record SyncItem(int ModelId, string Name, object Payload);            // Payload = the candidate record
public sealed record SyncItemResult(bool Succeeded, bool Skipped, string? FailureReason);
public interface ISyncStep
{
    SyncStepKind Kind { get; }
    string Description { get; }
    TimeSpan EstimatedPerItem { get; }                                                 // Identify 3 s (hash+lookup+pacing), Tags/Images 1.6 s
    Task<IReadOnlyList<SyncItem>> SelectAsync(SyncScope scope, SyncOptions options, DateTimeOffset now, CancellationToken ct);   // DB + policy; no network
    Task<SyncItemResult> ExecuteOneAsync(SyncItem item, string? apiKey, CancellationToken ct);                                    // records outcome itself
}
public static class FileHasher { public static string Sha256Upper(string path); public static Task<string> Sha256UpperAsync(string path, CancellationToken ct); }  // uppercase hex (D2)
```
- `DiscoverFilesStep`: `SelectAsync` returns a single pseudo-item `SyncItem(0, "Discover new files", scope)` (count is unknown until run); `ExecuteOneAsync` calls `IModelSyncService.DiscoverNewFilesAsync` in a fresh scope and stores `DiscoveredCount` on the step instance (read by the service for the report). `EstimatedPerItem = 2 s`.
- `IdentifyModelStep(IServiceScopeFactory scopes, ICivitaiClient client, CivitaiMetadataApplier civitai, SidecarMetadataApplier sidecar, IUnifiedLogger? logger = null)`:
  - `SelectAsync`: `uow.SyncStates.SelectIdentifyCandidatesAsync(scope)` filtered by `options.Policy.IsIdentifyDue(c.Outcome, c.CheckedAt, c.Attempts, now, options.ForceIdentify)` **and** (`File.Exists(c.LocalPath)`); additionally a candidate whose `Outcome` is `Sidecar`/`NotIdentified` is due immediately (ignoring the window) when `SidecarMetadataApplier.Find(c.LocalPath).Signature != c.SidecarSignature` (a new/changed sidecar appeared).
  - `ExecuteOneAsync`, per item in a fresh scope/uow: `hash = c.Sha256 is 64-hex ? upper : FileHasher.Sha256UpperAsync(path)` (persist the hash onto the `ModelFile` if it was missing) → `client.GetModelVersionByHashAsync(hash, apiKey)` → if found: `civitai.ApplyAsync(uow, modelId, fileId, version, apiKey)`, state `Matched`, attempts 0 → else `sidecar.ApplyAsync(uow, modelId, path)`: `Applied` ⇒ `Sidecar` (signature stored), else `NotIdentified` (signature stored, possibly ""); on `HttpRequestException`/`IOException`/`TaskCanceledException` (when `!ct.IsCancellationRequested`): state `Error`, `Attempts++`, `LastError = ex.Message` (truncate 500), result `Failed`. Always `MetadataCheckedAt = now`, `UpdatedAt = now`, `SaveChangesAsync`. On `OperationCanceledException` with `ct` cancelled: rethrow (no stamp). Log per spec.

- [ ] **Step 1: Write the failing tests** (DB harness, temp files, `Mock<ICivitaiClient>`):
  - `FileHasher_ProducesUppercaseSha256` (known vector: empty file ⇒ `E3B0C442…B855`).
  - `Select_AppliesRetryPolicyAndSkipsMissingFiles` — three candidates: never checked (in), `Matched` (out), `NotIdentified` 31 days ago (in), `NotIdentified` yesterday (out), never checked but file missing (out).
  - `Select_ChangedSidecarMakesCandidateDueImmediately`.
  - `Execute_MatchedStampsMatchedAndAppliesMetadata` — client returns a version; assert state `Matched`, `MetadataCheckedAt` set, model `CivitaiId` set, file hash persisted uppercase.
  - `Execute_404WithSidecarStampsSidecar` / `Execute_404WithoutSidecarStampsNotIdentified` (signature stored).
  - `Execute_HttpErrorStampsErrorAndIncrementsAttempts` — client throws `HttpRequestException`; attempts 0→1, `LastError` set, result failed; second run after policy window allowed (unit-check `IsIdentifyDue`).
  - `Execute_CancellationDoesNotStamp`.

- [ ] **Step 2: Run to verify fail.** **Step 3: Implement** as specified. **Step 4: Run tests — pass.**

- [ ] **Step 5: Commit**
```bash
git add DiffusionNexus.Service/Services/Sync DiffusionNexus.Tests/Sync/Service
git commit -m "feat(sync): ISyncStep + DiscoverFiles + IdentifyModel steps with outcome stamping (#521 WP2)"
```

---

### Task 10: `FetchTagsStep` and `FetchImagesStep`

**Files:**
- Create: `DiffusionNexus.Service/Services/Sync/Steps/FetchTagsStep.cs`, `FetchImagesStep.cs`
- Test: `DiffusionNexus.Tests/Sync/Service/Steps/FetchTagsStepTests.cs`, `FetchImagesStepTests.cs`

**Interfaces:** both `ISyncStep`, ctor `(IServiceScopeFactory scopes, CivitaiMetadataApplier applier, IUnifiedLogger? logger = null)`, `EstimatedPerItem = 1.6 s`.
- `FetchTagsStep.SelectAsync`: `SelectTagCandidatesAsync(scope)` where `Policy.IsFetchDue(c.TagsCheckedAt, options.ForceTags)`. `ExecuteOneAsync`: `applier.ApplyTagsAsync(...)`; **always** stamp `TagsCheckedAt = now` on success — including when 0 tags came back (that is the whole point); on exception: result failed, **no stamp** (transient; the item comes back next run — bounded by the user, not by attempts, because a tag fetch is one cheap call) and `Warn` one line.
- `FetchImagesStep`: same shape over `SelectImageCandidatesAsync` / `ApplyImagesAsync` / `ImagesCheckedAt`. Several versions of one model may each be an item; stamping is per model, so stamp after the model's last item (group items by model in `SelectAsync` — one `SyncItem` per model with the version list as payload).

- [ ] **Step 1: Tests:** `Select_ReturnsOnlyNeverChecked_UnlessForced`; `Execute_ZeroTagsStillStampsChecked` (the 68-models bug, as a test); `Execute_ErrorDoesNotStamp`; images: `Execute_StampsAfterAllVersionsOfModel`.
- [ ] **Step 2–4:** fail → implement → pass.
- [ ] **Step 5: Commit** `feat(sync): FetchTags/FetchImages steps stamp checked-and-empty (#521 WP2)`.

---

### Task 11: `LibrarySyncService` (plan + execute)

**Files:**
- Create: `DiffusionNexus.Service/Services/Sync/LibrarySyncService.cs`, `SyncServiceCollectionExtensions.cs`
- Test: `DiffusionNexus.Tests/Sync/Service/LibrarySyncServiceTests.cs`

**Interfaces:**
- Consumes: `ILibrarySyncService` (Task 4), `ISyncStep` (Task 9), `SyncStateInitializer` (Task 6), `IAppSettingsService.GetCivitaiApiKeyAsync`.
- Produces: `public sealed class LibrarySyncService(IEnumerable<ISyncStep> steps, SyncStateInitializer initializer, IServiceScopeFactory scopes, IUnifiedLogger? logger = null) : ILibrarySyncService`; `public static IServiceCollection AddLibrarySync(this IServiceCollection s)` registering `CivitaiMetadataApplier`, `SidecarMetadataApplier` (transient), the four steps as `ISyncStep` (transient, in order Discover → Identify → Tags → Images), `SyncStateInitializer` (transient), `ILibrarySyncService` as **singleton** (owns the single-flight gate; it creates scopes itself and holds no DbContext).

Behavior:
- `PlanAsync`: `await initializer.EnsureInitializedAsync(ct)` (S3) → for each registered step whose `Kind ∈ options.Steps`, in registration order: `items = await step.SelectAsync(scope, options, now, ct)`; `SyncPlanStep(kind, items.Count, items.Count * step.EstimatedPerItem, step.Description)`; Discover always count 0 but included. Log `Info(Network, "LibrarySync", "Plan: Identify 3 · Tags 68 · Images 0 (~2 min)")`. No network.
- `ExecuteAsync`: `if (!_gate.Wait(0)) throw new InvalidOperationException("A library sync is already running.")`; `IsRunning = true`; `apiKey = await settings.GetCivitaiApiKeyAsync()` (fresh scope); for each plan step: re-`SelectAsync` (the plan may be minutes old; never act on stale ids), then for each item: `ct.ThrowIfCancellationRequested()`, `progress?.Report(new(kind, i+1, total, item.Name))`, `await step.ExecuteOneAsync(item, apiKey, ct)`, tally; API-bound steps pace with `await Task.Delay(1500, ct)` **only between items that actually made a network call** (`SyncItemResult.Skipped == false`). Catch `OperationCanceledException` → `Cancelled = true`, stop; any other exception from a step is a bug → `Error` log + rethrow. `finally`: `IsRunning = false; _gate.Release()`. Build `SyncReport` with `Failures` (one per failed item: step, modelId, name, reason) and `NewFilesDiscovered` from the Discover step. Log `Info` per step start/end with counts and `Info` final `report.Summary` + elapsed.

- [ ] **Step 1: Tests** with fake `ISyncStep`s (in-memory, scripted results) and a fake initializer over the DB harness:
  - `Plan_IncludesOnlyRequestedSteps_AndCounts`.
  - `Plan_RunsInitializerFirst` (legacy rows get state before selection).
  - `Execute_ReportsPerStepCountsAndFailures`.
  - `Execute_CancellationMidStepReturnsPartialReportFlaggedCancelled`.
  - `Execute_IsSingleFlight` (second concurrent call throws `InvalidOperationException`, `IsRunning` true during, false after).
  - `Execute_ReSelectsItemsAtRunTime` (step's Select returns 2 at plan time, 1 at run time ⇒ processed 1).
  - `Execute_PacesOnlyAfterNetworkItems` (fake step marks items Skipped ⇒ elapsed < 200 ms for 10 items).
- [ ] **Step 2–4:** fail → implement → pass.
- [ ] **Step 5: Commit** `feat(sync): LibrarySyncService plan/execute with single-flight + report (#521 WP2)`.

---

### Task 12: Wire the ViewModel, delete the old phases, docs

**Files:**
- Modify: `DiffusionNexus.UI/App.axaml.cs:583-627` (`services.AddLibrarySync();` after `AddTransient<IModelSyncService, …>`)
- Modify: `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs` — `DownloadMissingMetadataAsync` (637-760), `DownloadMetadataForTileAsync` (2506-~2600), delete `SyncMetadataPhaseAsync`, `ReprocessLocalFileModelsPhaseAsync`, `RefetchMissingImagesPhaseAsync`, `BackfillMissingTagsPhaseAsync`, `DownloadMissingThumbnailsPhaseAsync`, `MarkModelSyncedAsync`, `IsPlaceholderBaseModel` (if unused), `ComputeFullSha256` (→ `FileHasher`; keep the sorter's usage compiling by pointing `LoraSorterViewModel`'s hash delegate at `FileHasher.Sha256Upper` — note: sorter tests compare lowercase? check `NormalizeHash` there, it upper/lower-normalizes), all `[Obsolete]` members from Tasks 7–8
- Modify: `DiffusionNexus.UI/Doc/LoraViewer.md` (section on "Download Metadata")
- Test: `DiffusionNexus.Tests/Viewer/LoraViewerViewModelSyncTests.cs` (new) — VM calls `ILibrarySyncService.PlanAsync` then `ExecuteAsync` with `SyncScope.Library`; per-tile button uses `SyncScope.ForModels(id)`; status text = `report.Summary`; tiles rebuilt once after the run (count `LoadCachedFilesAsync` calls on a `Mock<IModelSyncService>`); `"Library is up to date"` status when `!plan.HasWork`

Implementation notes:
- `DownloadMissingMetadataAsync` becomes: busy on → `plan = await sync.PlanAsync(SyncScope.Library, SyncOptions.All with Thumbnails excluded until Plan B, ct)` → if `!plan.HasWork`: `SyncStatus = "Library is up to date — nothing to do"`; return → (Plan B adds the dialog here; for now log the plan and start) → `report = await sync.ExecuteAsync(plan, progress, ct)` where `progress` marshals to `SyncStatus = $"{SyncReport.Label(p.Step)} [{p.Index}/{p.Total}] {p.CurrentItem}"` via `Dispatcher.UIThread.Post` → `await RebuildTilesFromDatabaseAsync()` → `SyncStatus = report.Summary` (+ `" · {n} failed"` when failures exist). Inject `ILibrarySyncService?` through the constructor like `ILoraUpdateChecker?` (optional, null in design-time ctor) and resolve via `App.Services` only as fallback, matching the existing style.
- `DownloadMetadataForTileAsync(tile)`: `PlanAsync(SyncScope.ForModels(tile.ModelEntity.Id), new SyncOptions(steps: Identify+Tags+Images, ForceIdentify: true))` → execute → reload the model with `GetByIdWithIncludesAsync` and `tile.RefreshModelData(...)` (keep the existing detail-VM status plumbing). Return `report.Steps.Any(s => s.Succeeded > 0)`.
- Phase 4 (thumbnails) is **removed** in this task and returns as the `Thumbnails` step in Plan B; on-screen tiles still lazy-load their thumbnails through `ModelTileViewModel.Activate()`, so nothing visible regresses in the interim. State that in the task report.
- Delete obsolete members only after the new tests pass and `grep -n "Obsolete(\"Replaced by" DiffusionNexus.UI` returns nothing else referencing them.
- Docs: replace the phase description in `Doc/LoraViewer.md` with: what the plan shows, what each step does, where state lives (`ModelSyncStates`), retry windows, how to force, and the "checked-and-empty is final" rule.

- [ ] **Step 1: Tests** (as listed) → **Step 2: fail** → **Step 3: implement + delete** → **Step 4:** full suite `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release` green except the known flake; `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -c Release` (or Debug if locked) zero warnings introduced (no `CS0618` from the deleted obsoletes).
- [ ] **Step 5: Commit** `feat(sync): LoRA Viewer uses LibrarySyncService; tile-driven phases removed (#521 WP2)`.

---

### Task 13: Acceptance run on the reference library (WP7 slice for Plan A)

Manual, performed by the controller with the user's app (not a subagent):

- [ ] Close the app. Build Release. Start it. Log (`%LocalAppData%\DiffusionNexus\Logs\log-<today>.txt`) shows `PreMigrationBackup: wrote …pre-AddModelSyncStateAndThumbnailAttempts-….db` once and the migration applied; the backup file exists next to the DB.
- [ ] Press **Download Metadata**. Log shows `Initializing sync state for N legacy models` exactly once, then `Plan: Identify ≈3 · Tags ≈68 · Images 0`. Run completes in ≈ (3×3 s + 68×1.6 s) ≈ 2 min, **no** `Phase 4`, **no** `The 'file' scheme is not supported`, **no** video downloads.
- [ ] Press it again: status `Library is up to date — nothing to do` in < 5 s; log shows `Plan: Identify 0 · Tags 0 · Images 0`.
- [ ] Restart the app, press again: same as previous line (the original complaint).
- [ ] Detail view → Download Metadata on one tile: runs with `ForModels`, tile refreshes.
- [ ] Record the three run times + counts in the PR description draft (`scratchpad/pr521-body.md`).

---

## Self-Review

**Spec coverage (Plan A scope = WP1 + WP2):** §3 S1 (Task 2 additive test), S2 (Task 3), S3 (Task 6 + plan calls initializer Task 11), S4 (Task 8 never overwrites thumbnail; Task 7 appends images by id), S5 (Task 7 `IsUserEdited` tests), S6 (downgrade: `CleanStaleMigrationHistory` already exists; Task 3 adds no destructive path — documented rather than tested, flagged in Task 13 report), S7 (nothing added to startup — Task 12 only changes button handlers), S8 (Task 12 per-tile uses the same service). §4.1 table → Task 1/2 (all columns present, retry policy D1 in Task 4). §4.2 → Tasks 4, 9–12 (single-flight, re-select at run time, pacing, logging, fresh scopes, cancellation). §4.3/§4.4/§4.5/§4.6 → Plans B/C/D (seams: `SyncStepKind.Thumbnails`, `ISyncStep` registration order, `IdentifyModelStep` fallback sequence, `SyncPlan` consumed by the future dialog). D2 → Task 2 SQL + `FileHasher` uppercase.

**Placeholder scan:** Tasks 5, 6, 9–12 describe tests by name + behavior rather than full code listings; each names the exact assertion and seed, so the implementer has no open design decision — acceptable for a plan this size, but the implementer must write every listed test, not a subset.

**Type consistency:** `SyncScope.ForModels(params int[])` used in Tasks 4/5/12; `SyncOptions.All` / `Policy` in 4/9/11; `SyncReport.Label` in 4/12; `ISyncStateRepository` method names identical in 5/6/9/10; `CivitaiMetadataApplier.ApplyAsync(uow, modelId, fileId, version, apiKey, ct)` in 7/9; `SidecarMetadataApplier.Find/ApplyAsync` + `SidecarLookup.Signature` in 8/9; `FileHasher.Sha256UpperAsync` in 9/12; `SyncStepReport(Kind, Planned, Processed, Succeeded, Skipped, Failed)` in 4/11/12.
