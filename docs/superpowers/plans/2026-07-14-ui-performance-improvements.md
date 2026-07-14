# UI Performance Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the three reported UI stalls in the main DiffusionNexus app — slow startup, complete UI hang while a backup runs, and a multi-second freeze when opening the LoRA Viewer.

**Architecture:** Three independent fix clusters, ordered by impact-to-risk. Phase 1 removes the forced software renderer, throttles the backup progress dispatcher storm at its source, and moves LoRA thumbnail decoding off the UI thread. Phase 2 restructures startup so the main window shows before database init and module registration. Phase 3 hardens the remaining amplifiers (console log batching, SQLite WAL, fake-async restore paths).

**Tech Stack:** .NET 10, Avalonia 11, CommunityToolkit.Mvvm, EF Core + Microsoft.Data.Sqlite, SkiaSharp, xUnit (DiffusionNexus.Tests).

## Global Constraints

- Repo: `e:\Repos\DiffusionNexus`. Branch off `develop`; name the branch `feature/ui-performance-improvements` (pre-push hook rejects non-conforming names; never push directly to `develop`/`main`).
- Follow `.github/copilot-instructions.md`: bug fixes get a reproducing unit test where feasible; new UI reuses existing components; Windows is the primary target — platform-specific code gets a `// TODO: Linux Implementation` comment; do not confuse the two databases (`Diffusion_Nexus-core.db` = app core DB, `diffusion_nexus.db` = Installer SDK DB).
- Do NOT add global Avalonia test bootstrapping to DiffusionNexus.Tests (it destabilized the test host before — see git history). Platform-bound rendering/decoding code is verified by running the app, not by unit tests.
- Build: `dotnet build DiffusionNexus.sln`. Tests: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` (filtered during TDD loops; run the full suite before each commit).
- All `file:line` references below were verified on branch `feature/batch-metadata-distiller` (2026-07-14); line numbers may drift a few lines — anchor by the quoted code, not the number.
- Startup timing is measurable without new code: Serilog writes timestamped phase lines (`Initializing app database...`, `Main window Show() called`, …) to `%LocalAppData%\DiffusionNexus\Logs`. Record a before/after comparison for the final PR description.

---

## Root causes (investigated & code-verified 2026-07-14)

**Slow startup** — everything in `App.OnFrameworkInitializationCompleted` runs synchronously on the UI thread before `mainWindow.Show()` (`App.axaml.cs:92-183`): two SQLite database init/migration passes (`:124`, `:127`), then eager construction of all 8 module ViewModels **and** their XAML views (`RegisterModules`, `:1158-1354`). On top of that, the whole app runs on a forced **software renderer** — a leftover diagnostic override (`Program.cs:58-62`).

**Backup UI hang** — the ZIP work itself is correctly on a background thread (`Task.Run`, `DatasetManagementViewModel.cs:1006`). The freeze is a **progress dispatcher storm**: a report every 10 files (`DatasetBackupService.cs:139`) where each report costs ~6 UI-thread posts — `Progress<T>`'s own sync-context post, a redundant nested `Dispatcher.UIThread.Post` (`DatasetManagementViewModel.cs:997`), then `ActivityLogServiceBridge.ReportBackupProgress` fans out `StatusChanged` + `BackupProgressChanged` to 4 more posting subscribers (`ActivityLogServiceBridge.cs:253-261`). A many-small-file dataset (typical LoRA training data) emits thousands of reports at NoCompression copy speed → the dispatcher starves input. Software rendering makes each resulting repaint more expensive.

**LoRA Viewer freeze** — the VM and data are loaded at startup; the click cost is **first-time visual-tree realization**: up to `WindowSize = 200` (`LoraViewerViewModel.cs:189`) rich `ModelTileControl` subtrees built in one synchronous layout pass under a non-virtualized `ItemsControl`+`WrapPanel` inside a `StackPanel` (`LoraViewerView.axaml:227-241`), followed by ~200 thumbnail decodes **on the UI thread** — each a full SKBitmap decode → JPEG re-encode → Avalonia re-decode (`ModelTileViewModel.cs:1314-1335`, dispatched at `:1375`).

---

# Phase 1 — Quick wins

### Task 1: Hardware rendering by default (software rendering as env-var escape hatch)

The single broadest fix: `Program.cs` unconditionally forces `Win32RenderingMode.Software` with the comment "Force software rendering to diagnose GPU issues". Avalonia's default (`AngleEgl` with automatic software fallback) must become the norm; keep software mode reachable for machines with broken GPU drivers.

**Files:**
- Create: `DiffusionNexus.UI/Startup/RenderingConfig.cs`
- Modify: `DiffusionNexus.UI/Program.cs:55-64`
- Test: `DiffusionNexus.Tests/Startup/RenderingConfigTests.cs` (if the test project groups tests differently, mirror its existing folder convention)

**Interfaces:**
- Produces: `static bool RenderingConfig.UseSoftwareRendering(Func<string, string?> getEnvironmentVariable)` and `const string RenderingConfig.SoftwareRenderingEnvVar = "DIFFUSIONNEXUS_SOFTWARE_RENDERING"`. No other task consumes these.

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.UI.Startup;

namespace DiffusionNexus.Tests.Startup;

public class RenderingConfigTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    public void UseSoftwareRendering_IsTrue_WhenEnvVarOptsIn(string value)
    {
        Assert.True(RenderingConfig.UseSoftwareRendering(_ => value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("off")]
    public void UseSoftwareRendering_IsFalse_ByDefault(string? value)
    {
        Assert.False(RenderingConfig.UseSoftwareRendering(_ => value));
    }

    [Fact]
    public void UseSoftwareRendering_ReadsTheDocumentedVariable()
    {
        string? requested = null;
        RenderingConfig.UseSoftwareRendering(name => { requested = name; return null; });
        Assert.Equal("DIFFUSIONNEXUS_SOFTWARE_RENDERING", requested);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~RenderingConfigTests"`
Expected: FAIL — `RenderingConfig` does not exist (compile error).

- [ ] **Step 3: Implement `RenderingConfig`**

```csharp
using System;

namespace DiffusionNexus.UI.Startup;

/// <summary>
/// Decides which Win32 rendering mode the app requests. Hardware rendering
/// (Avalonia's default: ANGLE with automatic software fallback) is the norm;
/// setting DIFFUSIONNEXUS_SOFTWARE_RENDERING=1 forces the software compositor
/// as an escape hatch for machines with broken GPU drivers.
/// </summary>
public static class RenderingConfig
{
    public const string SoftwareRenderingEnvVar = "DIFFUSIONNEXUS_SOFTWARE_RENDERING";

    public static bool UseSoftwareRendering(Func<string, string?> getEnvironmentVariable)
    {
        var value = getEnvironmentVariable(SoftwareRenderingEnvVar);
        return value is not null
            && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~RenderingConfigTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Rewire `BuildAvaloniaApp`**

Replace `Program.cs:55-64` (currently):

```csharp
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Force software rendering to diagnose GPU issues
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software]
            })
            .WithInterFont()
            .LogToTrace();
