using System.Text.RegularExpressions;

namespace DiffusionNexus.Service.Services.Sync.Identity;

/// <summary>
/// Guesses a Civitai base-model display label from a model FILE NAME when no header or sidecar
/// metadata is available. Last-resort signal — used only after
/// <see cref="BaseModelHeaderMap"/> and any sidecar read have already come up empty.
/// </summary>
public static partial class FilenameBaseModelHeuristic
{
    // Only strip a trailing extension when it is a KNOWN model-file extension. A blind
    // Path.GetFileNameWithoutExtension call would eat the ".5" off "detailer_sd1.5" (turning it
    // into "detailer_sd1" and losing the version digit the token match below depends on), and the
    // Task 3 caller already pre-strips real extensions before calling in — so a second, unguarded
    // strip here would be the live failure path, not a defensive no-op.
    private static readonly string[] KnownModelExtensions =
    {
        ".safetensors", ".pt", ".ckpt", ".bin", ".sft", ".gguf",
    };

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex TokenSplitRegex();

    // Rung 1: distinctive whole-name substrings for architectures whose full name is itself
    // unambiguous — safe because they're long/specific enough not to false-positive inside an
    // unrelated word.
    private static readonly (string Needle, string Label)[] WholeNameSubstrings =
    {
        ("illustrious", "Illustrious"),
        ("noobai", "NoobAI"),
    };

    // Rung 2: refinement token signals. MUST be checked — and win — before the generic "sdxl"
    // whole-name substring in rung 3, mirroring BaseModelHeaderMap's own class remarks: Pony,
    // Illustrious and NoobAI checkpoints are all trained on the SDXL architecture, so a filename
    // carrying both the refinement name and the literal word "sdxl" (extremely common for Pony
    // LoRAs, e.g. "stylemix_pony_sdxl_v1") must label as the refinement, not the architecture it
    // was built on. Token-level (StartsWith on a split token, or an exact token for "pdxl"), not a
    // raw substring test against the whole name, because "pony"/"illust"/"noob" are short enough
    // to false-positive inside an unrelated word if matched that way — the negative-case tests
    // pin exactly this ("harmony_lora" -> null).
    private static readonly (string Prefix, string Label)[] RefinementTokenPrefixes =
    {
        ("pony", "Pony"),
        ("illust", "Illustrious"),
        ("noob", "NoobAI"),
    };

    private static readonly Dictionary<string, string> RefinementExactTokens = new(StringComparer.Ordinal)
    {
        ["pdxl"] = "Pony",
    };

    // Rung 3: generic architecture whole-name substring — checked only after every refinement
    // above has had its chance, so a refinement name never loses to the architecture it's built on.
    private const string SdxlNeedle = "sdxl";
    private const string SdxlLabel = "SDXL 1.0";

    // Rung 4: exact token, or exact adjacent-pair concatenation (handles a version number split
    // across a separator, e.g. "sd_15" or "sd1.5" tokenizing to "sd1" + "5").
    private static readonly Dictionary<string, string> ExactTokenOrPairMap = new(StringComparer.Ordinal)
    {
        ["sd15"] = "SD 1.5",
        ["sd21"] = "SD 2.1",
        ["sd35"] = "SD 3.5",
        ["sd3"] = "SD 3",
        ["il"] = "Illustrious",
        ["wan"] = "Wan Video",
        ["wan21"] = "Wan Video",
        ["wan22"] = "Wan Video",
    };

    // Rung 5: token prefix match — last resort, so only distinctive prefixes that won't collide
    // with common English words belong here.
    private static readonly (string Prefix, string Label)[] TokenPrefixes =
    {
        ("flux", "Flux.1 D"),
    };

    /// <summary>
    /// Every label this heuristic can ever return. Test-only seam (DiffusionNexus.Tests,
    /// InternalsVisibleTo) used to verify each label is a real Civitai display label — mirrors
    /// <see cref="BaseModelHeaderMap.AllLabels"/>.
    /// </summary>
    internal static IReadOnlyCollection<string> AllLabels { get; } = WholeNameSubstrings.Select(s => s.Label)
        .Concat(RefinementTokenPrefixes.Select(p => p.Label))
        .Concat(RefinementExactTokens.Values)
        .Concat(new[] { SdxlLabel })
        .Concat(ExactTokenOrPairMap.Values)
        .Concat(TokenPrefixes.Select(p => p.Label))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    /// <summary>Civitai display label guessed from a model FILE NAME (no directory, extension ignored), or null.</summary>
    public static string? Guess(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var name = StripKnownExtension(fileName).ToLowerInvariant();
        if (name.Length == 0)
            return null;

        foreach (var (needle, label) in WholeNameSubstrings)
        {
            if (name.Contains(needle, StringComparison.Ordinal))
                return label;
        }

        var tokens = TokenSplitRegex().Split(name).Where(t => t.Length > 0).ToArray();
        if (tokens.Length == 0)
            return null;

        // Refinement rung — see the field's remarks. Must run before the "sdxl" substring check
        // below, or a Pony/Illustrious/NoobAI filename that also carries the literal word "sdxl"
        // would always collapse to the generic architecture label.
        foreach (var token in tokens)
        {
            if (RefinementExactTokens.TryGetValue(token, out var refinementLabel))
                return refinementLabel;

            foreach (var (prefix, label) in RefinementTokenPrefixes)
            {
                if (token.StartsWith(prefix, StringComparison.Ordinal))
                    return label;
            }
        }

        if (name.Contains(SdxlNeedle, StringComparison.Ordinal))
            return SdxlLabel;

        foreach (var token in tokens)
        {
            if (ExactTokenOrPairMap.TryGetValue(token, out var tokenLabel))
                return tokenLabel;
        }

        for (var i = 0; i < tokens.Length - 1; i++)
        {
            var pair = tokens[i] + tokens[i + 1];
            if (ExactTokenOrPairMap.TryGetValue(pair, out var pairLabel))
                return pairLabel;
        }

        foreach (var token in tokens)
        {
            foreach (var (prefix, label) in TokenPrefixes)
            {
                if (token.StartsWith(prefix, StringComparison.Ordinal))
                    return label;
            }
        }

        return null;
    }

    private static string StripKnownExtension(string fileName)
    {
        foreach (var extension in KnownModelExtensions)
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return fileName[..^extension.Length];
        }

        return fileName;
    }
}
