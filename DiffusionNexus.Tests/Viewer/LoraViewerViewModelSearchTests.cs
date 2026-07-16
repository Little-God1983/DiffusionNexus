using System.Collections.Specialized;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the LoRA viewer search debounce. Typing in the search bar used to run
/// the full filter pass (including the visible tile-window rebuild) synchronously
/// on every keystroke, blocking the UI thread per character. The pass must now be
/// debounced so it runs once after typing pauses.
/// </summary>
public class LoraViewerViewModelSearchTests
{
    /// <summary>
    /// Design-time constructor loads demo data, giving a populated tile set
    /// without DI. The debounce interval is shortened to keep tests fast.
    /// </summary>
    private static LoraViewerViewModel CreateViewModel() => new()
    {
        SearchDebounceInterval = TimeSpan.FromMilliseconds(100),
    };

    [Fact]
    public void WhenSearchTextChangesThenFilterIsNotAppliedSynchronously()
    {
        var vm = CreateViewModel();
        var allCount = vm.FilteredTiles.Count;
        allCount.Should().BeGreaterThan(1, "demo data must be loaded for this test to be meaningful");

        vm.SearchText = "Cyberpunk";

        vm.FilteredTiles.Count.Should().Be(allCount,
            "the filter pass must be debounced, not run synchronously on the keystroke");
    }

    [Fact]
    public async Task WhenTypingPausesThenFilterIsApplied()
    {
        var vm = CreateViewModel();
        var allCount = vm.FilteredTiles.Count;

        vm.SearchText = "Cyberpunk";
        await vm.SearchDebounceTask!;

        vm.FilteredTiles.Should().Contain(t => t.DisplayName.Contains("Cyberpunk"));
        vm.FilteredTiles.Count.Should().BeLessThan(allCount);
    }

    [Fact]
    public async Task WhenTypingRapidlyThenOnlyOneFilterPassRuns()
    {
        var vm = CreateViewModel();
        var rebuilds = 0;
        vm.FilteredTiles.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) rebuilds++;
        };

        vm.SearchText = "C";
        vm.SearchText = "Cy";
        vm.SearchText = "Cyberpunk";
        await vm.SearchDebounceTask!;

        rebuilds.Should().Be(1,
            "rapid keystrokes must coalesce into a single debounced filter pass");
    }

    [Fact]
    public async Task WhenSearchResultIsUnchangedThenTileWindowIsNotRebuilt()
    {
        var vm = CreateViewModel();
        vm.SearchText = "Cyberpunk";
        await vm.SearchDebounceTask!;

        var rebuilds = 0;
        vm.FilteredTiles.CollectionChanged += (_, _) => rebuilds++;

        // "Cyberpunk Aesthetic" still matches — same result set as before.
        vm.SearchText = "Cyberpunk Aes";
        await vm.SearchDebounceTask!;

        rebuilds.Should().Be(0,
            "an unchanged result set must keep the current window instead of rebuilding it");
    }

    [Fact]
    public async Task WhenFiltersAreResetThenRestoreIsImmediate()
    {
        var vm = CreateViewModel();
        var allCount = vm.FilteredTiles.Count;

        vm.SearchText = "Cyberpunk";
        await vm.SearchDebounceTask!;
        vm.FilteredTiles.Count.Should().BeLessThan(allCount);

        vm.ResetFiltersCommand.Execute(null);

        vm.FilteredTiles.Count.Should().Be(allCount,
            "reset is an explicit action and must apply without the typing debounce");
    }
}