```

with:

```csharp
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        // Hardware rendering by default (ANGLE, with Avalonia's built-in software
        // fallback). DIFFUSIONNEXUS_SOFTWARE_RENDERING=1 forces the software
        // compositor on machines with broken GPU drivers.
        // TODO: Linux Implementation — the override below is Win32-specific.
        if (Startup.RenderingConfig.UseSoftwareRendering(Environment.GetEnvironmentVariable))
        {
            Log.Information("Rendering: software compositor forced via {EnvVar}",
                Startup.RenderingConfig.SoftwareRenderingEnvVar);
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software]
            });
        }

        return builder;
    }
```

Add `using Serilog;` if not present (check existing usings — `Log.CloseAndFlush()` at `Program.cs:51` implies it already is).

- [ ] **Step 6: Build and verify by running the app**

Run: `dotnet build DiffusionNexus.sln` → expected: Build succeeded.
Run the app (`dotnet run --project DiffusionNexus.UI`) → window renders correctly, no visual corruption; scrolling any list feels smoother. Then set `DIFFUSIONNEXUS_SOFTWARE_RENDERING=1`, run again → log line `Rendering: software compositor forced…` appears; unset it.

- [ ] **Step 7: Full test suite + commit**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` → expected: all green.

```bash
git add DiffusionNexus.UI/Startup/RenderingConfig.cs DiffusionNexus.UI/Program.cs DiffusionNexus.Tests/Startup/RenderingConfigTests.cs
git commit -m "perf(rendering): default to hardware rendering; env-var escape hatch for software mode"
```

---

### Task 2: Kill the backup progress dispatcher storm

Two changes: (a) gate progress emission in `DatasetBackupService` so a backup emits ≤ ~100 reports total instead of one per 10 files, and (b) remove the redundant nested `Dispatcher.UIThread.Post` inside all three `Progress<BackupProgress>` callbacks (`Progress<T>` already marshals to the UI SynchronizationContext it was created on). The `ActivityLogServiceBridge` fan-out (4 posts per report) stays — at ≤100 reports it is harmless.

**Files:**
- Create: `DiffusionNexus.Service/Services/BackupProgressGate.cs`
- Modify: `DiffusionNexus.Service/Services/DatasetBackupService.cs:124-150`
- Modify: `DiffusionNexus.UI/ViewModels/Tabs/DatasetManagementViewModel.cs:995-1002` and `:1065-1072`
- Modify: `DiffusionNexus.UI/ViewModels/SettingsViewModel.cs:976-983`
- Test: `DiffusionNexus.Tests/Services/BackupProgressGateTests.cs`

**Interfaces:**
- Produces: `sealed class BackupProgressGate` with `bool ShouldReport(int percent, string? phase)`. Consumed only inside `DatasetBackupService.BackupDatasetsAsync`.

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.Service.Services;

namespace DiffusionNexus.Tests.Services;

public class BackupProgressGateTests
{
    [Fact]
    public void FirstReport_AlwaysPasses()
    {
        var gate = new BackupProgressGate();
        Assert.True(gate.ShouldReport(5, "Creating backup"));
    }

    [Fact]
    public void SamePercentAndPhase_IsSuppressed()
    {
        var gate = new BackupProgressGate();
        gate.ShouldReport(42, "Creating backup");
        Assert.False(gate.ShouldReport(42, "Creating backup"));
    }

    [Fact]
    public void PercentChange_Passes()
    {
        var gate = new BackupProgressGate();
        gate.ShouldReport(42, "Creating backup");
        Assert.True(gate.ShouldReport(43, "Creating backup"));
    }

    [Fact]
    public void PhaseChange_PassesEvenAtSamePercent()
    {
        var gate = new BackupProgressGate();
        gate.ShouldReport(98, "Creating backup");
        Assert.True(gate.ShouldReport(98, "Cleaning up old backups"));
    }

    [Fact]
    public void ManySmallFileUpdates_CollapseToAtMostOnePerPercent()
    {
        var gate = new BackupProgressGate();
        var emitted = 0;
        for (var file = 1; file <= 100_000; file++)
        {
            var percent = 5 + (int)(90.0 * file / 100_000);
            if (gate.ShouldReport(percent, "Creating backup")) emitted++;
        }
        Assert.InRange(emitted, 1, 91);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~BackupProgressGateTests"`
Expected: FAIL — `BackupProgressGate` does not exist (compile error).

- [ ] **Step 3: Implement the gate**

```csharp
using System;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Suppresses redundant backup progress reports. A report passes only when the
/// integer percentage or the phase changed. This bounds UI dispatcher traffic
/// to ~100 posts per backup regardless of dataset file count — reporting every
/// N files froze the UI on many-small-file datasets (dispatcher storm).
/// </summary>
public sealed class BackupProgressGate
{
    private int _lastPercent = -1;
    private string? _lastPhase;

    public bool ShouldReport(int percent, string? phase)
    {
        if (percent == _lastPercent && string.Equals(phase, _lastPhase, StringComparison.Ordinal))
            return false;

        _lastPercent = percent;
        _lastPhase = phase;
        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~BackupProgressGateTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Use the gate in the ZIP loop**

In `DatasetBackupService.BackupDatasetsAsync`, immediately before the `using (var fileStream = …)` block at `:121`, add:

```csharp
                var progressGate = new BackupProgressGate();
```

Replace the per-file report block at `:137-150` (currently):

```csharp
                            processedFiles++;

                            if (processedFiles % 10 == 0 || processedFiles == totalFiles)
                            {
                                var percent = 5 + (int)(90.0 * processedFiles / totalFiles);
                                progress?.Report(new BackupProgress
                                {
                                    Phase = "Creating backup",
                                    CurrentFile = relativePath,
                                    ProgressPercent = percent,
                                    FilesProcessed = processedFiles,
                                    TotalFiles = totalFiles
                                });
                            }
```

with:

```csharp
                            processedFiles++;

                            var percent = 5 + (int)(90.0 * processedFiles / totalFiles);
                            if (progressGate.ShouldReport(percent, "Creating backup"))
                            {
                                progress?.Report(new BackupProgress
                                {
                                    Phase = "Creating backup",
                                    CurrentFile = relativePath,
                                    ProgressPercent = percent,
                                    FilesProcessed = processedFiles,
                                    TotalFiles = totalFiles
                                });
                            }
```

The phase-transition reports at `:88-96`, `:110-115`, `:174-180` and the completion report stay untouched (they are few and carry phase changes the gate would pass anyway).

- [ ] **Step 6: Remove the nested dispatcher post in all three Progress callbacks**

There are exactly three construction sites (verify with `grep -rn "new Progress<BackupProgress>" DiffusionNexus.UI/`). All three are constructed on the UI thread, so `Progress<T>` already marshals its callback — the inner `Post` was a second, redundant hop.

`DatasetManagementViewModel.cs:995-1002` (in `ExecuteBackupIfDueAsync`) — replace:

```csharp
            var progress = new Progress<BackupProgress>(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    BackupStatusText = $"Backup: {p.ProgressPercent}%";
                    _activityLog?.ReportBackupProgress(p.ProgressPercent, p.Phase);
                });
            });
```

with:

```csharp
            // Progress<T> captures the UI SynchronizationContext at construction
            // (this runs on the UI thread), so the callback is already marshaled.
            var progress = new Progress<BackupProgress>(p =>
            {
                BackupStatusText = $"Backup: {p.ProgressPercent}%";
                _activityLog?.ReportBackupProgress(p.ProgressPercent, p.Phase);
            });
