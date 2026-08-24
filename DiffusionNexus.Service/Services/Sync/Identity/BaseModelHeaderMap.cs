namespace DiffusionNexus.Service.Services.Sync.Identity;

/// <summary>
/// Maps the fields of a parsed safetensors header (<see cref="SafetensorsHeaderInfo"/>) to a
/// Civitai base-model display label.
/// </summary>
/// <remarks>
/// Evaluation order is load-bearing, not incidental: Pony, Illustrious and NoobAI checkpoints are
/// all trained on the SDXL architecture, so <c>modelspec.architecture</c> alone reads as plain
/// SDXL for every one of them. The model-name hint (<c>ss_sd_model_name</c>) is checked FIRST so
/// those refinements are identified correctly instead of collapsing into <c>"SDXL 1.0"</c>.
/// </remarks>
public static class BaseModelHeaderMap
{
    // Rung 1: ss_sd_model_name substring hints. Checked first — see class remarks. kohya writes a
    // full file PATH into this field, not a bare file name, so only the file-name portion (see
    // ExtractFileNameHint) is needle-matched: a directory segment (e.g.
    // "...\noobs\base.safetensors") must never decide the label.
    private static readonly (string Needle, string Label)[] NameHints =
    {
        ("pony", "Pony"),
        ("illustrious", "Illustrious"),
        ("noob", "NoobAI"),
    };

    // Extensions ExtractFileNameHint will drop from the file-name portion of the hint.
    private static readonly string[] KnownModelExtensions = ModelFileExtensions.All;

    // Rung 2: modelspec.architecture, lowercased, with everything from the first '/' stripped
    // (drops suffixes such as "/lora").
    private static readonly Dictionary<string, string> ArchitectureMap = new(StringComparer.Ordinal)
    {
        ["stable-diffusion-xl-v1-base"] = "SDXL 1.0",
        ["stable-diffusion-v1"] = "SD 1.5",
        ["stable-diffusion-v2-768-v"] = "SD 2.1",
        ["stable-diffusion-v2"] = "SD 2.0",
        ["stable-diffusion-3-medium"] = "SD 3",
        ["flux-1-dev"] = "Flux.1 D",
        ["flux-1-schnell"] = "Flux.1 S",
    };

    // Rung 3: ss_base_model_version, lowercased prefix match. kohya writes the coarse "sd_v1" /
    // "sd_v2" for every 1.x / 2.x checkpoint it trains against — there is no finer signal in that
    // field — so this collapses to the dominant member of each family (1.5, 2.1) as an accepted
    // approximation rather than a precise minor-version read.
    private static readonly (string Prefix, string Label)[] VersionPrefixes =
    {
        ("sdxl_base_v1-0", "SDXL 1.0"),
        ("sdxl_base_v0-9", "SDXL 0.9"),
        ("sd_v1", "SD 1.5"),
        ("sd_v2", "SD 2.1"),
    };

    /// <summary>
    /// Every label this map can ever return. Test-only seam (DiffusionNexus.Tests,
    /// InternalsVisibleTo) used to verify each label is a real Civitai display label.
    /// </summary>
    internal static IReadOnlyCollection<string> AllLabels { get; } = NameHints.Select(h => h.Label)
        .Concat(ArchitectureMap.Values)
        .Concat(VersionPrefixes.Select(p => p.Label))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    /// <summary>Civitai display label for a parsed header, or null when the header says nothing usable.</summary>
    public static string? Map(SafetensorsHeaderInfo info)
    {
        if (info is null)
            return null;

        if (!string.IsNullOrEmpty(info.ModelNameHint))
        {
            var hint = ExtractFileNameHint(info.ModelNameHint).ToLowerInvariant();
            foreach (var (needle, label) in NameHints)
            {
                if (hint.Contains(needle, StringComparison.Ordinal))
                    return label;
            }
        }

        if (!string.IsNullOrEmpty(info.Architecture))
        {
            var architecture = info.Architecture.ToLowerInvariant();
            var slashIndex = architecture.IndexOf('/');
            if (slashIndex >= 0)
                architecture = architecture[..slashIndex];

            if (ArchitectureMap.TryGetValue(architecture, out var architectureLabel))
                return architectureLabel;
        }

        if (!string.IsNullOrEmpty(info.BaseModelVersion))
        {
            var version = info.BaseModelVersion.ToLowerInvariant();
            foreach (var (prefix, label) in VersionPrefixes)
            {
                if (version.StartsWith(prefix, StringComparison.Ordinal))
                    return label;
            }
        }

        return null;
    }

    /// <summary>
    /// Strips a directory prefix and a known trailing extension from an <c>ss_sd_model_name</c>
    /// hint, so rung 1 needle-matches only the actual file name — never a directory segment.
    /// Checks '/' and '\' explicitly rather than calling <see cref="Path.GetFileName(string)"/>,
    /// which strips only the platform's own separator and would leave a Windows-style prefix
    /// intact on a non-Windows build/CI runner.
    /// </summary>
    private static string ExtractFileNameHint(string hint)
    {
        var separatorIndex = Math.Max(hint.LastIndexOf('/'), hint.LastIndexOf('\\'));
        var fileName = separatorIndex >= 0 ? hint[(separatorIndex + 1)..] : hint;

        foreach (var extension in KnownModelExtensions)
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return fileName[..^extension.Length];
        }

        return fileName;
    }
}
