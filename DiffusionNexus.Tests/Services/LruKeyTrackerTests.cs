using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// Unit tests for <see cref="LruKeyTracker"/> — the Avalonia-free LRU bookkeeping
/// class extracted from <see cref="ThumbnailService"/> to fix #429 (a comparer
/// mismatch between the case-insensitive cache dictionary and the case-sensitive
/// <c>LinkedList&lt;string&gt;</c> access-order list let the cache grow unbounded).
///
/// These tests pin the fixed semantics: casing must never create duplicate tracked
/// keys, eviction must actually shrink the tracked set, and both removal and
/// re-touch must work regardless of casing.
/// </summary>
public class LruKeyTrackerTests
{
    [Fact]
    public void Touch_SameKeyDifferentCasing_CountsAsOneTrackedKey()
    {
        var tracker = new LruKeyTracker();

        tracker.Touch(@"C:\ds\Img.png");
        tracker.Touch(@"c:\ds\img.png");

        tracker.Count.Should().Be(1);
    }

    [Fact]
    public void Touch_ManyMixedCasingTouchesOfSameKey_NeverExceedsOneEntry()
    {
        var tracker = new LruKeyTracker();

        tracker.Touch(@"C:\ds\Img.png");
        tracker.Touch(@"c:\ds\IMG.png");
        tracker.Touch(@"C:\DS\img.PNG");
        tracker.Touch(@"c:\ds\img.png");

        tracker.Count.Should().Be(1);
    }

    [Fact]
    public void EvictLeastRecentlyUsed_RemovesOldestKeyFirst()
    {
        var tracker = new LruKeyTracker();
        tracker.Touch("a");
        tracker.Touch("b");
        tracker.Touch("c");

        var evicted = tracker.EvictLeastRecentlyUsed();

        evicted.Should().Be("a");
        tracker.Count.Should().Be(2);
    }

    [Fact]
    public void EvictLeastRecentlyUsed_ShrinksToCapEvenAfterMixedCasingTouches()
    {
        // Regression for the exact #429 scenario: a duplicate-appearing key (differing
        // only by casing) must not let eviction drain the order list without the
        // tracked count actually dropping to the cap.
        var tracker = new LruKeyTracker();
        tracker.Touch(@"C:\ds\a.png");
        tracker.Touch(@"C:\ds\b.png");
        tracker.Touch(@"C:\ds\c.png");

        // Re-touch "a" and "b" with different casing — under the old LinkedList<string>
        // behavior this would append stale duplicates instead of moving existing nodes.
        tracker.Touch(@"c:\ds\A.PNG");
        tracker.Touch(@"c:\ds\B.PNG");

        // Only 3 distinct keys are tracked, so evicting down to a cap of 1 should take
        // exactly 2 evictions and leave exactly 1 key tracked.
        const int cap = 1;
        var evictedCount = 0;
        while (tracker.Count > cap)
        {
            var evicted = tracker.EvictLeastRecentlyUsed();
            evicted.Should().NotBeNull();
            evictedCount++;
        }

        evictedCount.Should().Be(2);
        tracker.Count.Should().Be(cap);
    }

    [Fact]
    public void Remove_WithMismatchedCasing_RemovesTrackedKey()
    {
        var tracker = new LruKeyTracker();
        tracker.Touch(@"C:\ds\Img.png");

        var removed = tracker.Remove(@"c:\ds\IMG.png");

        removed.Should().BeTrue();
        tracker.Count.Should().Be(0);
    }

    [Fact]
    public void Remove_KeyNotTracked_ReturnsFalseAndLeavesOthersIntact()
    {
        var tracker = new LruKeyTracker();
        tracker.Touch("a");

        var removed = tracker.Remove("does-not-exist");

        removed.Should().BeFalse();
        tracker.Count.Should().Be(1);
    }

    [Fact]
    public void Touch_ExistingKey_MovesItToMostRecentlyUsed()
    {
        var tracker = new LruKeyTracker();
        tracker.Touch("a");
        tracker.Touch("b");
        tracker.Touch("c");

        // "a" is currently the least-recently-used. Re-touching it should push it to
        // the most-recently-used end so the *next* eviction takes "b" instead.
        tracker.Touch("a");

        var evicted = tracker.EvictLeastRecentlyUsed();

        evicted.Should().Be("b");
    }

    [Fact]
    public void Touch_ExistingKeyDifferentCasing_MovesItToMostRecentlyUsed()
    {
        var tracker = new LruKeyTracker();
        tracker.Touch(@"C:\ds\a.png");
        tracker.Touch(@"C:\ds\b.png");
        tracker.Touch(@"C:\ds\c.png");

        // Re-touch "a" via a different casing — should still count as the same key
        // and move to most-recently-used, not append a duplicate.
        tracker.Touch(@"c:\ds\A.PNG");

        tracker.Count.Should().Be(3);
        var evicted = tracker.EvictLeastRecentlyUsed();
        evicted.Should().Be(@"C:\ds\b.png");
    }

    [Fact]
    public void EvictLeastRecentlyUsed_AfterDifferentCasingTouch_ReturnsOriginalFirstSeenCasing()
    {
        // Pins the casing-identity half of #429: re-touching with different casing
        // moves the existing node (proven above), but the *stored* key string must
        // still be the originally recorded casing, not the later touch's casing.
        var tracker = new LruKeyTracker();
        tracker.Touch(@"C:\ds\A.png");
        tracker.Touch(@"C:\ds\b.png");

        tracker.Touch(@"c:\ds\a.png");

        // "A.png" is now most-recently-used, so "b.png" evicts first.
        tracker.EvictLeastRecentlyUsed().Should().Be(@"C:\ds\b.png");

        var evicted = tracker.EvictLeastRecentlyUsed();
        evicted.Should().Be(@"C:\ds\A.png");
    }

    [Fact]
    public void EvictLeastRecentlyUsed_WhenEmpty_ReturnsNull()
    {
        var tracker = new LruKeyTracker();

        tracker.EvictLeastRecentlyUsed().Should().BeNull();
    }

    [Fact]
    public void Clear_RemovesAllTrackedKeys()
    {
        var tracker = new LruKeyTracker();
        tracker.Touch("a");
        tracker.Touch("b");

        tracker.Clear();

        tracker.Count.Should().Be(0);
        tracker.EvictLeastRecentlyUsed().Should().BeNull();
    }
}