```

`DatasetManagementViewModel.cs:1065-1072` (in `BackupNowAsync`) — the identical block: apply the identical transformation (same before/after as above).

`SettingsViewModel.cs:976-983` (in `BackupNowAsync`) — replace:

```csharp
            var progress = new Progress<BackupProgress>(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    BusyMessage = $"Backup: {p.Phase} ({p.ProgressPercent}%)";
                    _activityLogService?.ReportBackupProgress(p.ProgressPercent, p.Phase);
                });
            });
```

with:

```csharp
            // Progress<T> captures the UI SynchronizationContext at construction
            // (this runs on the UI thread), so the callback is already marshaled.
            var progress = new Progress<BackupProgress>(p =>
            {
                BusyMessage = $"Backup: {p.Phase} ({p.ProgressPercent}%)";
                _activityLogService?.ReportBackupProgress(p.ProgressPercent, p.Phase);
            });
```

- [ ] **Step 7: Build, run existing backup tests, verify manually**

Run: `dotnet build DiffusionNexus.sln` → Build succeeded.
Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~Backup"` → all green (fix any test that asserted the old `% 10` cadence — the new contract is "at most one report per percent/phase change").
Manual: point `DatasetStoragePath` at a folder with a few thousand small files, trigger **Backup Now** — the window must stay fully interactive (drag it, scroll, type) while the status bar percent climbs. (Optional harness trick from issue #397: poll `Process.Responding` from a script while the backup runs — it must stay `true`.)

- [ ] **Step 8: Full suite + commit**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` → all green.

```bash
git add DiffusionNexus.Service/Services/BackupProgressGate.cs DiffusionNexus.Service/Services/DatasetBackupService.cs DiffusionNexus.UI/ViewModels/Tabs/DatasetManagementViewModel.cs DiffusionNexus.UI/ViewModels/SettingsViewModel.cs DiffusionNexus.Tests/Services/BackupProgressGateTests.cs
git commit -m "perf(backup): gate progress to percent/phase changes; drop redundant dispatcher hop"
```

---

### Task 3: Decode LoRA tile thumbnails off the UI thread, downscaled, without the JPEG round-trip

`DecodeThumbnailFromBytes` (`ModelTileViewModel.cs:1314-1335`) runs **on the UI thread** (marshaled at `:1375`) and does three image ops per tile: `SKBitmap.Decode` → `SKImage.Encode(Jpeg, 90)` → `new Bitmap(stream)`. Avalonia `Bitmap`s are immutable and may be created on any thread, and `Bitmap.DecodeToWidth` downscales during decode. Only the bound-property assignment needs the UI thread.

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/ModelTileViewModel.cs:1314-1335` (decode helper), `:1375` (lazy path), plus every other `DecodeThumbnailFromBytes` caller (find with `grep -n "DecodeThumbnailFromBytes" DiffusionNexus.UI/ViewModels/ModelTileViewModel.cs`)
- Test: none (bitmap decoding needs an initialized Avalonia platform; per Global Constraints we do not add Avalonia bootstrapping to the test host). Verification is by running the app.

**Interfaces:**
- Produces: `internal static Bitmap? ModelTileViewModel.CreateTileBitmap(byte[] data)` — safe to call from any thread. Consumed only within `ModelTileViewModel`.

- [ ] **Step 1: Replace the decode helper**

Replace `DecodeThumbnailFromBytes` (`:1311-1335`) with:

```csharp
    /// <summary>Decode width for tile thumbnails: 250px tile at up to 200% display scaling.</summary>
    private const int TileDecodeWidth = 500;

    /// <summary>
    /// Decodes thumbnail bytes into a displayable Bitmap, downscaled to the tile
    /// width. Safe to call from any thread — Avalonia Bitmaps are immutable and
    /// may be created off the UI thread. Falls back to a Skia transcode for
    /// formats Avalonia's decoder rejects.
    /// </summary>
    internal static Bitmap? CreateTileBitmap(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            return Bitmap.DecodeToWidth(stream, TileDecodeWidth);
        }
        catch
        {
            try
            {
                using var skBitmap = SKBitmap.Decode(data);
                if (skBitmap is null) return null;
                using var skImage = SKImage.FromBitmap(skBitmap);
                using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, 90);
                using var stream = new MemoryStream(encoded.ToArray());
                return new Bitmap(stream);
            }
            catch
            {
                return null;
            }
        }
    }
```

(Keep the existing `using` directives; `Bitmap` is `Avalonia.Media.Imaging.Bitmap`, already imported for the current code.)

- [ ] **Step 2: Rewire the lazy DB path (already on a pool thread)**

In `LazyLoadThumbnailFromDbAsync`, replace `:1375`:

```csharp
                await Dispatcher.UIThread.InvokeAsync(() => DecodeThumbnailFromBytes(data));
```

with:

```csharp
                // Decode on this (pool) thread; only the bound-property
                // assignment needs the UI thread.
                var bitmap = CreateTileBitmap(data);
                ct.ThrowIfCancellationRequested();
                await Dispatcher.UIThread.InvokeAsync(() => ThumbnailImage = bitmap);
```

- [ ] **Step 3: Rewire every remaining caller**

Run `grep -n "DecodeThumbnailFromBytes" DiffusionNexus.UI/ViewModels/ModelTileViewModel.cs`. For each remaining caller (e.g. the in-memory path inside `LoadThumbnailFromVersion`, which runs on the UI thread during tile activation), apply this transformation — a call of the form:

```csharp
DecodeThumbnailFromBytes(someBytes);
```

becomes:

```csharp
var bytes = someBytes; // capture before hopping threads
_ = Task.Run(() =>
{
    var bitmap = CreateTileBitmap(bytes);
    Dispatcher.UIThread.Post(() => ThumbnailImage = bitmap);
});
```

After this step the grep must return zero call sites and the old method must be deleted (it is fully replaced by `CreateTileBitmap`; per copilot-instructions this inline replacement of a private helper needs no `[Obsolete]` staging because no external caller exists).

- [ ] **Step 4: Build + full suite**

Run: `dotnet build DiffusionNexus.sln` → Build succeeded.
Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` → all green.

- [ ] **Step 5: Verify in the running app**

Run the app with a large LoRA library. Click **LoRA Viewer**: thumbnails must stream in without the window stuttering; scrolling while thumbnails load must stay smooth. Broken/exotic images must show the placeholder (fallback path), not crash.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/ViewModels/ModelTileViewModel.cs
git commit -m "perf(lora-viewer): decode tile thumbnails off the UI thread, downscaled, no JPEG round-trip"
```

---

### Task 4: Batch the LoRA tile window fill so first navigation never realizes 200 tiles at once

`FilteredTiles` is fully populated (up to 200 items) at startup; the first click on LoRA Viewer attaches the view and realizes all 200 `ModelTileControl` subtrees in one synchronous layout pass. Refill the window in dispatcher-batched chunks (first chunk synchronous for instant content, rest at `Background` priority) triggered on first attach; reuse the same batched fill for filter changes.

**Files:**
- Create: `DiffusionNexus.UI/Helpers/BatchedListFiller.cs`
- Modify: `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs` — `RebuildFilteredTilesWindow` (`:2699-2707`), `SlideForward` (`:2716`), `SlideBackward` (`:2748`), new `OnViewAttached()`
- Modify: `DiffusionNexus.UI/Views/LoraViewerView.axaml.cs` — attach hook
- Test: `DiffusionNexus.Tests/Helpers/BatchedListFillerTests.cs`

**Interfaces:**
- Produces: `static Action BatchedListFiller.Fill<T>(IList<T> target, IReadOnlyList<T> source, int start, int endExclusive, int batchSize, Action<Action> post, Action? onCompleted = null)` — returns a cancel action. Produces `void LoraViewerViewModel.OnViewAttached()` — consumed by `LoraViewerView` code-behind.

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.UI.Helpers;

namespace DiffusionNexus.Tests.Helpers;

public class BatchedListFillerTests
{
    [Fact]
    public void FillsEverything_InOrder_AcrossBatches()
    {
        var source = Enumerable.Range(0, 10).ToList();
        var target = new List<int>();
        var posts = new Queue<Action>();

        BatchedListFiller.Fill(target, source, 0, 10, batchSize: 3, post: posts.Enqueue);

        Assert.Equal(new[] { 0, 1, 2 }, target); // first batch is synchronous
        while (posts.Count > 0) posts.Dequeue().Invoke();
        Assert.Equal(source, target);
    }

    [Fact]
    public void SmallSource_FillsSynchronously_WithoutPosting()
    {
        var target = new List<int>();
        var posts = new Queue<Action>();

        BatchedListFiller.Fill(target, new List<int> { 1, 2 }, 0, 2, batchSize: 5, post: posts.Enqueue);

        Assert.Equal(new[] { 1, 2 }, target);
        Assert.Empty(posts);
    }

    [Fact]
    public void Cancel_AbandonsRemainingBatches()
    {
        var source = Enumerable.Range(0, 10).ToList();
        var target = new List<int>();
        var posts = new Queue<Action>();

        var cancel = BatchedListFiller.Fill(target, source, 0, 10, batchSize: 4, post: posts.Enqueue);
        cancel();
        while (posts.Count > 0) posts.Dequeue().Invoke();

        Assert.Equal(new[] { 0, 1, 2, 3 }, target); // only the synchronous batch landed
    }

    [Fact]
    public void OnCompleted_FiresOnce_AfterLastBatch()
    {
        var source = Enumerable.Range(0, 7).ToList();
        var target = new List<int>();
        var posts = new Queue<Action>();
        var completed = 0;

        BatchedListFiller.Fill(target, source, 0, 7, 3, posts.Enqueue, () => completed++);
        while (posts.Count > 0) posts.Dequeue().Invoke();

        Assert.Equal(1, completed);
    }

    [Fact]
    public void RespectsStartOffset()
    {
        var source = Enumerable.Range(0, 10).ToList();
        var target = new List<int>();
        var posts = new Queue<Action>();

        BatchedListFiller.Fill(target, source, 6, 10, 10, posts.Enqueue);

        Assert.Equal(new[] { 6, 7, 8, 9 }, target);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~BatchedListFillerTests"`
Expected: FAIL — `BatchedListFiller` does not exist (compile error).

- [ ] **Step 3: Implement `BatchedListFiller`**

```csharp
using System;
using System.Collections.Generic;

namespace DiffusionNexus.UI.Helpers;

/// <summary>
/// Fills a target list from a source range in scheduler-batched chunks. The
/// first batch is added synchronously (instant first paint); subsequent batches
/// go through <paramref name="post"/> (in production: Dispatcher post at
/// Background priority) so layout and input can interleave. Prevents realizing
/// hundreds of item containers in a single layout pass.
/// </summary>
public static class BatchedListFiller
{
    /// <returns>A cancel action that abandons the not-yet-run batches.</returns>
    public static Action Fill<T>(
        IList<T> target,
        IReadOnlyList<T> source,
        int start,
        int endExclusive,
        int batchSize,
        Action<Action> post,
        Action? onCompleted = null)
    {
        var cancelled = false;
        var next = start;

        void AddBatch()
        {
            if (cancelled) return;

            var batchEnd = Math.Min(next + batchSize, endExclusive);
            for (; next < batchEnd; next++)
                target.Add(source[next]);

            if (next < endExclusive)
                post(AddBatch);
            else
                onCompleted?.Invoke();
        }

        AddBatch();
        return () => cancelled = true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~BatchedListFillerTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Wire it into `LoraViewerViewModel`**

Add fields next to the windowing fields (`:186-206` region):

```csharp
    private const int FillBatchSize = 24;
    private Action? _cancelWindowFill;
    private bool _windowFillInProgress;
    private bool _firstAttachHandled;
```

Replace `RebuildFilteredTilesWindow` (`:2699-2707`, currently a synchronous full-window loop):

```csharp
    private void RebuildFilteredTilesWindow()
    {
        FilteredTiles.Clear();
        var end = Math.Min(_windowStart + WindowSize, _allFiltered.Count);
        for (var i = _windowStart; i < end; i++)
        {
            FilteredTiles.Add(_allFiltered[i]);
        }
    }
```

with:

```csharp
    private void RebuildFilteredTilesWindow()
    {
        _cancelWindowFill?.Invoke();
        FilteredTiles.Clear();

        var end = Math.Min(_windowStart + WindowSize, _allFiltered.Count);
        _windowFillInProgress = true;
        _cancelWindowFill = Helpers.BatchedListFiller.Fill(
            FilteredTiles,
            _allFiltered,
            _windowStart,
            end,
            FillBatchSize,
            post: action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background),
            onCompleted: () =>
            {
                _windowFillInProgress = false;
                TriggerVisibleUpdateCheck();
            });
    }
```

(Check the file's existing `using`s: it needs `Avalonia.Threading` for `Dispatcher`/`DispatcherPriority` — it already dispatches elsewhere, so likely present.)

Guard the slide operations against running mid-fill (indices would drift). At the top of `SlideForward` (`:2716`), after `LastSlideForwardCount = 0;`, add:

```csharp
        if (_windowFillInProgress) return;
```

At the top of `SlideBackward` (`:2748`), after `LastSlideBackwardCount = 0;`, add:

```csharp
        if (_windowFillInProgress) return;
```

Add the attach hook (near the other public methods):

```csharp
    /// <summary>
    /// Called by the view when it enters the visual tree. On first attach the
    /// window is already fully populated from the startup load — refill it in
    /// batches so the initial navigation doesn't realize every tile container
    /// in one synchronous layout pass. Subsequent attaches keep their already
    /// generated containers, so no refill is needed (and refilling would reset
    /// the user's scroll position).
    /// </summary>
    public void OnViewAttached()
    {
        if (_firstAttachHandled) return;
        _firstAttachHandled = true;

        if (FilteredTiles.Count > FillBatchSize)
            RebuildFilteredTilesWindow();
    }
```

- [ ] **Step 6: Call the hook from the view**

In `LoraViewerView.axaml.cs`, add (keeping the existing constructor/InitializeComponent untouched):

```csharp
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is ViewModels.LoraViewerViewModel vm)
            vm.OnViewAttached();
    }
