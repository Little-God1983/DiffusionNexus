# Startup Ready-Check Screen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the frozen "Starting DiffusionNexus…" overlay with an animated ready-check list over a startup that never blocks the UI thread, vanishing only when the app is provably responsive.

**Architecture:** Three layers. (1) De-blocking: module registration is chunked one-module-per-Background-dispatcher-tick; the LoRA file verify moves its work inside `Task.Run`; the autocomplete trie load defers until the UI-ready signal and runs at `BelowNormal` priority. (2) Progress model: a plain-C# `StartupProgressService` singleton holds the ordered check list (Database → modules → Diffusion Engine → Updates) with Pending/Running/Done/Failed states. (3) UI: a checklist card bound to the service replaces the overlay's inner panel; the overlay is dismissed only after all core checks are terminal AND a Background-priority drain sentinel has run (structural guarantee: UI responsive at vanish).

**Tech Stack:** .NET 10, Avalonia 11, CommunityToolkit.Mvvm, xUnit (DiffusionNexus.Tests).

**Spec:** `docs/superpowers/specs/2026-07-15-startup-ready-check-design.md` (user-approved). Read it before starting a task if the task's intent is unclear.

## Global Constraints

- Repo: `e:\Repos\DiffusionNexus`, branch `feature/ui-performance-improvements` (all work goes into PR #410 — user decision). Never push to develop/main; do not open new PRs.
- Build: `dotnet build DiffusionNexus.sln`. Tests: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` (filtered while iterating; full project before each commit; NEVER bare `dotnet test` on the solution — it stalls).
- Do NOT add global Avalonia test bootstrapping to DiffusionNexus.Tests. `StartupProgressService` and `AutoCompleteService` are plain C# precisely so they are testable without it. UI/composition changes are verified by running the app (controller does this).
- Line numbers reference branch `feature/ui-performance-improvements` @ f7f3d77 + spec commit; they may drift a few lines — anchor by the quoted code.
- Hard acceptance criterion (user): **the UI must be responsive the moment the overlay vanishes.** The drain-sentinel ordering in Task 4 is the load-bearing implementation of this — do not reorder it.
- Do NOT launch the GUI app from implementer subagents; the controller runs all app-level verification.

---

### Task 1: `StartupProgressService` — the check-list state machine

**Files:**
- Create: `DiffusionNexus.UI/Services/Startup/StartupCheck.cs`
- Create: `DiffusionNexus.UI/Services/Startup/StartupProgressService.cs`
- Test: `DiffusionNexus.Tests/Services/StartupProgressServiceTests.cs`

**Interfaces:**
- Produces (consumed by Tasks 2, 4, 5):
  - `enum StartupCheckState { Pending, Running, Done, Failed }`
  - `sealed class StartupCheck { string Id; string DisplayName; StartupCheckState State; string? Error; bool GatesReadiness; }`
  - `sealed class StartupProgressService` with:
    - `IReadOnlyList<StartupCheck> Checks` (fixed order, built by ctor)
    - `event Action<StartupCheck>? CheckChanged` (raised synchronously on the caller's thread — callers are UI-thread startup phases)
    - `void Begin(string id)`, `void Complete(string id)`, `void Fail(string id, string message)`
    - `bool CoreChecksTerminal` — true when every check with `GatesReadiness == true` is Done or Failed
    - `void SignalUiReady()` / `Task UiReady` — TaskCompletionSource-backed; completed exactly once by the app after the drain sentinel (Task 4)
    - `static IReadOnlyList<StartupCheck> BuildDefaultChecks(bool includeDiffusionCanvas)` — the single source of truth for the list
- No Avalonia types anywhere in these files.

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.UI.Services.Startup;

namespace DiffusionNexus.Tests.Services;

public class StartupProgressServiceTests
{
    private static StartupProgressService NewService(bool canvas = false)
        => new(StartupProgressService.BuildDefaultChecks(includeDiffusionCanvas: canvas));

    [Fact]
    public void DefaultChecks_HaveExpectedOrderAndGating()
    {
        var svc = NewService();
        Assert.Equal(
            new[] { "database", "installer-manager", "lora-dataset-helper", "lora-viewer",
                    "generation-gallery", "image-comparer", "workflows", "settings",
                    "diffusion-engine", "updates" },
            svc.Checks.Select(c => c.Id).ToArray());
        Assert.All(svc.Checks.Where(c => c.Id != "updates"), c => Assert.True(c.GatesReadiness));
        Assert.False(svc.Checks.Single(c => c.Id == "updates").GatesReadiness);
    }

    [Fact]
    public void CanvasFlag_InsertsDiffusionCanvasAfterGenerationGallery()
    {
        var svc = NewService(canvas: true);
        var ids = svc.Checks.Select(c => c.Id).ToList();
        Assert.Equal(ids.IndexOf("generation-gallery") + 1, ids.IndexOf("diffusion-canvas"));
    }

    [Fact]
    public void BeginCompleteFail_TransitionStatesAndRaiseEvents()
    {
        var svc = NewService();
        var raised = new List<(string Id, StartupCheckState State)>();
        svc.CheckChanged += c => raised.Add((c.Id, c.State));

        svc.Begin("database");
        svc.Complete("database");
        svc.Begin("settings");
        svc.Fail("settings", "boom");

        Assert.Equal(StartupCheckState.Done, svc.Checks.Single(c => c.Id == "database").State);
        var settings = svc.Checks.Single(c => c.Id == "settings");
        Assert.Equal(StartupCheckState.Failed, settings.State);
        Assert.Equal("boom", settings.Error);
        Assert.Equal(
            new[] { ("database", StartupCheckState.Running), ("database", StartupCheckState.Done),
                    ("settings", StartupCheckState.Running), ("settings", StartupCheckState.Failed) },
            raised.ToArray());
    }

    [Fact]
    public void CoreChecksTerminal_IgnoresUpdates_AndCountsFailedAsTerminal()
    {
        var svc = NewService();
        foreach (var c in svc.Checks.Where(c => c.GatesReadiness))
        {
            Assert.False(svc.CoreChecksTerminal);
            svc.Begin(c.Id);
            if (c.Id == "lora-viewer") svc.Fail(c.Id, "x"); else svc.Complete(c.Id);
        }
        Assert.True(svc.CoreChecksTerminal); // "updates" still Pending — must not gate
    }

    [Fact]
    public void UiReady_CompletesOnceOnSignal()
    {
        var svc = NewService();
        Assert.False(svc.UiReady.IsCompleted);
        svc.SignalUiReady();
        svc.SignalUiReady(); // idempotent, must not throw
        Assert.True(svc.UiReady.IsCompletedSuccessfully);
    }

    [Fact]
    public void UnknownId_Throws()
    {
        var svc = NewService();
        Assert.Throws<ArgumentException>(() => svc.Begin("nope"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~StartupProgressServiceTests"`
Expected: FAIL — compile error, types do not exist.

- [ ] **Step 3: Implement**

`DiffusionNexus.UI/Services/Startup/StartupCheck.cs`:

```csharp
namespace DiffusionNexus.UI.Services.Startup;

public enum StartupCheckState { Pending, Running, Done, Failed }

/// <summary>
/// One row of the startup ready-check list. Mutable state is owned by
/// <see cref="StartupProgressService"/>; consumers only read.
/// </summary>
public sealed class StartupCheck
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>False for checks that must never delay overlay dismissal (Updates).</summary>
    public bool GatesReadiness { get; init; } = true;

    public StartupCheckState State { get; internal set; } = StartupCheckState.Pending;
    public string? Error { get; internal set; }
}
```

`DiffusionNexus.UI/Services/Startup/StartupProgressService.cs`:

```csharp
namespace DiffusionNexus.UI.Services.Startup;

/// <summary>
/// Ordered startup ready-check list (spec 2026-07-15). Plain C# — no Avalonia —
/// so it is unit-testable and usable from any startup phase. CheckChanged is
/// raised synchronously on the caller's thread; all production callers are
/// UI-thread startup phases, so the overlay ViewModel needs no marshaling.
/// </summary>
public sealed class StartupProgressService
{
    private readonly List<StartupCheck> _checks;
    private readonly TaskCompletionSource _uiReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StartupProgressService(IReadOnlyList<StartupCheck> checks)
        => _checks = [.. checks];

    public IReadOnlyList<StartupCheck> Checks => _checks;

    public event Action<StartupCheck>? CheckChanged;

    /// <summary>Completed by the app after the overlay's dispatcher-drain sentinel
    /// has run — i.e., when the UI is provably responsive. Deferred background
    /// work (autocomplete trie) starts on this signal.</summary>
    public Task UiReady => _uiReady.Task;

    public bool CoreChecksTerminal => _checks
        .Where(c => c.GatesReadiness)
        .All(c => c.State is StartupCheckState.Done or StartupCheckState.Failed);

    public void Begin(string id) => Set(id, StartupCheckState.Running, null);
    public void Complete(string id) => Set(id, StartupCheckState.Done, null);
    public void Fail(string id, string message) => Set(id, StartupCheckState.Failed, message);

    public void SignalUiReady() => _uiReady.TrySetResult();

    private void Set(string id, StartupCheckState state, string? error)
    {
        var check = _checks.FirstOrDefault(c => c.Id == id)
            ?? throw new ArgumentException($"Unknown startup check '{id}'.", nameof(id));
        check.State = state;
        check.Error = error;
        CheckChanged?.Invoke(check);
    }

    /// <summary>Single source of truth for the check list. Module entries MUST match
    /// the registration order in App.RegisterModulesAsync (Task 4).</summary>
    public static IReadOnlyList<StartupCheck> BuildDefaultChecks(bool includeDiffusionCanvas)
    {
        var checks = new List<StartupCheck>
        {
            new() { Id = "database", DisplayName = "Database" },
            new() { Id = "installer-manager", DisplayName = "Installer Manager" },
            new() { Id = "lora-dataset-helper", DisplayName = "LoRA Dataset Helper" },
            new() { Id = "lora-viewer", DisplayName = "LoRA Viewer" },
            new() { Id = "generation-gallery", DisplayName = "Generation Gallery" },
        };
        if (includeDiffusionCanvas)
            checks.Add(new() { Id = "diffusion-canvas", DisplayName = "Diffusion Canvas" });
        checks.AddRange(
        [
            new StartupCheck { Id = "image-comparer", DisplayName = "Image Comparer" },
            new StartupCheck { Id = "workflows", DisplayName = "Workflows" },
            new StartupCheck { Id = "settings", DisplayName = "Settings" },
            new StartupCheck { Id = "diffusion-engine", DisplayName = "Diffusion Engine" },
            new StartupCheck { Id = "updates", DisplayName = "Updates", GatesReadiness = false },
        ]);
        return checks;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~StartupProgressServiceTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Full suite + commit**

Run: `dotnet build DiffusionNexus.sln` → succeeded; `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` → all green.

```bash
git add DiffusionNexus.UI/Services/Startup/StartupCheck.cs DiffusionNexus.UI/Services/Startup/StartupProgressService.cs DiffusionNexus.Tests/Services/StartupProgressServiceTests.cs
git commit -m "feat(startup): StartupProgressService check-list state machine"
```

---

### Task 2: Defer + down-prioritize the autocomplete trie load

**Files:**
- Modify: `DiffusionNexus.UI/Services/SpellCheck/AutoCompleteService.cs:17-27` (constructor)
- Modify: `DiffusionNexus.UI/App.axaml.cs:1047` (DI registration — anchor: `services.AddSingleton<IAutoCompleteService, AutoCompleteService>();`)
- Test: extend `DiffusionNexus.Tests/...` wherever existing `AutoCompleteService` tests live (`grep -rln "AutoCompleteService" DiffusionNexus.Tests/` — add to that file; if none exists, create `DiffusionNexus.Tests/Services/AutoCompleteDeferralTests.cs`)

**Interfaces:**
- Consumes: `StartupProgressService.UiReady` (Task 1).
- Produces: `AutoCompleteService(Task startSignal, string? dictionaryDirectory = null)` ctor overload. The existing `AutoCompleteService(string?)` ctor keeps its immediate-load behavior (existing tests must not change).

- [ ] **Step 1: Write the failing test**

Append to the existing AutoCompleteService test file (or the new file named above):

```csharp
    [Fact]
    public async Task DeferredCtor_DoesNotStartLoading_UntilSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Nonexistent directory: LoadFromDictionary returns immediately once it runs,
        // so LoadCompleted completing == the load ran.
        var svc = new AutoCompleteService(signal.Task,
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));

        await Task.Delay(150);
        Assert.False(svc.LoadCompleted.IsCompleted);

        signal.SetResult();
        await svc.LoadCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(svc.LoadCompleted.IsCompletedSuccessfully);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~DeferredCtor_DoesNotStartLoading_UntilSignal"`
