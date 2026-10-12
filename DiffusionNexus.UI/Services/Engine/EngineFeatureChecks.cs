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
}

/// <summary>Checks every Engine feature against the Engine folder, for removal planning.</summary>
public static class EngineFeatureChecks
{
    /// <summary>
    /// One check per feature in <see cref="EngineFeatureCatalog.All"/> order. A workload the catalog
    /// lacks, or a check that returns nothing, contributes nothing: its files are not known.
    /// </summary>
    public static async Task<IReadOnlyList<EngineFeatureCheck>> LoadAsync(
        ICatalog catalog, IConfigurationCheckerService checker, string engineRoot, CancellationToken ct)
    {
        var options = new ConfigurationCheckOptions { CollectAllMatches = true };
        var checks = new List<EngineFeatureCheck>();

        foreach (var definition in EngineFeatureCatalog.All)
        {
            var results = new List<ConfigurationCheckResult>();
            foreach (var workloadId in definition.WorkloadIds)
            {
                var config = await catalog.GetWorkloadAsync(workloadId, ct);
                if (config is null) continue;

                var result = await checker.CheckConfigurationAsync(config, engineRoot, options, ct);
                if (result is not null) results.Add(result);
            }

            checks.Add(new EngineFeatureCheck(definition.Feature, definition.DisplayName, results));
        }

        return checks;
    }
}