```

Add `using Avalonia;` / adjust namespaces to match the file's existing style.

- [ ] **Step 7: Build + full suite**

Run: `dotnet build DiffusionNexus.sln` → Build succeeded.
Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` → all green (if existing LoraViewer VM tests assert that `FilteredTiles` is fully populated synchronously after a filter change, adapt them to drain via the new behavior — the full set is still reachable; only the timing is batched. Check first whether such tests exist: `grep -rln "RebuildFilteredTilesWindow\|FilteredTiles" DiffusionNexus.Tests/`).

- [ ] **Step 8: Verify in the running app**

With a library of 200+ LoRAs: click **LoRA Viewer** for the first time in a session — first tiles must appear near-instantly and the rest stream in; the window must respond to input throughout. Change a base-model filter with the view open — same streaming behavior, no freeze. Scroll to the window edge — slide (Load more) still works after the fill completes.

- [ ] **Step 9: Commit**

```bash
git add DiffusionNexus.UI/Helpers/BatchedListFiller.cs DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs DiffusionNexus.UI/Views/LoraViewerView.axaml.cs DiffusionNexus.Tests/Helpers/BatchedListFillerTests.cs
git commit -m "perf(lora-viewer): batch tile window fill so first navigation realizes tiles incrementally"
```

---

# Phase 2 — Startup structure

