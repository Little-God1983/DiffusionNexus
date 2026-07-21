using DiffusionNexus.UI.Services;

namespace DiffusionNexus.Tests.Helpers;

/// <summary>
/// Test double for <see cref="IUiScheduler"/> that runs everything inline on the
/// calling thread. This collapses the UI-thread marshalling seam so a ViewModel's
/// posted / invoked callbacks are observable synchronously in a unit test, with no
/// Avalonia dispatcher involved.
/// <para>
/// <see cref="IsOnUiThread"/> reports <c>true</c> so that "already on the UI
/// thread" fast paths (e.g. <c>TrainingRunCardViewModel</c>'s thumbnail apply)
/// take the inline branch.
/// </para>
/// </summary>
internal sealed class ImmediateUiScheduler : IUiScheduler
{
    /// <summary>Number of <see cref="Post"/> calls observed.</summary>
    public int PostCount { get; private set; }

    /// <summary>Number of <see cref="InvokeAsync"/> calls observed.</summary>
    public int InvokeCount { get; private set; }

    public void Post(Action action)
    {
        PostCount++;
        action();
    }

    public Task InvokeAsync(Action action)
    {
        InvokeCount++;
        action();
        return Task.CompletedTask;
    }

    public bool IsOnUiThread => true;
}
