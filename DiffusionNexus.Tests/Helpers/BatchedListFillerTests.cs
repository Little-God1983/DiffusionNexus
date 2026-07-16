using DiffusionNexus.UI.Helpers;

namespace DiffusionNexus.Tests.Helpers;

public class BatchedListFillerTests
{
    [Fact]
    public void FillsEverything_InOrder_AcrossBatches()
    {
        var source = Enumerable.Range(0, 10).ToList();
        var target = new List<int>();
        var posts = new Queue<Action>();

        BatchedListFiller.Fill(target, source, 0, 10, batchSize: 3, post: posts.Enqueue);

        Assert.Equal(new[] { 0, 1, 2 }, target); // first batch is synchronous
        while (posts.Count > 0) posts.Dequeue().Invoke();
        Assert.Equal(source, target);
    }

    [Fact]
    public void SmallSource_FillsSynchronously_WithoutPosting()
    {
        var target = new List<int>();
        var posts = new Queue<Action>();

        BatchedListFiller.Fill(target, new List<int> { 1, 2 }, 0, 2, batchSize: 5, post: posts.Enqueue);

        Assert.Equal(new[] { 1, 2 }, target);
        Assert.Empty(posts);
    }

    [Fact]
    public void Cancel_AbandonsRemainingBatches()
    {
        var source = Enumerable.Range(0, 10).ToList();
        var target = new List<int>();
        var posts = new Queue<Action>();

        var cancel = BatchedListFiller.Fill(target, source, 0, 10, batchSize: 4, post: posts.Enqueue);
        cancel();
        while (posts.Count > 0) posts.Dequeue().Invoke();

        Assert.Equal(new[] { 0, 1, 2, 3 }, target); // only the synchronous batch landed
    }

    [Fact]
    public void OnCompleted_FiresOnce_AfterLastBatch()
    {
        var source = Enumerable.Range(0, 7).ToList();
        var target = new List<int>();
        var posts = new Queue<Action>();
        var completed = 0;

        BatchedListFiller.Fill(target, source, 0, 7, 3, posts.Enqueue, () => completed++);
        while (posts.Count > 0) posts.Dequeue().Invoke();

        Assert.Equal(1, completed);
    }

    [Fact]
    public void RespectsStartOffset()
    {
        var source = Enumerable.Range(0, 10).ToList();
        var target = new List<int>();
        var posts = new Queue<Action>();

        BatchedListFiller.Fill(target, source, 6, 10, 10, posts.Enqueue);

        Assert.Equal(new[] { 6, 7, 8, 9 }, target);
    }

    [Fact]
    public void SourceShrinksMidFill_ClampsToNewTail_WithoutThrowing_AndCompletes()
    {
        var source = Enumerable.Range(0, 10).ToList();
        var target = new List<int>();
        var posts = new Queue<Action>();
        var completed = 0;

        BatchedListFiller.Fill(target, source, 0, 10, batchSize: 4, post: posts.Enqueue, onCompleted: () => completed++);

        // Synchronous batch landed [0..3]. The source shrinks (e.g. a tile is
        // deleted) before the posted batches run — the fill must clamp to the
        // new tail instead of reading past it.
        source.RemoveRange(5, 5); // count 10 -> 5

        while (posts.Count > 0) posts.Dequeue().Invoke();

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, target);
        Assert.Equal(1, completed);
    }

    [Fact]
    public void SourceShrinksBelowFilledCount_MidFill_StopsCleanly_AndCompletes()
    {
        var source = Enumerable.Range(0, 10).ToList();
        var target = new List<int>();
        var posts = new Queue<Action>();
        var completed = 0;

        BatchedListFiller.Fill(target, source, 0, 10, batchSize: 4, post: posts.Enqueue, onCompleted: () => completed++);

        // Shrink below what the synchronous batch already consumed — the next
        // index (4) is now past the tail entirely.
        source.RemoveRange(2, 8); // count 10 -> 2

        while (posts.Count > 0) posts.Dequeue().Invoke();

        Assert.Equal(new[] { 0, 1, 2, 3 }, target); // only the synchronous batch
        Assert.Equal(1, completed);
    }
}
