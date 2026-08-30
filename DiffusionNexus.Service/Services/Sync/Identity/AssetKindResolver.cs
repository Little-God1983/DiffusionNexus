using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Service.Services.Sync.Identity;

/// <summary>
/// What a file IS — a VAE, a text encoder, a ControlNet, an upscaler, or the LoRA the library is
/// actually for (#527).
/// </summary>
/// <remarks>
/// The precedence rule lives here, once, because three callers depend on it and they must not
/// drift: discovery (<c>ModelFileSyncService</c>), the identify step, and the sorter's own
/// <c>SorterMetadataResolver</c>.
/// <list type="number">
///   <item><description><see cref="AssetKindHeaderMap"/> — a reading of the weights.</description></item>
///   <item><description><see cref="AssetKindClassifier"/> — a guess about the file name.</description></item>
///   <item><description><see cref="ModelType.LORA"/> — the default, which is what discovery always assumed.</description></item>
/// </list>
/// The header wins outright and the name is not consulted when it answers. That is the whole
/// reason the header rung exists: the sorter turns this verdict into a physical move, and a LoRA
/// called <c>vae_finetune_lora</c> must not be filed as a VAE because of what its author called it.
/// Mirrors the same shape as the base-model chain (header, then
/// <see cref="FilenameBaseModelHeuristic"/>), for the same reason.
/// </remarks>
public static class AssetKindResolver
{
    /// <summary>The kind, from an already-parsed header and a file name.</summary>
    public static ModelType Resolve(SafetensorsHeaderInfo? header, string? fileName)
        => AssetKindHeaderMap.Map(header) ?? AssetKindClassifier.Classify(fileName);

    /// <summary>
    /// The kind for a file on disk. Reads the header when the file is a safetensors container and
    /// can be opened; falls back to the name otherwise. Never throws for an unreadable file —
    /// <see cref="SafetensorsHeaderReader.TryReadAsync"/> already answers null for that, and a
    /// file we could not read is exactly the case the name rung exists to cover.
    /// </summary>
    public static async Task<ModelType> ResolveAsync(string filePath, CancellationToken ct = default)
    {
        var header = await SafetensorsHeaderReader.TryReadAsync(filePath, ct).ConfigureAwait(false);
        // Pass only the file name to Resolve, not the full path — parent directory segments must
        // never influence the verdict (see the base-model chain for why). Classify also strips the
        // path, so this is the first of two defenses; if Classify stops stripping, it must not
        // silently break this rule.
        return Resolve(header, Path.GetFileName(filePath));
    }
}
