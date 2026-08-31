using System.Collections.ObjectModel;
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

    [Fact]
    public void VisibleItems_MirrorsItemsSourceInOrder_WhenSearchIsEmpty()
    {
        var sut = CreateSut();

        sut.VisibleItems.Should().Equal(Labels);
    }

    [Fact]
    public void SearchText_NarrowsCaseInsensitively_PreservingSourceOrder()
    {
        var sut = CreateSut();

        sut.SearchText = "sdxl";

        sut.VisibleItems.Should().Equal("SDXL 1.0", "SDXL Turbo");
    }

    [Fact]
    public void SearchText_ClearedAgain_RestoresFullList()
    {
        var sut = CreateSut();
        sut.SearchText = "sdxl";

        sut.SearchText = "";

        sut.VisibleItems.Should().Equal(Labels);
    }

    [Fact]
    public void SearchText_WhitespaceOnly_ShowsFullList()
    {
        var sut = CreateSut();

        sut.SearchText = "   ";

        sut.VisibleItems.Should().Equal(Labels);
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
        sut.VisibleItems.Should().Equal(Labels);
    }

    [Fact]
    public void VisibleItems_FollowsObservableSourceChanges_RespectingActiveSearch()
    {
        // The real sources are ObservableCollection<string> the VMs Clear() and re-Add()
        // when the Civitai catalog resolves asynchronously — the picker must follow.
        var source = new ObservableCollection<string> { "SD 1.5" };
        var sut = CreateSut(source);
        sut.SearchText = "sdxl";

        source.Add("SDXL 1.0");
        source.Add("Wan Video");

        sut.VisibleItems.Should().Equal("SDXL 1.0");
    }

    [Fact]
    public void VisibleItems_IgnoresChangesToAReplacedSource()
    {
        var oldSource = new ObservableCollection<string> { "SD 1.5" };
        var sut = CreateSut(oldSource);
        sut.ItemsSource = new[] { "SDXL 1.0" };

        oldSource.Add("Ghost");

        sut.VisibleItems.Should().Equal("SDXL 1.0");
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
}
