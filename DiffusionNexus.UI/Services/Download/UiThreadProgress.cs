using Avalonia.Threading;

namespace DiffusionNexus.UI.Services.Download;

/// <summary>
/// An <see cref="IProgress{T}"/> that marshals to the Avalonia UI thread in ONE hop.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Progress{T}"/> looks like it does this already, and that is the trap. It never
/// invokes its handler inline: <c>Report</c> posts to the <see cref="SynchronizationContext"/>
/// captured at construction (or the thread pool when there is none), and the download surfaces
/// then post <i>again</i> from inside that handler to reach the dispatcher. Two hops for a
/// progress report against ONE hop for the terminal "Done"/"Cancelled" post means a report issued
/// microseconds before the download returns can have its second hop enqueued <i>after</i> the
/// terminal one — leaving the tile reading "512.0 / 512.0 MB" at 99%, or the toolbar stuck on
/// "Downloading foo.safetensors", forever.
/// </para>
/// <para>
/// Posting straight onto the dispatcher queue puts progress and terminal state in the same queue
/// in issue order, which is what the FIFO reasoning at those call sites always assumed. It also
/// drops the redundant double-marshal when the adapter happens to be built on the UI thread.
/// </para>
/// </remarks>
internal sealed class UiThreadProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public UiThreadProgress(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    public void Report(T value) => Dispatcher.UIThread.Post(() => _handler(value));
}
