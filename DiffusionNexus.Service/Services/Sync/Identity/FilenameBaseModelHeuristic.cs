using System.Text.RegularExpressions;
using DiffusionNexus.Domain.Utilities;

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
    private static readonly string[] KnownModelExtensions = ModelFileExtensions.Recognized;

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

    // Rung 2: refinement signals for checkpoints that are architecturally plain SDXL under a
    // different name. MUST be checked — and win — before the generic "sdxl" whole-name substring
    // in rung 3, mirroring BaseModelHeaderMap's own class remarks: Pony, Illustrious and NoobAI
    // checkpoints are all trained on the SDXL architecture, so a filename carrying both the
    // refinement name and the literal word "sdxl" (extremely common for Pony LoRAs, e.g.
    // "stylemix_pony_sdxl_v1") must label as the refinement, not the architecture it was built on.
    //
    // Deliberately NOT a StartsWith prefix test against every token: "pony", "illust" and "noob"
    // are common enough as the start of an unrelated English word (ponytail, illustration,
    // noobie) that prefix matching false-positives constantly — verified against the built
    // assembly, "ponytail_v1", "illustration_style" and "noobie_lora" all used to match. Instead:
    //   (a) RefinementExactTokens — a closed set of exact tokens: the bare refinement word and the
    //       compound spelling that doesn't start with a decoration prefix at all ("pdxl"), plus
    //       "il"/"illust", which are too short to ever safely prefix-match.
    //   (b) RefinementDecorationPrefixes — a token that starts with one of these prefixes matches
    //       ONLY when what remains is pure version/XL decoration (see
    //       RefinementDecorationRemainderRegex), e.g. "ponyxl", "ponyv6", "ponyv6xl" — never an
    //       arbitrary remainder like "tail", "ration" or "ie". "ponydiffusion" is a prefix here
    //       (not only an exact token) because real filenames glue it straight to the version with
    //       no separator to tokenize on, e.g. "ponyDiffusionV6XL" -> single token
    //       "ponydiffusionv6xl".
    private static readonly Dictionary<string, string> RefinementExactTokens = new(StringComparer.Ordinal)
    {
        ["pdxl"] = "Pony",
        ["pony"] = "Pony",
        ["il"] = "Illustrious",
        ["illust"] = "Illustrious",
        ["noob"] = "NoobAI",
    };

    private static readonly (string Prefix, string Label)[] RefinementDecorationPrefixes =
    {
        ("ponydiffusion", "Pony"),
        ("pony", "Pony"),
        ("illust", "Illustrious"),
        ("noob", "NoobAI"),
    };

    // Matches an empty remainder or pure version/XL decoration left after stripping a refinement
    // prefix: "", "xl", "v6", "v6xl", "xl2" ... but not "tail", "ration", "ie".
    [GeneratedRegex(@"^(xl)?(v?\d+)?(xl)?$")]
    private static partial Regex RefinementDecorationRemainderRegex();

    // Rung 3: generic architecture whole-name substring — checked only after every refinement
    // above has had its chance, so a refinement name never loses to the architecture it's built on.
    private const string SdxlNeedle = "sdxl";
    private const string SdxlLabel = "SDXL 1.0";

    // Rung 4: exact match against the token/pair/triple candidate set built in Guess() (handles a
    // version number split across separators, e.g. "sd_3.5" tokenizing to "sd" + "3" + "5", where
    // the full "35" only exists as a triple concatenation). Matched longest-key-first so a coarse
    // key like "sd3" can never win over a finer one like "sd35" just because of where the
    // separators happened to fall — see the longest-key-first loop in Guess().
    //
    // The bare "wan" token is NOT here and must not be added: Star Wars character LoRAs
    // ("obi_wan_kenobi") are extremely common. The version-qualified spellings are NOT safe on
    // their own either — the candidate set below synthesizes them from adjacent tokens, so
    // "obi_wan_2" produces "wan2" and used to resolve to Wan Video despite that guard. See
    // NamesObiWan: the collision is one specific name, and naming it is what actually holds.
    //
    // Several labels here (LTXV*, Wan Video, Qwen, Chroma, HiDream, Flux.2*) are real Civitai
    // catalog labels that BaseModelTypeExtensions.ParseCivitai cannot map to a BaseModelType
    // member, so they store as Other. That is a gap in the ENUM, not a reason to withhold the
    // label: a Civitai-identified model of the same family already stores exactly this raw label
    // and exactly that Other, so emitting it here changes nothing for the worse — while the sorter,
    // which files by the raw string, gets a correct folder instead of dumping the file into
    // Unknown. Tracked separately; do not "fix" it by deleting these keys.
    private static readonly Dictionary<string, string> ExactTokenOrPairMap = new(StringComparer.Ordinal)
    {
        ["sd15"] = "SD 1.5",
        ["sd21"] = "SD 2.1",
        ["sd35"] = "SD 3.5",
        ["sd3"] = "SD 3",

        // LTX. Longest-key-first ordering is what keeps "ltx" from eating "ltx23" — see Guess().
        // "latex" does not contain the token "ltx" (l-a-t-e-x), so the bare key is safe.
        ["ltxv23"] = "LTXV 2.3",
        ["ltx23"] = "LTXV 2.3",
        ["ltxv2"] = "LTXV2",
        ["ltx2"] = "LTXV2",
        ["ltxv"] = "LTXV",
        ["ltx"] = "LTXV",

        // Wan. Every version-qualified spelling collapses to the family label: the finer catalog
        // entries encode t2v/i2v and parameter count, which a file name reports too unreliably to
        // act on — and a wrong "Wan Video 2.2 I2V-A14B" folder is worse than a right "Wan Video".
        ["wan25"] = "Wan Video",
        ["wan22"] = "Wan Video",
        ["wan21"] = "Wan Video",
        ["wan2"] = "Wan Video",

        ["qwen"] = "Qwen",
        // Video-qualified only. "Hunyuan 1" (HunyuanDiT, an image model) and "Hunyuan Video" are
        // separate catalog families rather than two versions of one, so unlike the Wan keys a bare
        // "hunyuan" cannot be collapsed to either: it would file an image LoRA into a video folder.
        // The pair set produces this from the usual "hunyuan_video_*" spelling.
        ["hunyuanvideo"] = "Hunyuan Video",
        ["chroma"] = "Chroma",
        ["hidream"] = "HiDream",

        // Flux beyond 1.D/1.S. "kontext" and "klein" are distinctive enough to stand alone;
        // a bare "klein" is not, because 4B and 9B are different base models and the file name is
        // the only thing that says which — so only the size-qualified pairs map.
        ["kontext"] = "Flux.1 Kontext",
        ["kleinbase9b"] = "Flux.2 Klein 9B-base",
        ["kleinbase4b"] = "Flux.2 Klein 4B-base",
        ["klein9b"] = "Flux.2 Klein 9B",
        ["klein4b"] = "Flux.2 Klein 4B",
        ["flux2"] = "Flux.2 D",
    };

    // Keys that must appear as a WHOLE TOKEN, never accepted from the synthesized pair/triple set.
    // That synthesis exists so a version split across separators still matches ("sd_3.5" tokenizes
    // to "sd","3","5"), which is right when the key's stem means nothing on its own — "sd", "ltx",
    // "klein". "flux" is not that: it stands alone as the family name, so synthesizing "flux2" from
    // a following bare digit reads an ordinary version suffix as a different family, and
    // "portrait_flux_2" (v2 of a Flux.1 LoRA) came out as Flux.2 D, beating the "flux" prefix rung
    // below that had it right. Requiring the glued spelling costs nothing measurable: the one file
    // in the reference library that needed the pair — "flux-2-klein-base-9b-fp8" — is matched
    // precisely by "kleinbase9b" above instead of coarsely by "flux2".
    private static readonly HashSet<string> GluedSpellingOnlyKeys = new(StringComparer.Ordinal)
    {
        "flux2",
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
        .Concat(RefinementExactTokens.Values)
        .Concat(RefinementDecorationPrefixes.Select(p => p.Label))
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

        // Refinement rung — see rung 2's remarks above. Must run before the "sdxl" substring check
        // below, or a Pony/Illustrious/NoobAI filename that also carries the literal word "sdxl"
        // would always collapse to the generic architecture label.
        foreach (var token in tokens)
        {
            if (RefinementExactTokens.TryGetValue(token, out var refinementLabel))
                return refinementLabel;

            foreach (var (prefix, label) in RefinementDecorationPrefixes)
            {
                if (token.Length <= prefix.Length || !token.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                var remainder = token[prefix.Length..];
                if (RefinementDecorationRemainderRegex().IsMatch(remainder))
                    return label;
            }
        }

        if (name.Contains(SdxlNeedle, StringComparison.Ordinal))
            return SdxlLabel;

        // Candidate set = every token, adjacent-pair concatenation, and adjacent-triple
        // concatenation, so a version number survives regardless of where separators land (e.g.
        // "sd_3.5" -> tokens "sd","3","5" -> the pair "35" and the triple "sd35" both exist as
        // candidates). Matched longest-key-first: without this, "sd3" (itself a key) would win
        // over "sd35" purely because the exact-token check used to run before the pair check.
        var candidates = new HashSet<string>(tokens, StringComparer.Ordinal);
        for (var i = 0; i < tokens.Length - 1; i++)
            candidates.Add(tokens[i] + tokens[i + 1]);
        for (var i = 0; i < tokens.Length - 2; i++)
            candidates.Add(tokens[i] + tokens[i + 1] + tokens[i + 2]);

        var tokenSet = new HashSet<string>(tokens, StringComparer.Ordinal);
        var namesObiWan = NamesObiWan(tokens);

        foreach (var key in ExactTokenOrPairMap.Keys.OrderByDescending(k => k.Length))
        {
            if (namesObiWan && key.StartsWith("wan", StringComparison.Ordinal)) continue;

            var pool = GluedSpellingOnlyKeys.Contains(key) ? tokenSet : candidates;
            if (pool.Contains(key))
                return ExactTokenOrPairMap[key];
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

    /// <summary>
    /// Whether the name is Obi-Wan Kenobi's rather than Wan Video's.
    /// </summary>
    /// <remarks>
    /// "wan" is a real word in exactly one place that matters, and the version-qualified keys cannot
    /// simply be made token-only to dodge it: the dominant real spelling separates the digits
    /// ("WAN-2.2-I2V-…", "wan_22_high_noise" — 34 of the 36 wan files in the reference library), so
    /// dropping the pair/triple synthesis would cost far more than it saves. Naming the one
    /// collision is what actually holds the guard the table above claims.
    /// </remarks>
    private static bool NamesObiWan(string[] tokens)
    {
        for (var i = 0; i < tokens.Length; i++)
        {
            if (string.Equals(tokens[i], "kenobi", StringComparison.Ordinal)) return true;
            if (string.Equals(tokens[i], "obi", StringComparison.Ordinal)
                && i + 1 < tokens.Length && string.Equals(tokens[i + 1], "wan", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
