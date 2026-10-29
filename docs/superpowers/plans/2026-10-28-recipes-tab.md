# Recipes tab (#667) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every Civitai image pasted into Browse Civitai is saved as a recipe and listed in a new Recipes tab of the LoRA Viewer, grouped by paste date, where the user keeps, queues, opens and deletes recipes and sees the disk space they take.

**Architecture:** A folder-per-recipe store (`RecipeStore`) owns the files; #628's service downloads originals into it and the browser saves every successful paste through it. Pure helpers decide the card status (`RecipeStatus`), the date groups (`RecipeDateGrouper`) and how a re-paste merges into a saved recipe (`RecipeMerge`). `RecipesTabViewModel` builds the lists from the store and reaches the browser's queue, installed snapshot and card factory through `IRecipeHost`, which `CivitaiBrowserViewModel` implements; opening a recipe reuses #628's `CivitaiRecipeViewModel` / `CivitaiRecipeView`.

**Tech Stack:** C# .NET 10, Avalonia 11.3, CommunityToolkit.Mvvm, System.Text.Json; xUnit, FluentAssertions, Moq.

**Spec:** `docs/superpowers/specs/2026-10-28-recipes-tab-design.md` (builds on `docs/superpowers/specs/2026-10-27-civitai-image-recipe-design.md`).

## Global Constraints

