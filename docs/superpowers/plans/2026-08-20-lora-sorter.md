# LoRA Sorter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A third "LoRA Sorter" tab in the LoRA Viewer that reorganizes LoRA files on disk into `{BaseModel}` or `{BaseModel}\{Category}` folders with a preview tree, move/copy choice, disk-space pre-flight, automatic collision handling, and a per-run sort-history manifest.

**Architecture:** Pure planning layer (`LoraSortPlanner` + path/category/sidecar helpers, no I/O beyond reads) feeding a view-model-rendered preview tree; a separate `LoraSortExecutor` performs disk operations via `IFileOperations` and updates `ModelFile.LocalPath` through a swappable `ILocalPathUpdater`. Data source is the DB graph (`IModelSyncService.LoadCachedFilesAsync`), with a metadata resolver (DB→sidecar→Civitai-by-hash with disk cache) for arbitrary folders.

**Tech Stack:** .NET 10, Avalonia 11 (MVVM, CommunityToolkit `[ObservableProperty]`/`[RelayCommand]`), EF Core via `IUnitOfWork`, xUnit + FluentAssertions + Moq.

**Spec:** `docs/superpowers/specs/2026-08-20-lora-sorter-design.md`

## Global Constraints

- Branch: `feature/lora-sorter` (already created). Never commit to `develop`/`main`.
- Tests: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release` — **NEVER full-solution `dotnet test` (stalls)**. Focused runs: `--filter "FullyQualifiedName~<Name>"`. Run from `e:\Repos\DiffusionNexus`.
- Standing rule: every working step logs to the Unified Console — `IUnifiedLogger`, `LogCategory.FileSystem`, source `"LoraSorter"`.
- **No overwrite path may exist anywhere** in the sorter (spec §7.1).
- Deterministic rename convention: `{stem}_{civitaiVersionId}{ext}`, fallback `{stem}_2{ext}`, `_3`… — must match `CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync` semantics.
- `ModelMetadataUtils` and `StaticFileTypes` are `internal` to `DiffusionNexus.Service` — NOT reachable from `DiffusionNexus.UI`. New UI-side helpers own their sidecar list.
- New sorter code lives under `DiffusionNexus.UI\Services\Lora\Sorting\` (namespace `DiffusionNexus.UI.Services.Lora.Sorting`); tests under `DiffusionNexus.Tests\Sorter\`.
- UI copy fixed by spec: tab header `LoRA Sorter`, headline `Sort your LoRAs`, right pane title `Folder structure preview`, move warning `Move rearranges your files on disk — the old folder structure cannot be restored automatically.`
- Style: inline hex per app convention — surfaces `#1E1E1E`, borders `#333`, accent `#4CAF50`, warning `#FFA726`, danger `#FF6B6B`, panel `#2A2A2A`.

---

### Task 1: `SorterCategoryResolver` — category from `Model` (same logic as downloader)

**Files:**
- Create: `DiffusionNexus.UI\Services\Lora\Sorting\SorterCategoryResolver.cs`
- Test: `DiffusionNexus.Tests\Sorter\SorterCategoryResolverTests.cs`

**Interfaces:**
- Consumes: `DiffusionNexus.Domain.Entities.Model` (`UserCategory` is `CivitaiCategory?`, `Tags` is `ICollection<ModelTag>`, tag strings via `mt.Tag?.Name`), `DiffusionNexus.Domain.Enums.CivitaiCategory`.
- Produces: `public static class SorterCategoryResolver` with:
  - `public static CivitaiCategory Resolve(CivitaiCategory? userCategory, IEnumerable<string?> tagNames)`
  - `public static CivitaiCategory ResolveForModel(Model model)` (convenience: `Resolve(model.UserCategory, model.Tags.Select(t => t.Tag?.Name))`)
  - `public static string ToFolderName(CivitaiCategory category)` → `"Base Model"` for `CivitaiCategory.BaseModel`, else `category.ToString()` (matches `CivitaiResultViewModel.InferCategoryFromTags` display convention).

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.Services.Lora.Sorting;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sorter;

public class SorterCategoryResolverTests
{
    [Fact]
    public void UserCategoryOverrideWinsOverTags()
        => SorterCategoryResolver.Resolve(CivitaiCategory.Style, new[] { "character" })
            .Should().Be(CivitaiCategory.Style);

    [Fact]
    public void InfersFromFirstMatchingTagCaseInsensitiveWithSpaces()
        => SorterCategoryResolver.Resolve(null, new[] { "anime", "base model" })
            .Should().Be(CivitaiCategory.BaseModel);

    [Fact]
    public void NullAndWhitespaceTagsAreSkipped()
        => SorterCategoryResolver.Resolve(null, new string?[] { null, "  ", "vehicle" })
            .Should().Be(CivitaiCategory.Vehicle);

    [Fact]
    public void NoMatchYieldsUnknown()
        => SorterCategoryResolver.Resolve(null, new[] { "anime", "photorealistic" })
            .Should().Be(CivitaiCategory.Unknown);

    [Fact]
    public void UnknownUserCategoryFallsThroughToTags()
        => SorterCategoryResolver.Resolve(CivitaiCategory.Unknown, new[] { "poses" })
            .Should().Be(CivitaiCategory.Poses);

    [Theory]
    [InlineData(CivitaiCategory.BaseModel, "Base Model")]
    [InlineData(CivitaiCategory.Character, "Character")]
    [InlineData(CivitaiCategory.Unknown, "Unknown")]
    public void ToFolderNameMatchesDownloaderDisplayConvention(CivitaiCategory category, string expected)
        => SorterCategoryResolver.ToFolderName(category).Should().Be(expected);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~SorterCategoryResolverTests"`
Expected: build FAILURE — `SorterCategoryResolver` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>
/// Resolves the sorting category for a model using the same rules as the
/// Civitai download pipeline (CivitaiResultViewModel.InferCategoryFromTags):
/// an explicit user override wins, otherwise the first tag that parses to a
/// CivitaiCategory value (spaces→underscores, case-insensitive) is used, so a
/// sorted library and freshly downloaded files land in identical folders.
/// </summary>
public static class SorterCategoryResolver
{
    public static CivitaiCategory Resolve(CivitaiCategory? userCategory, IEnumerable<string?> tagNames)
    {
        if (userCategory is { } explicitCategory && explicitCategory != CivitaiCategory.Unknown)
            return explicitCategory;

        foreach (var tagName in tagNames)
        {
            if (string.IsNullOrWhiteSpace(tagName)) continue;
            var normalized = tagName.Replace(" ", "_").Trim();
            if (Enum.TryParse<CivitaiCategory>(normalized, ignoreCase: true, out var category)
                && category != CivitaiCategory.Unknown)
            {
                return category;
            }
        }
        return CivitaiCategory.Unknown;
    }

    public static CivitaiCategory ResolveForModel(Model model)
        => Resolve(model.UserCategory, model.Tags.Select(t => t.Tag?.Name));

    public static string ToFolderName(CivitaiCategory category)
        => category == CivitaiCategory.BaseModel ? "Base Model" : category.ToString();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~SorterCategoryResolverTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Services/Lora/Sorting/SorterCategoryResolver.cs DiffusionNexus.Tests/Sorter/SorterCategoryResolverTests.cs
git commit -m "feat(sorter): category resolver mirroring downloader tag inference"
```

---

### Task 2: `SorterPathBuilder` — folder names, sanitization, deterministic collision rename

**Files:**
- Create: `DiffusionNexus.UI\Services\Lora\Sorting\SorterPathBuilder.cs`
- Test: `DiffusionNexus.Tests\Sorter\SorterPathBuilderTests.cs`

**Interfaces:**
- Consumes: nothing project-specific (pure `System.IO.Path` string work).
- Produces: `public static class SorterPathBuilder` with:
  - `public const string UnknownFolderName = "Unknown"`
  - `public static bool IsPlaceholderBaseModel(string? baseModel)` — true for null/whitespace/`"???"` (same predicate as `LoraViewerViewModel.IsPlaceholderBaseModel`).
  - `public static string SanitizeFolderName(string name)` — replaces `Path.GetInvalidFileNameChars()` with `_`, trims trailing dots/spaces; empty result → `"_"`. (No sanitizer exists anywhere in the app — the downloader combines raw strings; this is new, flagged in the spec.)
  - `public static string BuildTargetDirectory(string targetRoot, string? baseModelRaw, string categoryFolderName, bool includeCategory)` — `targetRoot\{sanitized baseModel or Unknown}` plus `\{categoryFolderName}` when `includeCategory`.
  - `public static string BuildCollisionFreeFileName(string fileName, int? civitaiVersionId, Func<string, bool> nameIsTaken)` — plain name if free; else `{stem}_{versionId}{ext}` if versionId present and free; else `{stem}_2{ext}`, `{stem}_3{ext}`… first free. `nameIsTaken` receives the candidate file name (not full path) so the planner can check both intra-plan claims and on-disk files.

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.UI.Services.Lora.Sorting;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sorter;

public class SorterPathBuilderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("???")]
    public void PlaceholderBaseModelsAreDetected(string? raw)
        => SorterPathBuilder.IsPlaceholderBaseModel(raw).Should().BeTrue();

    [Fact]
    public void RealBaseModelIsNotPlaceholder()
        => SorterPathBuilder.IsPlaceholderBaseModel("SDXL 1.0").Should().BeFalse();

    [Fact]
    public void SanitizeReplacesInvalidCharsAndTrimsTrailingDots()
        => SorterPathBuilder.SanitizeFolderName("Pony/XL: v2.").Should().Be("Pony_XL_ v2");

    [Fact]
    public void SanitizeOfOnlyInvalidCharsYieldsUnderscore()
        => SorterPathBuilder.SanitizeFolderName("..").Should().Be("_");

    [Fact]
    public void BaseModelOnlyStructureOmitsCategory()
        => SorterPathBuilder.BuildTargetDirectory(@"E:\Loras", "SDXL 1.0", "Character", includeCategory: false)
            .Should().Be(@"E:\Loras\SDXL 1.0");

    [Fact]
    public void CategoryStructureAppendsCategoryFolder()
        => SorterPathBuilder.BuildTargetDirectory(@"E:\Loras", "SDXL 1.0", "Character", includeCategory: true)
            .Should().Be(@"E:\Loras\SDXL 1.0\Character");

    [Fact]
    public void PlaceholderBaseModelMapsToUnknownFolder()
        => SorterPathBuilder.BuildTargetDirectory(@"E:\Loras", "???", "Style", includeCategory: true)
            .Should().Be(@"E:\Loras\Unknown\Style");

    [Fact]
    public void FreeNameIsKeptPlain()
        => SorterPathBuilder.BuildCollisionFreeFileName("V1.safetensors", 3204603, _ => false)
            .Should().Be("V1.safetensors");

    [Fact]
    public void TakenNameGetsVersionIdSuffix()
        => SorterPathBuilder.BuildCollisionFreeFileName("V1.safetensors", 3204603,
                n => n == "V1.safetensors")
            .Should().Be("V1_3204603.safetensors");

    [Fact]
    public void WithoutVersionIdNumericSuffixIsUsed()
        => SorterPathBuilder.BuildCollisionFreeFileName("V1.safetensors", null,
                n => n == "V1.safetensors")
            .Should().Be("V1_2.safetensors");

    [Fact]
    public void NumericSuffixSkipsTakenCandidates()
        => SorterPathBuilder.BuildCollisionFreeFileName("V1.safetensors", null,
                n => n is "V1.safetensors" or "V1_2.safetensors")
            .Should().Be("V1_3.safetensors");

    [Fact]
    public void TakenVersionIdSuffixFallsBackToNumeric()
        => SorterPathBuilder.BuildCollisionFreeFileName("V1.safetensors", 42,
                n => n is "V1.safetensors" or "V1_42.safetensors")
            .Should().Be("V1_2.safetensors");
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~SorterPathBuilderTests"`
Expected: build FAILURE — `SorterPathBuilder` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>
/// Pure path construction for the LoRA Sorter: folder naming, sanitization
/// (nothing in the download path sanitizes — this is deliberately new), and the
/// deterministic collision rename convention shared with
/// CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync
/// ({stem}_{versionId}{ext}), so re-runs are idempotent.
/// </summary>
public static class SorterPathBuilder
{
    public const string UnknownFolderName = "Unknown";

