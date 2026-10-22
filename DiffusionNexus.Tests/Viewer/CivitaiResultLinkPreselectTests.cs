using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Issue #630: a card a link or ID produced pre-ticks the linked version (or the latest), leaves
/// installed versions unticked, and its badge tells a partly installed model from a done one.
/// </summary>
public sealed class CivitaiResultLinkPreselectTests
{
    private static CivitaiResultViewModel Card(params int[] versionIds) => new(new CivitaiModel
    {
        Id = 1,
        Name = "Three versions",
        ModelVersions = versionIds.Select(v => new CivitaiModelVersion { Id = v, Name = $"v{v}", BaseModel = "SDXL 1.0" }).ToList(),
    }, showNsfwPreviews: false);

    [Fact]
    public void VersionLink_TicksThatVersionOnly_AndSelectsTheCard()
    {
        var card = Card(30, 20, 10);
        card.ApplyInstalledIndex(CivitaiInstalledIndex.Empty);

        card.PreselectForLink(20, "Version ID 20");

        card.Versions.Where(v => v.IsSelected).Select(v => v.Version.Id).Should().Equal(20);
        card.IsSelected.Should().BeTrue("one click on \"Add selection to queue\" should take it");
        card.LinkMatchLabel.Should().Be("Version ID 20");
    }

    /// <summary>
    /// Review finding: a bare number matching a model AND a version of another model yields two
    /// cards; selecting both made one "Add selection to queue" click download an unrelated model.
    /// </summary>
    [Fact]
    public void OneOfSeveralCards_TicksItsVersion_ButLeavesTheCardUnselected()
    {
        var card = Card(30, 20, 10);
        card.ApplyInstalledIndex(CivitaiInstalledIndex.Empty);

        card.PreselectForLink(20, "Version ID 20", selectCard: false);

        card.Versions.Where(v => v.IsSelected).Select(v => v.Version.Id).Should().Equal(20);
        card.IsSelected.Should().BeFalse();
    }

    /// <summary>
    /// Review round 2: link cards appear before the first installed-library scan finishes (that scan
    /// gating the grid was a past regression). When it lands, an untouched card's pre-tick is redone
    /// so a version that turns out installed is unticked after all.
    /// </summary>
    [Fact]
    public void LateInstalledIndex_RedoesAnUntouchedPretick()
    {
        var card = Card(30, 20, 10);
        card.ApplyInstalledIndex(CivitaiInstalledIndex.Empty);
        card.PreselectForLink(20, "Version ID 20");

        card.ApplyInstalledIndex(new CivitaiInstalledIndex([20], []));
        card.ReapplyLinkPreselectIfUntouched();

        card.Versions.Should().OnlyContain(v => !v.IsSelected);
        card.IsSelected.Should().BeFalse();
    }

    [Fact]
    public void LateInstalledIndex_LeavesTheUsersOwnTicksAlone()
    {
        var card = Card(30, 20, 10);
        card.ApplyInstalledIndex(CivitaiInstalledIndex.Empty);
        card.PreselectForLink(20, "Version ID 20");
        card.Versions.Single(v => v.Version.Id == 10).IsSelected = true; // the user ticks another

        card.ApplyInstalledIndex(new CivitaiInstalledIndex([20], []));
        card.ReapplyLinkPreselectIfUntouched();

        card.Versions.Where(v => v.IsSelected).Select(v => v.Version.Id).Should().BeEquivalentTo([20, 10]);
    }

    [Fact]
    public void ModelLink_TicksTheLatest()
    {
        var card = Card(30, 20, 10);
        card.ApplyInstalledIndex(CivitaiInstalledIndex.Empty);

        card.PreselectForLink(null, "Model ID 1");

        card.Versions.Where(v => v.IsSelected).Select(v => v.Version.Id).Should().Equal(30);
    }

    [Fact]
    public void InstalledLinkedVersion_StaysUnticked_AndTheCardUnselected()
    {
        var card = Card(30, 20, 10);
        card.ApplyInstalledIndex(new CivitaiInstalledIndex([20], []));

        card.PreselectForLink(20, "Version ID 20");

        card.Versions.Should().OnlyContain(v => !v.IsSelected);
        card.IsSelected.Should().BeFalse();
        card.Versions.Single(v => v.Version.Id == 20).IsInstalled.Should().BeTrue();
    }

    [Fact]
    public void InstalledBadge_CountsAPartlyInstalledModel()
    {
        var card = Card(30, 20, 10);

        card.ApplyInstalledIndex(new CivitaiInstalledIndex([20, 10], []));
        card.InstalledBadgeText.Should().Be("2 of 3 installed");

        card.ApplyInstalledIndex(new CivitaiInstalledIndex([30, 20, 10], []));
        card.InstalledBadgeText.Should().Be("Installed");
    }
}
