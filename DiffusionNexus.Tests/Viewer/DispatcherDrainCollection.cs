namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Test classes that drain <c>Dispatcher.UIThread.RunJobs()</c> from the test thread. The headless
/// dispatcher is one queue shared by the whole process, so a class running in parallel can
/// dequeue another class's posted job and still be half-way through it when that class's own
/// <c>RunJobs</c> finds the queue empty and returns — the test then reads state the job has not
/// finished writing (seen: Status already Completed, ActualSha256 still null). Classes in this
/// collection run one at a time.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DispatcherDrainCollection
{
    public const string Name = "Dispatcher drain";
}
