using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Utilities;

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
/// <para>
/// Rung 2 is reached only where there was evidence to gather — a header we DID read that matched
/// no rung, or a pickle that could never have had one. A <c>.safetensors</c> we merely FAILED to
/// read is neither, and answers <see cref="ModelType.LORA"/> rather than being named from its file
/// name; see <see cref="Resolve(SafetensorsHeaderInfo?, string?)"/>.
/// </para>
/// </remarks>
public static class AssetKindResolver
{
    /// <summary>
    /// The kind, from an already-parsed header and a file name.
    /// </summary>
    /// <remarks>
    /// <b>An unreadable container is not evidence.</b> A null <paramref name="header"/> means two
    /// very different things and they must not be conflated: for a pickle it means "there was never
    /// a header to read", and for a <c>.safetensors</c>/<c>.sft</c> container it means "we failed to
    /// open or parse one" — a file still being copied onto a NAS (<see cref="SafetensorsHeaderReader"/>
    /// answers null while the declared header runs past the current length), a transient IO fault, a
    /// writer holding it with <c>FileShare.None</c>. Discovery is frequently the FIRST thing ever to
    /// open such a file, so there is no earlier verdict to fall back on.
    /// <para>
    /// Letting the name rung answer in that case is unrecoverable: a wrong support-asset stamp makes
    /// a real LoRA both invisible in the Viewer (<c>ModelFileSyncService.IsLoraFamily</c>) and
    /// unselectable by any bulk sync (<c>SyncStateRepository</c>'s <c>LoraFamily</c> filter), fixable
    /// only by hand-editing the database. This is the same hazard the design's "Why the extension
    /// condition is load-bearing" section removed from the backfill, reached by a different route.
    /// So a container we could not read stays <see cref="ModelType.LORA"/> and is re-asked on the
    /// next pass that can actually open it.
    /// </para>
    /// <para>
    /// A header we DID read that matched no rung still reaches the name — that is not a failure to
    /// gather evidence, it is evidence that says nothing, and it is what lets a <c>.safetensors</c>
    /// upscaler be named at all (<see cref="AssetKindHeaderMap"/> has no upscaler rung by design).
    /// </para>
    /// </remarks>
    public static ModelType Resolve(SafetensorsHeaderInfo? header, string? fileName)
    {
        if (AssetKindHeaderMap.Map(header) is { } fromWeights) return fromWeights;

        if (header is null && fileName is not null
            && ModelFileExtensions.Matches(fileName, ModelFileExtensions.SafetensorsContainers))
            return ModelType.LORA;

        return AssetKindClassifier.Classify(fileName);
    }

    /// <summary>
    /// The kind for a file on disk. Reads the header when the file is a safetensors container and
    /// can be opened. Never throws for an unreadable file —
    /// <see cref="SafetensorsHeaderReader.TryReadAsync"/> already answers null for that — and per
    /// <see cref="Resolve(SafetensorsHeaderInfo?, string?)"/> a safetensors container we could not
    /// open answers <see cref="ModelType.LORA"/> rather than letting its name decide.
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