Expected: FAIL — no such constructor (compile error).

- [ ] **Step 3: Implement the deferred constructor**

In `AutoCompleteService.cs`, replace the single constructor (`:17-27`) with:

```csharp
    /// <summary>
    /// Creates an AutoCompleteService and seeds it from the Hunspell dictionary
    /// immediately on a background task. Used by tests and non-startup callers.
    /// </summary>
    public AutoCompleteService(string? dictionaryDirectory = null)
        : this(Task.CompletedTask, dictionaryDirectory)
    {
    }

    /// <summary>
    /// Defers the dictionary load until <paramref name="startSignal"/> completes,
    /// then runs it on a dedicated BelowNormal-priority thread. The load costs
    /// ~20s of CPU (Hunspell suffix expansion); at startup it must neither run
    /// on the UI thread nor compete with startup work for cores — the app signals
    /// StartupProgressService.UiReady after the overlay's drain sentinel, and the
    /// load begins only then. GetSuggestions/RecordWord tolerate the not-yet-loaded
    /// state (they briefly contend on _lock and see a partial trie).
    /// </summary>
    public AutoCompleteService(Task startSignal, string? dictionaryDirectory = null)
    {
        var dir = dictionaryDirectory ?? Path.Combine(AppContext.BaseDirectory, "Dictionaries");
        LoadCompleted = LoadAfterSignalAsync(startSignal, dir);
    }

    private async Task LoadAfterSignalAsync(Task startSignal, string dir)
    {
        try { await startSignal.ConfigureAwait(false); }
        catch { /* a faulted signal must not kill autocomplete; load anyway */ }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { LoadFromDictionary(dir); tcs.TrySetResult(); })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "AutoCompleteTrieLoad",
        };
        thread.Start();
        await tcs.Task.ConfigureAwait(false);
    }
```

