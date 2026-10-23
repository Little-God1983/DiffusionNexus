namespace DiffusionNexus.Domain.Services.Sync;

/// <summary>One planned step within a <see cref="SyncPlan"/>: how much work and how long it's expected to take.</summary>
public sealed record SyncPlanStep(SyncStepKind Kind, int Count, TimeSpan EstimatedDuration, string Description);

/// <summary>The result of <see cref="ILibrarySyncService.PlanAsync"/> — what a sync run against a scope would do.</summary>
public sealed record SyncPlan(SyncScope Scope, SyncOptions Options, IReadOnlyList<SyncPlanStep> Steps, DateTimeOffset PlannedAt)
{
    /// <summary>
    /// Whether any step has counted work. DiscoverFiles is deliberately not special-cased: its
    /// count is always 0 (it scans, it cannot be counted in advance), so a discovery-bearing plan
    /// must be executed on its own terms, not smuggled in as "work" here — that special case made
    /// this property constant-true for every SyncOptions.All plan and the up-to-date branch dead.
    /// </summary>
    public bool HasWork => Steps.Any(s => s.Count > 0);
    public TimeSpan EstimatedDuration => TimeSpan.FromTicks(Steps.Sum(s => s.EstimatedDuration.Ticks));
}
