using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiRecipe;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer.CivitaiRecipe;

/// <summary>
/// #628: a weight from the image file belongs to the Civitai resource whose file has the same hash
/// (any kind Civitai lists), else the same file name. The example's names differ: Civitai calls the
/// AIM file <c>AIM-KREA-V1 (1).safetensors</c>, the graph <c>Krea2\Style\AIM-KREA-V1.safetensors</c>.
/// </summary>
public sealed class RecipeResourceMatcherTests
{
    private static CivitaiModelVersion Version(int id, string file, CivitaiFileHashes? hashes = null) => new()
    {
        Id = id,
        Name = $"v{id}",
        Files = [new CivitaiModelFile { Name = file, Hashes = hashes ?? new CivitaiFileHashes() }],
    };

    [Fact]
    public void HashMatch_OnAutoV2()
    {
        var version = Version(3113770, "Purple_Graphics_KR2.safetensors", new CivitaiFileHashes { AutoV2 = "990D0DF641" });
        var use = new RecipeLoraUse("renamed_locally", null, 1, "990d0df641");

        var result = RecipeResourceMatcher.Match([version], [use]);

        result.ByVersionId[3113770].Should().Be(use);
        result.Unlinked.Should().BeEmpty();
    }

    [Fact]
    public void HashMatch_OnAutoV3AndSha256_12()
    {
        var a = Version(1, "a.safetensors", new CivitaiFileHashes { AutoV3 = "47A98D0C95BE" });
        var b = Version(2, "b.safetensors", new CivitaiFileHashes { SHA256_12 = "990D0DF64187" });

        var result = RecipeResourceMatcher.Match([a, b],
            [new RecipeLoraUse("x", null, 0.5, "47a98d0c95be"), new RecipeLoraUse("y", null, 0.7, "990d0df64187")]);

        result.ByVersionId[1].Weight.Should().Be(0.5);
        result.ByVersionId[2].Weight.Should().Be(0.7);
    }

    [Fact]
    public void NameMatch_IgnoresFoldersAndCopySuffix()
    {
        var version = Version(3343424, "AIM-KREA-V1 (1).safetensors");
        var use = new RecipeLoraUse("AIM-KREA-V1", @"Krea2\Style\AIM-KREA-V1.safetensors", 0.65, null);

        RecipeResourceMatcher.Match([version], [use]).ByVersionId[3343424].Weight.Should().Be(0.65);
    }

    [Fact]
    public void HashBeatsName()
    {
        var a = Version(1, "foo.safetensors");
        var b = Version(2, "other.safetensors", new CivitaiFileHashes { AutoV2 = "ABCDEF0123" });
        var use = new RecipeLoraUse("foo", null, 0.9, "abcdef0123");

        var result = RecipeResourceMatcher.Match([a, b], [use]);

        result.ByVersionId.Should().ContainKey(2).And.NotContainKey(1);
    }

    [Fact]
    public void UnmatchedUse_IsUnlinked_AndUnmatchedVersionHasNoEntry()
    {
        var version = Version(1, "foo.safetensors");
        var stranger = new RecipeLoraUse("private_lora", null, 0.8, "1111111111");

        var result = RecipeResourceMatcher.Match([version], [stranger]);

        result.ByVersionId.Should().BeEmpty();
        result.Unlinked.Should().Equal(stranger);
    }

    [Fact]
    public void OneUsePerVersion_AndOneVersionPerUse()
    {
        var version = Version(1, "foo.safetensors");
        var first = new RecipeLoraUse("foo", null, 0.5, null);
        var second = new RecipeLoraUse("FOO", null, 0.6, null);

        var result = RecipeResourceMatcher.Match([version], [first, second]);

        result.ByVersionId[1].Should().Be(first);
        result.Unlinked.Should().Equal(second);
    }
}
