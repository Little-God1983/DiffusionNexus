using System.Reflection;
using DiffusionNexus.Civitai;
using DiffusionNexus.Service.Services.Sync.Identity;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service.Identity;

/// <summary>
/// Covers <see cref="FilenameBaseModelHeuristic"/> — guessing a Civitai base-model label from a
/// model file name when no header/sidecar metadata is available. Match order is most-specific
/// first (whole-name substring, then exact token/pair, then token prefix) so short, common
/// fragments like "il" or "wan" can't false-positive inside longer unrelated words.
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
    [InlineData("wan21_motion", "Wan Video")]
    [InlineData("flux_dev_char", "Flux.1 D")]
    [InlineData("noob_artist", "NoobAI")]
    [InlineData("harmony_lora", null)]       // 'pony' inside a token must NOT match
    [InlineData("wander_style", null)]       // 'wan' prefix of a longer token must NOT match
    [InlineData("family_car", null)]         // 'il' inside a token must NOT match
    [InlineData("mySd150Style", null)]       // 'sd15' inside a longer merged token must NOT match
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
