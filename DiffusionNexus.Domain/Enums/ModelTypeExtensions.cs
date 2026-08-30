namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// What the application means by "not a LoRA", and what it calls those things on screen.
/// </summary>
/// <remarks>
/// A LoRA library routinely also holds the VAEs, text encoders, ControlNets and upscalers a
/// workflow needs — 35 of 328 unidentified files on one real library (#527). Three surfaces have
/// to agree about them: the folder the sorter moves them into, the chip on that folder's row in
/// the preview, and the badge the Viewer shows. Those are one string each, defined here, because
/// a folder named differently from the chip advertising it is a defect no diff makes visible.
/// <para>
/// Public rather than internal: DiffusionNexus.Domain has no InternalsVisibleTo, and the guard
/// tests exist precisely to stop the set below drifting from its consumers.
/// </para>
/// </remarks>
public static class ModelTypeExtensions
{
    /// <summary>
    /// Everything a LoRA folder can hold that is not a LoRA. The one definition of the set —
    /// nothing else in the application may restate it.
    /// </summary>
    public static readonly IReadOnlyList<ModelType> SupportAssetKinds =
    [
        ModelType.VAE,
        ModelType.Controlnet,
        ModelType.Upscaler,
        ModelType.TextEncoder,
    ];

    /// <summary>Whether this is one of the things the sorter is NOT for.</summary>
    public static bool IsSupportAsset(this ModelType type) => type switch
    {
        ModelType.VAE or ModelType.Controlnet or ModelType.Upscaler or ModelType.TextEncoder => true,
        _ => false,
    };

    /// <summary>
    /// The label a user sees. Only the kinds our own classifier can produce are spelled out; the
    /// rest of the Civitai taxonomy falls back to the enum name, which is what every existing
    /// display path already showed.
    /// </summary>
    public static string DisplayName(this ModelType type) => type switch
    {
        ModelType.LORA => "LoRA",
        ModelType.VAE => "VAE",
        ModelType.Controlnet => "ControlNet",
        ModelType.Upscaler => "Upscaler",
        ModelType.TextEncoder => "Text Encoder",
        _ => type.ToString(),
    };

    /// <summary>
    /// The folder a support asset sorts into, or null for anything that is not one. A LoRA
    /// deliberately returns null: its folder is its base model, which is a different question,
    /// and returning "LoRA" here would put a folder of that name beside the base-model folders.
    /// </summary>
    public static string? SupportFolderName(this ModelType type)
        => type.IsSupportAsset() ? type.DisplayName() : null;
}
