using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Covers the remove-installation dialog's wording. The checkbox label and the
/// held-back-folders note are rendered side by side, so they have to agree: a row
/// reading "none linked" directly above a note naming a kept gallery is a contradiction.
/// </summary>
public sealed class RemoveInstallationLabelsTests
{
    [Fact]
    public void ComposeCheckbox_ListsRemovableFolders()
    {
        RemoveInstallationLabels.ComposeCheckbox("Gallery", [@"D:\Output"])
            .Should().Be("Gallery\nD:\\Output");
    }

    [Fact]
    public void ComposeCheckbox_NoFoldersAtAll_SaysNoneLinked()
    {
        RemoveInstallationLabels.ComposeCheckbox("LoRA Source", [])
            .Should().Be("LoRA Source — none linked");
    }

    [Fact]
    public void ComposeCheckbox_OnlySharedFolders_SaysKeptNotNoneLinked()
    {
        // The bug: the installation's one gallery was held back for another install, and
        // the row still claimed nothing was linked — contradicting the note right below it.
        var label = RemoveInstallationLabels.ComposeCheckbox("Gallery", [], [@"E:\AI\comfy_output\"]);

        label.Should().NotContain("none linked");
        label.Should().Be("Gallery — kept, still used by another installation");
    }

    [Fact]
    public void ComposeCheckbox_MixOfRemovableAndShared_ListsOnlyTheRemovableOnes()
    {
        var label = RemoveInstallationLabels.ComposeCheckbox(
            "Gallery", [@"D:\Output"], [@"E:\AI\comfy_output\"]);

        label.Should().Be("Gallery\nD:\\Output", "the kept path is named by the note instead");
    }

    [Fact]
    public void ComposeSharedNote_SingleFolder_UsesSingularWording()
    {
        RemoveInstallationLabels.ComposeSharedNote([@"E:\AI\comfy_output\"])
            .Should().Be("Kept because another installation still uses it:\nE:\\AI\\comfy_output\\");
    }

    [Fact]
    public void ComposeSharedNote_SeveralFolders_UsesPluralWordingAndListsAll()
    {
        var note = RemoveInstallationLabels.ComposeSharedNote([@"E:\out", @"D:\models"]);

        note.Should().StartWith("Kept because another installation still uses them:");
        note.Should().Contain(@"E:\out").And.Contain(@"D:\models");
    }

    [Fact]
    public void ComposeSharedNote_DeduplicatesCaseInsensitively()
    {
        RemoveInstallationLabels.ComposeSharedNote([@"E:\Out", @"e:\out"])
            .Should().Be("Kept because another installation still uses it:\nE:\\Out");
    }

    [Fact]
    public void ComposeSharedNote_NothingHeldBack_IsEmpty()
    {
        RemoveInstallationLabels.ComposeSharedNote([]).Should().BeEmpty(
            "the note's border is shown only when this is non-empty");
    }
}
