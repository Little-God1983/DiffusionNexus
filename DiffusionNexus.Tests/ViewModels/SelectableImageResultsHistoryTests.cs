using System.Collections.ObjectModel;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels.Controls;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Covers <see cref="SelectableImageResultsViewModel.BeginRun"/> — the clear-vs-keep decision that
/// lets Workflows accumulate a run history while single-batch hosts keep wiping the strip.
/// </summary>
public sealed class SelectableImageResultsHistoryTests
{
    private static ImageActionsViewModel MakeActions() =>
        new(Mock.Of<IDatasetState>(), Mock.Of<IDatasetEventAggregator>());

    private static ImageStatusItemViewModel MakeItem(string name) =>
        new() { FileName = name, InputPath = $@"C:\in\{name}" };

    [Fact]
    public void BeginRun_ByDefault_ClearsPreviousItems()
    {
        var items = new ObservableCollection<ImageStatusItemViewModel> { MakeItem("a.png"), MakeItem("b.png") };
        var vm = new SelectableImageResultsViewModel(items, MakeActions());

        vm.BeginRun();

        items.Should().BeEmpty();
    }

    [Fact]
    public void BeginRun_WhenKeepingHistory_RetainsPreviousItems()
    {
        var first = MakeItem("a.png");
        var items = new ObservableCollection<ImageStatusItemViewModel> { first };
        var vm = new SelectableImageResultsViewModel(items, MakeActions(), clearOnNewRun: false);

        vm.BeginRun();
        items.Add(MakeItem("b.png"));

        items.Should().HaveCount(2);
        items[0].Should().BeSameAs(first, "history keeps the oldest run first");
    }

    [Fact]
    public void BeginRun_WhenKeepingHistory_DropsSelectionAndPrimaryItem()
    {
        var stale = MakeItem("a.png");
        var items = new ObservableCollection<ImageStatusItemViewModel> { stale };
        var vm = new SelectableImageResultsViewModel(items, MakeActions(), clearOnNewRun: false);
        vm.SelectWithModifiers(stale, isShiftPressed: false, isCtrlPressed: false);
        vm.PrimaryItem.Should().BeSameAs(stale);

        vm.BeginRun();

        stale.IsSelected.Should().BeFalse("a new run must not leave earlier tiles armed for Add/Send");
        vm.PrimaryItem.Should().BeNull();
        vm.SelectionCount.Should().Be(0);
        vm.HasSelection.Should().BeFalse();
    }

    [Fact]
    public void BeginRun_WhenKeepingHistory_TrimsOldestBeyondCap()
    {
        var items = new ObservableCollection<ImageStatusItemViewModel>();
        for (var i = 0; i < 5; i++)
            items.Add(MakeItem($"{i}.png"));

        var vm = new SelectableImageResultsViewModel(items, MakeActions(), clearOnNewRun: false)
        {
            MaxHistoryItems = 3,
        };

        vm.BeginRun();

        items.Should().HaveCount(3);
        items.Select(i => i.FileName).Should().Equal("2.png", "3.png", "4.png");
    }

    [Fact]
    public void BeginRun_WhenKeepingHistoryWithUnlimitedCap_TrimsNothing()
    {
        var items = new ObservableCollection<ImageStatusItemViewModel>();
        for (var i = 0; i < 5; i++)
            items.Add(MakeItem($"{i}.png"));

        var vm = new SelectableImageResultsViewModel(items, MakeActions(), clearOnNewRun: false)
        {
            MaxHistoryItems = 0,
        };

        vm.BeginRun();

        items.Should().HaveCount(5);
    }

    [Fact]
    public void BeginRun_WhenClearing_ResetsPrimaryItem()
    {
        var stale = MakeItem("a.png");
        var items = new ObservableCollection<ImageStatusItemViewModel> { stale };
        var vm = new SelectableImageResultsViewModel(items, MakeActions());
        vm.SelectWithModifiers(stale, isShiftPressed: false, isCtrlPressed: false);

        vm.BeginRun();

        vm.PrimaryItem.Should().BeNull();
        vm.SelectionCount.Should().Be(0);
    }
}
