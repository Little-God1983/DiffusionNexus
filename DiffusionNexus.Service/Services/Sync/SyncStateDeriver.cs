using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Service.Services.Sync;

/// <summary>
/// Turns a pre-existing <see cref="Model"/> into the <see cref="ModelSyncState"/> row it would
/// have had if the sync state had always been recorded — purely from data already in the
/// database. Never touches the network: a library that has been synced for years must not
/// re-ask Civitai about every model just because the state table is new.
/// <para>The derivation table (<c>stamp</c> = <c>LastSyncedAt ?? now</c>):</para>
/// <list type="table">
///   <listheader><term>Model</term><description>Outcome / MetadataCheckedAt / TagsCheckedAt / ImagesCheckedAt</description></listheader>
///   <item>
///     <term><c>CivitaiId != null</c></term>
///     <description><c>Matched</c> / <c>stamp</c> / <c>stamp</c> if it has tags else null / <c>stamp</c> if any version has images else null</description>
///   </item>
///   <item>
///     <term>no id, synced, local file, real base model</term>
///     <description><c>Sidecar</c> / <b><c>now</c></b> / null / null</description>
///   </item>
///   <item>
///     <term>no id, synced, anything else</term>
///     <description><c>NotIdentified</c> / <b><c>now</c></b> / null / null</description>
///   </item>
///   <item>
///     <term>no id, never synced</term>
///     <description><c>None</c> / null / null / null</description>
///   </item>
/// </list>
/// <para>
/// The two unmatched outcomes are stamped with <c>now</c> rather than the model's own
/// <c>LastSyncedAt</c> on purpose (R1, anti-herd): a library last synced years ago sits far
/// outside the 30-day retry window, so stamping history would make every unidentified model due
/// the instant the state table appears — the 545-item, 27-minute first run the live dry run
/// measured. The upgrade *is* the check; the next one falls due 30 days from it.
/// <c>Matched</c> keeps the historical stamp because it is terminal for the retry policy, and
/// <c>None</c> stays unstamped because nothing has genuinely ever been looked at.
/// </para>
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

        // `now`, not LastSyncedAt — see the anti-herd note on the class doc.
        state.MetadataCheckedAt = now;

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
