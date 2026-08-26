namespace DiffusionNexus.Domain.Services.Sync;

/// <summary>One item that failed during a sync step, for surfacing in the result view.</summary>
public sealed record SyncFailure(SyncStepKind Step, int ModelId, string Name, string Reason);

/// <summary>Per-step outcome counts for a completed <see cref="SyncReport"/>.</summary>
public sealed record SyncStepReport(SyncStepKind Kind, int Planned, int Processed, int Succeeded, int Skipped, int Failed);

/// <summary>The result of <see cref="ILibrarySyncService.ExecuteAsync"/>.</summary>
/// <param name="UnexpectedFailures">
/// How many items failed with an exception no step claimed — i.e. a bug (R5). Such an item is
/// counted as failed and the run carries on: one NullReferenceException destroying the tally of a
/// 2 500-model run, and showing the user a raw stack message instead of a report, cost far more
/// than it ever revealed. It is not swallowed — every one is logged at Error with the exception,
/// and this counter exists so the UI can say so out loud rather than leaving it in the log.
/// </param>
/// <param name="FirstUnexpectedError">
/// The message of the first such exception, so the status line can name it without the caller
/// digging through <see cref="Failures"/>. Null when <paramref name="UnexpectedFailures"/> is 0.
/// </param>
/// <param name="AbortReason">
/// Why the run stopped early without being cancelled: an exception escaped <i>outside</i> the item
/// loop — a step's <c>SelectAsync</c>, or the API-key read — which is a bug, not an item verdict
/// (#535). The steps that ran are still reported (their work is committed); the steps at and after
/// the failing one never ran, so they are absent from <paramref name="Steps"/>. Null for a run
/// that completed or was cancelled. A caller judging "did this run cover everything it was asked
/// to?" must treat a non-null value as no.
/// </param>
public sealed record SyncReport(
    SyncPlan Plan,
    IReadOnlyList<SyncStepReport> Steps,
    IReadOnlyList<SyncFailure> Failures,
    bool Cancelled,
    TimeSpan Elapsed,
    int NewFilesDiscovered,
    int UnexpectedFailures = 0,
    string? FirstUnexpectedError = null,
    string? AbortReason = null)
{
    public string Summary { get; } = BuildSummary(Steps, Cancelled, NewFilesDiscovered, AbortReason is not null);

    private static string BuildSummary(IReadOnlyList<SyncStepReport> steps, bool cancelled, int discovered, bool aborted)
    {
        var parts = new List<string> { $"Discovered {discovered}" };
        foreach (var s in steps.Where(s => s.Kind != SyncStepKind.DiscoverFiles && s.Planned > 0))
            parts.Add($"{Label(s.Kind)} {s.Succeeded}/{s.Planned}");
        if (cancelled) parts.Add("(cancelled)");
        if (aborted) parts.Add("(aborted)");
        return string.Join(" · ", parts);
    }

    public static string Label(SyncStepKind kind) => kind switch
    {
        SyncStepKind.DiscoverFiles => "Discovered",
        SyncStepKind.IdentifyModel => "Identified",
        SyncStepKind.FetchTags => "Tags",
        SyncStepKind.FetchImages => "Images",
        SyncStepKind.Thumbnails => "Thumbnails",
        _ => kind.ToString(),
    };
}