(`LoadFromDictionary` already catches internally and never throws.)

- [ ] **Step 4: Wire DI to the deferred signal**

In `App.axaml.cs` `ConfigureServices`, `StartupProgressService` must be registered (this task adds it; Task 4 consumes the same registration). Immediately BEFORE the line `services.AddSingleton<IAutoCompleteService, AutoCompleteService>();` add:

```csharp
        services.AddSingleton(_ => new DiffusionNexus.UI.Services.Startup.StartupProgressService(
            DiffusionNexus.UI.Services.Startup.StartupProgressService.BuildDefaultChecks(
                DiffusionNexus.UI.Services.Diffusion.DiffusionFeatureFlags.UseLocalDiffusionBackend)));
```

and replace the `IAutoCompleteService` registration with:

```csharp
        services.AddSingleton<IAutoCompleteService>(sp => new AutoCompleteService(
            sp.GetRequiredService<DiffusionNexus.UI.Services.Startup.StartupProgressService>().UiReady));
```

- [ ] **Step 5: Run tests (deferral + all existing AutoComplete tests), build, full suite, commit**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~AutoComplete"` → all green (existing tests use the immediate ctor and must be untouched).
Run: `dotnet build DiffusionNexus.sln`; full test project → all green.

```bash
git add DiffusionNexus.UI/Services/SpellCheck/AutoCompleteService.cs DiffusionNexus.UI/App.axaml.cs DiffusionNexus.Tests/
git commit -m "perf(startup): defer autocomplete trie load until UI-ready; BelowNormal priority"
```

