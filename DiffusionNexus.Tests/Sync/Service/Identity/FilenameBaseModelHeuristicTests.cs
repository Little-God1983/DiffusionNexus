using System.Reflection;
using DiffusionNexus.Civitai;
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
/// "illustration", "noobie") can't false-positive. "wan"/"wan21"/"wan22" aren't recognized at
/// all: "Wan Video" can't round-trip through <c>BaseModelType</c>, and "wan" collides with
/// Star Wars character names ("obi_wan_kenobi").
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
    [InlineData("wander_style", null)]       // no "wan" key exists at all anymore (A3) — must NOT match
    [InlineData("family_car", null)]         // 'il' is an exact-token match only; must NOT match inside another token
    [InlineData("mySd150Style", null)]       // 'sd15' inside a longer merged token must NOT match
    [InlineData("ponytail_v1", null)]        // A1: "pony" prefix with a non-decoration remainder must NOT match
    [InlineData("long_ponytail", null)]      // A1: same, "ponytail" mid-word
    [InlineData("anime_illustration_v2", null)]  // A1: "illust" prefix with a non-decoration remainder must NOT match
    [InlineData("illustration_style", null)]     // A1: same
    [InlineData("noobie_lora", null)]        // A1: "noob" prefix with a non-decoration remainder must NOT match
    [InlineData("obi_wan_kenobi", null)]     // A3: "wan" is a Star Wars-character false-positive magnet, key removed entirely
    [InlineData("wan21_motion", null)]       // A3: "Wan Video" can't round-trip through BaseModelType, key removed entirely
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
    /// Reflects over <see cref="CivitaiBaseModelCatalog"/>'s private bundled snapshot (rather than
    /// duplicating the label list here) and asserts every label the heuristic can ever return is a
    /// real Civitai display label, not a typo'd string nobody's dropdown recognizes. Mirrors
    /// <c>BaseModelHeaderMapTests.Map_EveryOutputIsACatalogLabel</c> so a future catalog edit can't
    /// silently orphan a heuristic label either.
    /// </summary>
    [Fact]
    public void Guess_EveryOutputIsACatalogLabel()
    {
        var bundledSnapshotField = typeof(CivitaiBaseModelCatalog)
            .GetField("BundledSnapshot", BindingFlags.NonPublic | BindingFlags.Static);
        bundledSnapshotField.Should().NotBeNull(
            "CivitaiBaseModelCatalog.BundledSnapshot must exist for this check to mean anything");

        var bundledSnapshot = (IReadOnlyList<string>)bundledSnapshotField!.GetValue(null)!;

        FilenameBaseModelHeuristic.AllLabels.Should().NotBeEmpty();
        foreach (var label in FilenameBaseModelHeuristic.AllLabels)
        {
            bundledSnapshot.Should().Contain(label, $"'{label}' must be a real Civitai base-model label");
        }
    }
}
