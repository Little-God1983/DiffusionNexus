using System.Collections.Specialized;
using System.ComponentModel;
using DiffusionNexus.UI.Utilities;
using FluentAssertions;

namespace DiffusionNexus.Tests.Utilities;

/// <summary>
/// Unit tests for <see cref="BatchObservableCollection{T}"/>.
/// The point of the type is that <c>ReplaceAll</c> raises exactly one Reset
/// notification (plus the two property notifications bindings require) instead
/// of one event per item, so these tests count events rather than inspect them loosely.
/// </summary>
public class BatchObservableCollectionTests
{
    private static (List<NotifyCollectionChangedEventArgs> Collection, List<string?> Properties) Subscribe(
        BatchObservableCollection<int> sut)
    {
        var collectionEvents = new List<NotifyCollectionChangedEventArgs>();
        var propertyEvents = new List<string?>();

        sut.CollectionChanged += (_, e) => collectionEvents.Add(e);
        ((INotifyPropertyChanged)sut).PropertyChanged += (_, e) => propertyEvents.Add(e.PropertyName);

        return (collectionEvents, propertyEvents);
    }

    // ---------------------------------------------------------------
    //  Content
    // ---------------------------------------------------------------

    [Fact]
    public void WhenReplaceAllIsCalledOnAnEmptyCollectionThenItContainsTheNewItemsInOrder()
    {
        var sut = new BatchObservableCollection<int>();

        sut.ReplaceAll(new[] { 3, 1, 2 });

        sut.Should().Equal(3, 1, 2);
    }

    [Fact]
    public void WhenReplaceAllIsCalledOnAPopulatedCollectionThenTheOldItemsAreGone()
    {
        var sut = new BatchObservableCollection<int> { 1, 2, 3, 4, 5 };

        sut.ReplaceAll(new[] { 9 });

        sut.Should().Equal(9);
        sut.Count.Should().Be(1);
    }

    [Fact]
    public void WhenReplaceAllIsCalledWithAnEmptyListThenTheCollectionIsCleared()
    {
        var sut = new BatchObservableCollection<int> { 1, 2, 3 };

        sut.ReplaceAll(Array.Empty<int>());

        sut.Should().BeEmpty();
        sut.Count.Should().Be(0);
    }

    [Fact]
    public void WhenReplacementContainsDuplicatesThenAllOccurrencesArePreserved()
    {
        var sut = new BatchObservableCollection<int>();

        sut.ReplaceAll(new[] { 7, 7, 7 });

        sut.Should().Equal(7, 7, 7);
    }

    [Fact]
    public void WhenTheSourceListIsMutatedAfterwardsThenTheCollectionIsUnaffected()
    {
        var source = new List<int> { 1, 2 };
        var sut = new BatchObservableCollection<int>();
        sut.ReplaceAll(source);

        source.Add(3);
        source[0] = 99;

        sut.Should().Equal(1, 2);
    }

    // ---------------------------------------------------------------
    //  Collection notifications
    // ---------------------------------------------------------------