    public static bool IsPlaceholderBaseModel(string? baseModel)
        => string.IsNullOrWhiteSpace(baseModel) || baseModel == "???";

    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).TrimEnd('.', ' ');
        return sanitized.Length == 0 ? "_" : sanitized;
    }

    public static string BuildTargetDirectory(
        string targetRoot, string? baseModelRaw, string categoryFolderName, bool includeCategory)
    {
        var baseFolder = IsPlaceholderBaseModel(baseModelRaw)
            ? UnknownFolderName
            : SanitizeFolderName(baseModelRaw!);
        var path = Path.Combine(targetRoot, baseFolder);
        if (includeCategory)
            path = Path.Combine(path, SanitizeFolderName(categoryFolderName));
        return path;
    }

    public static string BuildCollisionFreeFileName(
        string fileName, int? civitaiVersionId, Func<string, bool> nameIsTaken)
    {
        if (!nameIsTaken(fileName)) return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        if (civitaiVersionId is { } versionId)
        {
            var suffixed = $"{stem}_{versionId}{extension}";
            if (!nameIsTaken(suffixed)) return suffixed;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}_{i}{extension}";
            if (!nameIsTaken(candidate)) return candidate;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~SorterPathBuilderTests"`
Expected: PASS (13 tests).

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Services/Lora/Sorting/SorterPathBuilder.cs DiffusionNexus.Tests/Sorter/SorterPathBuilderTests.cs
git commit -m "feat(sorter): path builder with sanitization and deterministic collision renames"
```

---

### Task 3: `SidecarLocator` — companion files that travel with a LoRA

**Files:**
- Create: `DiffusionNexus.UI\Services\Lora\Sorting\SidecarLocator.cs`
- Test: `DiffusionNexus.Tests\Sorter\SidecarLocatorTests.cs`

**Interfaces:**
- Consumes: nothing project-specific (`ModelMetadataUtils`/`StaticFileTypes` are `internal` to DiffusionNexus.Service and NOT reachable from the UI project — this helper owns its list).
- Produces: `public static class SidecarLocator` with:
  - `public static readonly string[] SidecarExtensions` = `[".civitai.info", ".json", ".metadata.json", ".cm-info.json", ".preview.png", ".preview.jpg", ".preview.jpeg", ".preview.webp", ".png", ".jpg", ".jpeg", ".webp", ".thumb.jpg", ".txt", ".info", ".yaml"]` (union of `LoraDuplicateFixerViewModel.SidecarExtensions` and `ModelTileViewModel.LocalPreviewExtensions` conventions).
  - `public static IReadOnlyList<string> FindSidecars(string modelFilePath)` — for `{dir}\{stem}{ext}` returns every existing `{dir}\{stem}{sidecarExt}`. Never returns the model file itself.
  - `public static string DeriveSidecarTargetPath(string sidecarPath, string modelFilePath, string targetModelFilePath)` — maps `{stem}{sidecarExt}` onto the (possibly renamed) target stem so renamed models keep matching sidecar base names.

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.UI.Services.Lora.Sorting;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sorter;

public sealed class SidecarLocatorTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dn-sidecar-");

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch (IOException) { }
    }

    private string Write(string name)
    {
        var path = Path.Combine(_root.FullName, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void FindsExistingSidecarsAndSkipsMissingOnes()
    {
        var model = Write("mylora.safetensors");
        var info = Write("mylora.civitai.info");
        var preview = Write("mylora.preview.png");
        Write("otherlora.civitai.info"); // different stem — must not match

        var sidecars = SidecarLocator.FindSidecars(model);

        sidecars.Should().BeEquivalentTo(new[] { info, preview });
    }

    [Fact]
    public void ModelFileItselfIsNeverReturned()
    {
        var model = Write("mylora.safetensors");
        SidecarLocator.FindSidecars(model).Should().BeEmpty();
    }

    [Fact]
    public void SidecarTargetFollowsRenamedModelStem()
    {
        var mapped = SidecarLocator.DeriveSidecarTargetPath(
            sidecarPath: @"E:\src\V1.civitai.info",
            modelFilePath: @"E:\src\V1.safetensors",
            targetModelFilePath: @"E:\dst\SDXL 1.0\Character\V1_3204603.safetensors");

        mapped.Should().Be(@"E:\dst\SDXL 1.0\Character\V1_3204603.civitai.info");
    }

    [Fact]
    public void MultiDotSidecarExtensionIsPreserved()
    {
        var mapped = SidecarLocator.DeriveSidecarTargetPath(
            sidecarPath: @"E:\src\V1.preview.png",
            modelFilePath: @"E:\src\V1.safetensors",
            targetModelFilePath: @"E:\dst\Unknown\V1_2.safetensors");

        mapped.Should().Be(@"E:\dst\Unknown\V1_2.preview.png");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~SidecarLocatorTests"`
Expected: build FAILURE — `SidecarLocator` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>
/// Locates the companion files that must travel with a LoRA when it is moved,
/// copied, or renamed. Convention: sidecars share the model file's stem in the
/// same directory ({stem}.civitai.info, {stem}.preview.png, ...). The existing
/// delete path (ModelTileViewModel.DeleteFilesFromDisk) misses sidecars — the
/// sorter must not repeat that mistake.
/// </summary>
public static class SidecarLocator
{
    public static readonly string[] SidecarExtensions =
    [
        ".civitai.info", ".json", ".metadata.json", ".cm-info.json",
        ".preview.png", ".preview.jpg", ".preview.jpeg", ".preview.webp",
        ".png", ".jpg", ".jpeg", ".webp", ".thumb.jpg", ".txt", ".info", ".yaml"
    ];

    public static IReadOnlyList<string> FindSidecars(string modelFilePath)
    {
        var directory = Path.GetDirectoryName(modelFilePath);
        var stem = Path.GetFileNameWithoutExtension(modelFilePath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(stem))
            return [];

        var results = new List<string>();
        foreach (var extension in SidecarExtensions)
        {
            var candidate = Path.Combine(directory, stem + extension);
            if (!string.Equals(candidate, modelFilePath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(candidate))
            {
                results.Add(candidate);
            }
        }
        return results;
    }

    public static string DeriveSidecarTargetPath(
        string sidecarPath, string modelFilePath, string targetModelFilePath)
    {
        var sourceStem = Path.GetFileNameWithoutExtension(modelFilePath);
        var sidecarName = Path.GetFileName(sidecarPath);
        // Everything after the source stem is the (possibly multi-dot) sidecar extension.
        var sidecarExtension = sidecarName[sourceStem.Length..];

        var targetDirectory = Path.GetDirectoryName(targetModelFilePath)!;
        var targetStem = Path.GetFileNameWithoutExtension(targetModelFilePath);
        return Path.Combine(targetDirectory, targetStem + sidecarExtension);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~SidecarLocatorTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Services/Lora/Sorting/SidecarLocator.cs DiffusionNexus.Tests/Sorter/SidecarLocatorTests.cs
git commit -m "feat(sorter): sidecar locator so companion files travel with the model"
```

---

### Task 4: Plan types + `LoraSortPlanner` — pure planning with collision classification

**Files:**
- Create: `DiffusionNexus.UI\Services\Lora\Sorting\LoraSortModels.cs`
- Create: `DiffusionNexus.UI\Services\Lora\Sorting\LoraSortPlanner.cs`
- Test: `DiffusionNexus.Tests\Sorter\LoraSortPlannerTests.cs`

**Interfaces:**
- Consumes: `SorterPathBuilder` (Task 2: `BuildTargetDirectory`, `BuildCollisionFreeFileName`, `IsPlaceholderBaseModel`, `UnknownFolderName`), `SorterCategoryResolver.ToFolderName` (Task 1) at the call site that builds candidates (Task 8).
- Produces (in `LoraSortModels.cs`, namespace `DiffusionNexus.UI.Services.Lora.Sorting`):

```csharp
/// <summary>One LoRA the sorter may act on — decoupled from the DB graph so the planner is pure.</summary>
public sealed record SortCandidate(
    string FilePath,
    string? BaseModelRaw,
    string CategoryFolderName,   // already resolved via SorterCategoryResolver.ToFolderName
    int? CivitaiVersionId,
    string? Sha256,              // stored DB hash when known; null → hash lazily on collision
    long FileSizeBytes,
    IReadOnlyList<string> SidecarPaths);

public enum PlannedAction { Transfer, AlreadyInPlace, SkippedDuplicate }

public sealed record PlannedMove(
    SortCandidate Candidate,
    string TargetDirectory,
    string TargetFilePath,       // includes any collision rename
    PlannedAction Action,
    bool WasRenamed);

public sealed record LoraSortPlan(
    IReadOnlyList<PlannedMove> Moves,
    string SourceRoot,
    string TargetRoot,
    bool IsMove,
    long RequiredBytes,          // per spec §6: copy = all planned bytes; move = cross-volume bytes only
    int TransferCount,
    int AlreadyInPlaceCount,
    int RenamedCount,
    int SkippedDuplicateCount);

/// <summary>Options captured from the UI.</summary>
public sealed record LoraSortOptions(
    string SourceRoot,
    string TargetRoot,
    bool IncludeCategory,
    bool IsMove,
    bool DeleteEmptySourceFolders);
```

- Produces (in `LoraSortPlanner.cs`):

```csharp
public sealed class LoraSortPlanner
{
    // hashFile: lazy content hash (lowercase hex SHA256) — called ONLY for
    // collision candidates missing a stored hash. Injected for testability.
    // fileExistsOnDisk: injected File.Exists for target-exists collisions.
    public LoraSortPlanner(Func<string, string> hashFile, Func<string, bool> fileExistsOnDisk);

    public LoraSortPlan BuildPlan(IReadOnlyList<SortCandidate> candidates, LoraSortOptions options);
}
```

**`BuildPlan` algorithm (implement exactly):**
1. For each candidate compute `targetDir = SorterPathBuilder.BuildTargetDirectory(options.TargetRoot, c.BaseModelRaw, c.CategoryFolderName, options.IncludeCategory)`.
2. If `Path.GetDirectoryName(c.FilePath)` equals `targetDir` (OrdinalIgnoreCase) AND the plain file name is not yet claimed by an earlier candidate → `PlannedAction.AlreadyInPlace` (excluded from transfer/size counts); the name still claims its slot in that folder.
3. Maintain `claimed: Dictionary<string /*targetDir*/, Dictionary<string /*fileName*/, SortCandidate>>` (both OrdinalIgnoreCase). A name is "taken" if claimed by an earlier candidate OR `fileExistsOnDisk(Path.Combine(targetDir, name))`.
4. On a taken plain name, classify by content: candidate hash (`Sha256 ?? hashFile(FilePath)`) vs claimant hash (earlier candidate's `Sha256 ?? hashFile(...)`, or `hashFile(existing on-disk target)`). Hashes cached per path; comparison OrdinalIgnoreCase. Equal → `SkippedDuplicate` (second copy stays put). Different → rename via `SorterPathBuilder.BuildCollisionFreeFileName(name, c.CivitaiVersionId, nameIsTaken)` → `Transfer` with `WasRenamed = true`.
5. `RequiredBytes`: 0 when `options.IsMove` and `Path.GetPathRoot(SourceRoot)` equals `Path.GetPathRoot(TargetRoot)` (OrdinalIgnoreCase); otherwise the sum of `FileSizeBytes` over `Transfer` moves.
6. Counts fall out of the classification.

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.UI.Services.Lora.Sorting;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sorter;

public class LoraSortPlannerTests
{
    private static SortCandidate Candidate(
        string path, string? baseModel = "SDXL 1.0", string category = "Character",
        int? versionId = null, string? sha = null, long size = 1000)
        => new(path, baseModel, category, versionId, sha, size, []);

    private static LoraSortOptions Options(bool includeCategory = true, bool isMove = true,
        string source = @"E:\Loras", string target = @"E:\Loras")
        => new(source, target, includeCategory, isMove, DeleteEmptySourceFolders: false);

    private static LoraSortPlanner Planner(
        Func<string, string>? hash = null, Func<string, bool>? exists = null)
        => new(hash ?? (_ => throw new InvalidOperationException("hashFile must not be called")),
               exists ?? (_ => false));

    [Fact]
    public void SimpleCandidateIsPlannedIntoBaseModelCategoryFolder()
    {
        var plan = Planner().BuildPlan([Candidate(@"E:\Loras\flat\a.safetensors")], Options());

        var move = plan.Moves.Single();
        move.Action.Should().Be(PlannedAction.Transfer);
        move.TargetFilePath.Should().Be(@"E:\Loras\SDXL 1.0\Character\a.safetensors");
        move.WasRenamed.Should().BeFalse();
        plan.TransferCount.Should().Be(1);
    }

    [Fact]
    public void BaseModelOnlyStructureSkipsCategorySegment()
    {
        var plan = Planner().BuildPlan(
            [Candidate(@"E:\Loras\flat\a.safetensors")], Options(includeCategory: false));

        plan.Moves.Single().TargetFilePath.Should().Be(@"E:\Loras\SDXL 1.0\a.safetensors");
    }

    [Fact]
    public void PlaceholderBaseModelLandsInUnknown()
    {
        var plan = Planner().BuildPlan(
            [Candidate(@"E:\Loras\flat\a.safetensors", baseModel: "???")], Options());

        plan.Moves.Single().TargetFilePath.Should().Be(@"E:\Loras\Unknown\Character\a.safetensors");
    }

    [Fact]
    public void FileAlreadyAtComputedTargetIsMarkedInPlace()
    {
        var plan = Planner().BuildPlan(
            [Candidate(@"E:\Loras\SDXL 1.0\Character\a.safetensors")], Options());

        plan.Moves.Single().Action.Should().Be(PlannedAction.AlreadyInPlace);
        plan.AlreadyInPlaceCount.Should().Be(1);
        plan.TransferCount.Should().Be(0);
    }

    [Fact]
    public void DifferentContentCollisionGetsVersionIdRename()
    {
        var plan = Planner().BuildPlan(
        [
            Candidate(@"E:\Loras\x\V1.safetensors", versionId: 111, sha: "aaa"),
            Candidate(@"E:\Loras\y\V1.safetensors", versionId: 222, sha: "bbb"),
        ], Options());

        plan.Moves[0].TargetFilePath.Should().Be(@"E:\Loras\SDXL 1.0\Character\V1.safetensors");
        plan.Moves[1].TargetFilePath.Should().Be(@"E:\Loras\SDXL 1.0\Character\V1_222.safetensors");
        plan.Moves[1].WasRenamed.Should().BeTrue();
        plan.RenamedCount.Should().Be(1);
    }

    [Fact]
    public void IdenticalContentCollisionIsSkippedAsDuplicate()
    {
        var plan = Planner().BuildPlan(
        [
            Candidate(@"E:\Loras\x\V1.safetensors", sha: "aaa"),
            Candidate(@"E:\Loras\y\V1.safetensors", sha: "AAA"), // hash compare is case-insensitive
        ], Options());

        plan.Moves[1].Action.Should().Be(PlannedAction.SkippedDuplicate);
        plan.SkippedDuplicateCount.Should().Be(1);
        plan.TransferCount.Should().Be(1);
    }

    [Fact]
    public void MissingHashesAreComputedLazilyOnlyForCollidingFiles()
    {
        var hashed = new List<string>();
        var planner = Planner(hash: p => { hashed.Add(p); return p.Contains(@"\x\") ? "aaa" : "bbb"; });

        planner.BuildPlan(
        [
            Candidate(@"E:\Loras\x\V1.safetensors"),
            Candidate(@"E:\Loras\y\V1.safetensors", versionId: 5),
            Candidate(@"E:\Loras\z\unique.safetensors"),
        ], Options());

        hashed.Should().BeEquivalentTo(new[]
            { @"E:\Loras\x\V1.safetensors", @"E:\Loras\y\V1.safetensors" });
    }

    [Fact]
    public void OnDiskTargetCollisionWithDifferentContentIsRenamed()
    {
        var planner = Planner(
            hash: p => p == @"E:\Loras\SDXL 1.0\Character\V1.safetensors" ? "disk" : "mine",
            exists: p => p == @"E:\Loras\SDXL 1.0\Character\V1.safetensors");

        var plan = planner.BuildPlan([Candidate(@"E:\Loras\x\V1.safetensors", versionId: 9)], Options());

        plan.Moves.Single().TargetFilePath.Should().Be(@"E:\Loras\SDXL 1.0\Character\V1_9.safetensors");
    }

    [Fact]
    public void OnDiskTargetCollisionWithIdenticalContentIsSkipped()
    {
        var planner = Planner(
            hash: _ => "same",
            exists: p => p == @"E:\Loras\SDXL 1.0\Character\V1.safetensors");

        var plan = planner.BuildPlan([Candidate(@"E:\Loras\x\V1.safetensors")], Options());

        plan.Moves.Single().Action.Should().Be(PlannedAction.SkippedDuplicate);
    }

    [Fact]
    public void SameVolumeMoveRequiresZeroBytes()
    {
        var plan = Planner().BuildPlan([Candidate(@"E:\Loras\x\a.safetensors", size: 5000)],
            Options(isMove: true, source: @"E:\Loras", target: @"E:\Sorted"));

        plan.RequiredBytes.Should().Be(0);
    }

    [Fact]
    public void CopyRequiresAllPlannedBytesButNotSkippedOnes()
    {
        var plan = Planner().BuildPlan(
        [
            Candidate(@"E:\Loras\x\a.safetensors", size: 5000),
            Candidate(@"E:\Loras\x\V1.safetensors", sha: "s", size: 700),
            Candidate(@"E:\Loras\y\V1.safetensors", sha: "s", size: 700), // duplicate → skipped
        ], Options(isMove: false, source: @"E:\Loras", target: @"D:\Backup"));

        plan.RequiredBytes.Should().Be(5700);
    }

    [Fact]
    public void CrossVolumeMoveRequiresTransferredBytes()
    {
        var plan = Planner().BuildPlan([Candidate(@"E:\Loras\x\a.safetensors", size: 5000)],
            Options(isMove: true, source: @"E:\Loras", target: @"D:\Sorted"));

        plan.RequiredBytes.Should().Be(5000);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~LoraSortPlannerTests"`
Expected: build FAILURE — types do not exist.

- [ ] **Step 3: Write `LoraSortModels.cs`** exactly as declared in the Interfaces block above (records + enum, one file, namespace `DiffusionNexus.UI.Services.Lora.Sorting`).

- [ ] **Step 4: Write `LoraSortPlanner.cs`**

```csharp
namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>
/// Pure planner: computes where every candidate lands without touching the disk
/// (reads are injected). Collision policy per spec §7.1 — different content gets a
/// deterministic rename, identical content is skipped, overwrite does not exist.
/// </summary>
public sealed class LoraSortPlanner
{
    private readonly Func<string, string> _hashFile;
    private readonly Func<string, bool> _fileExistsOnDisk;

    public LoraSortPlanner(Func<string, string> hashFile, Func<string, bool> fileExistsOnDisk)
    {
        _hashFile = hashFile;
        _fileExistsOnDisk = fileExistsOnDisk;
    }

    public LoraSortPlan BuildPlan(IReadOnlyList<SortCandidate> candidates, LoraSortOptions options)
    {
        var moves = new List<PlannedMove>(candidates.Count);
        var claimed = new Dictionary<string, Dictionary<string, SortCandidate>>(StringComparer.OrdinalIgnoreCase);
        var hashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string HashOfFile(string path)
            => hashCache.TryGetValue(path, out var h) ? h : hashCache[path] = _hashFile(path);
        string HashOfCandidate(SortCandidate c)
            => !string.IsNullOrWhiteSpace(c.Sha256) ? c.Sha256! : HashOfFile(c.FilePath);

        foreach (var candidate in candidates)
        {
            var targetDir = SorterPathBuilder.BuildTargetDirectory(
                options.TargetRoot, candidate.BaseModelRaw, candidate.CategoryFolderName, options.IncludeCategory);
            var names = claimed.TryGetValue(targetDir, out var existing)
                ? existing
                : claimed[targetDir] = new Dictionary<string, SortCandidate>(StringComparer.OrdinalIgnoreCase);
            var fileName = Path.GetFileName(candidate.FilePath);

            bool NameIsTaken(string name)
                => names.ContainsKey(name) || _fileExistsOnDisk(Path.Combine(targetDir, name));

            var sourceDir = Path.GetDirectoryName(candidate.FilePath) ?? string.Empty;
            if (string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase)
                && !names.ContainsKey(fileName))
            {
                names[fileName] = candidate;
                moves.Add(new PlannedMove(candidate, targetDir,
                    Path.Combine(targetDir, fileName), PlannedAction.AlreadyInPlace, WasRenamed: false));
                continue;
            }

            if (!NameIsTaken(fileName))
            {
                names[fileName] = candidate;
                moves.Add(new PlannedMove(candidate, targetDir,
                    Path.Combine(targetDir, fileName), PlannedAction.Transfer, WasRenamed: false));
                continue;
            }

            // Collision: classify by content. Claimant is the earlier candidate if any,
            // otherwise the file already on disk at the plain target path.
            var myHash = HashOfCandidate(candidate);
            var claimantHash = names.TryGetValue(fileName, out var claimant)
                ? HashOfCandidate(claimant)
                : HashOfFile(Path.Combine(targetDir, fileName));

            if (string.Equals(myHash, claimantHash, StringComparison.OrdinalIgnoreCase))
            {
                moves.Add(new PlannedMove(candidate, targetDir,
                    Path.Combine(targetDir, fileName), PlannedAction.SkippedDuplicate, WasRenamed: false));
                continue;
            }

            var renamed = SorterPathBuilder.BuildCollisionFreeFileName(
                fileName, candidate.CivitaiVersionId, NameIsTaken);
            names[renamed] = candidate;
            moves.Add(new PlannedMove(candidate, targetDir,
                Path.Combine(targetDir, renamed), PlannedAction.Transfer, WasRenamed: true));
        }

        var transfers = moves.Where(m => m.Action == PlannedAction.Transfer).ToList();
        var sameVolumeMove = options.IsMove && string.Equals(
            Path.GetPathRoot(options.SourceRoot), Path.GetPathRoot(options.TargetRoot),
            StringComparison.OrdinalIgnoreCase);
        var requiredBytes = sameVolumeMove ? 0L : transfers.Sum(m => m.Candidate.FileSizeBytes);

        return new LoraSortPlan(
            moves, options.SourceRoot, options.TargetRoot, options.IsMove, requiredBytes,
            TransferCount: transfers.Count,
            AlreadyInPlaceCount: moves.Count(m => m.Action == PlannedAction.AlreadyInPlace),
            RenamedCount: moves.Count(m => m.WasRenamed),
            SkippedDuplicateCount: moves.Count(m => m.Action == PlannedAction.SkippedDuplicate));
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~LoraSortPlannerTests"`
Expected: PASS (12 tests).

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/Services/Lora/Sorting/LoraSortModels.cs DiffusionNexus.UI/Services/Lora/Sorting/LoraSortPlanner.cs DiffusionNexus.Tests/Sorter/LoraSortPlannerTests.cs
git commit -m "feat(sorter): pure sort planner with content-classified collision handling"
```

---


### Task 5: `SortHistoryWriter` — per-run manifest for the future Restore feature

**Files:**
- Create: `DiffusionNexus.UI\Services\Lora\Sorting\SortHistoryWriter.cs`
- Test: `DiffusionNexus.Tests\Sorter\SortHistoryWriterTests.cs`

**Interfaces:**
- Consumes: `LoraSortPlan`, `PlannedMove`, `PlannedAction` (Task 4).
- Produces:

```csharp
public sealed class SortHistoryWriter
{
    /// <param name="historyDirectory">Injected for tests; production uses
    /// Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    ///     "DiffusionNexus", "SortHistory").</param>
    public SortHistoryWriter(string historyDirectory);

    /// <summary>Writes the full plan before execution starts. Returns the manifest path.</summary>
    public string WritePlan(LoraSortPlan plan, DateTimeOffset startedAt);

    /// <summary>Flags one entry completed (by source path) and rewrites the manifest.</summary>
    public void MarkCompleted(string manifestPath, string sourceFilePath);
}
```

**Manifest JSON shape** (System.Text.Json, indented; one file per run named `{startedAt:yyyyMMdd-HHmmss}.json`):

```json
{
  "startedAt": "2026-08-20T14:00:00+02:00",
  "sourceRoot": "E:\\Loras",
  "targetRoot": "E:\\Loras",
  "isMove": true,
  "entries": [
    { "source": "E:\\Loras\\x\\a.safetensors", "target": "E:\\Loras\\SDXL 1.0\\Character\\a.safetensors",
      "action": "Transfer", "renamed": false, "sizeBytes": 1000, "completed": false }
  ]
}
```

Internal DTOs (`SortHistoryManifest`, `SortHistoryEntry`) are private nested classes or file-local records — the Restore follow-up will promote them when it ships. `MarkCompleted` does read-modify-write of the whole file; per spec the run is sequential so there is no concurrency concern.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;
using DiffusionNexus.UI.Services.Lora.Sorting;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sorter;

public sealed class SortHistoryWriterTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dn-sorthist-");

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch (IOException) { }
    }

    private static LoraSortPlan SamplePlan()
    {
        var candidate = new SortCandidate(
            @"E:\Loras\x\a.safetensors", "SDXL 1.0", "Character", null, null, 1000, []);
        var move = new PlannedMove(candidate, @"E:\Loras\SDXL 1.0\Character",
            @"E:\Loras\SDXL 1.0\Character\a.safetensors", PlannedAction.Transfer, WasRenamed: false);
        return new LoraSortPlan([move], @"E:\Loras", @"E:\Loras", IsMove: true,
            RequiredBytes: 0, TransferCount: 1, AlreadyInPlaceCount: 0,
            RenamedCount: 0, SkippedDuplicateCount: 0);
    }

    [Fact]
    public void WritePlanCreatesTimestampNamedManifestWithAllEntries()
    {
        var writer = new SortHistoryWriter(_root.FullName);
        var startedAt = new DateTimeOffset(2026, 8, 20, 14, 0, 0, TimeSpan.FromHours(2));

        var path = writer.WritePlan(SamplePlan(), startedAt);

        Path.GetFileName(path).Should().Be("20260820-140000.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        doc.RootElement.GetProperty("isMove").GetBoolean().Should().BeTrue();
        var entry = doc.RootElement.GetProperty("entries")[0];
        entry.GetProperty("source").GetString().Should().Be(@"E:\Loras\x\a.safetensors");
        entry.GetProperty("completed").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void MarkCompletedFlagsOnlyTheMatchingEntry()
    {
        var writer = new SortHistoryWriter(_root.FullName);
        var path = writer.WritePlan(SamplePlan(), DateTimeOffset.Now);

        writer.MarkCompleted(path, @"E:\Loras\x\a.safetensors");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        doc.RootElement.GetProperty("entries")[0].GetProperty("completed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void HistoryDirectoryIsCreatedOnDemand()
    {
        var nested = Path.Combine(_root.FullName, "does", "not", "exist");
        var writer = new SortHistoryWriter(nested);

        var path = writer.WritePlan(SamplePlan(), DateTimeOffset.Now);

        File.Exists(path).Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~SortHistoryWriterTests"`
Expected: build FAILURE — `SortHistoryWriter` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>
/// Writes the per-run sort-history manifest (spec §7 step 5): the full plan is
/// persisted before the first file is touched, then each completed file is
/// flagged. This is the data source for the future "Restore previous structure"
/// feature; v1 only writes it.
/// </summary>
public sealed class SortHistoryWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _historyDirectory;

    public SortHistoryWriter(string historyDirectory) => _historyDirectory = historyDirectory;

    public static string DefaultHistoryDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiffusionNexus", "SortHistory");

    public string WritePlan(LoraSortPlan plan, DateTimeOffset startedAt)
    {
        Directory.CreateDirectory(_historyDirectory);
        var manifest = new Manifest(
            startedAt, plan.SourceRoot, plan.TargetRoot, plan.IsMove,
            plan.Moves.Select(m => new Entry(
                m.Candidate.FilePath, m.TargetFilePath, m.Action, m.WasRenamed,
                m.Candidate.FileSizeBytes, Completed: false)).ToList());
        var path = Path.Combine(_historyDirectory, $"{startedAt:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));
        return path;
    }

    public void MarkCompleted(string manifestPath, string sourceFilePath)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonOptions)!;
        var updated = manifest with
        {
            Entries = manifest.Entries
                .Select(e => string.Equals(e.Source, sourceFilePath, StringComparison.OrdinalIgnoreCase)
                    ? e with { Completed = true } : e)
                .ToList()
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(updated, JsonOptions));
    }

    private sealed record Manifest(
        DateTimeOffset StartedAt, string SourceRoot, string TargetRoot, bool IsMove,
        List<Entry> Entries);

    private sealed record Entry(
        string Source, string Target, PlannedAction Action, bool Renamed,
        long SizeBytes, bool Completed);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~SortHistoryWriterTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Services/Lora/Sorting/SortHistoryWriter.cs DiffusionNexus.Tests/Sorter/SortHistoryWriterTests.cs
git commit -m "feat(sorter): per-run sort-history manifest"
```

---

### Task 6: `ILocalPathUpdater` + `LoraSortExecutor` — disk operations + DB updates

**Files:**
- Create: `DiffusionNexus.UI\Services\Lora\Sorting\ILocalPathUpdater.cs`
- Create: `DiffusionNexus.UI\Services\Lora\Sorting\DbLocalPathUpdater.cs`
- Create: `DiffusionNexus.UI\Services\Lora\Sorting\LoraSortExecutor.cs`
- Test: `DiffusionNexus.Tests\Sorter\LoraSortExecutorTests.cs`

**Interfaces:**
- Consumes: `LoraSortPlan`/`PlannedMove`/`PlannedAction` (Task 4), `SidecarLocator.DeriveSidecarTargetPath` (Task 3), `IFileOperations` (`DiffusionNexus.UI.Utilities` — `CopyFile(src,dst,overwrite)`, `MoveFile(src,dst,overwrite)`, `CreateDirectory(path)`, `FileExists(path)`), `SortHistoryWriter` (Task 5), `IUnifiedLogger` (`Info/Warn/Error(LogCategory, string source, string message, ...)` — category `LogCategory.FileSystem`, source `"LoraSorter"`), `DiskUtility.DeleteEmptyDirectoriesAsync` (`DiffusionNexus.Service.Services.IO`).
- Produces:

```csharp
public interface ILocalPathUpdater
{
    /// <summary>Repoints every DB ModelFile row at oldPath to newPath
    /// (LocalPath + LocalFileVerifiedAt = now, IsLocalFileValid = true).</summary>
    Task UpdateLocalPathsAsync(IReadOnlyList<(string OldPath, string NewPath)> changes,
        CancellationToken ct = default);
}

public sealed record LoraSortResult(int Moved, int Copied, int Skipped, int Failed, bool Cancelled,
    string? ManifestPath);

public sealed class LoraSortExecutor
{
    public LoraSortExecutor(IFileOperations fileOperations, ILocalPathUpdater pathUpdater,
        SortHistoryWriter historyWriter, IUnifiedLogger? logger);

    public Task<LoraSortResult> ExecuteAsync(LoraSortPlan plan,
        IProgress<(double Fraction, string Status)>? progress = null,
        CancellationToken ct = default);
}
```

**`ExecuteAsync` algorithm (implement exactly):**
1. `manifestPath = _historyWriter.WritePlan(plan, DateTimeOffset.Now)`; log Info: plan summary (`{TransferCount} to transfer, {SkippedDuplicateCount} duplicates skipped, {RenamedCount} renamed`).
2. For each `move` in `plan.Moves` where `Action == PlannedAction.Transfer` (sequential):
   - `ct.ThrowIfCancellationRequested()` → catch `OperationCanceledException` at loop level, set `Cancelled = true`, break.
   - `try`:
     - `_fileOperations.CreateDirectory(move.TargetDirectory)`.
     - Model file: move → `_fileOperations.MoveFile(src, move.TargetFilePath, overwrite: false)`; copy → `CopyFile(..., overwrite: false)`. **`overwrite` is always `false`** — the planner guarantees a free name; a race is surfaced as a failure, never an overwrite.
     - Each sidecar in `move.Candidate.SidecarPaths`: target via `SidecarLocator.DeriveSidecarTargetPath(sidecar, src, move.TargetFilePath)`; same move/copy; a sidecar failure logs Warn and continues (the model file already transferred).
     - Move mode: collect `(src, move.TargetFilePath)` into `pendingDbChanges`; every 20 entries flush via `_pathUpdater.UpdateLocalPathsAsync(batch, ct)` and clear.
     - `_historyWriter.MarkCompleted(manifestPath, src)`; log Info `"{src} → {target}"`; `progress.Report(((double)done / plan.TransferCount, fileName))`.
   - `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)`: log Error, increment `Failed`, continue (spec §7 step 4 — a locked file is not fatal).
3. Flush remaining `pendingDbChanges` (also on cancellation — completed moves must stay consistent; wrap the final flush in `CancellationToken.None`).
4. `Skipped = plan.SkippedDuplicateCount`. Log final tally Info.
5. Return `LoraSortResult`.

`DbLocalPathUpdater` (thin, not unit-tested — exercised by the manual smoke; keep it tiny):

```csharp
using DiffusionNexus.DataAccess.UnitOfWork;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>Repoints ModelFile.LocalPath rows inside a fresh UoW scope per batch —
/// same pattern as LoraViewerViewModel's local-metadata writes.</summary>
public sealed class DbLocalPathUpdater : ILocalPathUpdater
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DbLocalPathUpdater(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task UpdateLocalPathsAsync(
        IReadOnlyList<(string OldPath, string NewPath)> changes, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        foreach (var (oldPath, newPath) in changes)
        {
            // One file may be owned by multiple rows (historic dedup edge) — update all.
            var owners = await unitOfWork.ModelFiles.GetByLocalPathAsync(oldPath, ct);
            foreach (var file in owners)
            {
                file.LocalPath = newPath;
                file.IsLocalFileValid = true;
                file.LocalFileVerifiedAt = DateTimeOffset.UtcNow;
            }
        }
        await unitOfWork.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.UI.Services.Lora.Sorting;
using DiffusionNexus.UI.Utilities;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sorter;

public sealed class LoraSortExecutorTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dn-sortexec-");
    private readonly List<(string OldPath, string NewPath)> _dbChanges = [];

    private sealed class RecordingPathUpdater(List<(string, string)> sink) : ILocalPathUpdater
    {
        public Task UpdateLocalPathsAsync(IReadOnlyList<(string OldPath, string NewPath)> changes,
            CancellationToken ct = default)
        {
            sink.AddRange(changes.Select(c => (c.OldPath, c.NewPath)));
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch (IOException) { }
    }

    private string In(params string[] parts) => Path.Combine([_root.FullName, .. parts]);

    private string Write(string relative, string content = "weights")
    {
        var path = In(relative.Split('\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private LoraSortExecutor Executor()
        => new(new FileOperations(), new RecordingPathUpdater(_dbChanges),
               new SortHistoryWriter(In("history")), logger: null);

    private LoraSortPlan Plan(bool isMove, params PlannedMove[] moves)
        => new(moves, _root.FullName, _root.FullName, isMove,
            RequiredBytes: 0,
            TransferCount: moves.Count(m => m.Action == PlannedAction.Transfer),
            AlreadyInPlaceCount: moves.Count(m => m.Action == PlannedAction.AlreadyInPlace),
            RenamedCount: moves.Count(m => m.WasRenamed),
            SkippedDuplicateCount: moves.Count(m => m.Action == PlannedAction.SkippedDuplicate));

    private PlannedMove Move(string sourceRel, string targetRel, params string[] sidecarRels)
    {
        var source = In(sourceRel.Split('\\'));
        var target = In(targetRel.Split('\\'));
        var candidate = new SortCandidate(source, "SDXL 1.0", "Character", null, null,
            new FileInfo(source).Length, sidecarRels.Select(r => In(r.Split('\\'))).ToList());
        return new PlannedMove(candidate, Path.GetDirectoryName(target)!, target,
            PlannedAction.Transfer, WasRenamed: false);
    }

    [Fact]
    public async Task MoveTransfersModelAndSidecarsAndReportsDbChange()
    {
        var model = Write(@"flat\a.safetensors");
        Write(@"flat\a.civitai.info", "meta");
        var move = Move(@"flat\a.safetensors", @"SDXL 1.0\Character\a.safetensors",
            @"flat\a.civitai.info");

        var result = await Executor().ExecuteAsync(Plan(isMove: true, move));

        result.Moved.Should().Be(1);
        File.Exists(model).Should().BeFalse();
        File.Exists(In("SDXL 1.0", "Character", "a.safetensors")).Should().BeTrue();
        File.Exists(In("SDXL 1.0", "Character", "a.civitai.info")).Should().BeTrue();
        _dbChanges.Should().ContainSingle()
            .Which.Should().Be((model, In("SDXL 1.0", "Character", "a.safetensors")));
    }

    [Fact]
    public async Task CopyLeavesSourceIntactAndDoesNotTouchDb()
    {
        var model = Write(@"flat\a.safetensors");
        var move = Move(@"flat\a.safetensors", @"SDXL 1.0\Character\a.safetensors");

        var result = await Executor().ExecuteAsync(Plan(isMove: false, move));

        result.Copied.Should().Be(1);
        File.Exists(model).Should().BeTrue();
        File.Exists(In("SDXL 1.0", "Character", "a.safetensors")).Should().BeTrue();
        _dbChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task RenamedTargetRenamesSidecarsToo()
    {
        Write(@"flat\V1.safetensors");
        Write(@"flat\V1.preview.png", "img");
        var move = Move(@"flat\V1.safetensors", @"SDXL 1.0\Character\V1_42.safetensors",
            @"flat\V1.preview.png");

        await Executor().ExecuteAsync(Plan(isMove: true, move));

        File.Exists(In("SDXL 1.0", "Character", "V1_42.preview.png")).Should().BeTrue();
    }

    [Fact]
    public async Task FailedFileIsSkippedAndRunContinues()
    {
        Write(@"flat\a.safetensors");
        var ghost = Move(@"flat\ghost.safetensors", @"SDXL 1.0\Character\ghost.safetensors");
        // ghost source never written → FileOperations.MoveFile fallback throws FileNotFoundException(IOException)
        var ok = Move(@"flat\a.safetensors", @"SDXL 1.0\Character\a.safetensors");

        var result = await Executor().ExecuteAsync(Plan(isMove: true, ghost, ok));

        result.Failed.Should().Be(1);
        result.Moved.Should().Be(1);
        File.Exists(In("SDXL 1.0", "Character", "a.safetensors")).Should().BeTrue();
    }

    [Fact]
    public async Task CancellationStopsBetweenFilesButKeepsCompletedWork()
    {
        Write(@"flat\a.safetensors");
        Write(@"flat\b.safetensors");
        using var cts = new CancellationTokenSource();
        var progress = new Progress<(double, string)>();
        var executor = Executor();
        var plan = Plan(isMove: true,
            Move(@"flat\a.safetensors", @"SDXL 1.0\Character\a.safetensors"),
            Move(@"flat\b.safetensors", @"SDXL 1.0\Character\b.safetensors"));

        // Cancel after the first file via a progress callback.
        var syncProgress = new SynchronousProgress(cts);
        var result = await executor.ExecuteAsync(plan, syncProgress, cts.Token);

        result.Cancelled.Should().BeTrue();
        result.Moved.Should().Be(1);
        File.Exists(In("SDXL 1.0", "Character", "a.safetensors")).Should().BeTrue();
        File.Exists(In("flat", "b.safetensors")).Should().BeTrue();
        _dbChanges.Should().HaveCount(1); // pending batch flushed on cancel
    }

    private sealed class SynchronousProgress(CancellationTokenSource cts)
        : IProgress<(double Fraction, string Status)>
    {
        public void Report((double Fraction, string Status) value) => cts.Cancel();
    }

    [Fact]
    public async Task ManifestIsWrittenBeforeExecutionAndEntriesGetCompleted()
    {
        Write(@"flat\a.safetensors");
        var executor = Executor();

        var result = await executor.ExecuteAsync(Plan(isMove: true,
            Move(@"flat\a.safetensors", @"SDXL 1.0\Character\a.safetensors")));

        result.ManifestPath.Should().NotBeNull();
        var json = File.ReadAllText(result.ManifestPath!);
        json.Should().Contain("\"completed\": true");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~LoraSortExecutorTests"`
Expected: build FAILURE — types do not exist.

- [ ] **Step 3: Write `ILocalPathUpdater.cs` and `DbLocalPathUpdater.cs`** exactly as declared in the Interfaces block.

- [ ] **Step 4: Write `LoraSortExecutor.cs`** implementing the algorithm above. Progress is reported AFTER each completed file (so the cancellation test cancels post-file-1). `Moved`/`Copied` count per `plan.IsMove`. The DB batch size constant: `private const int DbBatchSize = 20;`. Cancellation check: `ct.IsCancellationRequested` tested at the top of each loop iteration (no throw needed — set `cancelled = true` and break). Final flush of pending DB changes always runs with `CancellationToken.None`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~LoraSortExecutorTests"`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/Services/Lora/Sorting/ILocalPathUpdater.cs DiffusionNexus.UI/Services/Lora/Sorting/DbLocalPathUpdater.cs DiffusionNexus.UI/Services/Lora/Sorting/LoraSortExecutor.cs DiffusionNexus.Tests/Sorter/LoraSortExecutorTests.cs
git commit -m "feat(sorter): executor with sidecar transfer, DB repointing, manifest and cancellation"
```

---


### Task 7: `SorterMetadataResolver` — metadata for files the DB doesn't know

**Files:**
- Create: `DiffusionNexus.UI\Services\Lora\Sorting\SorterMetadataResolver.cs`
- Test: `DiffusionNexus.Tests\Sorter\SorterMetadataResolverTests.cs`

**Interfaces:**
- Consumes: `ICivitaiClient.GetModelVersionByHashAsync(string hash, string? apiKey = null, CancellationToken ct = default)` (`DiffusionNexus.Civitai`, returns `DiffusionNexus.Civitai.Models.CivitaiModelVersion?`), `IUnifiedLogger`.
- Produces:

```csharp
/// <summary>Metadata resolved for one on-disk file outside the DB.</summary>
public sealed record ResolvedLoraMetadata(string? BaseModelRaw, int? CivitaiVersionId, string Sha256);

public sealed class SorterMetadataResolver
{
    /// <param name="cacheDirectory">Injected for tests; production uses
    /// Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    ///     "DiffusionNexus", "SorterCache").</param>
    /// <param name="hashFile">SHA256 (lowercase hex) of a file — injected for tests.</param>
    public SorterMetadataResolver(
        DiffusionNexus.Civitai.ICivitaiClient? civitaiClient,
        Func<Task<string?>> apiKeyProvider,
        string cacheDirectory,
        Func<string, string> hashFile,
        DiffusionNexus.Domain.Services.UnifiedLogging.IUnifiedLogger? logger);

    public static string DefaultCacheDirectory { get; } // LocalAppData\DiffusionNexus\SorterCache

    /// <summary>Resolution chain per spec §3: local .civitai.info sidecar → per-hash disk
    /// cache → Civitai by-hash API (result cached). Never throws for a 404/offline —
    /// returns metadata with null BaseModelRaw so the file sorts into Unknown.</summary>
    public Task<ResolvedLoraMetadata> ResolveAsync(string filePath, CancellationToken ct = default);
}
```

**Resolution algorithm (implement exactly):**
1. **Sidecar first (no hashing needed):** if `{stem}.civitai.info` exists next to the file, parse with `JsonDocument`: `baseModel` (string) and `id` (int) at the root. On success return `new ResolvedLoraMetadata(baseModel, id, Sha256: "")` — the hash is only needed for API/cache lookups, and an empty string keeps the planner's lazy hashing behavior intact (planner treats null/whitespace `Sha256` as "hash lazily"). Malformed JSON → log Warn, fall through.
2. `sha = hashFile(filePath)`.
3. **Cache:** `{cacheDirectory}\{sha}.json` — our own compact shape `{"baseModel":"SDXL 1.0","versionId":123}` (camelCase, nullable fields). Parse and return with `sha`. Malformed → delete the cache file, fall through.
4. **API:** if `_civitaiClient` is null → return `(null, null, sha)`. Else `var version = await _civitaiClient.GetModelVersionByHashAsync(sha, await apiKeyProvider(), ct)`. Null (404) → cache `{"baseModel":null,"versionId":null}` (negative cache so re-previews stay offline) and return `(null, null, sha)`. Non-null → write cache from `version.BaseModel` / `version.Id`, return them. Any `HttpRequestException`/`TaskCanceledException` from the client → log Warn, return `(null, null, sha)` WITHOUT writing the cache (transient — retry next preview).

> **Verify before coding:** open `DiffusionNexus.Civitai\Models\CivitaiModelVersion.cs` and confirm the property names used above (`BaseModel` — the API's `baseModel` field — and `Id`). They are referenced by `TypedCivitaiMetadataProvider.PopulateFromModelVersion` (`DiffusionNexus.Service\Services\TypedCivitaiMetadataProvider.cs:56-79`) — mirror whatever names that mapper uses. Category cannot come from the by-hash response (the API's version-level `model` object carries no tags), so arbitrary-folder files without a rich sidecar sort as `Unknown` category — this matches spec §3 step 4.

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.Lora.Sorting;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Sorter;

public sealed class SorterMetadataResolverTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dn-sortmeta-");
    private readonly Mock<ICivitaiClient> _client = new();

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch (IOException) { }
    }

    private string In(string name) => Path.Combine(_root.FullName, name);

    private string WriteModel(string name = "lora.safetensors")
    {
        var path = In(name);
        File.WriteAllText(path, "weights");
        return path;
    }

    private SorterMetadataResolver Resolver(ICivitaiClient? client = null, string sha = "abc123")
        => new(client, () => Task.FromResult<string?>(null), In("cache"), _ => sha, logger: null);

    [Fact]
    public async Task CivitaiInfoSidecarWinsWithoutTouchingHashOrApi()
    {
        var model = WriteModel();
        File.WriteAllText(In("lora.civitai.info"), """{"id": 555, "baseModel": "Illustrious"}""");
        var resolver = new SorterMetadataResolver(_client.Object, () => Task.FromResult<string?>(null),
            In("cache"), _ => throw new InvalidOperationException("must not hash"), logger: null);

        var meta = await resolver.ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("Illustrious");
        meta.CivitaiVersionId.Should().Be(555);
        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ApiResultIsReturnedAndCachedByHash()
    {
        var model = WriteModel();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 777, BaseModel = "SDXL 1.0" });

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.Should().Be(new ResolvedLoraMetadata("SDXL 1.0", 777, "abc123"));
        File.Exists(In(Path.Combine("cache", "abc123.json"))).Should().BeTrue();
    }

    [Fact]
    public async Task SecondResolveIsServedFromCacheWithoutApiCall()
    {
        var model = WriteModel();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 777, BaseModel = "SDXL 1.0" });
        var resolver = Resolver(_client.Object);

        await resolver.ResolveAsync(model);
        var second = await resolver.ResolveAsync(model);

        second.BaseModelRaw.Should().Be("SDXL 1.0");
        _client.Verify(c => c.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotFoundIsNegativelyCachedAndSortsAsUnknown()
    {
        var model = WriteModel();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CivitaiModelVersion?)null);
        var resolver = Resolver(_client.Object);

        var meta = await resolver.ResolveAsync(model);
        await resolver.ResolveAsync(model);

        meta.BaseModelRaw.Should().BeNull();
        _client.Verify(c => c.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoClientYieldsUnknownWithoutThrowing()
    {
        var model = WriteModel();

        var meta = await Resolver(client: null).ResolveAsync(model);

        meta.Should().Be(new ResolvedLoraMetadata(null, null, "abc123"));
    }
}
```

> If `CivitaiModelVersion` uses different property names or is not object-initializable, adjust the test construction to match the real DTO (this is the verification called out above) — the assertions stay the same.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~SorterMetadataResolverTests"`
Expected: build FAILURE — `SorterMetadataResolver` does not exist.

- [ ] **Step 3: Write the implementation** per the algorithm above. Cache DTO is a private nested record `CacheEntry(string? BaseModel, int? VersionId)` serialized camelCase.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~SorterMetadataResolverTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Services/Lora/Sorting/SorterMetadataResolver.cs DiffusionNexus.Tests/Sorter/SorterMetadataResolverTests.cs
git commit -m "feat(sorter): metadata resolver with sidecar, per-hash cache and Civitai by-hash fallback"
```

---

### Task 8: `LoraSorterViewModel` + preview tree node view model

**Files:**
- Create: `DiffusionNexus.UI\ViewModels\LoraSorterViewModel.cs`
- Create: `DiffusionNexus.UI\ViewModels\SortPreviewNodeViewModel.cs`
- Test: `DiffusionNexus.Tests\Sorter\LoraSorterViewModelTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–7; `IModelSyncService.LoadCachedFilesAsync(CancellationToken)` → `IReadOnlyList<InstalledModelFile>` where `InstalledModelFile(Model Model, ModelVersion Version, ModelFile File, string SourceRoot)`; `IAppSettingsService.GetEnabledLoraSourcesAsync` / `GetFavoriteLoraSourceAsync`; `BusyViewModelBase` (`IsBusy`, `BusyMessage`, `RunBusyAsync`, `DialogService`); `IDialogService.ShowConfirmAsync(title, message)` / `ShowOpenFolderDialogAsync(title)`; `DiskUtility.GetAvailableSpace` (production free-space func).
- Produces: `public partial class LoraSorterViewModel : BusyViewModelBase` — the type `LoraSorterView` binds to (Task 9) and `LoraViewerViewModel` constructs (Task 9).

**Constructor (testable — all I/O seams injected):**

```csharp
public LoraSorterViewModel(
    IAppSettingsService? settingsService,
    IModelSyncService? syncService,
    IUnifiedLogger? logger,
    ILocalPathUpdater pathUpdater,
    SorterMetadataResolver metadataResolver,
    IFileOperations fileOperations,
    Func<string, long> getAvailableSpace,   // production: DiskUtility.GetAvailableSpace
    Func<string, string> hashFile,          // production: SHA256 lowercase hex (see below)
    Func<string, bool> fileExistsOnDisk,    // production: File.Exists
    string historyDirectory)                // production: SortHistoryWriter.DefaultHistoryDirectory
```

Plus a parameterless design-time ctor (demo tree, no services) — required because `LoraViewerViewModel` has a design-time ctor that must also build a `SorterViewModel`.

Production hash helper (private static in the VM, same as the downloader's convention):

```csharp
private static string ComputeSha256(string filePath)
{
    using var stream = File.OpenRead(filePath);
    return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
}
```

**Observable state** (CommunityToolkit `[ObservableProperty]`):
`ObservableCollection<string> SourceFolders`, `string? SelectedSourceFolder`, `string? CustomTargetFolder` (null → "Same as source"), `bool IncludeCategory = true`, `bool IsMove = true`, `bool DeleteEmptySourceFolders`, `ObservableCollection<SortPreviewNodeViewModel> PreviewRoots`, `string? PreviewSummary` (the `✓/✎` footer line), `string? DiskSummary`, `bool HasEnoughSpace`, `string? BlockReason`, `string? StatusMessage`, `int TransferCount`.
`EffectiveTargetRoot => string.IsNullOrWhiteSpace(CustomTargetFolder) ? SelectedSourceFolder : CustomTargetFolder`.
`CanStart => !IsBusy && HasEnoughSpace && TransferCount > 0 && EffectiveTargetRoot is not null` (`StartSortingCommand` uses `[RelayCommand(CanExecute = nameof(CanStart))]`; notify via `partial void On...Changed` hooks).

**Commands:**
- `InitializeAsync()` — called from the view's `OnAttachedToVisualTree` (Task 9): loads `SourceFolders` (enabled sources; favorite preselected via `GetFavoriteLoraSourceAsync`, else first), then `RecomputePreviewAsync()`.
- `[RelayCommand] BrowseSourceAsync()` — `DialogService.ShowOpenFolderDialogAsync("Select folder to sort")`; non-null → add to `SourceFolders` (if absent) and select it.
- `[RelayCommand] BrowseTargetAsync()` / `[RelayCommand] ClearTargetOverride()`.
- `[RelayCommand] RecomputePreviewAsync()` — also triggered by `partial void OnIncludeCategoryChanged`, `OnIsMoveChanged`, `OnSelectedSourceFolderChanged`, `OnCustomTargetFolderChanged`.
- `[RelayCommand(CanExecute = nameof(CanStart))] StartSortingAsync()`.

**`RecomputePreviewAsync` (inside `RunBusyAsync`, message `"Computing preview…"`):**
1. Guard: `SelectedSourceFolder` null → clear preview, return.
2. Build candidates:
   - `var cached = _syncService is null ? [] : await _syncService.LoadCachedFilesAsync(ct);`
   - DB-known candidates: `cached` entries whose `File.LocalPath` starts with `SelectedSourceFolder` (OrdinalIgnoreCase) and whose `File.LocalPath` file exists → `new SortCandidate(f.File.LocalPath!, f.Version.BaseModelRaw, SorterCategoryResolver.ToFolderName(SorterCategoryResolver.ResolveForModel(f.Model)), f.Version.CivitaiId, f.File.HashSHA256, f.File.FileSizeBytes ?? new FileInfo(path).Length, SidecarLocator.FindSidecars(path))`. (`ModelFile.FileSizeBytes` — check its nullability in `DiffusionNexus.Domain\Entities\ModelFile.cs:27`; if it is non-nullable `long`, use `f.File.FileSizeBytes > 0 ? f.File.FileSizeBytes : new FileInfo(path).Length` instead of `??`.)
   - Unknown files (only when the selected source is NOT in the DB-known set, i.e. a browsed folder, or files under a registered source with no DB row): enumerate `Directory.EnumerateFiles(SelectedSourceFolder, "*", SearchOption.AllDirectories)` filtered to model extensions `[".safetensors", ".ckpt", ".pt", ".pth"]`, minus paths already covered by DB candidates (OrdinalIgnoreCase set) — for each, `await _metadataResolver.ResolveAsync(path, ct)` → candidate with resolved base model/version id, `CategoryFolderName = SorterCategoryResolver.ToFolderName(CivitaiCategory.Unknown)`. Report per-file progress into `BusyMessage` (`"Resolving metadata {i}/{n}…"`).
3. `_lastPlan = new LoraSortPlanner(_hashFile, _fileExistsOnDisk).BuildPlan(candidates, BuildOptions());` where `BuildOptions()` = `new LoraSortOptions(SelectedSourceFolder!, EffectiveTargetRoot!, IncludeCategory, IsMove, DeleteEmptySourceFolders)`.
4. Build tree: group `Transfer` + `AlreadyInPlace` moves by relative path segments of `TargetDirectory` under `EffectiveTargetRoot` → nested `SortPreviewNodeViewModel` with rolled-up counts/sizes (see below).
5. `PreviewSummary = $"✓ {plan.TransferCount} files will {(IsMove ? "move" : "copy")}   ·   {plan.AlreadyInPlaceCount} already in place   ·   ✎ {plan.RenamedCount} auto-renamed · {plan.SkippedDuplicateCount} duplicates skipped"`.
6. Disk gate: `free = _getAvailableSpace(EffectiveTargetRoot!)`; `HasEnoughSpace = free >= plan.RequiredBytes + SafetyMarginBytes` (`private const long SafetyMarginBytes = 1L << 30; // 1 GB`); `DiskSummary` shows required vs free (`FormatBytes` helper: B/KB/MB/GB with one decimal); `BlockReason = HasEnoughSpace ? null : "Not enough free space on the target drive."`.
7. Warnings (set `StatusMessage`): copy with `EffectiveTargetRoot` equal to `SelectedSourceFolder` → `HasEnoughSpace = false`, `BlockReason = "Copy into the source folder would duplicate every file — pick a different target."`; target inside a DIFFERENT enabled LoRA source → `StatusMessage = "⚠ Target is another LoRA source — colliding sources can lead to unpredictable outcomes (duplicate imports on the next scan)."`.
8. Log Info (`LogCategory.FileSystem`, `"LoraSorter"`): `"Preview: {TransferCount} transfers, {RenamedCount} renames, {SkippedDuplicateCount} duplicates"`.

**`StartSortingAsync`:**
1. `var confirmed = DialogService is null ? false : await DialogService.ShowConfirmAsync("Start sorting?", $"{plan.TransferCount} files will be {(IsMove ? "moved" : "copied")} into {EffectiveTargetRoot}.\n{plan.RenamedCount} will be renamed, {plan.SkippedDuplicateCount} duplicates skipped.\nTotal {FormatBytes(totalTransferBytes)}.");` — abort if false.
2. Inside `RunBusyAsync("Sorting LoRAs…")` with a `CancellationTokenSource` exposed through a `[RelayCommand] CancelSort()` (mirrors the Installed tab's busy-overlay Cancel):
   - Global status bar (spec §7.3): `var taskTracker = App.Services?.GetService<ITaskTracker>();` then `using var taskHandle = taskTracker?.BeginTask("Sorting LoRAs", LogCategory.FileSystem);` and link cancellation: `using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_sortCts.Token, taskHandle?.CancellationToken ?? CancellationToken.None);` (same pattern as `LoraDownloadService.cs:61-77`). `App.Services` is null under tests, so the handle is simply absent there.
   - `var executor = new LoraSortExecutor(_fileOperations, _pathUpdater, new SortHistoryWriter(_historyDirectory), _logger);`
   - `var result = await executor.ExecuteAsync(_lastPlan!, progress, linkedCts.Token);` — progress updates `BusyMessage` (`"{fileName} ({percent}%)"`) and forwards to `taskHandle?.ReportProgress(fraction, fileName)`.
   - On finish: `taskHandle?.Complete(...)`, or `taskHandle?.Fail(ex, ...)` if the run threw.
   - Move mode + `DeleteEmptySourceFolders` and not cancelled: `await new DiskUtility().DeleteEmptyDirectoriesAsync(SelectedSourceFolder!, CancellationToken.None);`
3. `StatusMessage = result.Cancelled ? $"Cancelled — {result.Moved + result.Copied} done, rest untouched." : $"Done: {result.Moved + result.Copied} sorted, {result.Skipped} duplicates skipped, {result.Failed} failed.";` then `await RecomputePreviewAsync()` (refreshes tree; everything should now be AlreadyInPlace) and raise `SortCompleted` event (`public event EventHandler? SortCompleted;`) so the parent can refresh the Installed tab.

**`SortPreviewNodeViewModel`** (plain, no base class needed):

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>One folder (or leaf file) in the LoRA Sorter's "Folder structure preview" tree.</summary>
public partial class SortPreviewNodeViewModel : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public int LoraCount { get; set; }
    public long TotalBytes { get; set; }
    public bool IsFile { get; init; }
    /// <summary>Dimmed in the view: file already at its computed destination.</summary>
    public bool IsAlreadyInPlace { get; init; }
    /// <summary>Shown with the ✎ marker: file arrives under a collision rename.</summary>
    public bool IsRenamed { get; init; }
    public ObservableCollection<SortPreviewNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    public string CountAndSizeDisplay => IsFile ? FormatBytes(TotalBytes)
        : $"{LoraCount} LoRAs · {FormatBytes(TotalBytes)}";

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B"
    };
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services.Lora.Sorting;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Sorter;

public sealed class LoraSorterViewModelTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dn-sortervm-");
    private readonly Mock<IAppSettingsService> _settings = new();
    private readonly Mock<IModelSyncService> _sync = new();

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch (IOException) { }
    }

    private string SourceRoot => Path.Combine(_root.FullName, "Loras");

    private string WriteLora(string relative)
    {
        var path = Path.Combine(SourceRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "weights");
        return path;
    }

    private static InstalledModelFile Installed(string path, string baseModel, string tag)
    {
        var model = new Model { Tags = { new ModelTag { Tag = new Tag { Name = tag } } } };
        var version = new ModelVersion { BaseModelRaw = baseModel };
        var file = new ModelFile { LocalPath = path };
        return new InstalledModelFile(model, version, file, Path.GetDirectoryName(path)!);
    }

    private LoraSorterViewModel CreateVm(long freeSpace = long.MaxValue,
        IReadOnlyList<InstalledModelFile>? cached = null)
    {
        _settings.Setup(s => s.GetEnabledLoraSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([SourceRoot]);
        _settings.Setup(s => s.GetFavoriteLoraSourceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _sync.Setup(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached ?? []);

        return new LoraSorterViewModel(
            _settings.Object, _sync.Object, logger: null,
            pathUpdater: Mock.Of<ILocalPathUpdater>(),
            metadataResolver: new SorterMetadataResolver(null, () => Task.FromResult<string?>(null),
                Path.Combine(_root.FullName, "cache"), _ => "hash", logger: null),
            fileOperations: new FileOperations(),
            getAvailableSpace: _ => freeSpace,
            hashFile: _ => "hash",
            fileExistsOnDisk: File.Exists,
            historyDirectory: Path.Combine(_root.FullName, "history"));
    }

    [Fact]
    public async Task PreviewGroupsCachedFilesByBaseModelAndCategory()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var b = WriteLora(@"flat\b.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(a, "SDXL 1.0", "character"),
            Installed(b, "Illustrious", "style"),
        ]);

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(2);
        var rootNames = vm.PreviewRoots.Select(n => n.Name);
        rootNames.Should().Contain(["SDXL 1.0", "Illustrious"]);
        vm.PreviewRoots.First(n => n.Name == "SDXL 1.0")
            .Children.Select(c => c.Name).Should().Contain("Character");
    }

    [Fact]
    public async Task BaseModelOnlyModeFlattensCategoryLevel()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();

        vm.IncludeCategory = false;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.PreviewRoots.First(n => n.Name == "SDXL 1.0")
            .Children.Where(c => !c.IsFile).Should().BeEmpty();
    }

    [Fact]
    public async Task InsufficientDiskSpaceBlocksStartWithReason()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(freeSpace: 0, cached: [Installed(a, "SDXL 1.0", "character")]);
        vm.IsMove = false; // copy → RequiredBytes > 0, and 0 free < margin
        vm.CustomTargetFolder = Path.Combine(_root.FullName, "Elsewhere");

        await vm.InitializeAsync();

        vm.HasEnoughSpace.Should().BeFalse();
        vm.BlockReason.Should().NotBeNull();
        vm.StartSortingCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task CopyIntoSourceRootIsBlocked()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();

        vm.IsMove = false; // target still "same as source"
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.StartSortingCommand.CanExecute(null).Should().BeFalse();
        vm.BlockReason.Should().Contain("source");
    }

    [Fact]
    public async Task UnknownFileInBrowsedFolderIsResolvedIntoUnknownBuckets()
    {
        WriteLora(@"flat\mystery.safetensors"); // no DB row, no sidecar, no client → Unknown
        var vm = CreateVm(cached: []);

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(1);
        vm.PreviewRoots.Single().Name.Should().Be("Unknown");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~LoraSorterViewModelTests"`
Expected: build FAILURE — `LoraSorterViewModel` does not exist.

- [ ] **Step 3: Write `SortPreviewNodeViewModel.cs`** (code above), then **`LoraSorterViewModel.cs`** implementing the state/commands/algorithms specified in the Interfaces block. Tree building helper (private): walk each non-skipped move's `TargetDirectory`, strip the `EffectiveTargetRoot` prefix, split on `Path.DirectorySeparatorChar`, materialize/reuse child nodes level by level, add a leaf file node per move (`IsFile = true`, `IsAlreadyInPlace`/`IsRenamed` from the move), and roll `LoraCount`/`TotalBytes` up every ancestor. Root nodes are the first-level folders (base models), sorted by `TotalBytes` descending.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~LoraSorterViewModelTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/ViewModels/LoraSorterViewModel.cs DiffusionNexus.UI/ViewModels/SortPreviewNodeViewModel.cs DiffusionNexus.Tests/Sorter/LoraSorterViewModelTests.cs
git commit -m "feat(sorter): sorter view model with live preview, disk gate and guarded start"
```

---


### Task 9: `LoraSorterView` + tab wiring into the LoRA Viewer

**Files:**
- Create: `DiffusionNexus.UI\Views\LoraSorterView.axaml`
- Create: `DiffusionNexus.UI\Views\LoraSorterView.axaml.cs`
- Modify: `DiffusionNexus.UI\ViewModels\LoraViewerViewModel.cs` (both constructors + one property, around lines 382-447)
- Modify: `DiffusionNexus.UI\Views\LoraViewerView.axaml` (after the Browse Civitai `TabItem`, lines 469-471 of the current file)

**Interfaces:**
- Consumes: `LoraSorterViewModel` (Task 8: `InitializeAsync()`, `SortCompleted` event, all bindable members), `DbLocalPathUpdater` (Task 6), `SorterMetadataResolver`/`SortHistoryWriter` defaults (Tasks 5/7), `DiskUtility.GetAvailableSpace`.
- Produces: the visible tab. No new DI registrations — the child VM is constructed by `LoraViewerViewModel` exactly like `BrowserViewModel` (services pulled from `App.Services` in the runtime ctor).

- [ ] **Step 1: Wire the child view model into `LoraViewerViewModel`**

In the **design-time ctor** (after `BrowserViewModel = new CivitaiBrowserViewModel();`):

```csharp
SorterViewModel = new LoraSorterViewModel();
```

In the **runtime ctor** (after the `BrowserViewModel = ...` assignment):

```csharp
// LoRA Sorter sub-tab. Same DB-backed source of truth as the Installed tab;
// disk seams are the production implementations.
var scopeFactory = App.Services?.GetService<IServiceScopeFactory>();
SorterViewModel = scopeFactory is null
    ? new LoraSorterViewModel()
    : new LoraSorterViewModel(
        _settingsService, _syncService, _logger,
        new DbLocalPathUpdater(scopeFactory),
        new SorterMetadataResolver(_civitaiClient, GetApiKeyForSorterAsync,
            SorterMetadataResolver.DefaultCacheDirectory, ComputeFullSha256, _logger),
        new FileOperations(),
        DiskUtility.GetAvailableSpace,
        ComputeFullSha256,
        File.Exists,
        SortHistoryWriter.DefaultHistoryDirectory);
SorterViewModel.SortCompleted += (_, _) => _ = RefreshAsync();
```

Property (next to `BrowserViewModel`):

```csharp
/// <summary>View model for the "LoRA Sorter" sub-tab.</summary>
public LoraSorterViewModel SorterViewModel { get; }
```

Notes for the implementer:
- `ComputeFullSha256(string)` already exists in `LoraViewerViewModel` (used at line ~2484 for the by-hash metadata download) — reuse it; it must return lowercase hex (verify; wrap with `.ToLowerInvariant()` if it doesn't).
- `GetApiKeyForSorterAsync` — add a tiny private method delegating to the same secure-storage lookup the metadata-download flow uses (find the `apiKey` retrieval near the `GetModelVersionByHashAsync` call site around line 2486 and extract/reuse it as `private Task<string?> GetApiKeyForSorterAsync()`).
- Usings to add: `DiffusionNexus.UI.Services.Lora.Sorting`, `DiffusionNexus.UI.Utilities`, `DiffusionNexus.Service.Services.IO`, `Microsoft.Extensions.DependencyInjection`.

- [ ] **Step 2: Add the TabItem to `LoraViewerView.axaml`**

After the closing `</TabItem>` of "Browse Civitai" (line 471), inside the same `TabControl`:

```xml
    <TabItem Header="LoRA Sorter">
      <views:LoraSorterView DataContext="{Binding SorterViewModel}"/>
    </TabItem>
```

Add the namespace on the root element if not present: `xmlns:views="using:DiffusionNexus.UI.Views"`.

- [ ] **Step 3: Create `LoraSorterView.axaml`**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:DiffusionNexus.UI.ViewModels"
             x:Class="DiffusionNexus.UI.Views.LoraSorterView"
             x:DataType="vm:LoraSorterViewModel">

  <Grid RowDefinitions="Auto,*,Auto">

    <!-- Headline -->
    <Border Grid.Row="0" Background="#1E1E1E" Padding="16,12" BorderBrush="#333" BorderThickness="0,0,0,1">
      <StackPanel Spacing="2">
        <TextBlock Text="Sort your LoRAs" FontSize="18" FontWeight="Bold"/>
        <TextBlock Text="Reorganize your LoRA library into clean folders by base model and category."
                   FontSize="12" Opacity="0.6"/>
      </StackPanel>
    </Border>

    <Grid Grid.Row="1" ColumnDefinitions="300,*">

      <!-- Options rail -->
      <Border Grid.Column="0" Background="#1E1E1E" BorderBrush="#333" BorderThickness="0,0,1,0" Padding="16,14">
        <StackPanel Spacing="14">

          <StackPanel Spacing="4">
            <TextBlock Classes="fieldLabel" Text="SOURCE FOLDER" FontSize="11" Opacity="0.6"/>
            <ComboBox ItemsSource="{Binding SourceFolders}"
                      SelectedItem="{Binding SelectedSourceFolder}"
                      HorizontalAlignment="Stretch"/>
            <Button Content="Browse any folder…" Command="{Binding BrowseSourceCommand}"
                    HorizontalAlignment="Stretch" FontSize="12"/>
          </StackPanel>

          <StackPanel Spacing="4">
            <TextBlock Classes="fieldLabel" Text="TARGET FOLDER" FontSize="11" Opacity="0.6"/>
            <Grid ColumnDefinitions="*,Auto">
              <TextBlock Grid.Column="0" VerticalAlignment="Center" FontSize="12" TextTrimming="CharacterEllipsis">
                <TextBlock.Text>
                  <Binding Path="CustomTargetFolder" TargetNullValue="Same as source"/>
                </TextBlock.Text>
              </TextBlock>
              <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="4">
                <Button Content="…" Command="{Binding BrowseTargetCommand}"/>
                <Button Content="✕" Command="{Binding ClearTargetOverrideCommand}"
                        IsVisible="{Binding CustomTargetFolder, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>
              </StackPanel>
            </Grid>
          </StackPanel>

          <StackPanel Spacing="4">
            <TextBlock Classes="fieldLabel" Text="FOLDER STRUCTURE" FontSize="11" Opacity="0.6"/>
            <RadioButton GroupName="Structure" Content="Base model only"
                         IsChecked="{Binding !IncludeCategory}"/>
            <RadioButton GroupName="Structure" Content="Base model + category"
                         IsChecked="{Binding IncludeCategory}"/>
          </StackPanel>

          <StackPanel Spacing="4">
            <TextBlock Classes="fieldLabel" Text="OPERATION" FontSize="11" Opacity="0.6"/>
            <RadioButton GroupName="Operation" Content="Move (reorganize)" IsChecked="{Binding IsMove}"/>
            <RadioButton GroupName="Operation" Content="Copy (duplicate)" IsChecked="{Binding !IsMove}"/>
          </StackPanel>

          <Border Background="#2A2A2A" BorderBrush="#444" BorderThickness="1" CornerRadius="4"
                  Padding="8,6" IsVisible="{Binding IsMove}">
            <TextBlock Text="⚠ Move rearranges your files on disk — the old folder structure cannot be restored automatically."
                       Foreground="#FFA726" FontSize="11.5" TextWrapping="Wrap"/>
          </Border>

          <CheckBox Content="Delete empty source folders"
                    IsChecked="{Binding DeleteEmptySourceFolders}"
                    IsEnabled="{Binding IsMove}" FontSize="12"/>

          <Border Background="#252526" BorderBrush="#333" BorderThickness="1" CornerRadius="4" Padding="8,6">
            <StackPanel Spacing="2">
              <TextBlock Text="{Binding DiskSummary}" FontSize="12"/>
              <TextBlock Text="{Binding BlockReason}" Foreground="#FF6B6B" FontSize="11.5"
                         TextWrapping="Wrap"
                         IsVisible="{Binding BlockReason, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>
            </StackPanel>
          </Border>

          <Button Command="{Binding StartSortingCommand}"
                  HorizontalAlignment="Stretch" HorizontalContentAlignment="Center"
                  Background="#2D7D46" Foreground="White" FontWeight="SemiBold" Padding="0,8">
            <StackPanel Orientation="Horizontal" Spacing="6">
              <TextBlock Text="▶"/>
              <TextBlock Text="{Binding TransferCount, StringFormat='Start Sorting ({0} files)'}"/>
            </StackPanel>
          </Button>

        </StackPanel>
      </Border>

      <!-- Folder structure preview -->
      <Grid Grid.Column="1" RowDefinitions="Auto,*,Auto">
        <TextBlock Grid.Row="0" Text="FOLDER STRUCTURE PREVIEW" FontSize="11" Opacity="0.6" Margin="18,14,18,6"/>
        <ScrollViewer Grid.Row="1" Padding="18,0" HorizontalScrollBarVisibility="Auto">
          <ItemsControl ItemsSource="{Binding PreviewRoots}"/>
        </ScrollViewer>
        <TextBlock Grid.Row="2" Text="{Binding PreviewSummary}" Margin="18,8,18,12" FontSize="12" Opacity="0.85"/>
      </Grid>
    </Grid>

    <!-- Status bar -->
    <Border Grid.Row="2" Background="#1E1E1E" Padding="8,4" BorderBrush="#333" BorderThickness="0,1,0,0"
            IsVisible="{Binding StatusMessage, Converter={x:Static StringConverters.IsNotNullOrEmpty}}">
      <TextBlock Text="{Binding StatusMessage}" Opacity="0.8" FontSize="12"/>
    </Border>

    <!-- Busy overlay (same convention as the Installed tab) -->
    <Border Grid.Row="0" Grid.RowSpan="3" Background="#CC000000" IsVisible="{Binding IsBusy}">
      <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center" Spacing="12">
        <ProgressBar IsIndeterminate="True" Width="300"/>
        <TextBlock Text="{Binding BusyMessage}" HorizontalAlignment="Center"/>
        <Button Content="Cancel" Command="{Binding CancelSortCommand}" HorizontalAlignment="Center"/>
      </StackPanel>
    </Border>

  </Grid>
</UserControl>
```

**Preview-tree template:** recursion without `TreeView` works by declaring the node template ONCE in `<UserControl.DataTemplates>` — Avalonia resolves `DataTemplates` by data type, so the inner `ItemsControl` for `Children` picks up the same template implicitly. Add this directly under the root `<UserControl ...>` element (before the `<Grid>`):

```xml
  <UserControl.Styles>
    <Style Selector="TextBlock.dimmed"><Setter Property="Opacity" Value="0.45"/></Style>
    <Style Selector="TextBlock.renamed"><Setter Property="Foreground" Value="#FFA726"/></Style>
  </UserControl.Styles>

  <UserControl.DataTemplates>
    <DataTemplate DataType="vm:SortPreviewNodeViewModel">
      <StackPanel>
        <Grid ColumnDefinitions="Auto,*,Auto" Margin="0,1">
          <ToggleButton Grid.Column="0" IsChecked="{Binding IsExpanded}"
                        Content="▸" Padding="2,0" Background="Transparent" BorderThickness="0"
                        IsVisible="{Binding !IsFile}"/>
          <TextBlock Grid.Column="1" Text="{Binding Name}"
                     FontFamily="Consolas,monospace" FontSize="12" Margin="4,0,12,0"
                     Classes.dimmed="{Binding IsAlreadyInPlace}"
                     Classes.renamed="{Binding IsRenamed}"/>
          <TextBlock Grid.Column="2" Text="{Binding CountAndSizeDisplay}" FontSize="11" Opacity="0.5"/>
        </Grid>
        <ItemsControl ItemsSource="{Binding Children}" Margin="18,0,0,0"
                      IsVisible="{Binding IsExpanded}"/>
      </StackPanel>
    </DataTemplate>
  </UserControl.DataTemplates>
```

(`Classes.classname="{Binding ...}"` is Avalonia's binding-controlled style class syntax.)

- [ ] **Step 4: Create `LoraSorterView.axaml.cs`**

```csharp
using Avalonia.Interactivity;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

public partial class LoraSorterView : ViewBase
{
    private bool _initialized;

    public LoraSorterView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (_initialized || DataContext is not LoraSorterViewModel vm) return;
        _initialized = true;
        _ = vm.InitializeAsync();
    }
}
```

> Check `Views\ViewBase.cs` first: it injects `IDialogService` into `IDialogServiceAware` DataContexts on attach (the pattern every other view here uses). If `ViewBase` is generic or named differently, mirror whatever `CivitaiBrowserView.axaml.cs` does.

- [ ] **Step 5: Build and run the full sorter test namespace**

Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -c Release` then
`dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~DiffusionNexus.Tests.Sorter"`
Expected: build clean (XAML compiles — `x:DataType` errors surface here), all sorter tests PASS.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/Views/LoraSorterView.axaml DiffusionNexus.UI/Views/LoraSorterView.axaml.cs DiffusionNexus.UI/Views/LoraViewerView.axaml DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs
git commit -m "feat(sorter): LoRA Sorter tab UI wired into the LoRA Viewer"
```

---

### Task 10: Full-suite verification + documentation

**Files:**
- Modify: `DiffusionNexus.UI\Doc\LoraViewer.md` (add a "LoRA Sorter tab" section)

- [ ] **Step 1: Run the complete test project**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release`
Expected: PASS, zero failures, zero regressions in pre-existing tests (baseline before this feature: run once on the branch point if unsure).

- [ ] **Step 2: Document the tab**

Append to `DiffusionNexus.UI\Doc\LoraViewer.md` a section covering: what the LoRA Sorter tab does (spec §1 summary), the collision policy (deterministic version-id rename / duplicate skip, no overwrite), the sort-history manifest location (`%LocalAppData%\DiffusionNexus\SortHistory\`), the metadata cache (`%LocalAppData%\DiffusionNexus\SorterCache\`), and a pointer to `docs/superpowers/specs/2026-08-20-lora-sorter-design.md`.

- [ ] **Step 3: Commit**

```bash
git add DiffusionNexus.UI/Doc/LoraViewer.md
git commit -m "docs: document the LoRA Sorter tab in the LoRA Viewer doc"
```

- [ ] **Step 4: Manual GUI smoke (owed — record results before the PR)**

1. Launch the app (Debug is fine), open LoRA Viewer → LoRA Sorter tab.
2. Point at a scratch folder containing a handful of real LoRAs (+ `.civitai.info`/preview sidecars), verify the preview tree, counts, and disk summary.
3. Run a **move** with category structure — verify files + sidecars land, DB stays consistent (Installed tab still shows the models, paths updated), manifest written under `%LocalAppData%\DiffusionNexus\SortHistory\`.
4. Run a **copy** to a separate folder — source untouched, DB untouched.
5. Cancel mid-run on a larger set — completed files stay, statusbar reports partial.
6. Browse an unregistered folder with an un-synced LoRA — resolves via Civitai (or lands in Unknown offline).
7. Re-run the same sort — everything reports "already in place", zero transfers.

**Do not push or open a PR from this plan** — when all tasks are complete, hand off to the superpowers:finishing-a-development-branch skill (per project rules: verify `feature/lora-sorter` isn't merged, PR targets `develop`).

