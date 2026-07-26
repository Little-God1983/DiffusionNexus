using System.Collections.ObjectModel;
using System.Collections.Specialized;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Unit tests for <see cref="ImageViewerViewModel"/>, focused on the index
/// bookkeeping: clamping in the constructor, range rejection in
/// <c>NavigateTo</c>, and the re-index / close behaviour when items are removed
/// from the backing collection while the viewer is open.
/// <para>
/// Images always use a non-<c>.png</c> extension so the metadata panel takes its
/// early-out branch and never touches the file system, and are never videos so
/// the LibVLC player is never initialised.
/// </para>
/// </summary>
public class ImageViewerViewModelTests
{
    // Path.Combine keeps FileName assertions valid on any platform separator.
    private static DatasetImageViewModel Img(string name)
        => new() { ImagePath = Path.Combine("ds", $"{name}.jpg") };

    private static ObservableCollection<DatasetImageViewModel> Collection(params string[] names)
        => new(names.Select(Img));

    /// <summary>
    /// An <see cref="ObservableCollection{T}"/> that can replace its entire contents
    /// through the protected <c>Items</c> list (no per-item Add/Remove events) and then
    /// raise a single <see cref="NotifyCollectionChangedAction.Reset"/>, exactly like a
    /// real "bulk reload" / "switch dataset" implementation would. This is the only way
    /// to exercise a Reset that leaves some items in place — plain <c>Clear()</c> always
    /// empties the collection.
    /// </summary>
    private sealed class ResettableCollection : ObservableCollection<DatasetImageViewModel>
    {
        public ResettableCollection(IEnumerable<DatasetImageViewModel> items) : base(items)
        {
        }

