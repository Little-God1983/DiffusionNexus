namespace DiffusionNexus.Domain.Services.Sync;

/// <summary>One item that failed during a sync step, for surfacing in the result view.</summary>
public sealed record SyncFailure(SyncStepKind Step, int ModelId, string Name, string Reason);

/// <summary>Per-step outcome counts for a completed <see cref="SyncReport"/>.</summary>
public sealed record SyncStepReport(SyncStepKind Kind, int Planned, int Processed, int Succeeded, int Skipped, int Failed);

/// <summary>The result of <see cref="ILibrarySyncService.ExecuteAsync"/>.</summary>
public sealed record SyncReport(SyncPlan Plan, IReadOnlyList<SyncStepReport> Steps, IReadOnlyList<SyncFailure> Failures, bool Cancelled, TimeSpan Elapsed, int NewFilesDiscovered)
{
    public string Summary { get; } = BuildSummary(Steps, Cancelled, NewFilesDiscovered);

    private static string BuildSummary(IReadOnlyList<SyncStepReport> steps, bool cancelled, int discovered)
    {
        var parts = new List<string> { $"Discovered {discovered}" };
        foreach (var s in steps.Where(s => s.Kind != SyncStepKind.DiscoverFiles && s.Planned > 0))
            parts.Add($"{Label(s.Kind)} {s.Succeeded}/{s.Planned}");
        if (cancelled) parts.Add("(cancelled)");
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