---

### Task 3: Move the LoRA file verify genuinely off the UI thread

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs:418-442` (`VerifyFilesInBackgroundAsync`)
- Test: none new (thin dispatch change on a VM path that needs a live dispatcher; verified by trace/app-run — controller owns it). Existing suite must stay green.

**Interfaces:** none produced; self-contained.

**Why:** the CPU trace showed 10.1s of UI-thread time in `ModelFileSyncService.TryFindMovedFileAsync` (7.3s hashing) because this method awaits the service call on the UI context — every continuation of the fake-async service runs on the dispatcher. Additionally its `Progress<SyncProgress>` handler double-hops (Progress marshal + nested `Dispatcher.UIThread.Post`) per report.

- [ ] **Step 1: Replace the method**

Replace `VerifyFilesInBackgroundAsync` (`:418-442`) with:

```csharp
    private async Task VerifyFilesInBackgroundAsync()
    {
        try
        {
            // Progress<T> captures the UI SynchronizationContext here (UI thread),
            // so the callback is already marshaled — no nested Post needed.
            var progress = new Progress<SyncProgress>(p =>
            {
                if (p.Phase == "Verification complete")
                {
                    SyncStatus = null; // Clear status when done
                }
            });

            // The verify walks the library and SHA-hashes candidate files; its
            // "async" service continuations otherwise resume on the UI thread
            // (10s dispatcher hog on a cold cache — see 2026-07-15 startup trace).
            // Run the whole thing on the pool with its own scope.
            await Task.Run(async () =>
            {
                using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
                var syncService = scope.ServiceProvider.GetRequiredService<IModelSyncService>();
                await syncService.VerifyAndSyncFilesAsync(progress);
            });
        }
        catch
        {
            // Silently fail - this is background work
        }
    }
