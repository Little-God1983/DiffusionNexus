namespace DiffusionNexus.Domain.Services.Sync;

/// <summary>One planned step within a <see cref="SyncPlan"/>: how much work and how long it's expected to take.</summary>
public sealed record SyncPlanStep(SyncStepKind Kind, int Count, TimeSpan EstimatedDuration, string Description);

/// <summary>The result of <see cref="ILibrarySyncService.PlanAsync"/> — what a sync run against a scope would do.</summary>
public sealed record SyncPlan(SyncScope Scope, SyncOptions Options, IReadOnlyList<SyncPlanStep> Steps, DateTimeOffset PlannedAt)
{
    public bool HasWork => Steps.Any(s => s.Count > 0 || s.Kind == SyncStepKind.DiscoverFiles);
    public TimeSpan EstimatedDuration => TimeSpan.FromTicks(Steps.Sum(s => s.EstimatedDuration.Ticks));
}
