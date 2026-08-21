using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.Sync;

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
    /// Derives the state row from the four facts the table above actually asks about.
    /// </summary>
    /// <param name="input">The projected facts — see <see cref="SyncDerivationInput"/>.</param>
    /// <param name="now">The derivation timestamp; also the row's <c>UpdatedAt</c>.</param>
    public static ModelSyncState Derive(SyncDerivationInput input, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);

        var stamp = input.LastSyncedAt ?? now;
        var state = new ModelSyncState { ModelId = input.ModelId, UpdatedAt = now };

        if (input.CivitaiId is not null)
        {
            state.MetadataOutcome = SyncOutcome.Matched;
            state.MetadataCheckedAt = stamp;

            // A matched model with no tags / no images keeps those columns null on purpose:
            // it is genuinely unknown whether they were ever fetched, so the tag and image
            // steps get to ask once and stamp the answer.
            state.TagsCheckedAt = input.HasTags ? stamp : null;
            state.ImagesCheckedAt = input.HasImages ? stamp : null;
            return state;
        }

        // Never synced and never matched: nothing has ever been checked (SyncOutcome.None).
        if (input.LastSyncedAt is null) return state;

        // `now`, not LastSyncedAt — see the anti-herd note on the class doc.
        state.MetadataCheckedAt = now;

        // A local file that came out of a sync with a real base model was identified by a
        // sidecar; anything else was looked at and stayed unidentified.
        state.MetadataOutcome = input.Source == DataSource.LocalFile && input.HasRealBaseModel
            ? SyncOutcome.Sidecar
            : SyncOutcome.NotIdentified;

        return state;
    }

    /// <summary>
    /// True when a stored base model carries no information — blank, or the legacy
    /// "<c>???</c>" placeholder written by earlier scans.
    /// </summary>
    /// <remarks>
    /// The in-memory twin of the base-model test inside
    /// <c>SyncStateRepository.GetDerivationInputsAsync</c>'s projection, which is where
    /// <see cref="SyncDerivationInput.HasRealBaseModel"/> is actually decided. Kept here because it
    /// is the readable statement of the rule, and it is what the repository test asserts against.
    /// </remarks>
    public static bool IsPlaceholder(string? baseModelRaw) =>
        string.IsNullOrWhiteSpace(baseModelRaw) || baseModelRaw == "???";
}