### Task 5: Gate the SDK database migration (stop running `Migrate()` unconditionally)

The core DB already gates `Migrate()` on pending migrations (`App.axaml.cs:352`); the SDK DB does not (`:490`) — every launch pays a full EF migration pass on `diffusion_nexus.db` (the **SDK** database; do not touch the core DB here).

**Files:**
- Modify: `DiffusionNexus.UI/App.axaml.cs:487-491`
- Test: none new (App-static plumbing; verified via Serilog output + existing suite).

- [ ] **Step 1: Apply the gate**

Replace `App.axaml.cs:487-491` (currently):

```csharp
            // Apply any pending schema migrations on top of the (seed) data.
            var sdkContext = Services!.GetRequiredService<SdkContext>();
            Serilog.Log.Information("InitializeSdkDatabase: Applying migrations to SDK database...");
            sdkContext.Database.Migrate();
            Serilog.Log.Information("InitializeSdkDatabase: Migration completed successfully");
```

with:

```csharp
            // Apply pending schema migrations on top of the (seed) data — but only
            // when there are any; an unconditional Migrate() costs a full EF
            // migration pass on every launch (mirrors the core-DB gating above).
            var sdkContext = Services!.GetRequiredService<SdkContext>();
            var pendingSdkMigrations = sdkContext.Database.GetPendingMigrations().ToList();
            if (pendingSdkMigrations.Count > 0)
            {
                Serilog.Log.Information("InitializeSdkDatabase: Applying {Count} pending migration(s)...", pendingSdkMigrations.Count);
                sdkContext.Database.Migrate();
                Serilog.Log.Information("InitializeSdkDatabase: Migration completed successfully");
            }
            else
            {
                Serilog.Log.Information("InitializeSdkDatabase: No pending migrations - SKIPPING Migrate()");
            }
```

(`System.Linq` is already imported in App.axaml.cs.)

- [ ] **Step 2: Build, run, verify logs**

Run: `dotnet build DiffusionNexus.sln` → Build succeeded.
Run the app twice. Second launch's log must contain `InitializeSdkDatabase: No pending migrations - SKIPPING Migrate()` and the SDK workloads must still load (open Installer Manager, confirm packages/workloads list).

- [ ] **Step 3: Full suite + commit**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` → all green.

```bash
git add DiffusionNexus.UI/App.axaml.cs
git commit -m "perf(startup): skip SDK database Migrate() when no migrations are pending"
```

---

### Task 6: Show the main window before database init and module registration

Today the window appears only after: 2 DB inits + 8 module VM graphs + 8 XAML view inflations (`App.axaml.cs:92-183`). Restructure: build services → main VM + status bar → **Show()** → then (async) DB init on a pool thread → UI-affine service init → `RegisterModules` on the UI thread. The window shows a lightweight "Starting…" overlay until modules land. `InitializeDatabase`/`InitializeSdkDatabase` are pure logging + EF work (verified `App.axaml.cs:321-500`) — safe off the UI thread.

**Files:**
- Modify: `DiffusionNexus.UI/App.axaml.cs:92-183` (restructure), new private method `CompleteStartupAsync`
- Modify: `DiffusionNexus.UI/ViewModels/DiffusionNexusMainWindowViewModel.cs` (add `IsStartupComplete` observable property)
- Modify: `DiffusionNexus.UI/Views/DiffusionNexusMainWindow.axaml` (startup overlay around the `ContentControl` at `:216`)
- Test: none new (startup composition; verified by running — see Step 6).

**Interfaces:**
- Produces: `bool DiffusionNexusMainWindowViewModel.IsStartupComplete` (observable; bound by the main window overlay).

- [ ] **Step 1: Add the readiness flag to the main VM**

In `DiffusionNexusMainWindowViewModel` (near the other `[ObservableProperty]` fields, `:61-66`):

```csharp
    /// <summary>
    /// False until deferred startup (database init + module registration) has
    /// finished. The main window shows a lightweight loading overlay while false.
    /// </summary>
    [ObservableProperty]
    private bool _isStartupComplete;
```

- [ ] **Step 2: Add the startup overlay to the main window XAML**

Locate the module host `ContentControl` (`DiffusionNexusMainWindow.axaml:216`, `Content="{Binding CurrentModuleView}"`). Wrap it in a `Panel` so an overlay can sit on top (reuse the app's existing spinner/progress styling if one exists — check `IsBusy` overlays in other views first, per the reuse rule):

```xml
<Panel>
  <ContentControl Content="{Binding CurrentModuleView}"/>
  <StackPanel IsVisible="{Binding !IsStartupComplete}"
              HorizontalAlignment="Center"
              VerticalAlignment="Center"
              Spacing="12">
    <ProgressBar IsIndeterminate="True" Width="220"/>
    <TextBlock Text="Starting DiffusionNexus…"
               HorizontalAlignment="Center"
               Opacity="0.7"/>
  </StackPanel>
</Panel>
```

Preserve all existing attributes (Grid placement, classes) on the `ContentControl`; only introduce the wrapping `Panel` at the same grid position.

- [ ] **Step 3: Restructure `OnFrameworkInitializationCompleted`**

Reorder the body (`App.axaml.cs:92-183`) to:

```csharp
                // Avoid duplicate validations from both Avalonia and the CommunityToolkit
                Serilog.Log.Information("Disabling data annotation validation...");
                DisableAvaloniaDataAnnotationValidation();

                Serilog.Log.Information("Configuring services...");
                var services = new ServiceCollection();
                ConfigureServices(services);
                var rootProvider = services.BuildServiceProvider();

                _appScope = rootProvider.CreateScope();
                Services = _appScope.ServiceProvider;

                Serilog.Log.Information("Creating main window view model...");
                var mainViewModel = new DiffusionNexusMainWindowViewModel();

                Serilog.Log.Information("Initializing status bar...");
                mainViewModel.InitializeStatusBar();

                Serilog.Log.Information("Creating main window...");
                var mainWindow = new DiffusionNexusMainWindow
                {
                    DataContext = mainViewModel
                };
                desktop.MainWindow = mainWindow;

                // Cleanup on shutdown — keep the existing handler registration here,
                // unchanged, so it is wired before anything can close the app.
                desktop.ShutdownRequested += (_, _) => { /* existing body, unchanged */ };

                // Show FIRST: the window becomes visible and interactive immediately;
                // databases, service warm-up and module registration follow async.
                mainWindow.Show();
                Serilog.Log.Information("Main window Show() called");

                _ = CompleteStartupAsync(mainViewModel);
