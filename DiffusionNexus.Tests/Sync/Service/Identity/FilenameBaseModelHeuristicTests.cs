using DiffusionNexus.Service.Services.Sync.Identity;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service.Identity;

/// <summary>
/// Covers <see cref="FilenameBaseModelHeuristic"/> — guessing a Civitai base-model label from a
/// model file name when no header/sidecar metadata is available. Match order is most-specific
/// first, with refinement names (Pony/Illustrious/NoobAI) checked before the generic "sdxl"
/// substring so a name carrying both (e.g. a Pony LoRA) labels as the refinement, then exact
/// token/pair/triple (longest key first), then token prefix. The refinement rung itself is an
/// exact-token set plus a "prefix + version/XL-decoration-only remainder" rule — never a raw
/// prefix match — so common English words that merely start the same way ("ponytail",
/// "illustration", "noobie") can't false-positive. The BARE "wan" token is still not recognized —
/// it collides with Star Wars character names ("obi_wan_kenobi") — but the version-qualified
/// spellings are, and deliberately so: see the Wan rows below.
/// </summary>
public class FilenameBaseModelHeuristicTests
{
    [Theory]
    [InlineData("MyChar_Pony_v2", "Pony")]
    [InlineData("ponyxl-style", "Pony")]
    [InlineData("sdxl_lineart", "SDXL 1.0")]
    [InlineData("myIllustriousMix", "Illustrious")]
    [InlineData("style-il", "Illustrious")]
    [InlineData("detailer_sd15", "SD 1.5")]
    [InlineData("detailer_sd1.5", "SD 1.5")]
    [InlineData("flux_dev_char", "Flux.1 D")]
    [InlineData("noob_artist", "NoobAI")]
    [InlineData("stylemix_pony_sdxl_v1", "Pony")]    // refinement must beat the generic "sdxl" substring
    [InlineData("noob_sdxl_mix", "NoobAI")]          // same principle for the NoobAI refinement
    [InlineData("mystyle_il_sdxl", "Illustrious")]   // A4: "il" refinement must beat the generic "sdxl" substring too
    [InlineData("style_sd3.5", "SD 3.5")]            // A2: the finer "sd35" pair/triple must beat the coarser "sd3" token
    [InlineData("sd_3.5_lora", "SD 3.5")]            // A2: same, with every separator splitting the version apart
    [InlineData("ponyv6_style", "Pony")]             // A1: unseen version spelling via prefix + decoration-only remainder
    [InlineData("ponyv6xl", "Pony")]                 // A1: decoration remainder can carry both a version AND "xl"
    [InlineData("harmony_lora", null)]       // 'pony' inside a token (not a prefix) must NOT match
    [InlineData("wander_style", null)]       // "wander" is one token and no bare "wan" key exists — must NOT match
    [InlineData("family_car", null)]         // 'il' is an exact-token match only; must NOT match inside another token
    [InlineData("mySd150Style", null)]       // 'sd15' inside a longer merged token must NOT match
    [InlineData("ponytail_v1", null)]        // A1: "pony" prefix with a non-decoration remainder must NOT match
    [InlineData("long_ponytail", null)]      // A1: same, "ponytail" mid-word
    [InlineData("anime_illustration_v2", null)]  // A1: "illust" prefix with a non-decoration remainder must NOT match
    [InlineData("illustration_style", null)]     // A1: same
    [InlineData("noobie_lora", null)]        // A1: "noob" prefix with a non-decoration remainder must NOT match
    [InlineData("obi_wan_kenobi", null)]     // the bare "wan" key stays absent — Star Wars-character false-positive magnet
    // REVERSED from the A3 review ruling, on evidence. A3 dropped every wan* key because
    // "Wan Video" cannot round-trip through BaseModelType and so stores as Other. That reasoning
    // held the label hostage to a gap in the ENUM: a Civitai-identified Wan model already stores
    // this exact raw label and this exact Other, so withholding it here bought nothing and cost
    // the sorter — which files by the raw string — a correct folder. Measured on a live library,
    // Wan was 12 of the files the table could not name. The enum gap is tracked separately.
    [InlineData("wan21_motion", "Wan Video")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData(".safetensors", null)]       // extension-only filename must not crash or match
    public void Guess_MatchesExpectedLabel(string? fileName, string? expected)
    {
        FilenameBaseModelHeuristic.Guess(fileName).Should().Be(expected);
    }

    // Ruling 1: extension stripping only fires for a KNOWN model-file extension, so
    // "detailer_sd1.5" above (no recognized extension) is tokenized whole rather than having its
    // ".5" eaten by a blind Path.GetFileNameWithoutExtension call. These confirm the real-file
    // path still strips a genuine extension correctly.
    [Theory]
    [InlineData("ponyDiffusionV6XL.safetensors", "Pony")]
    [InlineData("detailer_sd15.pt", "SD 1.5")]
    [InlineData("noob_artist.ckpt", "NoobAI")]
    public void Guess_StripsKnownModelExtensionsBeforeMatching(string fileName, string expected)
    {
        FilenameBaseModelHeuristic.Guess(fileName).Should().Be(expected);
    }

