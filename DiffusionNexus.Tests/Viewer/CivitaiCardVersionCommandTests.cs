using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// The version picker's own enqueue commands: "Add selected to queue" acts on the
/// ticked rows only, and must track ticking live — the card has to hear its version
/// rows change or the button stays stuck at whatever it was when the card loaded.
/// </summary>
public sealed class CivitaiCardVersionCommandTests
{
    private static CivitaiResultViewModel Card(int versionCount = 3)
    {
        var model = new CivitaiModel
        {
            Id = 900,
            Name = "MiniMax",
            ModelVersions = [.. Enumerable.Range(1, versionCount).Select(i => new CivitaiModelVersion
            {
                Id = i,
                Name = $"v1.{versionCount - i}",
                BaseModel = "MiniMax H3"
            })]
        };
        return new CivitaiResultViewModel(model, showNsfwPreviews: false);
    }

    [Fact]
    public void EnqueueSelected_HandsBackOnlyTheTickedRows()
    {
        var card = Card();
        List<CivitaiVersionPickItemViewModel>? handed = null;
        card.EnqueueSelectedVersionsHandler = c => handed = [.. c.Versions.Where(v => v.IsSelected)];

        card.Versions[0].IsSelected = true;   // latest is pre-selected already
        card.Versions[1].IsSelected = true;
        card.EnqueueSelectedVersionsCommand.Execute(null);

        handed.Should().NotBeNull();
        handed!.Select(v => v.Name).Should().Equal("v1.2", "v1.1");
    }

    [Fact]
    public void EnqueueSelected_IsDisabledWhenNothingIsTicked()
    {
        var card = Card();
        card.Versions[0].IsSelected = false;   // untick the pre-selected latest

        card.HasSelectedVersions.Should().BeFalse();
        card.EnqueueSelectedVersionsCommand.CanExecute(null).Should().BeFalse();

        card.Versions[2].IsSelected = true;

        card.HasSelectedVersions.Should().BeTrue();
        card.EnqueueSelectedVersionsCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void TickingARow_RefreshesTheSummaryLine()
    {
        var card = Card();
        var raised = new List<string?>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        card.Versions[1].IsSelected = true;

        card.SelectedVersionSummary.Should().Be("2 versions selected");
        raised.Should().Contain(nameof(CivitaiResultViewModel.SelectedVersionSummary));
    }

    [Fact]
    public void EnqueueAll_IgnoresTheTickBoxes()
    {
        var card = Card();
        var handedCount = 0;
        card.EnqueueAllVersionsHandler = c => handedCount = c.Versions.Count;

        card.EnqueueAllVersionsCommand.Execute(null);

        handedCount.Should().Be(3, "'Add all to queue' means every version, ticked or not");
    }
}