```

Move (do not rewrite) the existing shutdown-handler body from `:186` into the position shown. Then add the deferred-completion method:

```csharp
    /// <summary>
    /// Deferred startup: everything that used to run before Show() but doesn't
    /// need to. Database init runs on a pool thread; module registration (which
    /// inflates XAML views) resumes on the UI thread afterwards.
    /// </summary>
    private async Task CompleteStartupAsync(DiffusionNexusMainWindowViewModel mainViewModel)
    {
        try
        {
            await Task.Run(() =>
            {
                Serilog.Log.Information("Initializing app database...");
                InitializeDatabase();
                Serilog.Log.Information("Initializing SDK database...");
                InitializeSdkDatabase();
            });

            // UI-affine init (converter singletons, spell check, console wiring).
            Serilog.Log.Information("Initializing thumbnail service...");
            InitializeThumbnailService();
            Serilog.Log.Information("Initializing spell check services...");
            InitializeSpellCheckServices();
            Serilog.Log.Information("Initializing instance process manager...");
            _ = Services!.GetRequiredService<IInstanceProcessManager>();
            Serilog.Log.Information("Initializing Civitai base-model catalog...");
            InitializeCivitaiBaseModelCatalog();

            Serilog.Log.Information("Registering modules...");
            RegisterModules(mainViewModel);

            if (DiffusionNexus.UI.Services.Diffusion.DiffusionFeatureFlags.UseLocalDiffusionBackend)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = Services!.CreateScope();
                        var registrar = scope.ServiceProvider.GetRequiredService<DiffusionNexus.UI.Services.Diffusion.OutputsFolderRegistrar>();
                        await registrar.EnsureRegisteredAsync();
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "OutputsFolderRegistrar failed during startup.");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Fatal(ex, "Deferred startup initialization failed");
            Services?.GetService<IActivityLogService>()
                ?.LogError("Startup", "Startup initialization failed — some features may be unavailable", ex);
        }
        finally
        {
            mainViewModel.IsStartupComplete = true;
        }
    }
```

Notes for the implementer:
- `await Task.Run(...)` resumes on the Avalonia UI thread (Avalonia installs a SynchronizationContext) — `RegisterModules` and the service inits after the `await` therefore run on the UI thread, which they require.
- Delete the now-moved blocks from their old positions (`InitializeThumbnailService` `:116`, `InitializeSpellCheckServices` `:120`, DB inits `:124-127`, instance manager `:141`, catalog `:147`, `RegisterModules` `:160`, outputs registrar `:164-179`). The method should contain no duplicate calls afterwards.
- `RegisterModules` already fires `LoadStartupDataAsync` at its end (`:1347`) — unchanged, and now guaranteed to run after DB init.

- [ ] **Step 4: Guard against pre-module DB access**

Run: `grep -rn "InitializeInstanceManagement(" DiffusionNexus.UI/ --include=*.cs`
Confirm every call site executes at-or-after `CompleteStartupAsync`'s post-DB section (it needs the DB for `LoadInstancesAsync`). If a call site runs earlier (e.g. from `InitializeStatusBar` or main-window code-behind), move that call into `CompleteStartupAsync` immediately after `InitializeCivitaiBaseModelCatalog();` — same pattern as the other moved calls. Also confirm `StatusBarViewModel`/`UnifiedConsoleViewModel` construction (`InitializeStatusBar`) performs no DB access in constructors (verified 2026-07-14: it subscribes to the in-memory log stream only — re-verify if the file changed).

- [ ] **Step 5: Build + full suite**

Run: `dotnet build DiffusionNexus.sln` → Build succeeded.
Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` → all green.

- [ ] **Step 6: Manual verification checklist (all must pass)**

1. Fresh launch: window appears fast with the "Starting DiffusionNexus…" overlay, then modules populate; nav rail items appear; LoRA Dataset Helper becomes the selected module (first registered → `RegisterModule` default still applies because `CurrentModuleView` stays null until then).
2. Compare Serilog timestamps: `Main window Show() called` now precedes `Initializing app database...`; delta from process start to Show is materially smaller than the pre-change log.
3. Window is draggable/resizable during the overlay phase.
4. Second launch (no pending migrations): same behavior, faster.
5. Settings, backup timer, Unified Console, and Installer Manager all function after startup completes (they depend on services that now init later).
6. Kill-switch sanity: rename `%LocalAppData%\DiffusionNexus\Data\Diffusion_Nexus-core.db` to simulate first-run; app must still come up and recreate the DB (the existing recovery paths in `InitializeDatabase` run unchanged, just on a pool thread).

- [ ] **Step 7: Commit**

```bash
git add DiffusionNexus.UI/App.axaml.cs DiffusionNexus.UI/ViewModels/DiffusionNexusMainWindowViewModel.cs DiffusionNexus.UI/Views/DiffusionNexusMainWindow.axaml
git commit -m "perf(startup): show main window before DB init and module registration"
```

---

### Task 7: Move the backup due-check and post-backup settings reads off the UI thread

`Microsoft.Data.Sqlite` executes "async" queries synchronously on the calling thread, so `await _backupService.IsBackupDueAsync()` (`DatasetManagementViewModel.cs:980`) and the post-backup `await _settingsService.GetSettingsAsync()` (`:1022`, `:1092`) are real UI-thread stalls. Wrap them in `Task.Run` with a fresh DI scope — the exact pattern already used for the backup itself (`:1006-1011`).

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/Tabs/DatasetManagementViewModel.cs:980`, `:1022-1024`, `:1092-1094`
- Test: none new (thin dispatch change; covered by existing backup tests + manual check).

- [ ] **Step 1: Rewire the due-check**

First confirm the settings service interface name used by the `_settingsService` field: `grep -n "_settingsService" DiffusionNexus.UI/ViewModels/Tabs/DatasetManagementViewModel.cs | head -3` (expected: `IAppSettingsService` — use whatever the field declares).

Replace `:980`:

```csharp
        var isDue = await _backupService.IsBackupDueAsync();
```

with:

```csharp
        // SQLite "async" queries execute synchronously on the calling thread —
        // run the due-check on a pool thread with its own scope, like the backup.
        var isDue = await Task.Run(async () =>
        {
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var scopedBackupService = scope.ServiceProvider.GetRequiredService<IDatasetBackupService>();
            return await scopedBackupService.IsBackupDueAsync();
        });
```

- [ ] **Step 2: Rewire both post-backup settings reads**

At `:1022-1024` and `:1092-1094`, replace (both occurrences — one in `ExecuteBackupIfDueAsync`, one in `BackupNowAsync`):

```csharp
                var settings = await _settingsService.GetSettingsAsync();
                settings.LastBackupAt = DateTimeOffset.UtcNow;
                UpdateBackupStatus(settings);
```

with:

```csharp
                var settings = await Task.Run(async () =>
                {
                    using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
                    var scopedSettings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
                    return await scopedSettings.GetSettingsAsync();
                });
                settings.LastBackupAt = DateTimeOffset.UtcNow;
                UpdateBackupStatus(settings);
```

(`UpdateBackupStatus` only reads from the entity, so a detached instance from the throwaway scope is fine; substitute the actual interface name from Step 1 if it differs from `IAppSettingsService`.)

- [ ] **Step 3: Build, test, verify, commit**

Run: `dotnet build DiffusionNexus.sln` → Build succeeded.
Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` → all green.
Manual: trigger Backup Now from the Dataset tab; status text still updates and "next backup" time refreshes after completion.

