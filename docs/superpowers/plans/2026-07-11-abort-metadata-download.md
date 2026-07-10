# Abort Metadata Download Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user abort the LoRA-viewer "Download Metadata" Civitai sync after it has started, via a Cancel button in the busy overlay.

**Architecture:** Cooperative cancellation. `DownloadMissingMetadataAsync` owns a `CancellationTokenSource`; a new `CancelMetadataDownload` command cancels it. The token is threaded through the five per-model phase loops, each of which calls `ct.ThrowIfCancellationRequested()` per iteration, so the sync stops after the in-flight model finishes. An `IsCancellable` flag scopes the Cancel button to this operation (the busy overlay is shared with the non-cancellable Refresh). Abort is non-destructive — metadata already written stays.

**Tech Stack:** C#, Avalonia UI, CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`), xUnit + FluentAssertions.

## Global Constraints

- Repo: `e:\Repos\DiffusionNexus` (main app), branch `feature/abort-metadata`. All paths below are relative to this repo.
- Never merge into develop/main directly; commit onto the current feature branch only.
- Follow existing patterns: `[ObservableProperty]` backing fields (`_camelCase`), `[RelayCommand]` methods, `IsBusy`/`BusyMessage` come from `BusyViewModelBase`.
- Cancelled-status string is exactly `"Metadata sync cancelled"`; in-progress cancel string is exactly `"Cancelling…"` (real ellipsis character `…`, U+2026).
- Command method name `CancelMetadataDownload` → generated command `CancelMetadataDownloadCommand` (the XAML binds this exact name).
- Cancellation must be non-destructive: already-processed models keep their metadata (guaranteed by throwing between models, never mid-write).

---

### Task 1: ViewModel cancellation plumbing

Adds the `IsCancellable` flag, the `CancelMetadataDownload` command, the CTS lifecycle in `DownloadMissingMetadataAsync`, the `OperationCanceledException` handling, and threads the token through the five phase methods.

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs`
- Test: `DiffusionNexus.Tests/Viewer/LoraViewerCancelMetadataTests.cs` (create)

**Interfaces:**
- Consumes: `BusyViewModelBase.IsBusy` / `BusyMessage` (existing); `SyncStatus` observable (existing, `LoraViewerViewModel.cs:118`); the design-time constructor `new LoraViewerViewModel()` (existing, loads demo data, `_civitaiClient == null`).
- Produces:
  - `bool IsCancellable` (observable property; getter `IsCancellable`, backing `_isCancellable`).
  - `IRelayCommand CancelMetadataDownloadCommand` (generated from `void CancelMetadataDownload()`).
  - `internal void SetActiveMetadataSyncCtsForTest(CancellationTokenSource cts)` — test seam that injects the in-flight sync's CTS.

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests/Viewer/LoraViewerCancelMetadataTests.cs`:

```csharp
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the abort affordance for the LoRA-viewer "Download Metadata" sync.
/// The full end-to-end abort path is exercised by a real-app run (see plan Task 3);
/// these tests lock down the command surface that does not require App.Services / DI.
/// </summary>
public class LoraViewerCancelMetadataTests
{
    private static LoraViewerViewModel CreateViewModel() => new();

    [Fact]
    public void IsCancellableIsFalseByDefault()
    {
        var vm = CreateViewModel();

        vm.IsCancellable.Should().BeFalse(
            "the Cancel button must stay hidden until a metadata sync is running");
    }

    [Fact]
    public void CancelWhenNoSyncRunningIsSafeNoOp()
    {
        var vm = CreateViewModel();

        var act = () => vm.CancelMetadataDownloadCommand.Execute(null);

        act.Should().NotThrow("cancelling with no sync in flight must be a no-op");
        vm.SyncStatus.Should().BeNull(
            "a no-op cancel must not post a misleading 'Cancelling…' status");
        vm.IsCancellable.Should().BeFalse();
    }

    [Fact]
    public void CancelSignalsTheActiveSyncTokenAndFlipsStatus()
    {
        var vm = CreateViewModel();
        using var cts = new System.Threading.CancellationTokenSource();
        vm.SetActiveMetadataSyncCtsForTest(cts); // simulates an in-flight sync
        vm.IsCancellable = true;

        vm.CancelMetadataDownloadCommand.Execute(null);

        cts.IsCancellationRequested.Should().BeTrue(
            "cancel must request cancellation on the running sync's token");
        vm.SyncStatus.Should().Be("Cancelling…");
        vm.IsCancellable.Should().BeFalse(
            "the Cancel button must hide the moment cancellation is requested");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter FullyQualifiedName~LoraViewerCancelMetadataTests`
