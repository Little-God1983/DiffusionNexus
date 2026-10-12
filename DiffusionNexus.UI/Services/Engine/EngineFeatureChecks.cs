using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>One Engine feature as the disk check found it, every copy of every model included.</summary>
public sealed record EngineFeatureCheck(
    EngineFeature Feature,
    string DisplayName,
    IReadOnlyList<ConfigurationCheckResult> Results)
{
    /// <summary>Installed or Partial: at least one of its node packs or (non-placeholder) models is on disk.</summary>
    public bool IsPresent => Results.Any(r =>
        r.CustomNodeResults.Any(n => n.IsInstalled) || r.ModelResults.Any(m => m.IsInstalled && !m.IsPlaceholder));

    /// <summary>
    /// The feature's workloads the catalog has no description of. Their files are unknown: the feature
    /// may share any file with another one, so a plan built without them cannot tell what is safe to delete.
    /// </summary>
    public IReadOnlyList<Guid> MissingWorkloads { get; init; } = [];

    /// <summary>Every workload of the feature was found in the catalog, so <see cref="Results"/> is its whole file set.</summary>
    public bool IsComplete => MissingWorkloads.Count == 0;
}

/// <summary>Checks every Engine feature against the Engine folder, for removal planning.</summary>
public static class EngineFeatureChecks
{
    /// <summary>
    /// One check per feature in <see cref="EngineFeatureCatalog.All"/> order. A workload the catalog
    /// lacks contributes no files and is recorded in <see cref="EngineFeatureCheck.MissingWorkloads"/>;
    /// a check that returns nothing contributes nothing.
    /// </summary>
    public static async Task<IReadOnlyList<EngineFeatureCheck>> LoadAsync(
        ICatalog catalog, IConfigurationCheckerService checker, string engineRoot, CancellationToken ct)
    {
        var options = new ConfigurationCheckOptions { CollectAllMatches = true };
        var checks = new List<EngineFeatureCheck>();

        foreach (var definition in EngineFeatureCatalog.All)
        {
            var results = new List<ConfigurationCheckResult>();
            var missing = new List<Guid>();
            foreach (var workloadId in definition.WorkloadIds)
            {
                var config = await catalog.GetWorkloadAsync(workloadId, ct);
                if (config is null)
                {
                    missing.Add(workloadId);
                    continue;
                }

                var result = await checker.CheckConfigurationAsync(config, engineRoot, options, ct);
                if (result is not null) results.Add(result);
            }

            checks.Add(new EngineFeatureCheck(definition.Feature, definition.DisplayName, results) { MissingWorkloads = missing });
        }

        return checks;
    }
}
