using System.Collections.ObjectModel;
using System.Collections.Specialized;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Unit tests for <see cref="SearchableBaseModelPickerViewModel"/> — the per-instance
/// engine behind the searchable base-model picker control (detail card, Create Training
/// Run dialog, Training Runs card). Single-select over a shared list of raw labels; the
/// search box narrows with the same rule the viewer/browser flyouts use.
/// </summary>
public class SearchableBaseModelPickerViewModelTests
{
    private static readonly string[] Labels = ["SD 1.5", "SDXL 1.0", "SDXL Turbo", "Wan Video 2.2 T2V-A14B"];

    private static SearchableBaseModelPickerViewModel CreateSut(IEnumerable<string>? items = null)
    {
        var sut = new SearchableBaseModelPickerViewModel();
        sut.ItemsSource = items ?? Labels;
        return sut;
    }

    private static IEnumerable<string> VisibleLabels(SearchableBaseModelPickerViewModel sut)
        => sut.VisibleItems.Select(i => i.Label);

    [Fact]
    public void VisibleItems_MirrorsItemsSourceInOrder_WhenSearchIsEmpty()
    {
        var sut = CreateSut();

        VisibleLabels(sut).Should().Equal(Labels);
    }

    [Fact]
    public void SearchText_NarrowsCaseInsensitively_PreservingSourceOrder()
    {
        var sut = CreateSut();

        sut.SearchText = "sdxl";

        VisibleLabels(sut).Should().Equal("SDXL 1.0", "SDXL Turbo");
    }

    [Fact]
    public void SearchText_ClearedAgain_RestoresFullList()
    {
        var sut = CreateSut();
        sut.SearchText = "sdxl";

        sut.SearchText = "";

        VisibleLabels(sut).Should().Equal(Labels);
    }

    [Fact]
    public void SearchText_WhitespaceOnly_ShowsFullList()
    {
        var sut = CreateSut();

        sut.SearchText = "   ";

        VisibleLabels(sut).Should().Equal(Labels);
    }

    [Fact]
    public void Select_SetsSelectedItem_AndRequestsClose()
    {
        var sut = CreateSut();
        var closeRequests = 0;
        sut.CloseRequested += (_, _) => closeRequests++;

        sut.SelectCommand.Execute("SDXL Turbo");

        sut.SelectedItem.Should().Be("SDXL Turbo");
        closeRequests.Should().Be(1);
    }

    [Fact]
    public void OnFlyoutOpened_ClearsSearch_SoTheFullListShowsAgain()
    {
        var sut = CreateSut();
        sut.SearchText = "sdxl";

        sut.OnFlyoutOpened();

        sut.SearchText.Should().BeEmpty();
        VisibleLabels(sut).Should().Equal(Labels);
    }

    [Fact]
    public void VisibleItems_FollowsObservableSourceChanges_RespectingActiveSearch()
    {
        // The real sources are observable collections the VMs refill when the
        // Civitai catalog resolves asynchronously — the picker must follow.
        var source = new ObservableCollection<string> { "SD 1.5" };
        var sut = CreateSut(source);
        sut.SearchText = "sdxl";

        source.Add("SDXL 1.0");
        source.Add("Wan Video");

        VisibleLabels(sut).Should().Equal("SDXL 1.0");
    }

    [Fact]
    public void VisibleItems_IgnoresChangesToAReplacedSource()
    {
        var oldSource = new ObservableCollection<string> { "SD 1.5" };
        var sut = CreateSut(oldSource);
        sut.ItemsSource = new[] { "SDXL 1.0" };

        oldSource.Add("Ghost");

        VisibleLabels(sut).Should().Equal("SDXL 1.0");
    }

    [Fact]
    public void DisplayText_ShowsPlaceholder_UntilSomethingIsPicked()
    {
        var sut = CreateSut();
        sut.PlaceholderText = "Select a base model...";

        sut.DisplayText.Should().Be("Select a base model...");

        sut.SelectedItem = "Pony";

        sut.DisplayText.Should().Be("Pony");
    }

    [Fact]
    public void Rebuild_FiresASingleResetNotification_NotOnePerItem()
    {
        // The owning VMs refill their source with dozens of events per catalog
        // refresh; each one must cost the realized flyout list exactly one Reset,
        // not Clear + N Adds.
        var sut = CreateSut();
        var events = new List<NotifyCollectionChangedEventArgs>();
        sut.VisibleItems.CollectionChanged += (_, e) => events.Add(e);

        sut.SearchText = "sdxl";

        events.Should().ContainSingle().Which.Action.Should().Be(NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public void HasNoMatches_OnlyWhenAnActiveSearchYieldsNothing()
    {
        var sut = CreateSut();
        sut.HasNoMatches.Should().BeFalse();

        sut.SearchText = "zzz";
        sut.HasNoMatches.Should().BeTrue();

        sut.SearchText = "";
        sut.HasNoMatches.Should().BeFalse();
    }

    [Fact]
    public void HasNoMatches_StaysFalse_WhileTheSourceIsSimplyEmpty()
    {
        // An empty source with no search typed is a loading/empty state,
        // not a failed search — "No matches." must not show.
        var sut = CreateSut(new ObservableCollection<string>());

        sut.HasNoMatches.Should().BeFalse();

        sut.SearchText = "sdxl";
        sut.HasNoMatches.Should().BeTrue();
    }

    [Fact]
    public void TryCommitSingleMatch_PicksTheOnlyVisibleItem_AndCloses()
    {
        var sut = CreateSut();
        var closeRequests = 0;
        sut.CloseRequested += (_, _) => closeRequests++;
        sut.SearchText = "turbo";

        var committed = sut.TryCommitSingleMatch();

        committed.Should().BeTrue();
        sut.SelectedItem.Should().Be("SDXL Turbo");
        closeRequests.Should().Be(1);
    }

    [Fact]
    public void TryCommitSingleMatch_DoesNothing_WhenZeroOrSeveralItemsAreVisible()
    {
        var sut = CreateSut();
        var closeRequests = 0;
        sut.CloseRequested += (_, _) => closeRequests++;

        sut.SearchText = "sdxl";
        sut.TryCommitSingleMatch().Should().BeFalse();

        sut.SearchText = "zzz";
        sut.TryCommitSingleMatch().Should().BeFalse();

        sut.SelectedItem.Should().BeNull();
        closeRequests.Should().Be(0);
    }

    [Fact]
    public void IsSelected_MarksExactlyTheActiveLabel_AndFollowsSelectionChanges()
    {
        var sut = CreateSut();

        sut.SelectedItem = "SDXL Turbo";
        sut.VisibleItems.Where(i => i.IsSelected).Select(i => i.Label).Should().Equal("SDXL Turbo");

        sut.SelectedItem = "SD 1.5";
        sut.VisibleItems.Where(i => i.IsSelected).Select(i => i.Label).Should().Equal("SD 1.5");
    }

    [Fact]
    public void IsSelected_SurvivesARebuild_WhenTheSelectionStaysVisible()
    {
        var sut = CreateSut();
        sut.SelectedItem = "SDXL Turbo";

        sut.SearchText = "sdxl";

        sut.VisibleItems.Where(i => i.IsSelected).Select(i => i.Label).Should().Equal("SDXL Turbo");
    }
}
