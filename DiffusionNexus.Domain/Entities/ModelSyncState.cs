using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// Persisted record of what the library sync has already <i>tried</i> for one <see cref="Model"/>,
/// so "checked and genuinely empty" is distinguishable from "never checked". One row per model
/// (PK = FK). A model without a row is a legacy row whose state is derived from existing data
/// on first plan (<c>SyncStateDeriver</c>) — never by calling the network.
/// </summary>
public class ModelSyncState
{
    /// <summary>Primary key and foreign key to <see cref="Model"/>.</summary>
    public int ModelId { get; set; }

    public Model? Model { get; set; }

    /// <summary>Last identity attempt (hash lookup + fallback chain).</summary>
    public DateTimeOffset? MetadataCheckedAt { get; set; }

    public SyncOutcome MetadataOutcome { get; set; } = SyncOutcome.None;

    /// <summary>Consecutive failed identity attempts; reset to 0 on any non-error outcome.</summary>
    public int MetadataAttempts { get; set; }

    /// <summary>One-line description of the last failure. Never a stack trace.</summary>
    public string? LastError { get; set; }

    /// <summary>Tags were fetched for the model's Civitai id — stamped even when the result was empty.</summary>
    public DateTimeOffset? TagsCheckedAt { get; set; }

    /// <summary>Image records were fetched for the model's versions — stamped even when the result was empty.</summary>
    public DateTimeOffset? ImagesCheckedAt { get; set; }

    /// <summary>
    /// <c>{fullPath}|{lastWriteUtcTicks}|{length}</c> of the sidecar last parsed, so an unchanged sidecar is not
    /// re-parsed on every run and a changed one is.
    /// </summary>
    public string? SidecarSignature { get; set; }

    /// <summary>The safetensors header was read (WP4).</summary>
    public DateTimeOffset? HeaderCheckedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
