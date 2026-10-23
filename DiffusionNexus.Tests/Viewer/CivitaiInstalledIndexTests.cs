using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// The installed lookup answers two questions from one rule: does this card own
/// anything I already have (card badge), and do I already have this exact version
/// (version-row badge + the re-download prompt).
/// </summary>
public sealed class CivitaiInstalledIndexTests
{
    private static CivitaiModelVersion Version(int id, string? sha = null) => new()
    {
        Id = id,
        Name = $"v{id}",
        BaseModel = "Krea 2",
        Files = sha is null ? [] : [new CivitaiModelFile { Hashes = new CivitaiFileHashes { SHA256 = sha } }]
    };

    [Fact]
    public void MatchesOnCivitaiVersionId()
    {
        var index = new CivitaiInstalledIndex([42], []);

        index.IsInstalled(Version(42)).Should().BeTrue();
        index.IsInstalled(Version(43)).Should().BeFalse();
    }

    [Fact]
    public void FallsBackToFileHash_CaseInsensitively()
    {
        // Repository rows are lower-cased; Civitai returns upper-case hex. The fallback
        // exists for orphan rows with no CivitaiId, so it must survive that mismatch.
        var index = new CivitaiInstalledIndex([], ["abc123"]);

        index.IsInstalled(Version(99, sha: "ABC123")).Should().BeTrue();
        index.IsInstalled(Version(99, sha: "def456")).Should().BeFalse();
    }

    [Fact]
    public void ModelCountsAsInstalledWhenAnySingleVersionIs()
    {
        var model = new CivitaiModel { Id = 7, Name = "Mixed", ModelVersions = [Version(1), Version(2)] };
        var index = new CivitaiInstalledIndex([2], []);

        index.IsInstalled(model).Should().BeTrue();
        index.IsInstalled(new CivitaiModel { Id = 8, Name = "None", ModelVersions = [Version(3)] })
            .Should().BeFalse();
    }

    [Fact]
    public void EmptyIndex_AndNulls_AreNotInstalled()
    {
        CivitaiInstalledIndex.Empty.IsInstalled(Version(1)).Should().BeFalse();
        CivitaiInstalledIndex.Empty.IsInstalled((CivitaiModelVersion?)null).Should().BeFalse();
        CivitaiInstalledIndex.Empty.IsInstalled((CivitaiModel?)null).Should().BeFalse();
    }

    [Fact]
    public void ApplyingTheIndexBadgesTheOwnedRowOnly_AndLightsTheCard()
    {
        var model = new CivitaiModel
        {
            Id = 500,
            Name = "Seven versions",
            ModelVersions = [Version(10), Version(11), Version(12)]
        };
        var card = new CivitaiResultViewModel(model, showNsfwPreviews: false);

        card.ApplyInstalledIndex(new CivitaiInstalledIndex([11], []));

        card.Versions.Select(v => v.IsInstalled).Should().Equal(false, true, false);
        card.IsInstalled.Should().BeTrue("the card badge means 'you own at least one version'");
    }

    [Fact]
    public void ReapplyingAFreshIndexClearsStaleRowBadges()
    {
        var model = new CivitaiModel { Id = 501, Name = "Two", ModelVersions = [Version(20), Version(21)] };
        var card = new CivitaiResultViewModel(model, showNsfwPreviews: false);
        card.ApplyInstalledIndex(new CivitaiInstalledIndex([20, 21], []));

        // e.g. the user deleted both files, or disabled the LoRA source they live under
        card.ApplyInstalledIndex(CivitaiInstalledIndex.Empty);

        card.Versions.Should().OnlyContain(v => !v.IsInstalled);
        card.IsInstalled.Should().BeFalse();
    }
}
