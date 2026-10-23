using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Models.Enums;

namespace DiffusionNexus.Tests.Catalog;

/// <summary>Hand-built SDK check results. The SDK's own tests cover CheckAsync; these describe its output.</summary>
internal static class CatalogChecks
{
    public static CatalogManifest Remote(int version, CatalogChannel channel) => new()
    {
        CatalogVersion = version,
        Channel = channel,
        Commit = "0123456",
        GeneratedAt = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero),
        Archive = new CatalogArchiveInfo("catalog.zip", new string('0', 64), 1024),
    };

    public static CatalogUpdateCheck Available(int version = 7, CatalogChannel channel = CatalogChannel.Stable,
        IReadOnlyList<WorkloadChange>? workloads = null, IReadOnlyList<WorkflowChange>? workflows = null) =>
        new(CatalogUpdateOutcome.UpdatesAvailable, channel, Remote(version, channel), new LocalCatalogState(),
            workloads ?? [WorkloadUpdated("Upscaling-Z-Image-Turbo", "V1.6", "V1.7")], workflows ?? [], null);

    public static CatalogUpdateCheck Outcome(CatalogUpdateOutcome outcome, CatalogChannel channel = CatalogChannel.Stable, string? error = null) =>
        new(outcome, channel, null, new LocalCatalogState(), [], [], error);

    public static WorkloadChange WorkloadUpdated(string name, string from, string to) => new(Guid.NewGuid(), name, ChangeKind.Updated, from, to);
    public static WorkloadChange WorkloadAdded(string name, string version) => new(Guid.NewGuid(), name, ChangeKind.Added, null, version);
    public static WorkloadChange WorkloadRemoved(string name, string version) => new(Guid.NewGuid(), name, ChangeKind.Removed, version, null);

    /// <summary>Every workload the check mentions marked as <paramref name="target"/>'s; workflows owned by all of them. What a 2.3.0 manifest carries.</summary>
    public static CatalogUpdateCheck Targeting(this CatalogUpdateCheck check, WorkloadTargetType target, bool released = true)
    {
        IReadOnlyList<Guid> owners = check.Workloads.Select(w => w.Id).ToList();
        return check with
        {
            Index = new CatalogAudienceIndex(
                check.Workloads.ToDictionary(w => w.Id, _ => new WorkloadAudienceInfo(target, released)),
                check.Workflows.ToDictionary(w => w.Id, _ => owners)),
        };
    }

    public static LocalCatalogState State(int version, CatalogChannel? channel, SectionOrigin origin = SectionOrigin.Remote) => new()
    {
        Workloads = new SectionState(version, "abc", DateTimeOffset.UtcNow) { Channel = channel, Origin = origin },
        Workflows = new SectionState(version, "abc", DateTimeOffset.UtcNow) { Channel = channel, Origin = origin },
    };
}
