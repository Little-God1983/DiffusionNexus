using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Identifies what triggered a LoRA update check. Logged alongside each
/// attempt so the log file shows why a Civitai call was issued.
/// </summary>
public enum LoraUpdateTriggerSource
{
    /// <summary>
    /// The model's <c>LastCheckedForUpdatesUtc</c> was older than the configured
    /// staleness threshold — triggered by paginating or filtering the LoRA Viewer.
    /// </summary>
    Stale,

    /// <summary>
    /// The user opened the detail view for a single tile.
    /// </summary>
    DetailView,

    /// <summary>
    /// The user pressed the "Download Metadata" button. This path operates on
    /// the entire library; per-tile trigger logging is suppressed to avoid
    /// flooding the log with 10K identical entries.
    /// </summary>
    DownloadMetadataButton,
}

/// <summary>
/// Silently checks Civitai for additional model versions for the currently
/// visible <see cref="ModelTileViewModel"/> instances. Skips tiles whose last
/// check is younger than the configured staleness, respects Civitai rate
/// limits, and is bounded so a paginated view only checks the tiles that are
/// actually on screen.
/// </summary>
public interface ILoraUpdateChecker
{
    /// <summary>
    /// Runs an update check against Civitai for the supplied tiles. Tiles that
    /// were checked within <paramref name="staleness"/> are skipped, as are
    /// tiles without a Civitai identifier. Persisted version counts and the
    /// <see cref="DiffusionNexus.Domain.Entities.Model.LastCheckedForUpdatesUtc"/>
    /// timestamp are updated for every successful check.
    /// </summary>
    /// <param name="tiles">The tiles currently in the paginated view.</param>
    /// <param name="staleness">
    /// Maximum age a previous check may have before the tile is re-checked.
    /// <see cref="TimeSpan.Zero"/> disables the check entirely.
    /// </param>
    /// <param name="source">
    /// What caused this batch to run. Recorded on every per-tile log entry so
    /// the log file makes it obvious whether a check came from staleness, the
    /// detail view, or another path.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token. Callers should pass a fresh token per page so
    /// in-flight checks for a previous page are cancelled when the user
    /// paginates quickly.
    /// </param>
    Task CheckVisibleAsync(
        IEnumerable<ModelTileViewModel> tiles,
        TimeSpan staleness,
        LoraUpdateTriggerSource source,
        CancellationToken cancellationToken);
}