Expected: FAIL — compile error (`IsCancellable`, `CancelMetadataDownloadCommand`, `SetActiveMetadataSyncCtsForTest` do not exist yet).

- [ ] **Step 3: Add the observable property, command, and test seam**

In `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs`, add to the Observable Properties region (after `_syncStatus` at line 119):

```csharp
    /// <summary>
    /// True only while a metadata-download sync is running. Drives the Cancel button
    /// in the busy overlay so it does not appear during the (non-cancellable) Refresh.
    /// </summary>
    [ObservableProperty]
    private bool _isCancellable;
```

Add the CTS field next to the other CTS fields (near `_updateCheckCts` at line 42):

```csharp
    /// <summary>Cancels the in-flight "Download Metadata" sync; null when idle.</summary>
    private CancellationTokenSource? _metadataSyncCts;
```

Add the command and the internal test seam. Put them immediately after the `DownloadMissingMetadataAsync` method (after its closing brace at line 538):

```csharp
    /// <summary>
    /// Requests cancellation of the in-flight metadata sync. Safe no-op when idle.
    /// The sync stops after the current model finishes (cooperative cancellation).
    /// </summary>
    [RelayCommand]
    private void CancelMetadataDownload()
    {
        if (_metadataSyncCts is null)
        {
            return;
        }

        _metadataSyncCts.Cancel();
        SyncStatus = "Cancelling…";
        // Hide the Cancel button immediately so the click reads as received; the
        // sync itself unwinds up to one model later, where finally resets the rest.
        IsCancellable = false;
    }

    /// <summary>
    /// Test seam: injects a CTS to simulate an in-flight sync without standing up
    /// the full App.Services/DI graph the real sync requires.
    /// </summary>
    internal void SetActiveMetadataSyncCtsForTest(CancellationTokenSource cts)
        => _metadataSyncCts = cts;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter FullyQualifiedName~LoraViewerCancelMetadataTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Wire the CTS lifecycle into `DownloadMissingMetadataAsync`**

Edit `DownloadMissingMetadataAsync` (starts line 428). In the `try` block, replace the opening:

```csharp
        try
        {
            IsBusy = true;
            BusyMessage = "Syncing with Civitai...";
```

with:

```csharp
        _metadataSyncCts = new CancellationTokenSource();
        var ct = _metadataSyncCts.Token;
        try
        {
            IsBusy = true;
            IsCancellable = true;
            BusyMessage = "Syncing with Civitai...";
```

Pass `ct` to each phase call (the calls at lines 501–518). Change:

```csharp
            await SyncMetadataPhaseAsync(apiKey, statusParts);
            await ReprocessLocalFileModelsPhaseAsync(statusParts);
            await RefetchMissingImagesPhaseAsync(apiKey, statusParts);
            await BackfillMissingTagsPhaseAsync(apiKey, statusParts);
            await RebuildTilesFromDatabaseAsync();
            await DownloadMissingThumbnailsPhaseAsync(statusParts);
```

to (leave the surrounding comments intact — only the calls change):

```csharp
            await SyncMetadataPhaseAsync(apiKey, statusParts, ct);
            await ReprocessLocalFileModelsPhaseAsync(statusParts, ct);
            await RefetchMissingImagesPhaseAsync(apiKey, statusParts, ct);
            await BackfillMissingTagsPhaseAsync(apiKey, statusParts, ct);
            await RebuildTilesFromDatabaseAsync();
            await DownloadMissingThumbnailsPhaseAsync(statusParts, ct);
```

Add a cancellation-aware catch **before** the existing `catch (Exception ex)` (line 528):

```csharp
        catch (OperationCanceledException)
        {
            SyncStatus = "Metadata sync cancelled";
            _logger?.Info(LogCategory.Network, "CivitaiSync", "Metadata sync cancelled by user");
        }
```

Update the `finally` block (lines 533–537) to reset the new state and dispose the CTS:

```csharp
        finally
        {
            IsBusy = false;
            IsCancellable = false;
            BusyMessage = null;
            _metadataSyncCts?.Dispose();
            _metadataSyncCts = null;
        }
```

- [ ] **Step 6: Thread the token through the five phase signatures + loop guards**

For each phase, add a `CancellationToken ct` parameter and a `ct.ThrowIfCancellationRequested();` as the **first statement inside the `for` loop body**. Where a phase already has an `await Task.Delay(...)`, pass `ct` to it so a pending cancel interrupts the pacing delay.

`SyncMetadataPhaseAsync` (line 748): change signature to
```csharp
    private async Task SyncMetadataPhaseAsync(string? apiKey, List<string> statusParts, CancellationToken ct)
```
Inside `for (var i = 0; ...)` (line 770), make the first line of the loop body:
```csharp
            ct.ThrowIfCancellationRequested();
```

`ReprocessLocalFileModelsPhaseAsync` (line 686): change signature to
```csharp
    private async Task ReprocessLocalFileModelsPhaseAsync(List<string> statusParts, CancellationToken ct)
```
First line inside its `for` (line 706 loop body, before `var tile = ...`):
```csharp
            ct.ThrowIfCancellationRequested();
```

`RefetchMissingImagesPhaseAsync` (line 877): change signature to
```csharp
    private async Task RefetchMissingImagesPhaseAsync(string? apiKey, List<string> statusParts, CancellationToken ct)
```
First line inside its `for` (line 895 loop body):
```csharp
            ct.ThrowIfCancellationRequested();
```
And change its delay (line 928) `await Task.Delay(1500);` to:
```csharp
            await Task.Delay(1500, ct);
```

`BackfillMissingTagsPhaseAsync` (line 944): change signature to
```csharp
    private async Task BackfillMissingTagsPhaseAsync(string? apiKey, List<string> statusParts, CancellationToken ct)
```
First line inside its `for` (line 966 loop body):
```csharp
            ct.ThrowIfCancellationRequested();
```
And change its delay (line 991) `await Task.Delay(1500);` to:
```csharp
            await Task.Delay(1500, ct);
```

`DownloadMissingThumbnailsPhaseAsync` (line 1005): change signature to
```csharp
    private async Task DownloadMissingThumbnailsPhaseAsync(List<string> statusParts, CancellationToken ct)
```
First line inside its `for` (line 1023 loop body):
```csharp
            ct.ThrowIfCancellationRequested();
```
And change its delay (line 1045) `await Task.Delay(500);` to:
```csharp
            await Task.Delay(500, ct);
```

- [ ] **Step 7: Build to verify the token threading compiles**

Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj`
Expected: Build succeeded, 0 errors. (`OperationCanceledException` from `Task.Delay(..., ct)` and `ThrowIfCancellationRequested()` propagates to the new catch.)

- [ ] **Step 8: Run the full viewer test suite for regressions**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter FullyQualifiedName~Viewer`
Expected: PASS — the three new tests plus the existing `LoraViewerViewModelSearchTests` still green.

- [ ] **Step 9: Commit**

```bash
git add DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs DiffusionNexus.Tests/Viewer/LoraViewerCancelMetadataTests.cs
git commit -m "feat(lora-viewer): cooperative cancellation for metadata sync

Add IsCancellable flag, CancelMetadataDownload command, and thread a
CancellationToken through the five metadata-sync phase loops so the user
can abort the Civitai sync. Abort is non-destructive.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Cancel button in the busy overlay

Adds the visible Cancel affordance, scoped to the metadata sync via `IsCancellable`.

**Files:**
- Modify: `DiffusionNexus.UI/Views/LoraViewerView.axaml:266-274`

**Interfaces:**
- Consumes: `CancelMetadataDownloadCommand` and `IsCancellable` from Task 1.
- Produces: (UI only — no downstream code consumes this.)

- [ ] **Step 1: Add the Cancel button to the busy overlay**

In `DiffusionNexus.UI/Views/LoraViewerView.axaml`, replace the busy-overlay `StackPanel` (lines 268–273):

```xml
              <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center" Spacing="16">
                <ProgressBar IsIndeterminate="True" Width="300"/>
                <TextBlock Text="{Binding BusyMessage}"
                           Foreground="White"
                           HorizontalAlignment="Center"/>
              </StackPanel>
```

with (adds the button below the message, visible only while cancellable — the command sets `IsCancellable = false` on click, so the button hides immediately and the click reads as received while the "Cancelling…" status shows the state):

```xml
              <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center" Spacing="16">
                <ProgressBar IsIndeterminate="True" Width="300"/>
                <TextBlock Text="{Binding BusyMessage}"
                           Foreground="White"
                           HorizontalAlignment="Center"/>
                <Button Content="Cancel"
                        Command="{Binding CancelMetadataDownloadCommand}"
                        IsVisible="{Binding IsCancellable}"
                        HorizontalAlignment="Center"
                        Padding="20,6"
                        ToolTip.Tip="Stop the metadata sync after the current model finishes"/>
              </StackPanel>
```

- [ ] **Step 2: Build to verify the XAML compiles**

Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj`
Expected: Build succeeded, 0 errors (Avalonia XAML compiler validates the bindings against `LoraViewerViewModel`).

- [ ] **Step 3: Commit**

```bash
git add DiffusionNexus.UI/Views/LoraViewerView.axaml
git commit -m "feat(lora-viewer): add Cancel button to metadata-sync busy overlay

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: End-to-end verification in the running app

The abort path (real Civitai sync interrupted mid-run) is not cheaply unit-testable — `DownloadMissingMetadataAsync` depends on `App.Services` (scope factory, `IModelSyncService`) and a live `ICivitaiClient`, and no test harness stands those up. Verify it by driving the real app.

**Files:** none (verification only).

- [ ] **Step 1: Launch the app and open the LoRA viewer**

Use the `run` skill (or `dotnet run --project DiffusionNexus.UI`). Navigate to the LoRA viewer Installed tab with at least one configured LoRA source folder that has un-synced models.

- [ ] **Step 2: Start the sync and abort it**

Click **Download Metadata**. Confirm the busy overlay appears with a **Cancel** button. While the status is cycling through models (e.g. "Looking up …"), click **Cancel**.

Expected:
- Status flips to "Cancelling…", the button disappears/greys, the sync stops within ~1 model, and the overlay closes.
- Final status bar reads "Metadata sync cancelled".
- Models processed before the cancel retain their fetched metadata (spot-check a tile that updated).

- [ ] **Step 3: Confirm Cancel does not appear for Refresh**

Click **Refresh**. Confirm the busy overlay shows **no** Cancel button (Refresh is not cancellable; `IsCancellable` stays false).

- [ ] **Step 4: Record the verification outcome**

Note the observed behavior. If any step fails, return to Task 1/Task 2 rather than claiming completion.

---

## Notes on test coverage

Automated tests (Task 1) cover the cancellation command surface that is reachable without DI: the `IsCancellable` default, idle-cancel safety, and cancel→token-signalled→status wiring. The full "abort a live sync, partial results preserved" behavior is covered by the Task 3 real-app run because the sync method is coupled to `App.Services` and a live `ICivitaiClient`, and building that harness would exceed the size of this feature. This is a deliberate, documented tradeoff, consistent with the spec's non-destructive-abort requirement.