```bash
git add DiffusionNexus.UI/ViewModels/Tabs/DatasetManagementViewModel.cs
git commit -m "perf(backup): run due-check and settings reads off the UI thread"
```

---

# Phase 3 — Robustness

### Task 8: Batch Unified Console log delivery (one dispatcher post per burst, not per line)

`UnifiedConsoleViewModel.OnLogEntryReceived` (`UnifiedConsoleViewModel.cs:166-181`) does one `Dispatcher.UIThread.Post` per log entry — with `UpdateCounts()` + filtered-collection add inside. Any chatty producer (child-process stdout during installs, verbose phases) floods the dispatcher. Coalesce: enqueue entries into a `ConcurrentQueue`, schedule at most one Background-priority flush at a time.

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/UnifiedConsoleViewModel.cs:166-181` (+ fields)
- Test: `DiffusionNexus.Tests/ViewModels/UnifiedConsoleBatchingTests.cs` (constructor takes `IUnifiedLogger` + `ITaskTracker`, both mockable; the test seam below avoids any Avalonia dependency)

**Interfaces:**
- Produces: `internal Action<Action> UnifiedConsoleViewModel.ScheduleFlush { get; set; }` — test seam, defaults to a Background-priority dispatcher post.

- [ ] **Step 1: Write the failing test**

Use the repo's existing mocking approach for `IUnifiedLogger`/`ITaskTracker` (check how existing `UnifiedConsoleViewModel` tests construct it: `grep -rln "UnifiedConsoleViewModel" DiffusionNexus.Tests/`; reuse their fakes if present, otherwise minimal stubs where `LogStream` is a controllable `IObservable<LogEntry>` — e.g. a tiny `Subject`-like fake — and `GetEntries()` returns an empty list).

```csharp
namespace DiffusionNexus.Tests.ViewModels;

public class UnifiedConsoleBatchingTests
{
    [Fact]
    public void BurstOfEntries_SchedulesSingleFlush_AndDeliversAll()
    {
        var logger = new FakeUnifiedLogger();          // reuse/create per existing test conventions
        var vm = new UnifiedConsoleViewModel(logger, new FakeTaskTracker());

        var scheduled = new Queue<Action>();
        vm.ScheduleFlush = scheduled.Enqueue;          // replace the dispatcher seam

        for (var i = 0; i < 50; i++)
            logger.Emit(TestLogEntry(i));              // pushes through LogStream

        Assert.Single(scheduled);                      // burst coalesced into one flush
        scheduled.Dequeue().Invoke();
        Assert.Equal(50, vm.FilteredEntries.Count);

        logger.Emit(TestLogEntry(50));                 // after a flush, a new one is scheduled
        Assert.Single(scheduled);
    }
}
```

(`TestLogEntry(int)` builds a minimal `LogEntry` that passes the default filter — mirror however existing tests construct `LogEntry`.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~UnifiedConsoleBatchingTests"`
Expected: FAIL — `ScheduleFlush` does not exist (compile error).

- [ ] **Step 3: Implement batching**

In `UnifiedConsoleViewModel`, add fields and the seam:

```csharp
    private readonly System.Collections.Concurrent.ConcurrentQueue<LogEntry> _pendingEntries = new();
    private int _flushScheduled;

    /// <summary>
    /// Test seam: how a pending log flush is scheduled onto the UI thread.
    /// Background priority lets bursts of process output coalesce into one pass
    /// instead of one dispatcher post per log line.
    /// </summary>
    internal Action<Action> ScheduleFlush { get; set; } =
        action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
```

Replace `OnLogEntryReceived` (`:166-181`):

```csharp
    private void OnLogEntryReceived(LogEntry entry)
    {
        _pendingEntries.Enqueue(entry);
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
            ScheduleFlush(FlushPendingLogEntries);
    }

    private void FlushPendingLogEntries()
    {
        // Reset the gate BEFORE draining: an entry arriving mid-drain schedules
        // the next flush instead of being stranded in the queue.
        Interlocked.Exchange(ref _flushScheduled, 0);

        var drained = new List<LogEntry>();
        while (_pendingEntries.TryDequeue(out var entry))
            drained.Add(entry);
        if (drained.Count == 0) return;

        lock (_entriesLock)
        {
            _allEntries.AddRange(drained);
        }
        UpdateCounts();

        foreach (var entry in drained)
        {
            if (ShouldInclude(entry))
                FilteredEntries.Add(entry);
        }
    }
```

Add `using System.Threading;` if missing.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~UnifiedConsoleBatchingTests"`
Expected: PASS.

- [ ] **Step 5: Build + full suite + manual check**

Run: `dotnet build DiffusionNexus.sln`; `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` → all green.
Manual: open the Unified Console, start a package install/update (or any process with chatty output) — log lines appear in near-real-time batches and the UI stays responsive.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/ViewModels/UnifiedConsoleViewModel.cs DiffusionNexus.Tests/ViewModels/UnifiedConsoleBatchingTests.cs
git commit -m "perf(console): batch log delivery into single background-priority dispatcher flushes"
```

---

### Task 9: Enable WAL journal mode on the core database

With `Cache=Shared`, no WAL, and rollback journaling, the end-of-backup write (`UpdateLastBackupAtAsync`) can block concurrent UI-thread reads for up to the 30s busy timeout. WAL lets readers proceed during writes. This touches the **core** DB only (`Diffusion_Nexus-core.db`); the SDK DB stays untouched. Per copilot-instructions, run `publish.ps1` before starting this task (no entity/migration change is made, but it is the DB-adjacent safety convention).

**Files:**
- Modify: `DiffusionNexus.DataAccess/Data/DiffusionNexusCoreDbContext.cs:114` (connection string)
- Modify: `DiffusionNexus.UI/App.axaml.cs` — `InitializeDatabase` (add PRAGMA after migration block) and `TryDeleteLockedDatabase` (`:505-…`, also delete `-wal`/`-shm` siblings)
- Test: `DiffusionNexus.Tests/DataAccess/WalJournalModeTests.cs`

- [ ] **Step 1: Write the failing test**

WAL requires a file-backed DB (in-memory SQLite ignores it), so use a temp file:

```csharp
using Microsoft.Data.Sqlite;

namespace DiffusionNexus.Tests.DataAccess;

