using DiffusionNexus.Domain.Services;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using Serilog;

namespace DiffusionNexus.UI.Services.Catalog;

/// <summary>
/// Checks the catalog channel once in the background after startup and applies what it finds. The
/// main app has no catalog-update UI, so without this it would only see new workloads after the user
/// happened to run the installer. Every outcome goes to the Unified Console; nothing is shown as a
/// dialog, and nothing escapes <see cref="RunAsync"/>: an offline machine simply keeps its catalog.
/// <para>
/// Runs after <see cref="CatalogStartup"/> has set <see cref="CatalogOptions.Channel"/> to the
/// installer's channel, which <see cref="ICatalogUpdateService.CheckAsync(CancellationToken)"/> reads.
/// An apply invalidates <c>ICatalog</c>, so the next read (e.g. the Workloads dialog) sees it.
/// </para>
/// </summary>
public sealed class CatalogStartupUpdater
{
    public const string LogSource = "Catalog";

    private readonly ICatalogUpdateService _updates;
    private readonly IActivityLogService? _activityLog;

    public CatalogStartupUpdater(ICatalogUpdateService updates, IActivityLogService? activityLog)
    {
        ArgumentNullException.ThrowIfNull(updates);
        _updates = updates;
        _activityLog = activityLog;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            var check = await _updates.CheckAsync(ct).ConfigureAwait(false);
            var remote = check.Remote is null ? "" : $" v{check.Remote.CatalogVersion}";

            switch (check.Outcome)
            {
                case CatalogUpdateOutcome.Failed:
                    _activityLog?.LogWarning(LogSource, $"Catalog update check failed ({check.Channel}); keeping the installed catalog.", check.Error);
                    return;
                case CatalogUpdateOutcome.OverrideActive:
                    _activityLog?.LogInfo(LogSource, "Catalog override active, updates off.");
                    return;
                case CatalogUpdateOutcome.UpToDate:
                    _activityLog?.LogInfo(LogSource, $"Catalog is up to date ({check.Channel}{remote}).");
                    return;
                case CatalogUpdateOutcome.RequiresNewerSoftware:
                    _activityLog?.LogWarning(LogSource, $"A newer catalog ({check.Channel}{remote}) needs a newer Diffusion Nexus; keeping the installed catalog.", check.Error);
                    return;
                case CatalogUpdateOutcome.UpdatesAvailable:
                    break;
                default:
                    _activityLog?.LogWarning(LogSource, $"Unknown catalog update outcome {check.Outcome}; nothing applied.");
                    return;
            }

            _activityLog?.LogInfo(LogSource, $"Catalog update available ({check.Channel}{remote}); applying in the background.");
            var result = await _updates.ApplyAsync(check, CatalogSections.All, progress: null, ct).ConfigureAwait(false);

            // The changes describe the Workloads section; list them only if it landed.
            var workloadChanges = result.Applied.HasFlag(CatalogSections.Workloads) ? check.Workloads : [];
            foreach (var change in workloadChanges)
            {
                var versions = change.Kind switch
                {
                    ChangeKind.Added => $"v{change.ToVersion}",
                    ChangeKind.Removed => $"was v{change.FromVersion}",
                    _ => $"v{change.FromVersion} → v{change.ToVersion}"
                };
                _activityLog?.LogInfo(LogSource, $"{change.Kind}: {change.Name} ({versions})");
            }

            if (result.Failed == CatalogSections.None)
            {
                _activityLog?.LogSuccess(LogSource, $"Catalog updated to{remote} ({check.Channel}).",
                    $"{check.Workloads.Count} workload and {check.Workflows.Count} workflow change(s).");
            }
            else
            {
                _activityLog?.LogWarning(LogSource,
                    $"Catalog update incomplete: {result.Failed} not applied (applied: {result.Applied}).", result.Error);
            }
        }
        catch (Exception ex)
        {
            // CheckAsync is documented never to throw; ApplyAsync can (I/O). Neither may escape.
            Log.Warning(ex, "CatalogStartupUpdater: background catalog update failed");
            _activityLog?.LogWarning(LogSource, "Background catalog update failed; keeping the installed catalog.", ex.Message);
        }
    }
}
