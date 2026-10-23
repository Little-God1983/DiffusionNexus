namespace DiffusionNexus.Domain.Services.Sync;

/// <summary>What a sync run targets.</summary>
public enum SyncScopeKind { Library, SourceFolder, Models }

/// <summary>
/// The target of a sync run: the whole library, one configured source folder,
/// or an explicit set of model ids.
/// </summary>
public sealed record SyncScope(SyncScopeKind Kind, string? SourceFolder = null, IReadOnlyList<int>? ModelIds = null)
{
    public static SyncScope Library { get; } = new(SyncScopeKind.Library);
    public static SyncScope ForFolder(string folder) => new(SyncScopeKind.SourceFolder, SourceFolder: folder);
    public static SyncScope ForModels(params int[] ids) => new(SyncScopeKind.Models, ModelIds: ids);
}