        public void ResetWith(IEnumerable<DatasetImageViewModel> items)
        {
            Items.Clear();
            foreach (var item in items) Items.Add(item);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    #region Constructor clamping

    [Fact]
    public void WhenImagesIsNullThenConstructorThrows()
    {
        var act = () => new ImageViewerViewModel(null!, 0);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("images");
    }

    [Fact]
    public void WhenCollectionIsEmptyThenViewerStartsWithNoCurrentImage()
    {
        using var vm = new ImageViewerViewModel([], 0);

        vm.CurrentImage.Should().BeNull();
        vm.HasCurrentImage.Should().BeFalse();
        vm.CurrentIndex.Should().Be(0);
        vm.TotalCount.Should().Be(0);
        vm.PositionText.Should().Be("0 / 0");
        vm.CanGoPrevious.Should().BeFalse();
        vm.CanGoNext.Should().BeFalse();
        vm.ImagePath.Should().BeNull();
        vm.FileName.Should().BeNull();
    }

    [Fact]
    public void WhenDesignTimeConstructorIsUsedThenViewerIsEmptyAndDoesNotThrow()
    {
        var act = () => new ImageViewerViewModel();

        act.Should().NotThrow();
        using var vm = new ImageViewerViewModel();
        vm.TotalCount.Should().Be(0);
        vm.CurrentImage.Should().BeNull();
    }

    [Theory]
    [InlineData(-100, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(int.MaxValue, 2)]
    public void WhenStartIndexIsOutOfRangeThenItIsClampedIntoTheCollection(int startIndex, int expectedIndex)
    {
        var images = Collection("a", "b", "c");

        using var vm = new ImageViewerViewModel(images, startIndex);

        vm.CurrentIndex.Should().Be(expectedIndex);
        vm.CurrentImage.Should().BeSameAs(images[expectedIndex]);
    }

    [Fact]
    public void WhenStartIndexIsValidThenPositionTextIsOneBased()
    {
        var images = Collection("a", "b", "c");

        using var vm = new ImageViewerViewModel(images, 1);

        vm.PositionText.Should().Be("2 / 3");
        vm.FileName.Should().Be("b.jpg");
        vm.ImagePath.Should().Be(images[1].ImagePath);
        vm.HasCurrentImage.Should().BeTrue();
    }

    [Fact]
    public void WhenStartIndexIsNonZeroOnAnEmptyCollectionThenItStaysAtZero()
    {
        using var vm = new ImageViewerViewModel([], 42);

        vm.CurrentIndex.Should().Be(0);
        vm.CurrentImage.Should().BeNull();
    }

    [Fact]
    public void WhenRatingControlsAreNotRequestedThenTheyAreHidden()
    {
        var images = Collection("a");

        using var shown = new ImageViewerViewModel(images, 0);
        using var hidden = new ImageViewerViewModel(images, 0, showRatingControls: false);

        shown.ShowRatingControls.Should().BeTrue();
        hidden.ShowRatingControls.Should().BeFalse();
    }

    #endregion

    #region NavigateTo range handling

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void WhenNavigateToIsOutOfRangeThenItIsIgnored(int index)
    {
        var images = Collection("a", "b", "c");
        using var vm = new ImageViewerViewModel(images, 1);

        vm.NavigateTo(index);

        vm.CurrentIndex.Should().Be(1);
        vm.CurrentImage.Should().BeSameAs(images[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void WhenNavigateToIsInRangeThenTheCurrentImageFollows(int index)
    {
        var images = Collection("a", "b", "c");
        using var vm = new ImageViewerViewModel(images, 0);

        vm.NavigateTo(index);

        vm.CurrentIndex.Should().Be(index);
        vm.CurrentImage.Should().BeSameAs(images[index]);
        vm.PositionText.Should().Be($"{index + 1} / 3");
    }

    [Fact]
    public void WhenNavigateToRunsOnAnEmptyCollectionThenStateIsResetRatherThanRejected()
    {
        // The empty-collection branch runs before the range check, so even an
        // out-of-range index clears the viewer instead of being ignored. Clear()
        // itself already resets/closes the viewer via the collection-changed
        // resync; this extra NavigateTo(5) call on the now-empty collection
        // confirms the method is still safe (idempotent) when invoked directly.
        var images = Collection("a", "b");
        using var vm = new ImageViewerViewModel(images, 1, isFavoriteCheck: _ => true);
        vm.CurrentIndex.Should().Be(1);
        vm.IsFavorite.Should().BeTrue();

        images.Clear();
        vm.NavigateTo(5);

        vm.CurrentImage.Should().BeNull();
        vm.CurrentIndex.Should().Be(0);
        vm.IsFavorite.Should().BeFalse();
    }

    [Fact]
    public void WhenNavigatingToTheSameIndexThenCurrentImageChangedIsNotRaised()
    {
        var images = Collection("a", "b");
        using var vm = new ImageViewerViewModel(images, 0);
        var raised = 0;
        vm.CurrentImageChanged += (_, _) => raised++;

        vm.NavigateTo(0);

        raised.Should().Be(0);
    }

    [Fact]
    public void WhenNavigatingToADifferentIndexThenCurrentImageChangedIsRaisedOnce()
    {
        var images = Collection("a", "b");
        using var vm = new ImageViewerViewModel(images, 0);
        var raised = 0;
        vm.CurrentImageChanged += (_, _) => raised++;

        vm.NavigateTo(1);

        raised.Should().Be(1);
    }

    #endregion

    #region GoPrevious / GoNext at the boundaries

    [Fact]
    public void WhenAtTheFirstImageThenPreviousIsDisabledAndDoesNothing()
    {
        var images = Collection("a", "b", "c");
        using var vm = new ImageViewerViewModel(images, 0);

        vm.CanGoPrevious.Should().BeFalse();
        vm.PreviousCommand.CanExecute(null).Should().BeFalse();

        vm.PreviousCommand.Execute(null);

        vm.CurrentIndex.Should().Be(0);
        vm.CurrentImage.Should().BeSameAs(images[0]);
    }

    [Fact]
    public void WhenAtTheLastImageThenNextIsDisabledAndDoesNothing()
    {
        var images = Collection("a", "b", "c");
        using var vm = new ImageViewerViewModel(images, 2);

        vm.CanGoNext.Should().BeFalse();
        vm.NextCommand.CanExecute(null).Should().BeFalse();

        vm.NextCommand.Execute(null);

        vm.CurrentIndex.Should().Be(2);
        vm.CurrentImage.Should().BeSameAs(images[2]);
    }

    [Fact]
    public void WhenInTheMiddleThenBothDirectionsAreEnabled()
    {
        var images = Collection("a", "b", "c");
        using var vm = new ImageViewerViewModel(images, 1);

        vm.CanGoPrevious.Should().BeTrue();
        vm.CanGoNext.Should().BeTrue();

        vm.NextCommand.Execute(null);
        vm.CurrentIndex.Should().Be(2);

        vm.PreviousCommand.Execute(null);
        vm.CurrentIndex.Should().Be(1);

        vm.PreviousCommand.Execute(null);
        vm.CurrentIndex.Should().Be(0);
    }

    [Fact]
    public void WhenWalkingPastTheEndThenTheIndexStopsAtTheLastImage()
    {
        var images = Collection("a", "b", "c");
        using var vm = new ImageViewerViewModel(images, 0);

        for (var i = 0; i < 10; i++) vm.NextCommand.Execute(null);

        vm.CurrentIndex.Should().Be(2);
        vm.PositionText.Should().Be("3 / 3");
    }

    [Fact]
    public void WhenWalkingPastTheStartThenTheIndexStopsAtZero()
    {
        var images = Collection("a", "b", "c");
        using var vm = new ImageViewerViewModel(images, 2);

        for (var i = 0; i < 10; i++) vm.PreviousCommand.Execute(null);

        vm.CurrentIndex.Should().Be(0);
        vm.PositionText.Should().Be("1 / 3");
    }

    [Fact]
    public void WhenOnlyOneImageExistsThenBothDirectionsAreDisabled()
    {
        var images = Collection("only");
        using var vm = new ImageViewerViewModel(images, 0);

        vm.CanGoPrevious.Should().BeFalse();
        vm.CanGoNext.Should().BeFalse();
        vm.PositionText.Should().Be("1 / 1");
    }

    [Fact]
    public void WhenNavigatingThenCommandCanExecuteStatesAreRefreshed()
    {
        var images = Collection("a", "b");
        using var vm = new ImageViewerViewModel(images, 0);
        var previousChanges = 0;
        var nextChanges = 0;
        vm.PreviousCommand.CanExecuteChanged += (_, _) => previousChanges++;
        vm.NextCommand.CanExecuteChanged += (_, _) => nextChanges++;

        vm.NavigateTo(1);

        previousChanges.Should().BeGreaterThan(0);
        nextChanges.Should().BeGreaterThan(0);
        vm.PreviousCommand.CanExecute(null).Should().BeTrue();
        vm.NextCommand.CanExecute(null).Should().BeFalse();
    }

    #endregion

    #region Collection removal while the viewer is open

    [Fact]
    public void WhenTheCurrentImageIsRemovedFromTheMiddleThenTheNextImageSlidesIntoPlace()
    {
        var images = Collection("a", "b", "c");
        using var vm = new ImageViewerViewModel(images, 1);
        var c = images[2];

        images.RemoveAt(1);

        vm.TotalCount.Should().Be(2);
        vm.CurrentIndex.Should().Be(1);
        vm.CurrentImage.Should().BeSameAs(c);
        vm.PositionText.Should().Be("2 / 2");
    }

    [Fact]
    public void WhenTheCurrentImageIsTheLastAndIsRemovedThenTheIndexStepsBack()
    {
        var images = Collection("a", "b", "c");
        using var vm = new ImageViewerViewModel(images, 2);
        var b = images[1];

        images.RemoveAt(2);

        vm.TotalCount.Should().Be(2);
        vm.CurrentIndex.Should().Be(1);
        vm.CurrentImage.Should().BeSameAs(b);
        vm.CanGoNext.Should().BeFalse();
        vm.CanGoPrevious.Should().BeTrue();
    }

    [Fact]
    public void WhenAnEarlierImageIsRemovedThenTheIndexShiftsDownButTheImageStays()
    {
        var images = Collection("a", "b", "c");
        using var vm = new ImageViewerViewModel(images, 2);
        var c = images[2];

        images.RemoveAt(0);

        vm.CurrentImage.Should().BeSameAs(c);
        vm.CurrentIndex.Should().Be(1);
        vm.PositionText.Should().Be("2 / 2");
    }

    [Fact]
    public void WhenALaterImageIsRemovedThenNeitherIndexNorImageChange()
    {
        var images = Collection("a", "b", "c");
        using var vm = new ImageViewerViewModel(images, 0);
        var a = images[0];

        images.RemoveAt(2);

        vm.CurrentImage.Should().BeSameAs(a);
        vm.CurrentIndex.Should().Be(0);
        vm.TotalCount.Should().Be(2);
    }

    [Fact]
    public void WhenTheOnlyImageIsRemovedThenTheViewerClearsAndRequestsClose()
    {
        var images = Collection("only");
        using var vm = new ImageViewerViewModel(images, 0);
        var closeRequests = 0;
        vm.CloseRequested += (_, _) => closeRequests++;

        images.RemoveAt(0);

        vm.CurrentImage.Should().BeNull();
        vm.HasCurrentImage.Should().BeFalse();
        vm.CurrentIndex.Should().Be(0);
        vm.TotalCount.Should().Be(0);
        vm.PositionText.Should().Be("0 / 0");
        closeRequests.Should().Be(1);
    }

    [Fact]
    public void WhenImagesAreRemovedOneByOneThenCloseIsRequestedOnlyOnTheLastRemoval()
    {
        var images = Collection("a", "b");
        using var vm = new ImageViewerViewModel(images, 0);
        var closeRequests = 0;
        vm.CloseRequested += (_, _) => closeRequests++;

        images.RemoveAt(0);
        closeRequests.Should().Be(0);

        images.RemoveAt(0);
        closeRequests.Should().Be(1);
    }

    [Fact]
    public void WhenTheCollectionIsClearedThenTheViewerClosesJustLikeRemovingTheLastItemWould()
    {
        // Clear() raises Reset, not Remove. The viewer must treat a Reset down to
        // an empty collection exactly like removing the last item: drop the stale
        // current image and request a close.
        var images = Collection("a", "b");
        using var vm = new ImageViewerViewModel(images, 0);
        var closeRequests = 0;
        vm.CloseRequested += (_, _) => closeRequests++;

        images.Clear();

        closeRequests.Should().Be(1);
        vm.CurrentImage.Should().BeNull();
        vm.HasCurrentImage.Should().BeFalse();
        vm.CurrentIndex.Should().Be(0);
        vm.TotalCount.Should().Be(0);
        vm.PositionText.Should().Be("0 / 0");
    }

    [Fact]
    public void WhenAnImageIsAddedWhileTheViewerIsOpenThenCountsRefresh()
    {
        var images = Collection("a");
        using var vm = new ImageViewerViewModel(images, 0);
        var totalCountNotifications = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImageViewerViewModel.TotalCount)) totalCountNotifications++;
        };
        vm.CanGoNext.Should().BeFalse();

        images.Add(Img("b"));

        totalCountNotifications.Should().BeGreaterThan(0);
        vm.TotalCount.Should().Be(2);
        vm.PositionText.Should().Be("1 / 2");
        vm.CanGoNext.Should().BeTrue();
        vm.NextCommand.CanExecute(null).Should().BeTrue();
        // The current image itself is untouched by an unrelated addition.
        vm.CurrentImage.Should().BeSameAs(images[0]);
    }

    [Fact]
    public void WhenAResetLeavesTheCurrentImageInPlaceThenItIsKeptAndTheIndexIsReclamped()
    {
        var images = new ResettableCollection(Collection("a", "b", "c"));
        using var vm = new ImageViewerViewModel(images, 2);
        var c = images[2];
        vm.CurrentImage.Should().BeSameAs(c);

        // Bulk "reload" that happens to keep the current image but move it earlier.
        var a = images[0];
        images.ResetWith([a, c]);

        vm.TotalCount.Should().Be(2);
        vm.CurrentImage.Should().BeSameAs(c);
        vm.CurrentIndex.Should().Be(1);
        vm.PositionText.Should().Be("2 / 2");
    }

    [Fact]
    public void WhenAResetRemovesTheCurrentImageThenTheIndexIsClampedToTheNearestValidPosition()
    {
        var images = new ResettableCollection(Collection("a", "b", "c"));
        using var vm = new ImageViewerViewModel(images, 2);
        var closeRequests = 0;
        vm.CloseRequested += (_, _) => closeRequests++;

        // Bulk "reload" that drops the current image entirely, leaving two others.
        var a = images[0];
        var b = images[1];
        images.ResetWith([a, b]);

        closeRequests.Should().Be(0);
        vm.TotalCount.Should().Be(2);
        vm.CurrentIndex.Should().Be(1);
        vm.CurrentImage.Should().BeSameAs(b);
    }

    [Fact]
    public void WhenAResetLeavesTheCollectionEmptyThenTheViewerCloses()
    {
        var images = new ResettableCollection(Collection("a"));
        using var vm = new ImageViewerViewModel(images, 0);
        var closeRequests = 0;
        vm.CloseRequested += (_, _) => closeRequests++;

        images.ResetWith([]);

        closeRequests.Should().Be(1);
        vm.CurrentImage.Should().BeNull();
        vm.TotalCount.Should().Be(0);
    }

    [Fact]
    public void WhenADifferentImageIsReplacedThenTheCurrentImageIsUnaffected()
    {
        var images = Collection("a", "b", "c");
        using var vm = new ImageViewerViewModel(images, 1);
        var b = images[1];

        images[2] = Img("d"); // Replace action on an item that isn't current

        vm.CurrentImage.Should().BeSameAs(b);
        vm.CurrentIndex.Should().Be(1);
        vm.TotalCount.Should().Be(3);
    }

    [Fact]
    public void WhenTheCurrentImageIsReplacedThenTheSlotsNewOccupantIsShown()
    {
        // The old reference is gone, so the resync clamps to the same index rather
        // than jumping to a neighbor - the replacement item that now sits in that
        // slot is what gets shown, same as landing on whatever fills a vacated slot.
        var images = Collection("a", "b", "c");
        using var vm = new ImageViewerViewModel(images, 1);
        var d = Img("d");

        images[1] = d; // Replace action on the current item itself

        vm.CurrentImage.Should().BeSameAs(d);
        vm.CurrentIndex.Should().Be(1);
        vm.TotalCount.Should().Be(3);
    }

    [Fact]
    public void WhenTheCurrentImageIsMovedThenTheIndexFollowsItAndTheImageStays()
    {
        var images = Collection("a", "b", "c");
        using var vm = new ImageViewerViewModel(images, 0);
        var a = images[0];

        images.Move(0, 2); // -> [b, c, a]

        vm.CurrentImage.Should().BeSameAs(a);
        vm.CurrentIndex.Should().Be(2);
        vm.PositionText.Should().Be("3 / 3");
    }

    [Fact]
    public void WhenTheViewerIsDisposedThenLaterRemovalsAreIgnored()
    {
        var images = Collection("a");
        var vm = new ImageViewerViewModel(images, 0);
        var closeRequests = 0;
        vm.CloseRequested += (_, _) => closeRequests++;

        vm.Dispose();
        images.RemoveAt(0);

        closeRequests.Should().Be(0);
        vm.CurrentImage.Should().NotBeNull();
    }

    [Fact]
    public void WhenDisposedTwiceThenItDoesNotThrow()
    {
        var vm = new ImageViewerViewModel(Collection("a"), 0);

        var act = () =>
        {
            vm.Dispose();
            vm.Dispose();
        };

        act.Should().NotThrow();
    }

    #endregion

    #region Favourites

    [Fact]
    public void WhenNoFavouriteToggleIsSuppliedThenFavouriteControlsAreHidden()
    {
        using var vm = new ImageViewerViewModel(Collection("a"), 0);

        vm.ShowFavoriteControls.Should().BeFalse();
        vm.IsFavorite.Should().BeFalse();
    }

    [Fact]
    public void WhenAFavouriteToggleIsSuppliedThenFavouriteControlsAreShown()
    {
        using var vm = new ImageViewerViewModel(
            Collection("a"), 0, onToggleFavorite: _ => Task.FromResult(true));

        vm.ShowFavoriteControls.Should().BeTrue();
    }

    [Fact]
    public void WhenNavigatingThenTheFavouriteCheckIsQueriedForTheNewImage()
    {
        var images = Collection("a", "b");
        var queried = new List<string>();
        using var vm = new ImageViewerViewModel(
            images, 0, isFavoriteCheck: p => { queried.Add(p); return p.EndsWith("b.jpg"); });

        vm.IsFavorite.Should().BeFalse();

        vm.NavigateTo(1);

        vm.IsFavorite.Should().BeTrue();
        queried.Should().HaveCount(2);
        queried[0].Should().EndWith("a.jpg");
        queried[1].Should().EndWith("b.jpg");
    }

    [Fact]
    public async Task WhenToggleFavoriteRunsThenIsFavoriteTakesTheCallbackResult()
    {
        var images = Collection("a");
        var toggled = new List<string>();
        using var vm = new ImageViewerViewModel(images, 0, onToggleFavorite: p =>
        {
            toggled.Add(p);
            return Task.FromResult(true);
        });

        await vm.ToggleFavoriteCommand.ExecuteAsync(null);

        vm.IsFavorite.Should().BeTrue();
        toggled.Should().ContainSingle().Which.Should().Be(images[0].ImagePath);
    }

    [Fact]
    public async Task WhenToggleFavoriteRunsWithoutACurrentImageThenItIsANoOp()
    {
        var called = false;
        using var vm = new ImageViewerViewModel([], 0, onToggleFavorite: _ =>
        {
            called = true;
            return Task.FromResult(true);
        });

        await vm.ToggleFavoriteCommand.ExecuteAsync(null);

        called.Should().BeFalse();
        vm.IsFavorite.Should().BeFalse();
    }

    #endregion

    #region Close and hand-off commands

    [Fact]
    public void WhenCloseCommandRunsThenCloseRequestedIsRaised()
    {
        using var vm = new ImageViewerViewModel(Collection("a"), 0);
        var closeRequests = 0;
        vm.CloseRequested += (_, _) => closeRequests++;

        vm.CloseCommand.Execute(null);

        closeRequests.Should().Be(1);
    }

    [Fact]
    public void WhenSendToImageEditorRunsThenTheCallbackFiresAndTheViewerCloses()
    {
        var images = Collection("a", "b");
        DatasetImageViewModel? sent = null;
        using var vm = new ImageViewerViewModel(
            images, 1, onSendToImageEditor: i => sent = i);
        var closeRequests = 0;
        vm.CloseRequested += (_, _) => closeRequests++;

        vm.SendToImageEditorCommand.Execute(null);

        sent.Should().BeSameAs(images[1]);
        closeRequests.Should().Be(1);
    }

    [Fact]
    public void WhenSendToCaptioningRunsThenTheCallbackFiresAndTheViewerCloses()
    {
        var images = Collection("a");
        DatasetImageViewModel? sent = null;
        using var vm = new ImageViewerViewModel(
            images, 0, onSendToCaptioning: i => sent = i);
        var closeRequests = 0;
        vm.CloseRequested += (_, _) => closeRequests++;

        vm.SendToCaptioningCommand.Execute(null);

        sent.Should().BeSameAs(images[0]);
        closeRequests.Should().Be(1);
    }

    [Fact]
    public void WhenDeleteRunsThenTheCallbackFiresButTheViewerStaysOpen()
    {
        var images = Collection("a", "b");
        DatasetImageViewModel? deleted = null;
        using var vm = new ImageViewerViewModel(
            images, 0, onDeleteRequested: i => deleted = i);
        var closeRequests = 0;
        vm.CloseRequested += (_, _) => closeRequests++;

        vm.DeleteCommand.Execute(null);

        deleted.Should().BeSameAs(images[0]);
        closeRequests.Should().Be(0);
    }

    [Fact]
    public void WhenThereIsNoCurrentImageThenHandOffCommandsDoNothing()
    {
        var invoked = false;
        using var vm = new ImageViewerViewModel(
            [], 0,
            onSendToImageEditor: _ => invoked = true,
            onSendToCaptioning: _ => invoked = true,
            onDeleteRequested: _ => invoked = true);
        var closeRequests = 0;
        vm.CloseRequested += (_, _) => closeRequests++;

        vm.SendToImageEditorCommand.Execute(null);
        vm.SendToCaptioningCommand.Execute(null);
        vm.DeleteCommand.Execute(null);

        invoked.Should().BeFalse();
        closeRequests.Should().Be(0);
    }

    #endregion

    #region Refresh notifications

    [Fact]
    public void WhenRefreshCurrentImageRunsThenCaptionAndRatingPropertiesAreReannounced()
    {
        using var vm = new ImageViewerViewModel(Collection("a"), 0);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RefreshCurrentImage();

        raised.Should().Contain(nameof(ImageViewerViewModel.Caption));
        raised.Should().Contain(nameof(ImageViewerViewModel.HasCaption));
        raised.Should().Contain(nameof(ImageViewerViewModel.IsApproved));
        raised.Should().Contain(nameof(ImageViewerViewModel.IsRejected));
    }

    [Fact]
    public void WhenNoImageIsLoadedThenTheRatingFlagsFallBackToSafeDefaults()
    {
        using var vm = new ImageViewerViewModel([], 0);

        vm.IsApproved.Should().BeFalse();
        vm.IsRejected.Should().BeFalse();
        vm.IsVideo.Should().BeFalse();
        vm.IsImage.Should().BeTrue(); // defaults to "image" when nothing is loaded
        vm.HasCaption.Should().BeFalse();
    }

    #endregion
}
