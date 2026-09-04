using DiffusionNexus.Inference.StableDiffusionCpp;

namespace DiffusionNexus.UI.Services.Lora;

/// <summary>
/// Which raw Civitai base-model labels a generation model can load LoRAs from.
/// </summary>
/// <remarks>
/// <para>
/// This map is <b>authored, not derived</b>. A model descriptor's <c>DisplayName</c> is not the Civitai
/// label and no string transform bridges them: the descriptor says <c>Z-Image-Turbo</c> while Civitai
/// writes <c>ZImageTurbo</c>, and Civitai's Qwen labels do not distinguish the image model from the edit
/// model at all. Anything computed here would be a plausible-looking guess that silently filters a user's
/// whole LoRA library down to nothing.
/// </para>
/// <para>
/// <see cref="ILoraCatalog"/> matches these by exact, case-insensitive, whole-string equality — no
/// trimming, no aliasing, no family prefix — so every spelling a model's LoRAs are published under has to
/// be listed separately. That is why the FLUX.2 entries carry both the plain and the <c>-base</c> forms.
/// </para>
/// </remarks>
public static class ModelBaseModelLabels
{
    /// <summary>
    /// FLUX.2 Klein, in every spelling Civitai publishes LoRAs under. Both parameter counts are listed
    /// because a LoRA trained on either loads onto the other.
    /// </summary>
    private static readonly string[] Flux2Klein =
    [
        "Flux.2 Klein 9B",
        "Flux.2 Klein 9B-base",
        "Flux.2 Klein 4B",
        "Flux.2 Klein 4B-base",
    ];

    private static readonly string[] ZImageTurbo =
    [
        "ZImageTurbo",
        "ZImageBase",
    ];

    /// <summary>
    /// Civitai has one <c>Qwen</c> family label and does not separate Qwen-Image from Qwen-Image-Edit, so
    /// both models share it. This is over-broad rather than wrong: the user may be offered a LoRA trained
    /// for the sibling model, which is the same risk Civitai's own filtering carries.
    /// </summary>
    private static readonly string[] Qwen =
    [
        "Qwen",
        "Qwen 2",
    ];

    private static readonly Dictionary<string, string[]> ByModelKey = new(StringComparer.Ordinal)
    {
        [ModelKeys.Flux2Klein] = Flux2Klein,
        [ModelKeys.ZImageTurbo] = ZImageTurbo,
        [ModelKeys.QwenImage2512] = Qwen,
        [ModelKeys.QwenImageEdit2511] = Qwen,

        // Krea 2 deliberately maps to nothing. Civitai has no Krea 2 base-model label — its "Flux.1 Krea"
        // entry is Flux.1 Krea dev, an unrelated model — so there is no correct value to filter on. An
        // empty list means "no compatible LoRAs can be identified", which the caller must surface as an
        // explanation. It must NOT be turned into a null filter: ILoraCatalog reads null as "return
        // everything", which on a real library is thousands of rows each decoding a thumbnail.
        [KreaModelKey] = [],
    };

    /// <summary>The engine backend's Krea 2 model key. Duplicated as a literal to avoid a UI-layer cycle.</summary>
    private const string KreaModelKey = "krea2";

    /// <summary>
    /// The Civitai labels whose LoRAs load onto <paramref name="modelKey"/>. An <b>empty</b> list means the
    /// model is known and has no identifiable compatible LoRAs; <c>null</c> means the model is unknown to
    /// this map.
    /// </summary>
    /// <remarks>
    /// Both answers must be handled the same way by a caller filtering a picker — show nothing and say why
    /// — but they are different facts, and an unknown model is a gap in this file rather than a property of
    /// the model.
    /// </remarks>
    public static IReadOnlyList<string>? ForModelKey(string? modelKey)
    {
        if (string.IsNullOrWhiteSpace(modelKey))
            return null;

        return ByModelKey.TryGetValue(modelKey, out var labels) ? labels : null;
    }

    /// <summary>True when this map has an entry for <paramref name="modelKey"/>, whether or not it is empty.</summary>
    public static bool IsKnown(string? modelKey) =>
        !string.IsNullOrWhiteSpace(modelKey) && ByModelKey.ContainsKey(modelKey);
}
