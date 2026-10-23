namespace DiffusionNexus.Domain.Services.Sync;

/// <summary>Progress reported by <see cref="ILibrarySyncService.ExecuteAsync"/> while a step runs.</summary>
public sealed record LibrarySyncProgress(SyncStepKind Step, int Index, int Total, string? CurrentItem);
