namespace DiffusionNexus.Domain.Services.Sync;

/// <summary>The individual steps a sync run can perform. Thumbnails is a reserved kind implemented in Plan B.</summary>
public enum SyncStepKind { DiscoverFiles, IdentifyModel, FetchTags, FetchImages, Thumbnails }
