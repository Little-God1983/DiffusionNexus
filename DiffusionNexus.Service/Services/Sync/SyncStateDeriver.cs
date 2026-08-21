using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Service.Services.Sync;

/// <summary>
/// Turns a pre-existing <see cref="Model"/> into the <see cref="ModelSyncState"/> row it would
/// have had if the sync state had always been recorded — purely from data already in the
/// database. Never touches the network: a library that has been synced for years must not
/// re-ask Civitai about every model just because the state table is new.
/// </summary>
public static class SyncStateDeriver
{
    /// <summary>
    /// Derives the state row for <paramref name="model"/>. The model is expected to be loaded
    /// with its Versions (and their Images) and Tags — see <c>GetByIdWithIncludesAsync</c>.
    /// </summary>
    /// <param name="model">The legacy model.</param>
    /// <param name="now">The derivation timestamp; also the row's <c>UpdatedAt</c>.</param>
    public static ModelSyncState Derive(Model model, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(model);

        var stamp = model.LastSyncedAt ?? now;
        var state = new ModelSyncState { ModelId = model.Id, UpdatedAt = now };

        if (model.CivitaiId is not null)
        {
            state.MetadataOutcome = SyncOutcome.Matched;
            state.MetadataCheckedAt = stamp;

            // A matched model with no tags / no images keeps those columns null on purpose:
            // it is genuinely unknown whether they were ever fetched, so the tag and image
            // steps get to ask once and stamp the answer.
            state.TagsCheckedAt = model.Tags.Count > 0 ? stamp : null;
            state.ImagesCheckedAt = model.Versions.Any(v => v.Images.Count > 0) ? stamp : null;
            return state;
        }

        // Never synced and never matched: nothing has ever been checked (SyncOutcome.None).
        if (model.LastSyncedAt is null) return state;

        state.MetadataCheckedAt = model.LastSyncedAt;

        // A local file that came out of a sync with a real base model was identified by a
        // sidecar; anything else was looked at and stayed unidentified.
        var hasRealBaseModel = model.Versions.Any(v => !IsPlaceholder(v.BaseModelRaw));
        state.MetadataOutcome = model.Source == DataSource.LocalFile && hasRealBaseModel
            ? SyncOutcome.Sidecar
            : SyncOutcome.NotIdentified;

        return state;
    }

    /// <summary>
    /// True when a stored base model carries no information — blank, or the legacy
    /// "<c>???</c>" placeholder written by earlier scans.
    /// </summary>
    public static bool IsPlaceholder(string? baseModelRaw) =>
        string.IsNullOrWhiteSpace(baseModelRaw) || baseModelRaw == "???";
}