```

- [ ] **Step 2: Build + full suite**

Run: `dotnet build DiffusionNexus.sln` → succeeded; `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` → all green.

- [ ] **Step 3: Commit**

```bash
git add DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs
git commit -m "perf(lora-viewer): run file verification on the thread pool, not the UI dispatcher"
```

---

### Task 4: Chunked module registration + readiness gate in `CompleteStartupAsync`

**Files:**
- Modify: `DiffusionNexus.UI/App.axaml.cs` — `CompleteStartupAsync` (`:179-254`), `RegisterModules` (`:1215-1411`), `LoadStartupDataAsync` (`:1419+`)
- Test: none new (startup composition — controller verifies by app run + the Task 1 unit tests already cover the gate logic).

**Interfaces:**
- Consumes: `StartupProgressService` (Task 1; DI-registered in Task 2).
- Produces: `RegisterModulesAsync(DiffusionNexusMainWindowViewModel, StartupProgressService)` replacing `RegisterModules`; `CompleteStartupAsync` drives Database/Diffusion-Engine checks, the drain sentinel, `SignalUiReady()`, and `IsStartupComplete`. Task 5's overlay binds to the same service instance.

- [ ] **Step 1: Convert `RegisterModules` to chunked `RegisterModulesAsync`**

Rename `private void RegisterModules(DiffusionNexusMainWindowViewModel mainViewModel)` to:

```csharp
    /// <summary>
    /// Builds and registers every module, one module per Background-priority
    /// dispatcher tick, so rendering and input interleave with construction
    /// (the old synchronous loop held the UI thread ~3.6s and froze the
    /// startup overlay). Each module reports Running/Done/Failed to the
    /// ready-check list. Must be called on the UI thread.
    /// </summary>
    private async Task RegisterModulesAsync(
        DiffusionNexusMainWindowViewModel mainViewModel,
        DiffusionNexus.UI.Services.Startup.StartupProgressService startupProgress)
```

Inside, wrap EACH existing module block in this exact pattern. The blocks and their check ids, in current source order (anchor by the comment headers quoted):

| # | Anchor comment in current code | Check id |
|---|---|---|
| 1 | `// Installer Manager module` | `installer-manager` |
| 2 | `// LoRA Dataset Helper module - default on startup` | `lora-dataset-helper` |
| 3 | `// LoRA Viewer module` | `lora-viewer` |
| 4 | `// Generation Gallery module` | `generation-gallery` |
| 5 | `// Diffusion Canvas module — local Z-Image-Turbo...` (inside its existing `if (DiffusionFeatureFlags.UseLocalDiffusionBackend)`) | `diffusion-canvas` |
| 6 | `// Image Comparer module` | `image-comparer` |
| 7 | `// Pipelines module — tile gallery...` | `workflows` |
| 8 | `// Settings module` | `settings` |

Pattern — shown in full for block 1; apply identically to blocks 2–8 (each keeps its existing body verbatim, including the Installer-Manager console wiring that follows its registration):

```csharp
        startupProgress.Begin("installer-manager");
        try
        {
            // ==== existing block body, unchanged, from
            // `var installerManagerVm = Services!.GetRequiredService<InstallerManagerViewModel>();`
            // through the `installerManagerVm.InstallerUpdateStateChanged += ...` console wiring ====
            startupProgress.Complete("installer-manager");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Module registration failed: {Module}", "installer-manager");
            startupProgress.Fail("installer-manager", ex.Message);
        }
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            static () => { }, Avalonia.Threading.DispatcherPriority.Background);
```

