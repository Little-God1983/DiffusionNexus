namespace DiffusionNexus.Domain.Services.Sync;

/// <summary>
/// Thrown by <see cref="ILibrarySyncService.ExecuteAsync"/> when a run is already holding the
/// service's single-flight slot. A "not now", not a fault — the caller reports it and moves on.
/// </summary>
/// <remarks>
/// It has its own type because a bare <see cref="InvalidOperationException"/> is far wider than the
/// gate: <c>GetRequiredService&lt;IUnitOfWork&gt;()</c> inside a step's selection, a DI registration
/// regression, a <c>Single()</c> over an empty sequence — all of them are
/// <see cref="InvalidOperationException"/> too, and a caller catching the base type told the user
/// "a sync is already running" about a genuine bug, logged it at Info without the exception, and
/// re-enabled the button so the same wrong answer came back forever.
/// It still derives from <see cref="InvalidOperationException"/> so existing callers keep working.
/// </remarks>
public sealed class SyncAlreadyRunningException : InvalidOperationException
{
    public SyncAlreadyRunningException()
        : base("A library sync is already running.")
    {
    }
}