- Branch `feature/recipes-tab-667` (cut from `feature/civitai-image-recipe-628`). PR targets `develop` after PR #666 is merged. Never merge without the owner's explicit go.
- Store root: `%LocalAppData%\DiffusionNexus\Recipes\<imageId>\` with `original.png|jpg|webp` (none for a video) and `recipe.json`.
- Nothing is imported from `%LocalAppData%\DiffusionNexus\Cache\civitai-recipes`; that folder is left untouched.
- The app never deletes a recipe on its own; only Delete… and Clear history… delete, both after `IDialogService.ShowConfirmAsync`.
- Never Ready while a LoRA is missing (spec "Card" status rules, verbatim there).
- Counts must agree: the counter counts every recipe on disk (History + Kept); hidden ones are named beside it.
- The tab's Show NSFW is its own, default checked, not persisted; an opened recipe hides nothing.
- Every save, delete, keep change, filter-toggle change and queue hand-off is logged to the Unified Console (`IUnifiedLogger`, source `"CivitaiRecipe"`), standing rule.
- UI strings are English; dates and month names use `CultureInfo.InvariantCulture` (my call: the rest of the UI is English, "Today · Oktober" would mix languages). Sizes use `FileSizeFormatter.Format`; tests must not depend on the decimal separator (owner's locale is German).
- Files keep LF line endings (check `git diff --numstat` vs `-w` before pushing). Long files are written with the Write tool, not Bash heredocs.
- UI tests on screen run on the BenQ monitor only, selecting the test build by exe path.

## Review Focus

1. A recipe folder deleted or edited by hand while the tab is open → the tab reloads it as missing or broken on the next store change, never throws. (Task 1: `LoadAll_FolderWithoutJson_IsBroken`, `Delete_MissingFolder_ReturnsZero`.)
2. Two pastes of the same image in quick succession (paste + Enter, #628's concurrent-load case) → one folder, one card, no "file in use" failure. (Task 1: `Save_SameImageTwice_OneFolder`; Task 5: `PasteTwice_OneSavedRecipe`.)
3. The installed snapshot changes while the tab is open (a download finishes) → cards turn Ready and the Ready / Missing filters re-sort without a reload. (Task 6: `InstalledIndexChanged_UpdatesStatusAndFilters`.)
4. A recipe with an AutoV3 or unknown hash for an unlinked LoRA → counted missing, not installed. (Task 3: `UnlinkedWithAutoV3Hash_IsMissing`.)
5. Paste times around midnight and on a Monday → Today vs This week vs month boundaries are right in local time. (Task 4: `MondayMorning_YesterdayIsLastMonthOrMonthHeader`, `ItemsAfterMidnight_AreToday`.)

---

### Task 1: Recipe store (save, load, keep, delete, size) and originals inside it

**Files:**
- Create: `DiffusionNexus.UI/Services/CivitaiRecipe/RecipeStore.cs`
- Create: `DiffusionNexus.UI/Services/CivitaiRecipe/SavedRecipe.cs`
- Modify: `DiffusionNexus.UI/Services/CivitaiRecipe/CivitaiImageRecipeService.cs` (originals go to `<root>/<id>/original.<ext>`, cache hit and temp file inside that folder; replace `DefaultCacheDirectory` with `RecipeStore.DefaultRoot`; add constants below)
- Modify: `DiffusionNexus.UI/Services/CivitaiRecipe/CivitaiImageRecipe.cs` (`CivitaiRecipeResource.IsDefiniteFailure`)
- Test: `DiffusionNexus.Tests/Viewer/CivitaiRecipe/RecipeStoreTests.cs`; update `CivitaiImageRecipeServiceTests.cs` for the new original path.

**Interfaces:**
- Produces:
  - `CivitaiImageRecipeService.NotFoundText = "not found on Civitai"`, `NoLongerAvailableText = "model no longer available on Civitai"` (internal consts, used by the existing `LoadResourceAsync`).
  - `CivitaiRecipeResource.IsDefiniteFailure` → `LoadError is NotFoundText or NoLongerAvailableText`.
  - `sealed record SavedRecipe(int FormatVersion, DateTimeOffset PastedAt, bool Kept, CivitaiImageRecipe Recipe)`; `SavedRecipe.CurrentFormat = 1`.
  - `sealed record StoredRecipe(long ImageId, string Folder, SavedRecipe? Saved, long SizeBytes, string? ReadError)` with `bool IsBroken => Saved is null`.
  - `sealed class RecipeStore(string root, IUnifiedLogger? logger)`:
    - `static string DefaultRoot` = `%LocalAppData%\DiffusionNexus\Recipes`
    - `string Root`
    - `string FolderFor(long imageId)`
    - `IReadOnlyList<StoredRecipe> LoadAll()`
    - `StoredRecipe? TryLoad(long imageId)`
    - `StoredRecipe Save(CivitaiImageRecipe recipe, DateTimeOffset pastedAt)` (Task 2 adds the merge; here: write, keep an existing `Kept`)
    - `StoredRecipe Update(CivitaiImageRecipe recipe)` (keeps `PastedAt` and `Kept`; used by the retry in Task 7)
    - `void SetKept(IEnumerable<long> imageIds, bool kept)`
    - `long Delete(IEnumerable<long> imageIds)` (bytes freed)
    - `event EventHandler? Changed` (raised after every Save/Update/SetKept/Delete)
- JSON: `System.Text.Json` with `JsonSerializerOptions { WriteIndented = true }`, written to `recipe.json.tmp` then `File.Move(overwrite: true)`. On load, `Recipe.OriginalFilePath` is replaced by the folder's `original.*` (or null), so a moved profile still works. Size = sum of the folder's file lengths.

- [ ] **Step 1: Write the failing tests** in `RecipeStoreTests` (temp root per test, deleted in `Dispose`):
  - `SaveThenLoadAll_RoundTripsTheWholeRecipe`: save a recipe whose resource carries a `CivitaiModel` with one version and a file with hashes, a `RecipeLoraUse` with `AllWeights`, `Data` with seed `832350830334358`; `LoadAll().Single().Saved!.Recipe` equals the original by `BeEquivalentTo` (excluding `OriginalFilePath`), `Kept` false, `PastedAt` equal.
  - `Save_KeepsExistingKeptFlag`: save, `SetKept([id], true)`, save again → `Kept` true.
  - `LoadAll_FolderWithoutJson_IsBroken`: a folder `<root>/42/` holding only `original.png` → one entry, `IsBroken`, `ReadError` not empty, `SizeBytes` = the png's length.
  - `LoadAll_UnreadableJson_IsBroken`: `recipe.json` containing `{` → broken, not thrown.
  - `LoadAll_OriginalPathPointsIntoTheFolder`: save with `OriginalFilePath = @"C:\elsewhere\x.png"` while `<folder>/original.png` exists → loaded path is `<folder>/original.png`.
  - `Delete_ReturnsBytesFreed_AndRemovesFolder`; `Delete_MissingFolder_ReturnsZero`.
  - `Save_SameImageTwice_OneFolder`: two saves of image 7 → `LoadAll()` has one entry.
  - `Changed_RaisedOnSaveKeptAndDelete`.
- [ ] **Step 2: Run** `dotnet test DiffusionNexus.Tests --filter "FullyQualifiedName~RecipeStoreTests"` — Expected: build fails, `RecipeStore` not defined.
- [ ] **Step 3: Implement** `SavedRecipe`, `StoredRecipe`, `RecipeStore`, `IsDefiniteFailure`, the two constants; move the service's originals into `FolderFor(id)` as `original.<ext>` (temp `original.<guid>.download` in the same folder, same no-overwrite move as today). Update `CivitaiImageRecipeServiceTests` expectations from `<cache>/<id>.png` to `<root>/<id>/original.png`.
- [ ] **Step 4: Run** `dotnet test DiffusionNexus.Tests --filter "FullyQualifiedName~RecipeStoreTests|FullyQualifiedName~CivitaiImageRecipeServiceTests"` — Expected: all pass.
- [ ] **Step 5: Commit** `feat(recipes): folder-per-recipe store; #628 originals land in it (#667)`

### Task 2: Re-paste merge (a refresh never makes a recipe worse)

**Files:**
- Create: `DiffusionNexus.UI/Services/CivitaiRecipe/RecipeMerge.cs`
- Modify: `RecipeStore.Save` uses it when a saved recipe exists.
- Test: `DiffusionNexus.Tests/Viewer/CivitaiRecipe/RecipeMergeTests.cs`

**Interfaces:**
- Produces: `static class RecipeMerge { CivitaiImageRecipe Refresh(CivitaiImageRecipe saved, CivitaiImageRecipe fresh); }`
- Rules (spec "Saving"): start from `fresh`. Per resource by `VersionId`: when fresh has a `LoadError` that is not `IsDefiniteFailure` and saved has that resource without a `LoadError`, keep the saved resource. When a fresh resource has `Use?.Weight == null` and the saved one has a weight, keep the saved `Use`. A saved resource missing from fresh's list is kept when fresh has no site data for it — i.e. when `fresh.HasApiKey` is false and saved's was true (my call: the public list can be shorter than the page's list, #628 image 146878246). When `fresh.Data.Source == None` and saved's is not None, keep saved `Data` (with fresh `Format`). `UnlinkedLoras` from fresh, minus any whose `Name` now matches a kept resource's `Use?.Name`.

- [ ] **Step 1: Write the failing tests** in `RecipeMergeTests`:
  - `TransientFailure_KeepsSavedResource` (fresh LoadError `"429 Too Many Requests"`, saved loaded → saved resource kept).
  - `DefiniteFailure_ReplacesSavedResource` (fresh LoadError `NoLongerAvailableText` → fresh row).
  - `MissingWeight_KeepsSavedWeight` (saved weight 0.65, fresh null → 0.65).
  - `NoKeyThisTime_KeepsResourcesOnlyTheSiteListed` (saved `HasApiKey` true with 6 resources, fresh false with 0 → 6).
  - `FreshWithoutData_KeepsSavedData` (saved `SettingsText` with seed, fresh `None` → saved data).
  - `FreshBetter_WinsEverywhere` (fresh all loaded, new weights → equals fresh).
  - In `RecipeStoreTests`: `Save_Existing_MergesAndUpdatesPastedAt` (second save with a transient failure keeps the model, `PastedAt` = second time, `Kept` kept).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~RecipeMergeTests|FullyQualifiedName~RecipeStoreTests"` — Expected: FAIL, `RecipeMerge` not defined.
- [ ] **Step 3: Implement** `RecipeMerge.Refresh` and call it from `Save` when `TryLoad(id)?.Saved` exists.
- [ ] **Step 4: Run** the same filter — Expected: PASS.
- [ ] **Step 5: Commit** `feat(recipes): a re-paste refreshes a saved recipe without making it worse (#667)`

### Task 3: Card status (Ready / missing / not loaded)

**Files:**
- Create: `DiffusionNexus.UI/Services/CivitaiRecipe/RecipeStatus.cs`
- Modify: `DiffusionNexus.UI/Services/CivitaiBrowser/CivitaiInstalledIndex.cs` (add `HasFileHashPrefix`)
- Modify: `DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiRecipeResourceRowViewModel.cs` (make `LoraTypes` `internal static` so the status reuses it)
- Test: `DiffusionNexus.Tests/Viewer/CivitaiRecipe/RecipeStatusTests.cs`

**Interfaces:**
- Produces:
  - `CivitaiInstalledIndex.HasFileHashPrefix(string? hash)`: true when `hash` has at least 10 characters and an installed SHA256 starts with it, ignoring case (AutoV2 = first 10, SHA256_12 = first 12, full SHA256).
  - `sealed record RecipeStatus(int Loras, int Missing, int MissingNotOnCivitai, int FailedToLoad)` with `bool IsReady => Missing == 0 && FailedToLoad == 0 && Loras > 0`, `bool IsMissing => !IsReady && !(Loras == 0 && FailedToLoad == 0)`, `string Text`, and `static RecipeStatus Compute(CivitaiImageRecipe recipe, CivitaiInstalledIndex installed)`.
  - LoRA = `Model?.Type` in `CivitaiRecipeResourceRowViewModel.LoraTypes`; installed = `installed.IsInstalled(Version)`. Unlinked LoRAs count in `Loras`; installed when `HasFileHashPrefix(use.Hash)`. A resource with `LoadError` counts in `FailedToLoad` only.
  - `Text` exact copy: `"Ready"`; `"No LoRAs"`; `"{Missing} of {Loras} LoRAs missing"` + `" ({MissingNotOnCivitai} not on Civitai)"` when > 0; `"{FailedToLoad} resource could not be loaded"` / `"resources could not be loaded"` (plural for > 1), joined to a missing text with `" · "`.

- [ ] **Step 1: Write the failing tests** in `RecipeStatusTests`:
  - `AllLorasInstalled_IsReady` (checkpoint not installed still Ready).
  - `TwoOfFiveMissing_Text` → `"2 of 5 LoRAs missing"`.
  - `UnlinkedInstalledByAutoV2_CountsInstalled`; `UnlinkedWithAutoV3Hash_IsMissing` → `"1 of 1 LoRAs missing (1 not on Civitai)"`.
  - `EarlyAccessNotInstalled_IsMissing`.
  - `FailedResource_NeverReady` → `"1 resource could not be loaded"`, `IsReady` false, `IsMissing` true.
  - `FailedAndMissing_JoinsBoth` → `"1 of 2 LoRAs missing · 2 resources could not be loaded"`.
  - `NoLoras_SaysSo` → `"No LoRAs"`, neither Ready nor Missing.
  - `CivitaiInstalledIndexTests.HasFileHashPrefix_MatchesAutoV2AndSha12_NotShortOrUnknown`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~RecipeStatusTests|FullyQualifiedName~CivitaiInstalledIndex"` — Expected: FAIL, not defined.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the same filter — Expected: PASS.
- [ ] **Step 5: Commit** `feat(recipes): card status — never Ready while a LoRA is missing (#667)`

### Task 4: Date groups and paste-time labels

**Files:**
- Create: `DiffusionNexus.UI/Services/CivitaiRecipe/RecipeDateGrouper.cs`
- Test: `DiffusionNexus.Tests/Viewer/CivitaiRecipe/RecipeDateGrouperTests.cs`

**Interfaces:**
- Produces: `static class RecipeDateGrouper`:
  - `IReadOnlyList<(string Header, IReadOnlyList<T> Items)> Group<T>(IEnumerable<T> items, Func<T, DateTimeOffset> pastedAt, DateTime nowLocal)` — newest first inside and across groups; headers `"Today"` (local date = today, or later), `"This week"` (from Monday 00:00 of `nowLocal`'s ISO week, before today), then `"MMMM"` for the current year, `"MMMM yyyy"` for earlier years, invariant culture. Empty groups are not returned.
  - `string TimeLabel(DateTimeOffset pastedAt, DateTime nowLocal)` — `"HH:mm"` today, `"ddd"` this week, `"MMM d"` this year, `"MMM d, yyyy"` earlier.

- [ ] **Step 1: Write the failing tests** (now = Wednesday 2026-10-28 14:00 local):
  - `GroupsTodayThisWeekMonthsAndYears`: items at 10-28 09:00, 10-26 18:00, 10-25 12:00 (Sunday, last week), 10-02, 2025-12-24 → headers `Today, This week, October, December 2025`, counts 1,1,2,1, newest first in October.
  - `MondayMorning_YesterdayIsLastMonthOrMonthHeader`: now = Monday 2026-11-02 08:00; item 11-01 (Sunday, previous ISO week) → `"November"`; item 10-31 → `"October"`; item 11-02 07:00 → `"Today"`; no `"This week"` group.
  - `ItemsAfterMidnight_AreToday`: item at 00:00:30 today → `"Today"`.
  - `TimeLabel_Cases` → `"09:00"`, `"Mon"`, `"Oct 2"`, `"Dec 24, 2025"`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~RecipeDateGrouperTests"` — Expected: FAIL, not defined.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `feat(recipes): Today / This week / month groups for recipes (#667)`

### Task 5: Browser saves every paste and shows the saved copy when a refresh fails

**Files:**
- Create: `DiffusionNexus.UI/ViewModels/CivitaiBrowser/IRecipeHost.cs`
- Modify: `DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiBrowserViewModel.cs`
- Test: `DiffusionNexus.Tests/Viewer/CivitaiBrowserRecipeTests.cs`

**Interfaces:**
- Produces `interface IRecipeHost` (implemented by `CivitaiBrowserViewModel`):
  - `CivitaiInstalledIndex Installed { get; }`
  - `event EventHandler? InstalledIndexChanged` (raised where `_installed` is replaced, after the UI-thread refresh)
  - `CivitaiResultViewModel CreateRecipeCard(CivitaiModel model)` (the existing private method, made the interface member)
  - `Task EnqueueRecipeAsync(List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)> pairs)` → `EnqueueWithPreflightPromptAsync`
  - `void OpenUrl(string url)`
  - `Task<string?> GetApiKeyAsync()`
- Browser gains `internal RecipeStore? RecipeStore { get; set; }` (production: set by `LoraViewerViewModel`, Task 8). The recipe service is built with `RecipeStore?.Root ?? RecipeStore.DefaultRoot`.
- After a successful load: `var stored = RecipeStore?.Save(result.Recipe, DateTimeOffset.Now)`; the view shows `stored.Saved.Recipe` (the merged one). Logged: `Recipe {id} saved — {size}`.
- When the load returns no recipe and `RecipeStore?.TryLoad(id)` has one: show it, with `LinkLookupNotice` = `"Could not refresh this recipe from Civitai ({message}). Showing the saved copy."`; not saved again, paste time unchanged.

- [ ] **Step 1: Write the failing tests** (extend `Create()` with a `RecipeStore` on `_dir/recipes`):
  - `Paste_SavesTheRecipe` → `store.LoadAll()` has image 7 with its version 100.
  - `PasteTwice_OneSavedRecipe`.
  - `RefreshFails_ShowsSavedCopy_NotMoved`: save image 7 at a fixed time, then make `GetImagesAsync` return no items, paste → `vm.Recipe` not null, notice starts with `"Could not refresh this recipe from Civitai"`, saved `PastedAt` unchanged.
  - `InstalledIndexChanged_Raised` when the installed refresh replaces the index (use the existing test seam for the installed refresh; if none exists, expose `internal void ApplyInstalledSnapshot(CivitaiInstalledIndex)` that the refresh calls and test through it).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~CivitaiBrowserRecipeTests"` — Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `--filter "FullyQualifiedName~CivitaiBrowser"` — Expected: PASS (all browser tests, including `CivitaiBrowserCardHandlerTests`, which finds `CreateResultCard` by reflection — keep that name unique).
- [ ] **Step 5: Commit** `feat(recipes): every pasted image is saved; a failed refresh shows the saved copy (#667)`

### Task 6: Recipes tab view model — lists, counts, filters, groups, selection actions

**Files:**
- Create: `DiffusionNexus.UI/ViewModels/CivitaiBrowser/RecipesTabViewModel.cs`
- Create: `DiffusionNexus.UI/ViewModels/CivitaiBrowser/RecipeCardViewModel.cs`
- Create: `DiffusionNexus.UI/ViewModels/CivitaiBrowser/RecipeGroupViewModel.cs`
- Test: `DiffusionNexus.Tests/Viewer/RecipesTabViewModelTests.cs` (store on a temp root, `Mock<IRecipeHost>`, `Mock<IDialogService>`, fixed `now`)

**Interfaces:**
- Consumes: Tasks 1–5.
- Produces:
  - `RecipesTabViewModel(RecipeStore store, IRecipeHost host, IDialogService? dialogs, IUnifiedLogger? logger, Func<DateTime>? nowLocal = null)`; reloads on `store.Changed` and re-computes statuses on `host.InstalledIndexChanged` (on the UI thread).
  - `enum RecipeStatusFilter { All, Ready, Missing }`
  - Observable: `bool IsKeptView`, `int HistoryCount`, `int KeptCount`, `RecipeStatusFilter StatusFilter`, `bool ShowNsfw = true`, `string SearchText`, `string CounterText`, `string? HiddenText`, `ObservableCollection<RecipeGroupViewModel> Groups`, `int SelectedCount`, `string QueueButtonText`, `string QueueTooltip`.
  - `RecipeGroupViewModel(string Header, IReadOnlyList<RecipeCardViewModel> Cards)` with `CountText`.
  - `RecipeCardViewModel`: `long ImageId`, `string Title`, `string Byline`, `string StatusText`, `bool IsReady`, `bool IsMissing`, `bool IsBroken`, `string RatingBadge`, `bool IsSafe`, `string? BaseModel`, `bool IsKept`, `bool IsSelected`, `string? ThumbnailPath`, `StoredRecipe Stored`.
  - Commands: `ShowHistoryCommand`, `ShowKeptCommand`, `ToggleKeptCommand(RecipeCardViewModel)`, `KeepSelectedCommand`, `UnkeepSelectedCommand`, `QueueMissingCommand`, `OpenOnCivitaiCommand`, `DeleteSelectedCommand`, `ClearHistoryCommand`, `ClearSelectionCommand`; `void Select(RecipeCardViewModel card, bool additive, bool range)`.
- Exact copy:
  - Counter: `"{n} recipes · {size} on disk (History {h}, Kept {k})"`, `"1 recipe"` singular.
  - Hidden: `"{x} NSFW recipes hidden in {History|Kept}"` when only NSFW hides; `"{x} recipes hidden by filters in {History|Kept}"` otherwise; null when nothing hidden.
  - Queue button: `"Queue missing LoRAs ({n})"`; tooltip lists what cannot be queued: `"{a} not on Civitai, {b} could not be loaded"` (only non-zero parts), or `"Queues the LoRAs these recipes are missing, each once."`.
  - Title: first prompt line trimmed to 60 chars, else `"Image {id}"`; broken: `"Recipe could not be read"`.
  - Byline: `"{creator} · {TimeLabel}"` + `" · no prompt"` when `Data.Prompt` is null.
  - Delete confirm: title `"Delete recipes"`, message `"Delete {n} recipe(s)? This frees {size}. The images are removed from disk."`; Clear history: title `"Clear history"`, message `"Delete all {n} recipes in History? This frees {size}. Kept recipes are not touched."` — broken recipes are not included in Clear history.
- Search: case-insensitive over prompt, `Image.Username`, image id, resource model names and `Use.Name`, unlinked names.
- Queue: build pairs with `host.CreateRecipeCard(model)` per distinct missing LoRA version (selected recipes, not installed, `Model` loaded), pick = card's version with that id; `host.EnqueueRecipeAsync(pairs)`.
- Open on Civitai: `host.OpenUrl(recipe.CivitaiUrl)` per selected; more than 5 → `ShowConfirmAsync("Open on Civitai", "Open {n} pages in your browser?")`.

- [ ] **Step 1: Write the failing tests** in `RecipesTabViewModelTests`:
  - `History_GroupsByPasteDate` (3 recipes today/this week/October → 3 groups, `Groups[0].Header == "Today"`).
  - `KeptView_ShowsOnlyKept` and `Counter_CountsBothLists` (`"3 recipes · … on disk (History 2, Kept 1)"`, assert the parts around the size).
  - `ShowNsfwOff_HidesNsfw_AndSaysSo` (rating 8 hidden; `HiddenText == "1 NSFW recipe hidden in History"`; "recipe" singular for 1, "recipes" otherwise, in every text).
  - `ReadyFilter_ShowsOnlyReady`, `MissingFilter_ShowsAmber`.
  - `Search_MatchesPromptCreatorIdAndLoraName`.
  - `InstalledIndexChanged_UpdatesStatusAndFilters` (Missing filter on; raise the event with the LoRA installed → card leaves the list).
  - `KeepSelected_MovesToKept_AndLogs`.
  - `QueueMissing_DedupesAcrossRecipes` (two recipes sharing version 100 → one pair for 100).
  - `QueueTooltip_NamesWhatCannotBeQueued`.
  - `DeleteSelected_AsksFirst_NoDeleteOnNo`; `DeleteSelected_Yes_RemovesFolders`.
  - `ClearHistory_LeavesKeptAndBroken`.
  - `BrokenFolder_ShowsInHistory_AsBrokenCard`.
  - `OpenOnCivitai_MoreThanFive_AsksFirst`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~RecipesTabViewModelTests"` — Expected: FAIL, not defined.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `feat(recipes): Recipes tab lists, counter, filters and selection actions (#667)`

### Task 7: Open a recipe inside the tab, Back, and retry failed resources once

**Files:**
- Modify: `RecipesTabViewModel.cs` (open/back)
- Modify: `DiffusionNexus.UI/Services/CivitaiRecipe/CivitaiImageRecipeService.cs` (retry)
- Test: `RecipesTabViewModelTests.cs`, `CivitaiImageRecipeServiceTests.cs`

**Interfaces:**
- Produces:
  - `RecipesTabViewModel`: `CivitaiRecipeViewModel? OpenRecipe`, `bool IsRecipeOpen`, `string BackText` (`"← Back to History"` / `"← Back to Kept"`), `OpenCommand(RecipeCardViewModel)` (broken cards do not open), `BackCommand`; Back keeps `IsKeptView`, filters, search and `IsSelected` flags. The view model is built as in the browser: `new CivitaiRecipeViewModel(recipe, host.Installed, host.CreateRecipeCard, clipboard) { UrlOpener = host.OpenUrl, EnqueueHandler = pairs => _ = host.EnqueueRecipeAsync(pairs) }`, then `LoadPreviewAsync`; `InstalledIndexChanged` also reaches `OpenRecipe.ApplyInstalledIndex`.
  - `CivitaiImageRecipeService.RetryFailedResourcesAsync(CivitaiImageRecipe recipe, string? apiKey, CancellationToken ct) -> Task<CivitaiImageRecipe>`: reloads only resources with a non-definite `LoadError` (same `LoadResourceAsync`), matches each loaded version to `recipe.Data.Loras` and `recipe.UnlinkedLoras` with `RecipeResourceMatcher`, removes newly linked entries from `UnlinkedLoras`, returns the recipe unchanged when nothing needed a retry.
  - The tab needs a service: constructor parameter `Func<CivitaiImageRecipeService>? retryService` (null = no retry). On open, when any resource failed transiently: retry in the background with `host.GetApiKeyAsync()`, `store.Update(result)`, and rebuild `OpenRecipe` if it is still the same image. Logged.

- [ ] **Step 1: Write the failing tests:**
  - `Open_ShowsRecipeInsideTheTab_BackRestoresListAndSelection`.
  - `Open_BrokenCard_DoesNothing`.
  - `Open_RetriesFailedResourceOnce_AndSaves` (service stub: version 100 fails with `"429"` in the saved recipe, now loads → store has it loaded; a second open makes no call).
  - `RetryFailedResources_SkipsDefiniteFailures` (service test: `NotFoundText` row → no `GetModelVersionAsync` call).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~RecipesTabViewModelTests|FullyQualifiedName~CivitaiImageRecipeServiceTests"` — Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `feat(recipes): open a recipe inside the tab with Back; failed resources are retried once (#667)`

### Task 8: The view, the tab, wiring — and the on-screen check

**Files:**
- Create: `DiffusionNexus.UI/Views/CivitaiBrowser/RecipesTabView.axaml` (+ `.axaml.cs`)
- Modify: `DiffusionNexus.UI/Views/LoraViewerView.axaml` (new `TabItem Header="Recipes"` after LoRA Sorter)
- Modify: `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs` (one `RecipeStore` on `RecipeStore.DefaultRoot`, set on `BrowserViewModel.RecipeStore`; `public RecipesTabViewModel RecipesViewModel`, also in the design-time constructor)
- Modify: `DiffusionNexus.UI/REUSABLES.md` only if a new reusable control appears (none planned).

**Interfaces:**
- Consumes: Tasks 5–7.
- The view follows the spec mockup `docs/superpowers/specs/assets/2026-10-28-recipes-tab/recipes-tab.png`: top row (History/Kept chips with counts, All/Ready/Missing chips, Show NSFW check box, search box, Clear history…), counter line with the amber hidden note, group headers with counts, a `WrapPanel`-style card grid (card 128 px wide, image 132 px, SFW/NSFW badge top left, ★ top right, base model bottom left, status line green/amber), the selection bar at the bottom while `SelectedCount > 0`, and, when `IsRecipeOpen`, a Back button above a `CivitaiRecipeView` bound to `OpenRecipe` (same Panel + `IsVisible` wrapping as the browser, so `DataContext` and `IsVisible` are on different elements). Plain click opens, the card's tick box / Ctrl-click toggles selection, Shift-click selects a range (code-behind calls `Select`). Thumbnails: `Bitmap.DecodeToWidth(stream, 256)` off the UI thread, loaded when the card is realised.

- [ ] **Step 1: Build** `dotnet build DiffusionNexus.sln` — Expected: 0 errors, no new warnings in the touched files.
- [ ] **Step 2: Full suite** `dotnet test DiffusionNexus.Tests > <workspace>/suite.txt` — Expected: all pass (7544 + the new tests; the known flaky `ImageEditorHandOffTests.EditedGif_HandsOverAPngCopy` is re-run alone if it fails).
- [ ] **Step 3: On-screen check on the BenQ** (Debug build, selected by exe path; the owner's Release instance is left alone): paste `https://civitai.com/images/143992601` and `https://civitai.red/images/146878246` in Browse Civitai; open Recipes → both under Today with the right status; ★ one → Kept count 1; untick Show NSFW → hidden note; open a card → recipe view with Back → Back restores the list; select both → Queue missing LoRAs (n) matches the rows; Delete… one → confirm → counter drops. Screenshot to `docs/superpowers/specs/assets/2026-10-28-recipes-tab/built-recipes-tab.png`.
- [ ] **Step 4: Commit** `feat(recipes): Recipes tab in the LoRA Viewer (#667)` and push.
- [ ] **Step 5: Open the PR** to `develop` (base `feature/civitai-image-recipe-628` if #666 is still open, retarget after it merges), body: what it does, the spec link, the screenshot, the owner decisions, `Closes #667`, milestone "Civitai image recipes".