Notes for the implementer:
- The trailing no-op `InvokeAsync(..., Background)` is the chunk boundary: awaiting it yields the UI thread until everything above Background priority (input, render) has run. One boundary after EVERY block, including the last.
- Block 5 keeps its existing `if (DiffusionFeatureFlags.UseLocalDiffusionBackend)` guard OUTSIDE the Begin/try (when the flag is off there is no `diffusion-canvas` check — `BuildDefaultChecks` didn't create one — so Begin must not be called).
- A failed block must not abort the loop: the `try/catch` per block replaces today's all-or-nothing behavior deliberately (spec §4). Locals used later (e.g. `installerManagerVm`) must be declared BEFORE the `try` (`InstallerManagerViewModel? installerManagerVm = null;`) and null-guarded where consumed: the event-aggregator wiring section and the `LoadStartupDataAsync` call at the end each get `if (x is null) return;`-style guards — concretely, wrap the entire "Subscribe to navigation events" section and the `LoadStartupDataAsync` call in `if (installerManagerVm is not null && loraDatasetHelperVm is not null && loraViewerVm is not null && generationGalleryVm is not null && imageCompareVm is not null && pipelinesVm is not null && settingsVm is not null && settingsModule is not null && loraDatasetHelperModule is not null && imageComparerModule is not null && pipelinesModule is not null)` (single guard; if any module failed to build, skip wiring + data load and log a warning — the app is already degraded and the checks show which module died).
- The `_ = LoadStartupDataAsync(...)` tail call gains one argument: `startupProgress` (see Step 3).

- [ ] **Step 2: Drive the gate from `CompleteStartupAsync`**

Replace the body of `CompleteStartupAsync` (`:179-254`) with (keep the existing doc comment, extend it with the gate note):

```csharp
    private async Task CompleteStartupAsync(DiffusionNexusMainWindowViewModel mainViewModel)
    {
        var startupProgress = Services!.GetRequiredService<DiffusionNexus.UI.Services.Startup.StartupProgressService>();
        try
        {
            startupProgress.Begin("database");
            // Database initialization is pure logging + EF work — safe off the UI thread.
            await Task.Run(() =>
            {
                Serilog.Log.Information("Initializing app database...");
                InitializeDatabase();
                Serilog.Log.Information("Initializing SDK database...");
                InitializeSdkDatabase();
            });
            startupProgress.Complete("database");

            // Everything below resumes on the Avalonia UI thread (its SynchronizationContext
            // was captured at the await above). Do NOT add ConfigureAwait(false) in this method.
            Serilog.Log.Information("Initializing thumbnail service...");
            InitializeThumbnailService();

            Serilog.Log.Information("Initializing spell check services...");
            InitializeSpellCheckServices();

            Serilog.Log.Information("Initializing instance process manager...");
            _ = Services!.GetRequiredService<IInstanceProcessManager>();

            Serilog.Log.Information("Initializing Civitai base-model catalog...");
            InitializeCivitaiBaseModelCatalog();

            Serilog.Log.Information("Initializing instance management...");
            mainViewModel.InitializeInstanceManagement();

            Serilog.Log.Information("Registering modules...");
            await RegisterModulesAsync(mainViewModel, startupProgress);

            // Diffusion Engine: the heavy native warm-up happens inside module
            // construction; this check confirms the backend singleton resolves.
            if (DiffusionNexus.UI.Services.Diffusion.DiffusionFeatureFlags.UseLocalDiffusionBackend)
            {
                startupProgress.Begin("diffusion-engine");
                try
                {
                    _ = Services!.GetRequiredService<DiffusionNexus.UI.Services.Diffusion.LocalDiffusionBackendProvider>();
                    startupProgress.Complete("diffusion-engine");
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "Diffusion engine warm-up failed");
                    startupProgress.Fail("diffusion-engine", ex.Message);
                }

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
            else
            {
                startupProgress.Complete("diffusion-engine");
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Fatal(ex, "Deferred startup initialization failed");
            foreach (var check in startupProgress.Checks)
            {
                if (check.State is DiffusionNexus.UI.Services.Startup.StartupCheckState.Pending
                                or DiffusionNexus.UI.Services.Startup.StartupCheckState.Running
                    && check.GatesReadiness)
                {
                    startupProgress.Fail(check.Id, ex.Message);
                }
            }
            Services?.GetService<IActivityLogService>()
                ?.LogError("Startup", "Startup initialization failed — some features may be unavailable", ex);
        }
        finally
        {
            // HARD REQUIREMENT (user): the UI must be responsive when the overlay
            // vanishes. All core checks are terminal here; drain everything at-and-
            // above Background priority before dismissing, then release deferred
            // background work (autocomplete trie) via SignalUiReady.
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                static () => { }, Avalonia.Threading.DispatcherPriority.Background);
            mainViewModel.IsStartupComplete = true;
            startupProgress.SignalUiReady();
        }
    }
```

Notes:
- `await` inside `finally` is legal C# and stays on the UI thread here.
- The old un-gated `startupProgress.Complete("diffusion-engine")` else-branch keeps the check honest when the local backend is compiled out.
- The `OutputsFolderRegistrar` block moved inside the flag branch it already depended on — verify with the diff that its behavior is unchanged.

- [ ] **Step 3: Wire the `updates` check into `LoadStartupDataAsync`**

Change the signature to accept the service:

```csharp
    private static async Task LoadStartupDataAsync(
        DiffusionNexusMainWindowViewModel mainViewModel,
        SettingsViewModel settingsVm,
        LoraViewerViewModel loraViewerVm,
        GenerationGalleryViewModel generationGalleryVm,
        InstallerManagerViewModel installerManagerVm,
        LoraDatasetHelperViewModel loraDatasetHelperVm,
        DiffusionNexus.UI.Services.Startup.StartupProgressService startupProgress)
```

and wrap the existing `installerManager` Timed phase (the update checks run inside `LoadInstallationsCommand`):

```csharp
                Timed(sw, "installerManager", async () =>
                {
                    startupProgress.Begin("updates");
                    try
                    {
                        await installerManagerVm.LoadInstallationsCommand.ExecuteAsync(null);
                        startupProgress.Complete("updates");
                    }
                    catch (Exception ex)
                    {
                        startupProgress.Fail("updates", ex.Message);
                        throw;
                    }
                }),
```

(The other Timed phases are unchanged. `updates` has `GatesReadiness == false`, so it never delays the overlay; when it completes after dismissal nobody is listening — that is by design.)

- [ ] **Step 4: Build + full suite**

Run: `dotnet build DiffusionNexus.sln` → succeeded; `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` → all green.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/App.axaml.cs
git commit -m "perf(startup): chunk module registration per dispatcher tick; readiness gate with drain sentinel"
```

---

### Task 5: The ready-check overlay UI

**Files:**
- Create: `DiffusionNexus.UI/ViewModels/StartupOverlayViewModel.cs`
- Modify: `DiffusionNexus.UI/ViewModels/DiffusionNexusMainWindowViewModel.cs` (add `StartupOverlay` property)
- Modify: `DiffusionNexus.UI/App.axaml.cs:114-122` (construct overlay VM pre-Show)
- Modify: `DiffusionNexus.UI/Views/DiffusionNexusMainWindow.axaml:225-238` (replace the overlay's inner StackPanel)
- Test: `DiffusionNexus.Tests/ViewModels/StartupOverlayViewModelTests.cs`

**Interfaces:**
- Consumes: `StartupProgressService` (Task 1), `IsStartupComplete` (existing).
- Produces: `StartupOverlayViewModel` with `IReadOnlyList<StartupCheckRowViewModel> Rows`; row exposes `DisplayName`, `IsPending`, `IsRunning`, `IsDone`, `IsFailed`, `Error` (INotifyPropertyChanged via CommunityToolkit).

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.UI.Services.Startup;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.Tests.ViewModels;

public class StartupOverlayViewModelTests
{
    [Fact]
    public void Rows_MirrorServiceChecks_InOrder()
    {
        var svc = new StartupProgressService(StartupProgressService.BuildDefaultChecks(false));
        var vm = new StartupOverlayViewModel(svc);
        Assert.Equal(svc.Checks.Select(c => c.DisplayName), vm.Rows.Select(r => r.DisplayName));
        Assert.All(vm.Rows, r => Assert.True(r.IsPending));
    }

    [Fact]
    public void CheckChanged_UpdatesTheMatchingRow_AndRaisesPropertyChanged()
    {
        var svc = new StartupProgressService(StartupProgressService.BuildDefaultChecks(false));
        var vm = new StartupOverlayViewModel(svc);
        var row = vm.Rows.Single(r => r.DisplayName == "Database");
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        svc.Begin("database");
        Assert.True(row.IsRunning);
        svc.Complete("database");
        Assert.True(row.IsDone);
        Assert.Contains(nameof(StartupCheckRowViewModel.IsRunning), raised);
        Assert.Contains(nameof(StartupCheckRowViewModel.IsDone), raised);
    }

    [Fact]
    public void FailedCheck_ExposesError()
    {
        var svc = new StartupProgressService(StartupProgressService.BuildDefaultChecks(false));
        var vm = new StartupOverlayViewModel(svc);
        svc.Begin("settings");
        svc.Fail("settings", "boom");
        var row = vm.Rows.Single(r => r.DisplayName == "Settings");
        Assert.True(row.IsFailed);
        Assert.Equal("boom", row.Error);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~StartupOverlayViewModelTests"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement the ViewModels**

`DiffusionNexus.UI/ViewModels/StartupOverlayViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Services.Startup;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Binds the startup ready-check list to the overlay. CheckChanged is raised
/// on the UI thread by every production caller (startup phases), so no
/// marshaling is needed here.
/// </summary>
public sealed partial class StartupOverlayViewModel : ViewModelBase
{
    public IReadOnlyList<StartupCheckRowViewModel> Rows { get; }

    public StartupOverlayViewModel(StartupProgressService service)
    {
        var rows = service.Checks.ToDictionary(c => c.Id, c => new StartupCheckRowViewModel(c));
        Rows = service.Checks.Select(c => rows[c.Id]).ToList();
        service.CheckChanged += check => rows[check.Id].Refresh(check);
    }
}

public sealed partial class StartupCheckRowViewModel : ObservableObject
{
    public string DisplayName { get; }

    [ObservableProperty] private bool _isPending;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private string? _error;

    public StartupCheckRowViewModel(StartupCheck check)
    {
        DisplayName = check.DisplayName;
        Refresh(check);
    }

    public void Refresh(StartupCheck check)
    {
        IsPending = check.State == StartupCheckState.Pending;
        IsRunning = check.State == StartupCheckState.Running;
        IsDone = check.State == StartupCheckState.Done;
        IsFailed = check.State == StartupCheckState.Failed;
        Error = check.Error;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~StartupOverlayViewModelTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Expose the overlay VM on the main window VM and construct it pre-Show**

In `DiffusionNexusMainWindowViewModel` (near the other `[ObservableProperty]` fields):

```csharp
    /// <summary>Ready-check list shown by the startup overlay (null only in design mode).</summary>
    [ObservableProperty]
    private StartupOverlayViewModel? _startupOverlay;
```

In `App.OnFrameworkInitializationCompleted`, after `var mainViewModel = new DiffusionNexusMainWindowViewModel();` (`:116`) add:

```csharp
                mainViewModel.StartupOverlay = new StartupOverlayViewModel(
                    Services!.GetRequiredService<DiffusionNexus.UI.Services.Startup.StartupProgressService>());
```

(Order matters: `Services` is assigned at `:112`, before this point — the checklist is fully rendered from the first frame.)

- [ ] **Step 6: Replace the overlay's inner panel in the XAML**

In `DiffusionNexusMainWindow.axaml`, replace the `<StackPanel ...>` inside the startup-overlay `Border` (`:230-237` — the one containing `ProgressBar` + `Starting DiffusionNexus…`) with:

```xml
                                    <Border Background="#E01E1E1E"
                                            CornerRadius="8"
                                            Padding="28,20"
                                            MinWidth="320"
                                            HorizontalAlignment="Center"
                                            VerticalAlignment="Center">
                                        <StackPanel Spacing="10">
                                            <TextBlock Text="Starting DiffusionNexus…"
                                                       FontSize="16" FontWeight="SemiBold"
                                                       HorizontalAlignment="Center"
                                                       Margin="0,0,0,6"/>
                                            <ItemsControl ItemsSource="{Binding StartupOverlay.Rows}">
                                                <ItemsControl.ItemTemplate>
                                                    <DataTemplate x:DataType="vm:StartupCheckRowViewModel">
                                                        <Grid ColumnDefinitions="24,*" Margin="0,2">
                                                            <Panel Grid.Column="0" Width="18" Height="18">
                                                                <TextBlock Text="•" Opacity="0.35"
                                                                           IsVisible="{Binding IsPending}"
                                                                           HorizontalAlignment="Center"/>
                                                                <ProgressBar IsIndeterminate="True"
                                                                             IsVisible="{Binding IsRunning}"
                                                                             Width="16" Height="16"/>
                                                                <TextBlock Text="✓" Foreground="#4CAF50" FontWeight="Bold"
                                                                           IsVisible="{Binding IsDone}"
                                                                           HorizontalAlignment="Center">
                                                                    <TextBlock.Transitions>
                                                                        <Transitions>
                                                                            <DoubleTransition Property="Opacity" Duration="0:0:0.25"/>
                                                                        </Transitions>
                                                                    </TextBlock.Transitions>
                                                                </TextBlock>
                                                                <TextBlock Text="✗" Foreground="#F44336" FontWeight="Bold"
                                                                           IsVisible="{Binding IsFailed}"
                                                                           ToolTip.Tip="{Binding Error}"
                                                                           HorizontalAlignment="Center"/>
                                                            </Panel>
                                                            <TextBlock Grid.Column="1" Text="{Binding DisplayName}"
                                                                       VerticalAlignment="Center" Margin="8,0,0,0"/>
                                                        </Grid>
                                                    </DataTemplate>
                                                </ItemsControl.ItemTemplate>
                                            </ItemsControl>
                                        </StackPanel>
                                    </Border>
```

The outer dim `Border` (`Background="#80000000"`, `IsVisible="{Binding !IsStartupComplete}"`) stays exactly as-is — semi-transparent per the spec, input-blocking while visible, dismissed by the Task 4 gate. If Avalonia's indeterminate `ProgressBar` looks wrong at 16×16, substitute the app's existing small-spinner idiom (check how busy overlays in other views render spinners and reuse that control/style — the reuse rule) and note the substitution in the report.

- [ ] **Step 7: Build + full suite + commit**

Run: `dotnet build DiffusionNexus.sln` → succeeded; `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` → all green.

```bash
git add DiffusionNexus.UI/ViewModels/StartupOverlayViewModel.cs DiffusionNexus.UI/ViewModels/DiffusionNexusMainWindowViewModel.cs DiffusionNexus.UI/App.axaml.cs DiffusionNexus.UI/Views/DiffusionNexusMainWindow.axaml DiffusionNexus.Tests/ViewModels/StartupOverlayViewModelTests.cs
git commit -m "feat(startup): animated ready-check overlay bound to StartupProgressService"
```

---

## Final integration (controller-owned)

- [ ] Full suite one last time: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj` → all green.
- [ ] Release build + app run (controller): PrintWindow screenshots at ~1s intervals during startup must show the checklist progressing (≥3 distinct frames — proves the animation never freezes); overlay gone ≤ ~8s warm.
- [ ] **Responsiveness proof:** injected click (PostMessage) ≤500ms after the overlay vanishes must act (e.g. nav toggle). This is the user's hard acceptance criterion.
- [ ] Cold-log check on next real launch: `LoadStartupData:` lines show no synchronous UI-thread head > ~250ms; no unexplained multi-second silent gaps.
- [ ] Push to `feature/ui-performance-improvements`; update PR #410 description with the ready-check screen section + new screenshots.

## Explicitly out of scope

- Hunspell/trie algorithmic optimization; per-module lazy construction; real health-check logic; gating on Updates or file-verify; skip/cancel affordances; fade-out animation of the overlay itself (instant dismissal is fine — the checks are the animation).