    [Fact]
    public void WhenReplaceAllIsCalledThenExactlyOneResetNotificationIsRaised()
    {
        var sut = new BatchObservableCollection<int> { 1, 2, 3 };
        var (collectionEvents, _) = Subscribe(sut);

        sut.ReplaceAll(new[] { 10, 20, 30, 40 });

        collectionEvents.Should().ContainSingle()
            .Which.Action.Should().Be(NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public void WhenManyItemsAreReplacedThenNoPerItemNotificationsAreRaised()
    {
        var sut = new BatchObservableCollection<int>();
        var (collectionEvents, _) = Subscribe(sut);

        sut.ReplaceAll(Enumerable.Range(0, 500).ToList());

        collectionEvents.Should().HaveCount(1);
        collectionEvents.Should().NotContain(e => e.Action == NotifyCollectionChangedAction.Add);
        collectionEvents.Should().NotContain(e => e.Action == NotifyCollectionChangedAction.Remove);
    }

    [Fact]
    public void WhenReplaceAllIsCalledWithAnEmptyListThenASingleResetIsStillRaised()
    {
        var sut = new BatchObservableCollection<int> { 1, 2, 3 };
        var (collectionEvents, _) = Subscribe(sut);

        sut.ReplaceAll(Array.Empty<int>());

        collectionEvents.Should().ContainSingle()
            .Which.Action.Should().Be(NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public void WhenAnEmptyCollectionIsReplacedWithAnEmptyListThenAResetIsStillRaised()
    {
        var sut = new BatchObservableCollection<int>();
        var (collectionEvents, _) = Subscribe(sut);

        sut.ReplaceAll(Array.Empty<int>());

        collectionEvents.Should().ContainSingle()
            .Which.Action.Should().Be(NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public void WhenTheResetIsRaisedThenItCarriesNoItemPayload()
    {
        var sut = new BatchObservableCollection<int>();
        var (collectionEvents, _) = Subscribe(sut);

        sut.ReplaceAll(new[] { 1, 2 });

        var reset = collectionEvents.Single();
        reset.NewItems.Should().BeNull();
        reset.OldItems.Should().BeNull();
        reset.NewStartingIndex.Should().Be(-1);
        reset.OldStartingIndex.Should().Be(-1);
    }

    [Fact]
    public void WhenReplaceAllIsCalledRepeatedlyThenOneResetIsRaisedPerCall()
    {
        var sut = new BatchObservableCollection<int>();
        var (collectionEvents, _) = Subscribe(sut);

        sut.ReplaceAll(new[] { 1 });
        sut.ReplaceAll(new[] { 2 });
        sut.ReplaceAll(new[] { 3 });

        collectionEvents.Should().HaveCount(3);
        collectionEvents.Should().OnlyContain(e => e.Action == NotifyCollectionChangedAction.Reset);
    }

    // ---------------------------------------------------------------
    //  Property notifications
    // ---------------------------------------------------------------

    [Fact]
    public void WhenReplaceAllIsCalledThenCountAndIndexerPropertyChangesAreRaisedInThatOrder()
    {
        var sut = new BatchObservableCollection<int>();
        var (_, propertyEvents) = Subscribe(sut);

        sut.ReplaceAll(new[] { 1, 2 });

        propertyEvents.Should().Equal("Count", "Item[]");
    }

    [Fact]
    public void WhenReplaceAllIsCalledWithAnEmptyListThenPropertyChangesAreStillRaised()
    {
        var sut = new BatchObservableCollection<int> { 1, 2 };
        var (_, propertyEvents) = Subscribe(sut);

        sut.ReplaceAll(Array.Empty<int>());

        propertyEvents.Should().Equal("Count", "Item[]");
    }

    [Fact]
    public void WhenCountPropertyChangeIsRaisedThenTheCollectionAlreadyReportsTheNewCount()
    {
        var sut = new BatchObservableCollection<int> { 1 };
        var observedCounts = new List<int>();
        ((INotifyPropertyChanged)sut).PropertyChanged += (_, _) => observedCounts.Add(sut.Count);

        sut.ReplaceAll(new[] { 1, 2, 3, 4 });

        observedCounts.Should().AllBeEquivalentTo(4);
    }

    [Fact]
    public void WhenTheResetIsHandledThenTheCollectionAlreadyExposesTheNewContent()
    {
        var sut = new BatchObservableCollection<int> { 1 };
        var snapshot = new List<int>();
        sut.CollectionChanged += (_, _) => snapshot.AddRange(sut);

        sut.ReplaceAll(new[] { 5, 6 });

        snapshot.Should().Equal(5, 6);
    }

    // ---------------------------------------------------------------
    //  Inherited behavior is preserved
    // ---------------------------------------------------------------

    [Fact]
    public void WhenAnItemIsAddedNormallyThenTheUsualAddNotificationIsStillRaised()
    {
        var sut = new BatchObservableCollection<int>();
        var (collectionEvents, _) = Subscribe(sut);

        sut.Add(1);

        collectionEvents.Should().ContainSingle()
            .Which.Action.Should().Be(NotifyCollectionChangedAction.Add);
    }

    [Fact]
    public void WhenClearIsCalledNormallyThenTheUsualResetNotificationIsStillRaised()
    {
        var sut = new BatchObservableCollection<int> { 1, 2 };
        var (collectionEvents, _) = Subscribe(sut);

        sut.Clear();

        collectionEvents.Should().ContainSingle()
            .Which.Action.Should().Be(NotifyCollectionChangedAction.Reset);
        sut.Should().BeEmpty();
    }

    [Fact]
    public void WhenReferenceTypesAreReplacedThenTheSameInstancesAreStored()
    {
        var a = new object();
        var b = new object();
        var sut = new BatchObservableCollection<object>();

        sut.ReplaceAll(new[] { a, b });

        sut[0].Should().BeSameAs(a);
        sut[1].Should().BeSameAs(b);
    }
}
