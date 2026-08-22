namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// How the identity step last resolved a model (base model / Civitai linkage).
/// Persisted as a string (see <c>ModelSyncStateConfiguration</c>) — append new members, never reorder.
/// </summary>
public enum SyncOutcome
{
    /// <summary>Never attempted.</summary>
    None = 0,
    /// <summary>Matched to a Civitai version by file hash.</summary>
    Matched,
    /// <summary>Not on Civitai; metadata came from a .civitai.info / .json sidecar.</summary>
    Sidecar,
    /// <summary>Not on Civitai, no sidecar; base model read from the safetensors header (WP4).</summary>
    Header,
    /// <summary>Base model guessed from the file name (WP4). Shown to the user as "guessed".</summary>
    Heuristic,
    /// <summary>Every source was tried and none identified the model. Re-checked after the retry window.</summary>
    NotIdentified,
    /// <summary>The attempt failed (network, disk, parse). Re-checked after the short retry window, bounded by attempts.</summary>
    Error
}
