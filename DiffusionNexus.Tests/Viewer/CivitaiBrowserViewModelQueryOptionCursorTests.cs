using System.Reflection;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Regression coverage for PR #547 review finding 12: Sort/Period/Model-type changes used to
/// defer clearing the pagination cursor behind the 400ms search debounce
/// (<see cref="CivitaiBrowserViewModel"/>'s <c>DebouncedSearchAsync</c>), so for that whole window
/// <see cref="CivitaiBrowserViewModel.HasMore"/> still advertised the OLD query's cursor under the
/// NEW filter — <c>LoadMoreAsync</c> (and the auto-load-more <c>MaybeTopUpVisibleResults</c> fires
/// whenever a filter change drops the visible count below its threshold, reachable with no fast
/// click at all) could then paginate a cursor that belongs to a query that no longer applies.
/// </summary>
/// <remarks>
/// The fix clears the cursor — and raises <see cref="CivitaiBrowserViewModel.HasMore"/> — inside
/// the property hook itself, synchronously, before the debounced re-search is even scheduled. That
/// is exactly what these tests assert: no <c>await</c>, no dispatcher pump, nothing that could let
/// the 400ms delay elapse — if the fix regressed back to clearing the cursor only after the delay,
/// these would still observe <c>HasMore == true</c> immediately after the property set.
/// <para>
/// The cursor field is private with no public search surface that reliably yields a live one
/// without driving a full mocked multi-iteration paginated fetch (see
/// <see cref="CivitaiBrowserCardHandlerTests"/> for why that is heavier than the mechanism here
/// warrants) — reflection to seed it directly follows the same pattern already used for that
/// class's private <c>CreateResultCard</c> factory.
/// </para>
/// </remarks>
public sealed class CivitaiBrowserViewModelQueryOptionCursorTests
{
    private static readonly FieldInfo NextCursorField = typeof(CivitaiBrowserViewModel)
        .GetField("_nextCursor", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static CivitaiBrowserViewModel CreateVm()
    {
        // Persist path redirected into a temp dir so the test never reads or clobbers the real
        // LocalAppData waitlist snapshot (see CivitaiBrowserClearQueueTests).
        var tempDir = Directory.CreateTempSubdirectory("dn-cursor-tests").FullName;
        var waitlist = new CivitaiWaitlist(null, null, persistPathOverride: Path.Combine(tempDir, "waitlist.json"));
        // civitaiClient: null — the debounced re-search this test's property changes fire bails
        // out on its own null-client guard inside LoadNextAsync, so there is no network call and
        // nothing racing the synchronous assertions below.
        return new CivitaiBrowserViewModel(null, null, null, new CivitaiDownloadQueue(null), waitlist, null);
    }

    /// <summary>Seeds a "live" cursor, as if a prior search had returned one to page from.</summary>
    private static void SeedCursor(CivitaiBrowserViewModel vm) =>
        NextCursorField.SetValue(vm, "cursor-from-the-query-in-effect-before-this-test-changes-it");

    [Fact]
    public void ChangingSort_ImmediatelyClearsHasMore_BeforeTheDebounceCouldFire()
    {
        var vm = CreateVm();
        SeedCursor(vm);
        vm.HasMore.Should().BeTrue("the test seeded a cursor as if a prior search had one");

        vm.SelectedSort = CivitaiModelSort.HighestRated; // default is Newest — must actually change

        vm.HasMore.Should().BeFalse(
            "the cursor must be cleared synchronously in the property hook, not deferred behind " +
            "the 400ms debounce, or LoadMoreAsync could pair it with the new sort in the meantime");
    }

    [Fact]
    public void ChangingPeriod_ImmediatelyClearsHasMore_BeforeTheDebounceCouldFire()
    {
        var vm = CreateVm();
        SeedCursor(vm);

        vm.SelectedPeriod = CivitaiPeriod.Month; // default is AllTime — must actually change

        vm.HasMore.Should().BeFalse();
    }

    [Fact]
    public void ChangingModelType_ImmediatelyClearsHasMore_BeforeTheDebounceCouldFire()
    {
        var vm = CreateVm();
        SeedCursor(vm);

        // Default is "All LoRA types" — pick a different option so the hook actually fires.
        vm.SelectedModelType = vm.ModelTypeOptions.Single(o => o.Label == "All models");

        vm.HasMore.Should().BeFalse();
    }

    [Fact]
    public void ReSettingTheSameSort_DoesNotClearHasMore()
    {
        // [ObservableProperty] setters no-op when the value is unchanged (default equality
        // comparer), so this never reaches the property hook at all — included to document that
        // the fix relies on that generated guard rather than re-deriving it, and to catch a
        // regression that starts unconditionally clearing the cursor outside the hook.
        var vm = CreateVm();
        SeedCursor(vm);

        vm.SelectedSort = vm.SelectedSort;

        vm.HasMore.Should().BeTrue();
    }
}
