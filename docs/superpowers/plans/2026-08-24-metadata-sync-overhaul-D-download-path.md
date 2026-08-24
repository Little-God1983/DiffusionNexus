# Metadata Sync Overhaul — Plan D: One Civitai Download Path (WP5) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every Civitai download path (Download LoRA dialog, Browse queue, Detail panel, waitlist→queue, Pipelines installer) goes through one `ICivitaiModelDownloader` with one target-path builder, one collision policy, one persister, one SHA verification, and leaves the model complete (metadata + tags + poster thumbnail) with the Installed tab notified.

**Architecture:** `ICivitaiModelDownloader` (UI Services) wraps the existing `LoraDownloadService` transport and persister. Path building moves to Service as `LoraPathBuilder` (today's `SorterPathBuilder`, gaining an `includeBaseModel` switch); the Browse queue's proven collision algorithm moves to Service as `DownloadCollisionPolicy`. The downloader owns the `IDownloadCoordinator` enqueue (callers never wrap it), fires the new `ILibraryChangeNotifier` after persist, and runs a `Tags + Thumbnails` completion sync via `ILibrarySyncService.ForModels(id)`. The `ModelDetailViewModel` inline clone (~500 lines) and the four caller-side coordinator/TCS copies are deleted.

**Tech Stack:** .NET 10, Avalonia, CommunityToolkit.Mvvm, EF Core 10 SQLite, xUnit + FluentAssertions + Moq.

**Spec:** `docs/superpowers/specs/2026-08-21-metadata-sync-overhaul-design.md` — §4.4 (One Civitai download path), safety contract §3 (S4, S5), decision D3. This plan is WP5. D2 (hash-case migration) already shipped in Plan A — do NOT re-implement it.

## Global Constraints

- Tests run with `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release` — never solution-level.
- **S4 (spec verbatim): no existing model file is overwritten by any download path.** Collision policy everywhere: `{stem}_{versionId}{ext}`, identical content → reuse. The only file the transfer may replace is one whose SHA256 equals the download's expected hash, or a `{stem}_{versionId}`-suffixed file (version-unique, so it can only be this version's earlier bytes).
- **S5 (spec verbatim): `IsUserEdited` is honored by every writer** (tags, images, base model, category). Mirror `CivitaiMetadataApplier`'s rule: `Model.IsUserEdited` / `ModelVersion.IsUserEdited` block user-editable text and tags; Civitai *linkage* fields (`CivitaiId`, `CivitaiModelPageId`, `Source`, `LastSyncedAt`) are identity, not user text, and stay writable.
- **D3 (decided): the queue's `MaxConcurrency` stays submit-parallelism; `IDownloadCoordinator` is the single global gate.** After this plan, ONLY `CivitaiModelDownloader` enqueues into the coordinator. No caller may also wrap `DownloadAsync` in `coordinator.EnqueueAsync` — that would double-enqueue (today the queue and the pipeline installer each wrap; both wraps are removed when they migrate).
- New/modified constructor parameters follow the codebase convention for optional service dependencies: `IFoo? foo = null`, trailing, never required — these ViewModels/services are constructed both via DI and via `new` in tests/design-time code.
- Standing rule: every new component logs its working steps to the Unified Console (`Info` start/end, `Debug` per step, failures one `Warn` line each — stack traces only at `Debug`).
- Preserve each file's existing line-ending style (this repo mixes CRLF and LF per file). Verify at byte level when scripting edits.
- Do not touch `LoraUpdateChecker`. Do not write sidecar files anywhere except `PipelineAssetInstaller`'s existing `.civitai.info` (it keeps writing it itself, after the downloader returns).
- Doc comments in moved code (e.g. `SorterPathBuilder`'s) are load-bearing narrative — move them with the code, do not strip them.

---

## File Structure

**Create**
- `DiffusionNexus.Civitai/Models/CivitaiVersionFiles.cs` — `PickPrimary` (the one "pick primary file").
- `DiffusionNexus.Domain/Enums/FileFormatMapper.cs` — extension → `FileFormat` (the one mapping).
- `DiffusionNexus.Service/Services/Lora/LoraPathBuilder.cs` — moved `SorterPathBuilder` + `includeBaseModel` overload.
- `DiffusionNexus.Service/Services/Lora/DownloadCollisionPolicy.cs` — moved queue collision algorithm + content-match reuse.
- `DiffusionNexus.Domain/Services/ICivitaiApiKeyProvider.cs` + `DiffusionNexus.Infrastructure/Services/CivitaiApiKeyProvider.cs`.
- `DiffusionNexus.Domain/Services/ILibraryChangeNotifier.cs` + `DiffusionNexus.Infrastructure/Services/LibraryChangeNotifier.cs`.
- `DiffusionNexus.UI/Services/Download/ICivitaiModelDownloader.cs` (interface + records) + `DiffusionNexus.UI/Services/Download/CivitaiModelDownloader.cs`.
- `DiffusionNexus.UI/Services/ILoraDownloadService.cs` — interface extracted from `LoraDownloadService`.

**Modify (main)**
- `DiffusionNexus.UI/Services/LoraDownloadService.cs` — implements `ILoraDownloadService`; S5 guards; hashing/pick-primary/file-format routed; optional `IServiceScopeFactory` seam.
- `DiffusionNexus.UI/Services/CivitaiBrowser/CivitaiDownloadQueue.cs` — `RunJobAsync` calls the downloader; collision/SHA code deleted.
- `DiffusionNexus.UI/ViewModels/ModelDetailViewModel.cs` — inline downloader + persister clone deleted; downloads via `ICivitaiModelDownloader`; category inference via `SorterCategoryResolver`.
- `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs` — toolbar download via downloader; subscribes `ILibraryChangeNotifier` (coalesced rebuild); detail `DownloadCompleted` plumbing removed.
- `DiffusionNexus.UI/ViewModels/DownloadDestinationViewModel.cs`, `DownloadLoraDialogViewModel.cs`, `DownloadLoraVersionDialogViewModel.cs` — path assembly delegates to `LoraPathBuilder`; `FormatFileSize` clones deleted.
- `DiffusionNexus.UI/Services/Pipelines/PipelineAssetInstaller.cs` — downloads via `ICivitaiModelDownloader` (own coordinator wrap removed).
- `DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiBrowserViewModel.cs` — card-handler bug; api-key provider.
- `DiffusionNexus.UI/App.axaml.cs` — DI registrations.
- `DiffusionNexus.Service/Services/DuplicateScanner.cs`, `DiffusionNexus.Service/Services/JsonInfoFileReaderService.cs` — riders: `.sft`-blind scans routed through `ModelFileExtensions.Sortable`.

**Delete**
- `DiffusionNexus.UI/Services/Lora/Sorting/SorterPathBuilder.cs` (moved to Service).
- `ModelDetailViewModel` members: legacy inline HTTP fallback in `DownloadFileAsync`, `PersistDownloadedModelAsync` clone, `CleanupTempFile`, `ParseBaseModel`, `GetFileFormat`, `InferCategoryFromTags`, `DownloadCompleted` event.
- `CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync`, `ComputeSha256Async`.
- Three `FormatFileSize` copies, four `GetApiKeyAsync` bodies, `LoraDownloadService.ComputeSha256`.

---

### Task 1: Shared primitives — pick-primary, file-format, file-size, hashing

**Files:**
- Create: `DiffusionNexus.Civitai/Models/CivitaiVersionFiles.cs`
- Create: `DiffusionNexus.Domain/Enums/FileFormatMapper.cs`
- Modify: `DiffusionNexus.UI/Helpers/FileSizeFormatter.cs` (add `FormatKilobytes`)
- Modify: `DiffusionNexus.UI/Services/LoraDownloadService.cs`, `DiffusionNexus.UI/Services/CivitaiBrowser/CivitaiDownloadQueue.cs`, `DiffusionNexus.UI/ViewModels/ModelDetailViewModel.cs`, `DiffusionNexus.UI/ViewModels/DownloadLoraDialogViewModel.cs`, `DiffusionNexus.UI/ViewModels/DownloadLoraVersionDialogViewModel.cs`, `DiffusionNexus.UI/Views/Dialogs/SelectLoraVersionsToDeleteDialog.axaml.cs`, `DiffusionNexus.UI/Services/Pipelines/PipelineAssetInstaller.cs`, `DiffusionNexus.Service/Services/ModelFileSyncService.cs`
- Test: `DiffusionNexus.Tests/Civitai/CivitaiVersionFilesTests.cs` (create), `DiffusionNexus.Tests/Domain/FileFormatMapperTests.cs` (create), `DiffusionNexus.Tests/Helpers/FileSizeFormatterTests.cs` (extend or create)

**Interfaces:**
- Produces: `CivitaiVersionFiles.PickPrimary(CivitaiModelVersion? version)` → `CivitaiModelFile?`; `CivitaiVersionFiles.PickPrimary(CivitaiModelVersion best, CivitaiModelVersion fallback)` → `CivitaiModelFile?` (the 4-level fallback); `FileFormatMapper.FromExtension(string extension)` → `FileFormat`; `FileSizeFormatter.FormatKilobytes(double sizeKb)` → `string`. Consumed by Tasks 2, 5, 6, 7.

- [ ] **Step 1: Write the failing tests**

```csharp
// DiffusionNexus.Tests/Civitai/CivitaiVersionFilesTests.cs
using DiffusionNexus.Civitai.Models;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Civitai;

public class CivitaiVersionFilesTests
{
    private static CivitaiModelFile File(string name, bool? primary) => new() { Name = name, Primary = primary };

    [Fact]
    public void PickPrimary_PrefersThePrimaryFlaggedFile()
    {
        var version = new CivitaiModelVersion { Files = [File("a", false), File("b", true)] };
        CivitaiVersionFiles.PickPrimary(version)!.Name.Should().Be("b");
    }

    [Fact]
    public void PickPrimary_FallsBackToTheFirstFile()
    {
        var version = new CivitaiModelVersion { Files = [File("a", null), File("b", false)] };
        CivitaiVersionFiles.PickPrimary(version)!.Name.Should().Be("a");
    }

    [Fact]
    public void PickPrimary_NullVersionOrNoFilesIsNull()
    {
        CivitaiVersionFiles.PickPrimary((CivitaiModelVersion?)null).Should().BeNull();
        CivitaiVersionFiles.PickPrimary(new CivitaiModelVersion()).Should().BeNull();
    }

    [Fact]
    public void PickPrimary_TwoVersionFallbackWalksAllFourRungs()
    {
        // best has no files at all -> fall through to the original version's primary,
        // exactly LoraDownloadService's 4-level chain (best primary, best first,
        // fallback primary, fallback first).
        var best = new CivitaiModelVersion();
        var fallback = new CivitaiModelVersion { Files = [File("x", false), File("y", true)] };
        CivitaiVersionFiles.PickPrimary(best, fallback)!.Name.Should().Be("y");
    }
}
```

```csharp
// DiffusionNexus.Tests/Domain/FileFormatMapperTests.cs
using DiffusionNexus.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Domain;

public class FileFormatMapperTests
{
    [Theory]
    [InlineData(".safetensors", FileFormat.SafeTensor)]
    [InlineData(".SAFETENSORS", FileFormat.SafeTensor)]
    [InlineData(".pt", FileFormat.PickleTensor)]
    [InlineData(".pth", FileFormat.PickleTensor)]
    [InlineData(".ckpt", FileFormat.Other)]
    [InlineData(".gguf", FileFormat.Unknown)]
    [InlineData("", FileFormat.Unknown)]
    public void FromExtension_MatchesTheThreeFormerCopies(string extension, FileFormat expected)
        => FileFormatMapper.FromExtension(extension).Should().Be(expected);
}
```

FileSizeFormatter test: `FormatKilobytes(0)` and negatives → `"Unknown"`; `FormatKilobytes(500)` → `"500 KB"`-style; `FormatKilobytes(1_258_291)` renders GB through the shared `Format` (F2 — the consolidated standard, see `SortPreviewNodeViewModel` doc comment). Assert by delegating expectation: `FileSizeFormatter.FormatKilobytes(2048).Should().Be(FileSizeFormatter.Format(2048L * 1024))`.

- [ ] **Step 2: Run the tests, confirm they fail** (types don't exist).

- [ ] **Step 3: Implement**

```csharp
// DiffusionNexus.Civitai/Models/CivitaiVersionFiles.cs
namespace DiffusionNexus.Civitai.Models;

/// <summary>
/// The one "pick the primary file" rule. Eight call sites carried private copies
/// (spec §1 RC5); they all route here so a future change to the preference cannot
/// diverge per path.
/// </summary>
public static class CivitaiVersionFiles
{
    /// <summary>Primary-flagged file, else the first file, else null.</summary>
    public static CivitaiModelFile? PickPrimary(CivitaiModelVersion? version)
        => version?.Files.FirstOrDefault(f => f.Primary == true) ?? version?.Files.FirstOrDefault();

    /// <summary>
    /// LoraDownloadService's 4-level chain: the richer version's primary/first file,
    /// falling back to the originally supplied version's primary/first file.
    /// </summary>
    public static CivitaiModelFile? PickPrimary(CivitaiModelVersion best, CivitaiModelVersion fallback)
        => PickPrimary(best) ?? PickPrimary(fallback);
}
```

```csharp
// DiffusionNexus.Domain/Enums/FileFormatMapper.cs
namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// Extension → <see cref="FileFormat"/>. Was copied verbatim in LoraDownloadService,
/// ModelDetailViewModel and ModelFileSyncService; single implementation now.
/// </summary>
public static class FileFormatMapper
{
    public static FileFormat FromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".safetensors" => FileFormat.SafeTensor,
        ".pt" => FileFormat.PickleTensor,
        ".ckpt" => FileFormat.Other,
        ".pth" => FileFormat.PickleTensor,
        _ => FileFormat.Unknown
    };
}
```

`FileSizeFormatter.FormatKilobytes(double sizeKb)`: `sizeKb <= 0 → "Unknown"`, else `Format((long)(sizeKb * 1024))`. (Display delta accepted: the old clones rendered GB with `:F1`; the shared formatter uses `:F2`, which is the standard the sorter already consolidated onto.)

- [ ] **Step 4: Route the copies** (mechanical; no behavior change beyond the named F1→F2 delta):
  - `GetFileFormat` bodies in `LoraDownloadService`, `ModelDetailViewModel`, `ModelFileSyncService` → delegate to `FileFormatMapper.FromExtension` (keep thin private wrappers only if a rename would churn many lines; prefer inlining the call and deleting the wrapper).
  - `FormatFileSize` in `DownloadLoraDialogViewModel`, `DownloadLoraVersionDialogViewModel`, `SelectLoraVersionsToDeleteDialog.axaml.cs` → `FileSizeFormatter.FormatKilobytes`.
  - Pick-primary call sites → `CivitaiVersionFiles.PickPrimary`: `LoraDownloadService.PersistDownloadedModelAsync` (the 4-level chain → two-version overload), `LoraDownloadService.DownloadFileAsync`'s callers stay as-is for now; `CivitaiDownloadQueue.Enqueue` + `EnqueueFromWaitlist`; `DownloadLoraDialogViewModel.SearchAsync`; `DownloadLoraVersionDialogViewModel.Initialize`; `ModelDetailViewModel.DownloadSelectedVersionAsync` + `OnVersionTabSelected`; `PipelineAssetInstaller.InstallCivitaiLoraAsync`.
  - SHA256: `LoraDownloadService.ComputeSha256` → `FileHasher.Sha256Upper` (delete the private); `CivitaiDownloadQueue.ComputeSha256Async` → `FileHasher.Sha256UpperAsync` (keep the queue's method as a one-line delegate for now — it is deleted with the queue migration in Task 7). Comparisons stay `OrdinalIgnoreCase`.

- [ ] **Step 5: Run the new tests + full suite** — expect green (`dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release`).

- [ ] **Step 6: Commit** — `refactor(downloads): one pick-primary, one file-format map, one size formatter, one hasher`

---

### Task 2: `LoraPathBuilder` moves to Service; `DownloadCollisionPolicy` is born

**Files:**
- Create: `DiffusionNexus.Service/Services/Lora/LoraPathBuilder.cs`, `DiffusionNexus.Service/Services/Lora/DownloadCollisionPolicy.cs`
- Delete: `DiffusionNexus.UI/Services/Lora/Sorting/SorterPathBuilder.cs`
- Modify: `DiffusionNexus.UI/Services/Lora/Sorting/LoraSortPlanner.cs`, `SorterMetadataResolver.cs`, `DiffusionNexus.UI/ViewModels/LoraSorterViewModel.cs` (reference updates only), `DiffusionNexus.UI/ViewModels/DownloadDestinationViewModel.cs`, `DownloadLoraDialogViewModel.cs`, `DownloadLoraVersionDialogViewModel.cs`, `DiffusionNexus.UI/Services/CivitaiBrowser/CivitaiDownloadQueue.cs` (collision method delegates)
- Test: rename `DiffusionNexus.Tests/Sorter/SorterPathBuilderTests.cs` → `DiffusionNexus.Tests/Service/Lora/LoraPathBuilderTests.cs`; retarget `DiffusionNexus.Tests/Viewer/CivitaiDownloadTargetCollisionTests.cs` at the policy; extend both; update `LoraSorterViewModelTests.cs` / `SorterMetadataResolverTests.cs` references.

**Interfaces:**
- Produces (namespace `DiffusionNexus.Service.Services.Lora`):
  - `LoraPathBuilder.UnknownFolderName` (`"Unknown"`), `IsPlaceholderBaseModel(string?)`, `SanitizeFolderName(string)`, `IsUnresolvedCategory(string?)`, `EnumerateCandidateNames(string fileName, int? civitaiVersionId)` — all verbatim from `SorterPathBuilder`, doc comments included.
  - `LoraPathBuilder.BuildTargetDirectory(string targetRoot, string? baseModelRaw, string? categoryFolderName, bool includeCategory)` — existing sorter signature, delegates to the 5-arg form with `includeBaseModel: true`.
  - `LoraPathBuilder.BuildTargetDirectory(string targetRoot, string? baseModelRaw, string? categoryFolderName, bool includeBaseModel, bool includeCategory)` — new: when `includeBaseModel` is false the base-model segment (and the Unknown fallback) is skipped entirely.
  - `DownloadCollisionPolicy.ResolveAsync(string targetDir, string fileName, int versionId, string? expectedSha256, CancellationToken ct)` → `Task<CollisionResolution>`; `record CollisionResolution(string TargetPath, bool ExistingContentMatches)`.
- Consumes: `SyncStateDeriver.IsPlaceholder` (Service), `FileHasher.Sha256UpperAsync` (Service), `LoraPathBuilder.EnumerateCandidateNames`.

- [ ] **Step 1: Move `SorterPathBuilder` → `LoraPathBuilder`.** New file in Service, class renamed, namespace `DiffusionNexus.Service.Services.Lora`, every doc comment preserved (update the class doc's "CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync" reference to `DownloadCollisionPolicy`). Add the 5-arg `BuildTargetDirectory`:

```csharp
public static string BuildTargetDirectory(
    string targetRoot, string? baseModelRaw, string? categoryFolderName,
    bool includeBaseModel, bool includeCategory)
{
    var path = targetRoot;
    if (includeBaseModel)
    {
        var baseFolder = IsPlaceholderBaseModel(baseModelRaw)
            ? UnknownFolderName
            : SanitizeFolderName(baseModelRaw!);
        path = Path.Combine(path, baseFolder);
    }
    if (includeCategory && !IsUnresolvedCategory(categoryFolderName))
        path = Path.Combine(path, SanitizeFolderName(categoryFolderName!));
    return path;
}
```

Delete `SorterPathBuilder.cs`; update the three sorter consumers + three test files (name + using only — sorter behavior is unchanged, its call sites hit the 4-arg overload). Rename/relocate `SorterPathBuilderTests` accordingly.

- [ ] **Step 2: Write failing tests for the new pieces**

```csharp
// LoraPathBuilderTests — appended
[Fact]
public void BuildTargetDirectory_WithoutBaseModelSegment_SkipsUnknownToo()
    => LoraPathBuilder.BuildTargetDirectory(@"C:\root", null, "Style", includeBaseModel: false, includeCategory: true)
        .Should().Be(@"C:\root\Style");

[Fact]
public void BuildTargetDirectory_DownloadShape_SanitizesTheSegments()
    => LoraPathBuilder.BuildTargetDirectory(@"C:\root", "SD 3.5?", "Chara<cter", includeBaseModel: true, includeCategory: true)
        .Should().Be(@"C:\root\SD 3.5_\Chara_cter");
```

```csharp
// DiffusionNexus.Tests/Service/Lora/DownloadCollisionPolicyTests.cs (retarget/extend the
// former CivitaiDownloadTargetCollisionTests — keep its existing scenarios, they are the contract)
[Fact]
public async Task ResolveAsync_PlainNameFree_UsesIt() { /* temp dir, no file -> (plain, false) */ }

[Fact]
public async Task ResolveAsync_PlainNameHoldsIdenticalContent_ReusesWithoutSuffix()
{
    // write file, expectedSha = FileHasher.Sha256Upper(file) -> (plain, ExistingContentMatches: true)
}

[Fact]
public async Task ResolveAsync_PlainNameHoldsDifferentContent_AppendsVersionId()
{
    // write file with other bytes -> (stem_123.safetensors, false)
}

[Fact]
public async Task ResolveAsync_SuffixedNameAlreadyHoldsIdenticalContent_ReusesIt()
{
    // plain = foreign bytes, stem_123 = matching bytes -> (stem_123, true)
}

[Fact]
public async Task ResolveAsync_NoExpectedHash_CannotProveOwnership_SoItSuffixes() { /* (stem_123, false) */ }
```

- [ ] **Step 3: Run — fail.** (Policy type missing; new overload missing.)

- [ ] **Step 4: Implement `DownloadCollisionPolicy`** — the queue's algorithm, generalized:

```csharp
namespace DiffusionNexus.Service.Services.Lora;

/// <summary>Where a download may land: the resolved path, and whether a file already
/// there is byte-identical to what would be downloaded (caller may skip the transfer).</summary>
public sealed record CollisionResolution(string TargetPath, bool ExistingContentMatches);

/// <summary>
/// The one collision policy for every Civitai download path (spec §4.4, S4). Moved from
/// CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync: Civitai file names are frequently
/// generic ("V1.safetensors"), so two unrelated models routed to the same folder collide — the
/// second download used to replace the first model's weights. When an existing file's SHA256
/// matches the expected hash it IS this download and is reused; otherwise the Civitai version id
/// is appended ({stem}_{versionId}, LoraPathBuilder.EnumerateCandidateNames convention) — unique
/// per version and stable across retries, so a suffixed target that already exists can only be
/// this same version's earlier bytes.
/// </summary>
public static class DownloadCollisionPolicy
{
    public static async Task<CollisionResolution> ResolveAsync(
        string targetDir, string fileName, int versionId, string? expectedSha256, CancellationToken ct)
    {
        var plain = Path.Combine(targetDir, fileName);
        if (!File.Exists(plain)) return new CollisionResolution(plain, false);

        if (await MatchesAsync(plain, expectedSha256, ct).ConfigureAwait(false))
            return new CollisionResolution(plain, true);

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var suffixed = Path.Combine(targetDir, $"{stem}_{versionId}{extension}");
        var suffixedMatches = File.Exists(suffixed)
            && await MatchesAsync(suffixed, expectedSha256, ct).ConfigureAwait(false);
        return new CollisionResolution(suffixed, suffixedMatches);
    }

    private static async Task<bool> MatchesAsync(string path, string? expectedSha256, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) return false;
        try
        {
            var actual = await FileHasher.Sha256UpperAsync(path, ct).ConfigureAwait(false);
            return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false; // unreadable/locked — can't prove it's ours, so don't overwrite it
        }
    }
}
```

`CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync` becomes a one-line delegate returning `resolution.TargetPath` (full deletion happens in Task 7 with the queue migration).

- [ ] **Step 5: Delegate the three picker path builders.**
  - `DownloadDestinationViewModel.BuildTargetDirectory`:

```csharp
public string? BuildTargetDirectory(string? baseModel, string? category)
{
    if (IsDownloadToFolder)
        return string.IsNullOrWhiteSpace(CustomFolderPath) ? null : CustomFolderPath;
    if (string.IsNullOrWhiteSpace(SelectedSourceFolder)) return null;
    return LoraPathBuilder.BuildTargetDirectory(
        SelectedSourceFolder, baseModel, category,
        includeBaseModel: CreateBaseModelFolder, includeCategory: CreateCategoryFolder);
}
```

  - `DownloadLoraDialogViewModel`: `GetTargetFolder()` and the `PreviewPath` getter both collapse to one private helper calling the same `LoraPathBuilder` overload with `ResolvedVersion?.BaseModel` / `Category` and the two toggles (custom-folder branch unchanged).
  - `DownloadLoraVersionDialogViewModel`: same, with `BaseModel` / `Category`.

  **Named behavioral deltas (spec-mandated, write a test for each on `DownloadDestinationViewModel`):**
  1. Base-model toggle ON + blank/`"???"` base model → the file now lands in `Unknown\` (previously the segment was silently skipped).
  2. Folder names are sanitized (`SD 3.5?` → `SD 3.5_`).
  3. A category literally named `Unknown` no longer creates a segment (`IsUnresolvedCategory`), matching the sorter — this is the drift §4.4 exists to kill.

- [ ] **Step 6: Run the full suite** — sorter tests must be green untouched apart from renames; new tests green.

- [ ] **Step 7: Commit** — `refactor(downloads): LoraPathBuilder + DownloadCollisionPolicy move to Service, pickers delegate`

---

### Task 3: `ICivitaiApiKeyProvider` — one key lookup

**Files:**
- Create: `DiffusionNexus.Domain/Services/ICivitaiApiKeyProvider.cs`, `DiffusionNexus.Infrastructure/Services/CivitaiApiKeyProvider.cs`
- Modify: `DiffusionNexus.UI/App.axaml.cs` (register singleton), `DiffusionNexus.UI/Services/LoraDownloadService.cs`, `DiffusionNexus.UI/ViewModels/DownloadLoraDialogViewModel.cs`, `DiffusionNexus.UI/ViewModels/ModelDetailViewModel.cs`, `DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiBrowserViewModel.cs`, `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs` (`GetApiKeyForSorterAsync` → provider)
- Test: `DiffusionNexus.Tests/Infrastructure/CivitaiApiKeyProviderTests.cs` (create)

**Interfaces:**
- Produces:

```csharp
namespace DiffusionNexus.Domain.Services;

/// <summary>
/// The one Civitai API-key lookup. Five verbatim copies existed (spec §1 RC5); each opened a
/// fresh DI scope because a long-lived IAppSettingsService can hold a stale cached AppSettings
/// entity loaded before the key was saved — that rationale moves here with the code.
/// </summary>
public interface ICivitaiApiKeyProvider
{
    Task<string?> GetApiKeyAsync(CancellationToken ct = default);
}
```

- Implementation `CivitaiApiKeyProvider(IServiceScopeFactory? scopeFactory, IAppSettingsService? fallbackSettings = null)`: fresh scope → `IAppSettingsService.GetCivitaiApiKeyAsync()`; null scope factory → `fallbackSettings` → `null`. Registered `services.AddSingleton<ICivitaiApiKeyProvider>(sp => new CivitaiApiKeyProvider(sp.GetRequiredService<IServiceScopeFactory>()))`.

- [ ] **Step 1: Failing tests** — with a `ServiceCollection`-built scope factory whose scoped `IAppSettingsService` fake returns `"key-from-scope"`, the provider returns it; with null factory and a fake fallback, returns the fallback's key; with neither, null.
- [ ] **Step 2: Run — fail.**
- [ ] **Step 3: Implement + register.**
- [ ] **Step 4: Route the five copies.** Each consumer gains a trailing optional `ICivitaiApiKeyProvider? apiKeyProvider = null` ctor parameter and keeps behavior when constructed without DI by building one locally:
  - `LoraDownloadService`: field `_apiKeyProvider`; `GetApiKeyAsync()` body → `(_apiKeyProvider ?? new CivitaiApiKeyProvider(App.Services?.GetService<IServiceScopeFactory>(), _settingsService)).GetApiKeyAsync()` — cache the constructed fallback in the field on first use so it isn't re-allocated per call.
  - `DownloadLoraDialogViewModel`, `CivitaiBrowserViewModel`: same shape (their old bodies were the App.Services-scope + `_settingsService` fallback — exactly what the provider encapsulates).
  - `ModelDetailViewModel`: old body used `_scopeFactory` only → `new CivitaiApiKeyProvider(_scopeFactory)` fallback.
  - `LoraViewerViewModel.GetApiKeyForSorterAsync`: delegate to the provider (the `SorterMetadataResolver` keeps its memoizing wrapper — it already takes a delegate; only the delegate's source changes).
  - DI construction sites in `App.axaml.cs` pass `sp.GetRequiredService<ICivitaiApiKeyProvider>()`.
  - Delete the four private `GetApiKeyAsync` bodies (they become one-line delegations or vanish).
- [ ] **Step 5: Full suite green.**
- [ ] **Step 6: Commit** — `refactor(downloads): one Civitai API-key provider replaces five copies`

---

### Task 4: The persister honors `IsUserEdited` (S5) + `ILoraDownloadService` seam

**Files:**
- Create: `DiffusionNexus.UI/Services/ILoraDownloadService.cs`
- Modify: `DiffusionNexus.UI/Services/LoraDownloadService.cs`
- Test: `DiffusionNexus.Tests/Services/LoraDownloadServicePersistTests.cs` (create)

**Interfaces:**
- Produces:

```csharp
namespace DiffusionNexus.UI.Services;

/// <summary>Transport + persister seam so CivitaiModelDownloader (Task 5) is unit-testable.</summary>
public interface ILoraDownloadService
{
    Task DownloadFileAsync(
        string downloadUrl, string targetPath, CivitaiModelVersion civitaiVersion, string taskName,
        Action<double, string>? reportProgress = null, Action? completed = null, Action? failed = null,
        int? existingModelId = null, CancellationToken externalCancellationToken = default,
        bool reportToActivityLog = true, Action? metadataIncomplete = null);

    Task<MetadataPersistOutcome> PersistDownloadedModelAsync(
        string filePath, CivitaiModelVersion civitaiVersion, int? existingModelId = null);
}
```

`LoraDownloadService` implements it (class stays `sealed`). New trailing ctor param `IServiceScopeFactory? scopeFactory = null`; `PersistDownloadedModelAsync` resolves `_scopeFactory ?? App.Services?.GetService<IServiceScopeFactory>()` — the seam that makes the persister testable against an in-memory SQLite container (reuse the DI/UoW fixture pattern from `DiffusionNexus.Tests/Sync`). Register in DI: `services.AddScoped<ILoraDownloadService>(sp => sp.GetRequiredService<LoraDownloadService>());` (concrete registration stays until Task 7 migrates the last concrete consumer).

- [ ] **Step 1: Failing tests — the S5 matrix** (in-memory SQLite, seeded existing model, fake `civitaiModel` data reached by passing a version whose `ModelId` is 0 and `_civitaiClient` null → persister uses only the supplied version; for the model-level guards seed the model and call with `existingModelId`):

```csharp
[Fact]
public async Task Persist_ExistingUserEditedModel_KeepsNameDescriptionAndTags()
{
    // seed: Model { IsUserEdited = true, Name = "My name", Description = "Mine", Tags = [hand] }
    // act:  PersistDownloadedModelAsync(newFile, version, existingModelId: model.Id)
    // assert: Name/Description unchanged, hand tag still present,
    //         CivitaiModelPageId/LastSyncedAt DID update (linkage is not user text)
}

[Fact]
public async Task Persist_ExistingUneditedModel_TakesCivitaiText() { /* control row */ }

[Fact]
public async Task Persist_DuplicateVersionUserEdited_KeepsItsBaseModel()
{
    // seed: version with matching CivitaiId, IsUserEdited = true, BaseModelRaw = "Pony" (hand-fixed)
    // assert: BaseModelRaw still "Pony"; the new ModelFile row was still attached (files are facts)
}
```

- [ ] **Step 2: Run — fail** (persister currently overwrites unconditionally).
- [ ] **Step 3: Implement the guards** in `PersistDownloadedModelAsync`, mirroring `CivitaiMetadataApplier` (`CanWriteModelText` = `!Model.IsUserEdited`, `CanWriteVersionText` = `!ModelVersion.IsUserEdited`):
  - Existing-model enrich block: wrap `model.Name`, `model.Description ??=`, `model.IsNsfw`, `model.IsPoi`, the three licence flags, and the Creator update in `if (!model.IsUserEdited)`. `CivitaiId`/`CivitaiModelPageId` backfill, `Source`, `LastSyncedAt` stay unconditional (linkage).
  - Tag block: `if (civitaiModel?.Tags is { Count: > 0 } tags && !model.IsUserEdited)` — never `Tags.Clear()` on a user-edited model.
  - Duplicate-version backfill block: the `Name/Description/BaseModel/BaseModelRaw/DownloadUrl/PublishedAt/EarlyAccessDays/DownloadCount` writes gain `&& !duplicateVersion.IsUserEdited`; the `CivitaiId` linkage backfill itself stays (guarded by the existing uniqueness check only). File attachment and stale-file invalidation are facts about disk, not user text — untouched.
  - Add one `Debug` log line when edits are preserved (standing rule).
- [ ] **Step 4: Run tests + full suite — green.**
- [ ] **Step 5: Commit** — `fix(downloads): persister honors IsUserEdited (S5); ILoraDownloadService seam`

---

### Task 5: `ILibraryChangeNotifier` + `ICivitaiModelDownloader` — the one path

**Files:**
- Create: `DiffusionNexus.Domain/Services/ILibraryChangeNotifier.cs`, `DiffusionNexus.Infrastructure/Services/LibraryChangeNotifier.cs`, `DiffusionNexus.UI/Services/Download/ICivitaiModelDownloader.cs`, `DiffusionNexus.UI/Services/Download/CivitaiModelDownloader.cs`
- Modify: `DiffusionNexus.UI/App.axaml.cs` (register both)
- Test: `DiffusionNexus.Tests/Infrastructure/LibraryChangeNotifierTests.cs`, `DiffusionNexus.Tests/Services/CivitaiModelDownloaderTests.cs` (create)

**Interfaces:**
- Produces:

```csharp
// DiffusionNexus.Domain/Services/ILibraryChangeNotifier.cs
namespace DiffusionNexus.Domain.Services;

public sealed class ModelDownloadedEventArgs(int modelId) : EventArgs
{
    public int ModelId { get; } = modelId;
}

/// <summary>
/// Cross-module "the library gained a model" signal. Raised by the one download path after
/// persist, subscribed by LoraViewerViewModel — fixes the Browse queue never notifying the
/// Installed tab (spec RC5) and replaces the detail panel's ad-hoc DownloadCompleted event.
/// Events are raised on the caller's thread; subscribers marshal to the UI thread themselves.
/// </summary>
public interface ILibraryChangeNotifier
{
    event EventHandler<ModelDownloadedEventArgs>? ModelDownloaded;
    void NotifyModelDownloaded(int modelId);
}
```

```csharp
// DiffusionNexus.UI/Services/Download/ICivitaiModelDownloader.cs
namespace DiffusionNexus.UI.Services.Download;

public enum DownloadTrigger { Dialog, BrowseQueue, DetailPanel, Waitlist, Pipeline }

public sealed record DownloadRequest(
    CivitaiModelVersion Version,
    string TargetDirectory,          // resolved by the caller's picker (LoraPathBuilder-backed)
    DownloadTrigger Trigger)
{
    public CivitaiModelFile? File { get; init; }        // null -> CivitaiVersionFiles.PickPrimary(Version)
    public int? ExistingModelId { get; init; }
    public string? FileNameOverride { get; init; }      // null -> File?.Name -> "{model}_{versionId}.safetensors"
    public string? TaskName { get; init; }              // null -> "Download {fileName}"
}

public sealed record DownloadProgress(int Percent, string Message);

public enum DownloadStatus
{
    Completed,                    // transferred, persisted with metadata
    CompletedMetadataIncomplete,  // transferred, model-page metadata unavailable ("Done — no metadata")
    ReusedExisting,               // byte-identical file already on disk; no transfer
    HashMismatch,                 // transferred but SHA256 != expected; file left for inspection
    Failed,
    Cancelled
}

public sealed record DownloadOutcome(
    DownloadStatus Status, string? FinalPath, int? ModelId, bool RenamedForCollision, string? Error)
{
    public bool Success => Status
        is DownloadStatus.Completed or DownloadStatus.CompletedMetadataIncomplete or DownloadStatus.ReusedExisting;
}

/// <summary>
/// The one Civitai download path (spec §4.4). Owns: file pick, collision policy, the
/// IDownloadCoordinator enqueue (callers must NOT wrap this call in the coordinator — D3),
/// SHA256 verification, persistence, the Tags+Thumbnails completion sync, and the
/// ILibraryChangeNotifier signal.
/// </summary>
public interface ICivitaiModelDownloader
{
    Task<DownloadOutcome> DownloadAsync(
        DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
}
```

- Implementation ctor: `CivitaiModelDownloader(ILoraDownloadService? downloadService, IDownloadCoordinator? coordinator = null, ILibrarySyncService? librarySync = null, ILibraryChangeNotifier? notifier = null, IServiceScopeFactory? scopeFactory = null, IUnifiedLogger? logger = null)`.
- DI: `services.AddSingleton<ILibraryChangeNotifier, LibraryChangeNotifier>();` and `services.AddScoped<ICivitaiModelDownloader>(sp => new CivitaiModelDownloader(sp.GetRequiredService<ILoraDownloadService>(), sp.GetService<IDownloadCoordinator>(), sp.GetService<Domain.Services.Sync.ILibrarySyncService>(), sp.GetService<ILibraryChangeNotifier>(), sp.GetService<IServiceScopeFactory>(), sp.GetService<Domain.Services.UnifiedLogging.IUnifiedLogger>()));`

**`DownloadAsync` flow (implement exactly this order):**
1. `file = request.File ?? CivitaiVersionFiles.PickPrimary(request.Version)`; `url = file?.DownloadUrl ?? request.Version.DownloadUrl`; no url → `Failed` ("no download URL").
2. `fileName = request.FileNameOverride ?? file?.Name ?? $"model_{request.Version.Id}.safetensors"`; `taskName = request.TaskName ?? $"Download {fileName}"`.
3. `Directory.CreateDirectory(request.TargetDirectory)`.
4. `resolution = await DownloadCollisionPolicy.ResolveAsync(dir, fileName, request.Version.Id, file?.Hashes?.SHA256, ct)`. `renamed = !string.Equals(Path.GetFileName(resolution.TargetPath), fileName, OrdinalIgnoreCase)` → one `Warn` line (the queue's existing wording).
5. **Reuse short-circuit:** if `resolution.ExistingContentMatches` → skip the transfer, `await _downloadService.PersistDownloadedModelAsync(resolution.TargetPath, request.Version, request.ExistingModelId)` (idempotent — ensures the DB row exists even when the bytes predate the DB), status `ReusedExisting`, continue at step 8.
6. **Transfer** — the ONE coordinator/TCS wrap (this kills the four caller-side copies):

```csharp
var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var metadataComplete = true;

async Task<bool> RunAsync(IProgress<DownloadTaskProgress>? coordinatorProgress, CancellationToken runCt)
{
    await _downloadService!.DownloadFileAsync(
        url, resolution.TargetPath, request.Version, taskName,
        reportProgress: (pct, msg) =>
        {
            progress?.Report(new DownloadProgress((int)(pct * 100), msg));
            coordinatorProgress?.Report(new DownloadTaskProgress((int)(pct * 100), msg));
        },
        completed: () => tcs.TrySetResult(true),
        failed: () => tcs.TrySetResult(false),
        existingModelId: request.ExistingModelId,
        externalCancellationToken: runCt,
        reportToActivityLog: _coordinator is null,
        metadataIncomplete: () => metadataComplete = false).ConfigureAwait(false);
    return await tcs.Task.ConfigureAwait(false);
}

var ok = _coordinator is not null
    ? await _coordinator.EnqueueAsync(taskName, RunAsync, ct).ConfigureAwait(false)
    : await RunAsync(null, ct).ConfigureAwait(false);
```

   `!ok && ct.IsCancellationRequested` → `Cancelled`; `!ok` → `Failed` (transport already logged the cause).
7. **Verify** (skip for `ReusedExisting`): when `file?.Hashes?.SHA256` present and the file exists — `FileHasher.Sha256UpperAsync`; mismatch → `Warn` + status `HashMismatch` (file left on disk, queue parity). Hash-compute exceptions → `Warn`, not fatal (queue parity).
8. **Resolve ModelId:** fresh scope → `IUnitOfWork.Models.FindByLocalFilePathAsync(resolution.TargetPath)`; null-tolerant.
9. **Completion sync** (modelId resolved, `_librarySync` present): inside a private `SemaphoreSlim(1,1)` (queue jobs finish concurrently; the sync service is single-flight) — skip with a `Debug` line when `_librarySync.IsRunning`; else `PlanAsync(SyncScope.ForModels(modelId), new SyncOptions(new HashSet<SyncStepKind> { SyncStepKind.FetchTags, SyncStepKind.Thumbnails }))` → `ExecuteAsync(plan)`; catch ALL exceptions → one `Warn` ("post-download completion skipped: …") — a failed completion must never fail a succeeded download.
10. **Notify:** `_notifier?.NotifyModelDownloaded(modelId.Value)` whenever an id was resolved — even when the completion sync was skipped.
11. Return the outcome. `Info` log at start and end with status (standing rule); each numbered step at `Debug`.

- [ ] **Step 1: Failing tests.** `LibraryChangeNotifierTests`: subscribe → `NotifyModelDownloaded(5)` → args carry 5; no subscriber → no throw. `CivitaiModelDownloaderTests` with `Mock<ILoraDownloadService>` (its `DownloadFileAsync` invokes the `completed` callback; `PersistDownloadedModelAsync` returns `Complete`), `Mock<ILibrarySyncService>`, real `LibraryChangeNotifier`, temp dirs:
  - happy path → `Completed`, final path = plain name, notifier fired once, sync planned with `ForModels` + exactly `{FetchTags, Thumbnails}` (assert via the captured `SyncScope`/`SyncOptions`);
  - collision with foreign bytes on disk → final path `{stem}_{versionId}`, `RenamedForCollision` true, foreign file untouched (S4);
  - byte-identical file on disk → `ReusedExisting`, transport's `DownloadFileAsync` NEVER called, persister called once;
  - `metadataIncomplete` callback fired → `CompletedMetadataIncomplete`;
  - transport reports `failed` → `Failed`, notifier NOT fired;
  - expected hash mismatches written bytes (mock `DownloadFileAsync` writes wrong bytes then calls `completed`) → `HashMismatch`;
  - `librarySync.IsRunning == true` → `ExecuteAsync` never called, outcome still `Completed`, notifier still fired;
  - no coordinator → `RunAsync(null, ct)` path works (transport told `reportToActivityLog: true`).
  ModelId resolution: with `scopeFactory` null the downloader must still succeed with `ModelId = null` and skip completion+notify — add that test; the id-resolved path is covered indirectly in caller-migration testing (building a full UoW container here is optional, allowed if the Sync fixtures make it cheap).
- [ ] **Step 2: Run — fail.**
- [ ] **Step 3: Implement both classes + DI.** `LibraryChangeNotifier`: trivial, thread-safe via the event's own delegate immutability.
- [ ] **Step 4: Run tests + full suite — green.**
- [ ] **Step 5: Commit** — `feat(downloads): ICivitaiModelDownloader — one path with collision policy, verify, completion, notify`

---

### Task 6: Detail panel + toolbar dialog migrate; category-inference fix; viewer subscribes

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/ModelDetailViewModel.cs`, `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs`, `DiffusionNexus.UI/App.axaml.cs` (pass downloader/notifier into the viewer + detail construction)
- Test: `DiffusionNexus.Tests/ViewModels/ModelDetailViewModelCategoryTests.cs` (create), extend existing viewer/detail tests as they break

**Interfaces:**
- Consumes: `ICivitaiModelDownloader`, `ILibraryChangeNotifier`, `SorterCategoryResolver.Resolve/ToFolderName` (Tasks 2/5 + existing).
- Produces: `ModelDetailViewModel` ctor gains trailing `ICivitaiModelDownloader? modelDownloader = null`; `LoraViewerViewModel` ctor gains trailing `ICivitaiModelDownloader? modelDownloader = null, ILibraryChangeNotifier? changeNotifier = null`. The `ModelDetailViewModel.DownloadCompleted` event is DELETED — later tasks/consumers must use the notifier.

- [ ] **Step 1: Failing test — the "2000" category bug** (spec §4.4: the detail clone lacks the `LooksLikeCategoryName` guard):

```csharp
// ModelDetailViewModelCategoryTests — drive PopulateTags via LoadAsync-adjacent seam or make
// the inference helper internal for the test, mirroring SorterCategoryResolverTests rows:
// tag "2000"            -> no category (numeric enum parse must not produce one)
// tag "5"               -> no category
// tag "character,style" -> no category (flags-style comma parse)
// tag "Character"       -> "Character"
// Model.UserCategory = CivitaiCategory.Style -> "Style" wins over tags
// Model.UserCategory = CivitaiCategory.BaseModel -> "Base Model" display form
```

- [ ] **Step 2: Run — the "2000"/"5"/comma rows fail** (today's `Enum.TryParse` accepts them).
- [ ] **Step 3: Fix category inference** — delete `ModelDetailViewModel.InferCategoryFromTags` and the manual user-override branch in `PopulateTags`; replace with:

```csharp
var category = Services.Lora.Sorting.SorterCategoryResolver.Resolve(
    model?.UserCategory, model?.Tags.Select(t => t.Tag?.Name) ?? []);
CategoryDisplay = category == Domain.Enums.CivitaiCategory.Unknown
    ? string.Empty
    : Services.Lora.Sorting.SorterCategoryResolver.ToFolderName(category);
HasCategory = category != Domain.Enums.CivitaiCategory.Unknown;
```

- [ ] **Step 4: Migrate the detail download.** In `DownloadSelectedVersionAsync`, after the destination dialog:

```csharp
tab.IsDownloading = true;
_ = Task.Run(async () =>
{
    try
    {
        if (_modelDownloader is null)
        {
            _logger?.Warn(LogCategory.Download, "LoraDownload",
                "Download unavailable: ICivitaiModelDownloader not provided.");
            return;
        }
        var request = new DownloadRequest(tab.CivitaiVersion, result.TargetFolder!, DownloadTrigger.DetailPanel)
        {
            File = primaryFile,
            ExistingModelId = SourceTile?.ModelEntity?.Id,
        };
        var outcome = await _modelDownloader.DownloadAsync(request).ConfigureAwait(false);
        if (outcome.Success && outcome.FinalPath is not null)
            await _uiScheduler.InvokeAsync(() => _ = RefreshAfterDownloadAsync(outcome.FinalPath));
    }
    finally
    {
        await _uiScheduler.InvokeAsync(() => tab.IsDownloading = false);
    }
});
```

  Then DELETE from `ModelDetailViewModel`: the whole private `DownloadFileAsync` (both branches — the service path is now the downloader's job, the legacy inline HTTP fallback dies with it), the `PersistDownloadedModelAsync` clone, `CleanupTempFile`, `ParseBaseModel`, `GetFileFormat`, the `DownloadCompleted` event and its invocation in `RefreshAfterDownloadAsync` (keep the rest of `RefreshAfterDownloadAsync` — the in-place tile refresh is a UI nicety the notifier doesn't cover). Remove now-unused usings/fields (`_downloadService`, `_downloadCoordinator`, `_taskTracker`, `_activityLog` if orphaned — check each for other uses first).
- [ ] **Step 5: Migrate the toolbar dialog flow.** `LoraViewerViewModel.DownloadLoraAsync`: replace the `LoraDownloadService` + coordinator/TCS block with one `DownloadAsync` call — `DownloadRequest(result.Version, result.TargetFolder!, DownloadTrigger.Dialog) { FileNameOverride = fileName }`; `SyncStatus` updates from the `IProgress<DownloadProgress>` and the outcome (`Downloaded {file}` / `Download failed: {file}`). No manual `RebuildTilesFromDatabaseAsync` here — the notifier handles it (next step).
- [ ] **Step 6: Viewer subscribes to the notifier, coalesced.** In `LoraViewerViewModel`: subscribe `changeNotifier.ModelDownloaded` (unsubscribe where the detail handlers are cleaned up today). Handler must (a) marshal via `Dispatcher.UIThread.Post`, (b) coalesce — a 20-job queue batch must NOT trigger 20 full rebuilds:

```csharp
private bool _rebuildQueued;

private void OnLibraryModelDownloaded(object? sender, ModelDownloadedEventArgs e)
{
    Dispatcher.UIThread.Post(async () =>
    {
        if (_rebuildQueued) return;          // one rebuild covers every arrival during the delay
        _rebuildQueued = true;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            await RebuildTilesFromDatabaseAsync();
        }
        finally { _rebuildQueued = false; }
    });
}
```

  Delete `OnDetailDownloadCompleted` and every `DetailViewModel.DownloadCompleted +=/-=` line. Wire the new ctor params through the `App.axaml.cs` construction lambda.
- [ ] **Step 7: Build + full suite green.** Fix compile fallout in tests that constructed the deleted members.
- [ ] **Step 8: Commit** — `refactor(downloads): detail panel + toolbar dialog on the one path; ~550 lines of clone deleted; notifier drives the Installed tab`

---

### Task 7: Queue + waitlist + pipeline migrate; card-handler bug

**Files:**
- Modify: `DiffusionNexus.UI/Services/CivitaiBrowser/CivitaiDownloadQueue.cs`, `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs` (queue construction), `DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiBrowserViewModel.cs`, `DiffusionNexus.UI/Services/Pipelines/PipelineAssetInstaller.cs`, `DiffusionNexus.UI/App.axaml.cs` (pipeline DI)
- Test: retarget queue tests; extend `DiffusionNexus.Tests/InstallerManager/PipelineAssetInstallerTests.cs`; `DiffusionNexus.Tests/Viewer/CivitaiBrowserCardHandlerTests.cs` (create)

**Interfaces:**
- `CivitaiDownloadQueue` ctor: `(ICivitaiModelDownloader? downloader, IUnifiedLogger? logger, ICivitaiClient? civitaiClient, DownloadDestinationViewModel? destination, string? persistPathOverride = null)`; single-arg convenience ctor retargets to the downloader. `PipelineAssetInstaller` ctor: `LoraDownloadService` param → `ICivitaiModelDownloader`.

- [ ] **Step 1: Queue migration.** `RunJobAsync` keeps: destination resolution (3-step, unchanged), `Directory.CreateDirectory`, version rehydration. Then replaces everything from collision resolution through SHA verify with:

```csharp
var request = new DownloadRequest(civVersion!, targetDir, DownloadTrigger.BrowseQueue)
{
    FileNameOverride = job.FileName,
    TaskName = $"Download {job.ModelName} ({job.VersionName})",
};
var progressAdapter = new Progress<DownloadProgress>(p => Dispatcher.UIThread.Post(() =>
{
    job.ProgressPercent = p.Percent;
    job.StatusMessage = p.Message;
}));
var outcome = await _downloader!.DownloadAsync(request, progressAdapter, ct).ConfigureAwait(false);
job.TargetPath = outcome.FinalPath;
if (outcome.FinalPath is not null) job.ActualSha256 ??= job.ExpectedSha256; // verified inside the downloader
```

  Outcome→status mapping: `Completed` → `Completed`/"Done"; `CompletedMetadataIncomplete` → `Completed`/"Done — no metadata" + the existing `Warn`; `ReusedExisting` → `Completed`/"Already downloaded"; `HashMismatch` → `Failed` with the hash message; `Cancelled` (or `job.WasCancelledByUser`) → `Cancelled`/"Cancelled"; `Failed` → `Failed` (keep the "Connecting..." fixup). **The queue's own coordinator wrap is deleted — the downloader owns the coordinator (D3); the queue's `_gate` stays as submit-parallelism.** Delete `ResolveCollisionFreeTargetPathAsync`, `ComputeSha256Async`, the local TCS/`RunDownloadAsync`, the `metadataComplete` flag, and the `_downloadService` field. `StartAllAsync`'s unavailable-service guard now checks the downloader.
- [ ] **Step 2: Queue construction site** (`LoraViewerViewModel` ~line 405): pass the downloader (new ctor param from Task 6's DI wiring). Waitlist path needs no change — `EnqueueFromWaitlist` jobs flow through the same `RunJobAsync`.
- [ ] **Step 3: Card-handler bug** (spec RC5, `CivitaiBrowserViewModel.cs:603`): primary-search cards wire only `EnqueueAllVersionsHandler`; add `EnqueueSelectedVersionsHandler = EnqueueSelectedVersionsForCard` so "enqueue selected versions" stops silently no-oping — mirror the tag-fallback wiring at ~741-742. Test: construct both card shapes through whatever seam the existing browser tests use and assert both handlers are non-null on both.
- [ ] **Step 4: Pipeline migration.** `PipelineAssetInstaller.InstallCivitaiLoraAsync`: keep model/version/url resolution, the `File.Exists(target)` self-heal skip, and `WriteCivitaiInfoSidecar` after success. Replace its `_coordinator.EnqueueAsync` + TCS block with:

```csharp
var request = new DownloadRequest(version, targetDir, DownloadTrigger.Pipeline)
{
    File = primary,
    FileNameOverride = fileName,
    TaskName = $"{asset.Name} — {fileName}",
};
var outcome = await _modelDownloader.DownloadAsync(request, progress: null, ct).ConfigureAwait(false);
if (!outcome.Success) { /* existing early-access/API-key hint + throw, unchanged */ }
WriteCivitaiInfoSidecar(outcome.FinalPath ?? target, version, modelId);
```

  The installer's own coordinator wrap goes away (D3 — no nesting); its `IDownloadCoordinator` ctor param stays only if other assets use it (they do — non-Civitai assets; keep it). Update DI + `PipelineAssetInstallerTests` (fake `ICivitaiModelDownloader` instead of `LoraDownloadService`).
- [ ] **Step 5: Full suite green.** The concrete `LoraDownloadService` should now have exactly one production consumer: `CivitaiModelDownloader` (via `ILoraDownloadService`). Grep to confirm; leave the concrete DI registration for the interface forward.
- [ ] **Step 6: Commit** — `refactor(downloads): queue, waitlist and pipeline on the one path; browse card enqueue-selected fixed`

---

### Task 8: Riders + spec/doc bookkeeping

**Files:**
- Modify: `DiffusionNexus.Service/Services/DuplicateScanner.cs`, `DiffusionNexus.Service/Services/JsonInfoFileReaderService.cs`, `docs/superpowers/specs/2026-08-21-metadata-sync-overhaul-design.md`, `DiffusionNexus.UI/Doc/LoraViewer.md`
- Test: `DiffusionNexus.Tests/LoraSort/Services/DuplicateScannerTests.cs` (extend/create), `JsonInfoFileReaderServiceTests.cs` (extend)

- [ ] **Step 1: Failing tests.** DuplicateScanner in a temp dir holding an identical pair of `.sft` files → the pair is reported (today: zero candidates, the scan is `*.safetensors`-only). JsonInfoFileReaderService: a model whose only weights file is `.sft` (or `.ckpt`) is not dropped by the `.safetensors|.pt`-only pick at line 33.
- [ ] **Step 2: Implement** — route both hand-rolled scans through the shared set (`DiffusionNexus.Domain.Utilities.ModelFileExtensions.Sortable` — the enumerate/discover set, deliberately NOT `Recognized`):
  - `DuplicateScanner.GetCandidateFiles`: `Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(f => ModelFileExtensions.Matches(f, ModelFileExtensions.Sortable))`; the metadata-lookup extension test at :55 → `ModelFileExtensions.Matches(fi.FullName, ModelFileExtensions.Sortable)`.
  - `JsonInfoFileReaderService:33` → `AssociatedFilesInfo.FirstOrDefault(f => ModelFileExtensions.Matches(f.FullName, ModelFileExtensions.Sortable))`.
- [ ] **Step 3: Spec checkbox fixes** (stale since Plans A/B/C review): §5 — tick WP1 and WP2 `[x]`; tick WP5 `[x]` with a trailing note `(Plan D — this branch)`. WP6/WP7 stay open.
- [ ] **Step 4: Docs.** `Doc/LoraViewer.md`: update whatever it says about the download flow (collision behavior, the base-model/category folder rules now shared with the sorter, the "Installed tab updates automatically after queue downloads" change). Check the file's CRLF endings before editing.
- [ ] **Step 5: Full suite green. Commit** — `fix(library): .sft-blind duplicate/sidecar scans routed through the shared extension set; spec + docs updated`

---

## Verification / acceptance

**Automated:** full suite green at every task boundary (`dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release`). The S4 collision matrix, S5 IsUserEdited matrix, downloader orchestration matrix, category "2000" rows, card-handler wiring, and `.sft` scans all have named tests above.

**Manual (user, after merge-ready):**
1. Download LoRA dialog into a folder already holding a same-named different file → `_versionId` rename, original untouched; same-named identical file → reused, no second copy, still registered in DB.
2. Browse queue download → tile appears in the Installed tab (no manual refresh) with a poster thumbnail; batch of several jobs → one rebuild, not one per job.
3. Detail panel → Download this version → same completion; version tab flips to blue.
4. Pipeline install of a Civitai LoRA still writes its `.civitai.info` and reports through the status bar.
5. A user-edited model re-downloaded → name/tags/base model survive (S5).

## Out of scope (explicitly)

- `SupportedTypes.ModelTypesByPriority` dead code and the mutable `public static readonly string[]` exposure on `ModelFileExtensions` — flagged in #525 review, not part of WP5; separate cleanup if the user wants it.
- Plan E (WP6 `SyncPlanDialog`, report view, settings) and WP7 acceptance-on-reference-library.
- Sorter preview-UI rework (separate follow-up).