    /// <summary>
    /// Real names taken from a live library where the pre-expansion table identified 6 of 328
    /// files. LTX alone accounted for 64 of the misses, which is why the family carries three
    /// spellings and relies on the longest-key-first ordering to keep them apart.
    /// </summary>
    [Theory]
    [InlineData("547698-ltx2.3-Nfj1nx_21000.safetensors", "LTXV 2.3")]
    [InlineData("852654_LTX2.3-22B_ReStyle_IC-LoRA_8000_v0.1.safetensors", "LTXV 2.3")]
    [InlineData("LTX-2.3 - Cum Shot.safetensors", "LTXV 2.3")]
    [InlineData("LTX2_video_lora.safetensors", "LTXV2")]
    [InlineData("(ltx)cum_13_000002500.safetensors", "LTXV")]
    [InlineData("5fingering-ltx-mfng-step00004500.safetensors", "LTXV")]
    [InlineData("Wan2.2 - T2V - Whale Tail - LOW 14B.safetensors", "Wan Video")]
    [InlineData("Wan21_CausVid_14B_T2V_lora_rank32.safetensors", "Wan Video")]
    [InlineData("Qwen-Image-Lightning-8steps-V2.0.safetensors", "Qwen")]
    [InlineData("kontext-turnaround-sheet-v1.safetensors", "Flux.1 Kontext")]
    [InlineData("Flux2-Klein-9B-True-v2-bf16.safetensors", "Flux.2 Klein 9B")]
    [InlineData("hidream_o1_image_bf16.safetensors", "HiDream")]
    // The "-base" variants are their own catalog labels and 10 versions in the reference library
    // carry one, so the precise key beats the coarse "flux2" that used to answer for this name.
    [InlineData("flux-2-klein-base-9b-fp8.safetensors", "Flux.2 Klein 9B-base")]
    [InlineData("hunyuan_video_20s_horror_900.safetensors", "Hunyuan Video")]
    public void Guess_IdentifiesTheFamiliesARealLibraryActuallyContains(string fileName, string expected)
        => FilenameBaseModelHeuristic.Guess(fileName).Should().Be(expected);

    /// <summary>
    /// The bare "ltx" key is only safe because "latex" tokenizes to <c>latex</c>, not <c>ltx</c> —
    /// and "wan" is deliberately absent, because Star Wars character LoRAs are everywhere. Both are
    /// the failure mode that cost the refinement rung its StartsWith matching in review.
    /// </summary>
    [Theory]
    [InlineData("latex_dress_v3.safetensors")]
    [InlineData("obi_wan_kenobi.safetensors")]
    [InlineData("wan_kenobi_portrait.safetensors")]
    // The rows above cannot fail: no wan key matches a name with no version-shaped token at all.
    // These can — the candidate set synthesizes "wan2"/"wan25"/"wan21" from a version suffix on a
    // character LoRA, which is at least as common as the names the guard was written for.
    [InlineData("obi_wan_2.safetensors")]
    [InlineData("Obi Wan 2.5 - portrait.safetensors")]
    [InlineData("kenobi_wan_21.safetensors")]
    // "hunyuan" alone cannot pick between HunyuanDiT ("Hunyuan 1") and Hunyuan Video.
    [InlineData("hunyuan_style_v3.safetensors")]
    public void Guess_DoesNotMatchWordsThatMerelyLookLikeAFamily(string fileName)
        => FilenameBaseModelHeuristic.Guess(fileName).Should().BeNull();

    /// <summary>
    /// A bare digit after a family word is a version suffix, not a different family. "flux" is the
    /// only key stem that is a standalone word, so it is the only one where the pair synthesis had
    /// to be given up; the glued spelling still matches.
    /// </summary>
    [Theory]
    [InlineData("portrait_flux_2.safetensors", "Flux.1 D")]
    [InlineData("mystyle_flux_2_final.safetensors", "Flux.1 D")]
    [InlineData("mystyle_flux_v2.safetensors", "Flux.1 D")]
    [InlineData("flux2-vae.safetensors", "Flux.2 D")]
    [InlineData("mistral_3_small_flux2_fp8.safetensors", "Flux.2 D")]
    public void Guess_ReadsAVersionSuffixAsAVersionNotAFamily(string fileName, string expected)
        => FilenameBaseModelHeuristic.Guess(fileName).Should().Be(expected);

    /// <summary>
    /// A coarse key must never win over a finer one just because of where separators fall — the
    /// bug that made "style_sd3.5" resolve to "SD 3". Same table, same longest-key-first loop.
    /// </summary>
    [Theory]
    [InlineData("myclip_ltx_2_3_v1.safetensors", "LTXV 2.3")]
    [InlineData("myclip_ltx_2_v1.safetensors", "LTXV2")]
    public void Guess_PrefersTheLongestFamilyKey(string fileName, string expected)
        => FilenameBaseModelHeuristic.Guess(fileName).Should().Be(expected);

    /// <summary>
    /// Reflects over <see cref="DiffusionNexus.Civitai.CivitaiBaseModelCatalog"/>'s private bundled
    /// snapshot (via <see cref="SafetensorsFixture.CatalogLabels"/>, rather than duplicating the
    /// label list here) and asserts every label the heuristic can ever return is a real Civitai
    /// display label, not a typo'd string nobody's dropdown recognizes. Mirrors
    /// <c>BaseModelHeaderMapTests.Map_EveryOutputIsACatalogLabel</c> so a future catalog edit can't
    /// silently orphan a heuristic label either.
    /// </summary>
    [Fact]
    public void Guess_EveryOutputIsACatalogLabel()
    {
        var bundledSnapshot = SafetensorsFixture.CatalogLabels;

        FilenameBaseModelHeuristic.AllLabels.Should().NotBeEmpty();
        foreach (var label in FilenameBaseModelHeuristic.AllLabels)
        {
            bundledSnapshot.Should().Contain(label, $"'{label}' must be a real Civitai base-model label");
        }
    }
}