public class WalJournalModeTests
{
    [Fact]
    public void ConnectionString_DoesNotUseSharedCache()
    {
        var cs = DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext
            .GetConnectionString(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        Assert.DoesNotContain("Cache=Shared", cs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JournalModePragma_PersistsWal()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var cs = DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext.GetConnectionString(dir);

            using (var connection = new SqliteConnection(cs))
            {
                connection.Open();
                using var enable = connection.CreateCommand();
                enable.CommandText = "PRAGMA journal_mode=WAL;";
                enable.ExecuteScalar();
            }

            // New connection: WAL must persist in the database file.
            using (var connection = new SqliteConnection(cs))
            {
                connection.Open();
                using var query = connection.CreateCommand();
                query.CommandText = "PRAGMA journal_mode;";
                Assert.Equal("wal", (string?)query.ExecuteScalar());
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify the first fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~WalJournalModeTests"`
Expected: `ConnectionString_DoesNotUseSharedCache` FAILS (string still contains `Cache=Shared`); `JournalModePragma_PersistsWal` PASSES (it exercises SQLite itself — it pins the behavior the app change relies on).

- [ ] **Step 3: Change the connection string**

`DiffusionNexusCoreDbContext.cs:113-114` — replace:

```csharp
        // Use default timeout and disable pooling to prevent connection locking issues
        return $"Data Source={path};Mode=ReadWriteCreate;Cache=Shared;Pooling=False;Default Timeout=30";
```

with:

```csharp
        // No shared cache: with WAL journaling (set at startup) private-cache
        // connections let readers proceed while a writer commits — shared cache
        // serialized them and stalled UI-thread reads behind background writes.
        return $"Data Source={path};Mode=ReadWriteCreate;Pooling=False;Default Timeout=30";
```

- [ ] **Step 4: Set WAL at startup**

In `App.InitializeDatabase`, immediately after the `CheckAndRepairSchema(dbContext);` call at `:371` (inside the `try`), add:

```csharp
            // WAL: readers no longer block behind writers (e.g. the end-of-backup
            // LastBackupAt write). Persistent — set once per launch is idempotent.
            Serilog.Log.Information("InitializeDatabase: Ensuring WAL journal mode...");
            dbContext.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
```

(`Microsoft.EntityFrameworkCore` is already imported — `ExecuteSqlRaw` is available.)

- [ ] **Step 5: Clean up WAL sidecars in the locked-DB recovery path**

In `TryDeleteLockedDatabase` (`App.axaml.cs:505-…`), wherever the `.sqlite`/db file is deleted, also delete the sidecars (adapt to the exact variable names in the method):

```csharp
            // WAL sidecars must go with the main file, or SQLite resurrects state.
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var sidecar = dbFile + suffix;
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
```

- [ ] **Step 6: Run tests, build, verify, commit**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~WalJournalModeTests"` → both PASS.
Run: `dotnet build DiffusionNexus.sln`; full `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` → all green (watch for tests that asserted the old connection string).
Manual: launch the app; confirm `Diffusion_Nexus-core.db-wal` appears in `%LocalAppData%\DiffusionNexus\Data\`; run a backup while clicking through Model/Dataset views — no multi-second stalls at backup completion.

```bash
git add DiffusionNexus.DataAccess/Data/DiffusionNexusCoreDbContext.cs DiffusionNexus.UI/App.axaml.cs DiffusionNexus.Tests/DataAccess/WalJournalModeTests.cs
git commit -m "perf(db): WAL journal mode + private cache so UI reads don't block behind background writes"
```

---

### Task 10: Make the restore-path analysis methods truly asynchronous

`AnalyzeBackupAsync` (`DatasetBackupService.cs:317`) and `GetCurrentStorageStatsAsync` (`:389`) do all their ZIP/filesystem scanning synchronously and return `Task.FromResult` — and they are awaited **directly on the UI thread** in the restore flow (`SettingsViewModel.cs:1080`, `:1088`). Fix at the service so every caller benefits.

**Files:**
- Modify: `DiffusionNexus.Service/Services/DatasetBackupService.cs:317-…` and `:389-…`
- Test: existing coverage — check `grep -rln "AnalyzeBackupAsync\|GetCurrentStorageStatsAsync" DiffusionNexus.Tests/`; if no test exists for `AnalyzeBackupAsync`, add the smoke test below.

- [ ] **Step 1 (conditional): Add a smoke test if none exists**

```csharp
namespace DiffusionNexus.Tests.Services;

public class DatasetBackupAnalysisTests
{
    [Fact]
    public async Task AnalyzeBackupAsync_CountsEntries_OfASmallZip()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var contentDir = Directory.CreateTempSubdirectory();
            await File.WriteAllTextAsync(Path.Combine(contentDir.FullName, "a.txt"), "alpha");
            await File.WriteAllTextAsync(Path.Combine(contentDir.FullName, "b.txt"), "beta");
            var zipPath = Path.Combine(dir.FullName, "backup.zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(contentDir.FullName, zipPath);

            var service = /* construct DatasetBackupService the same way existing
                             DatasetBackupService tests do (reuse their fakes) */;

            var result = await service.AnalyzeBackupAsync(zipPath);

            Assert.Equal(2, result.TotalFiles); // adapt property name to BackupAnalysisResult's actual shape
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
```

Mirror the construction/fakes and the result-property names from the existing `DatasetBackupService` tests — the assertion intent is fixed (2 entries counted), the exact member names must match the real `BackupAnalysisResult`.

- [ ] **Step 2: Run it (or the existing analysis tests) — must PASS against current code**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~DatasetBackup"`
Expected: PASS — this pins behavior before the refactor (the change is threading-only).

- [ ] **Step 3: Wrap the bodies in `Task.Run`**

For each of the two methods, rename the existing method body into a private synchronous `…Core` method and make the public method dispatch to it. Shape (apply to both, preserving the exact current signatures from `IDatasetBackupService`):

```csharp
    public Task<BackupAnalysisResult> AnalyzeBackupAsync(string backupFilePath, CancellationToken cancellationToken = default)
        => Task.Run(() => AnalyzeBackupCore(backupFilePath, cancellationToken), cancellationToken);

    private BackupAnalysisResult AnalyzeBackupCore(string backupFilePath, CancellationToken cancellationToken)
    {
        // former method body, minus the Task.FromResult wrapper: return the result directly
    }
```

```csharp
    public Task<StorageStats> GetCurrentStorageStatsAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => GetCurrentStorageStatsCore(cancellationToken), cancellationToken);

    private StorageStats GetCurrentStorageStatsCore(CancellationToken cancellationToken)
    {
        // former method body, minus the Task.FromResult wrapper: return the result directly
    }
```

(Take the true parameter lists and return types from the current file — the pattern is what matters: public method = `Task.Run` dispatch, private `…Core` = the old body returning the value directly. If the current methods `await` anything internally, keep those awaits by making the Core methods `async` and calling them directly inside `Task.Run`.)

- [ ] **Step 4: Build, test, verify, commit**

Run: `dotnet build DiffusionNexus.sln`; `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` → all green.
Manual: Settings → Load/analyze a backup zip — the analysis dialog data appears without freezing the window (open a large zip to feel it).

```bash
git add DiffusionNexus.Service/Services/DatasetBackupService.cs DiffusionNexus.Tests/Services/DatasetBackupAnalysisTests.cs
git commit -m "perf(restore): run backup analysis and storage stats on a pool thread"
```

---

## Final integration

- [ ] Run the full suite one last time: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` → all green.
- [ ] Capture before/after startup timings from the Serilog logs (process start → `Main window Show() called`) for the PR description.
- [ ] Push the branch and open a PR against `develop` (never merge directly). Suggested title: `perf: fix startup time, backup UI freeze, and LoRA Viewer navigation hang`.

## Explicitly out of scope (future candidates, do not do now)

- True virtualization of the tile grid (`ItemsRepeater` + `UniformGridLayout`) — would replace the manual windowing entirely; large blast radius.
- Lazy on-demand module construction — Task 6 already moves module construction off the pre-Show path; per-module laziness adds navigation complexity for little residual gain.
- Coalescing `ActivityLogServiceBridge` events — pointless once Task 2 caps report volume.
- Deferring the startup network calls (Civitai catalog, server-message gist, installer update checks) — already background; their UI-thread continuations are negligible after the tasks above.
