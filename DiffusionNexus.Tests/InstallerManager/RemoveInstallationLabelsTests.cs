using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Covers the remove-installation dialog's wording. Each folder kind gets exactly one
/// label, and it has to be self-contained: a row reading "none linked" while a folder of
/// that kind is being kept is a contradiction, and an explanation collected at the bottom
/// of the dialog cannot say which row it belongs to.
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
    public void ComposeCheckbox_OnlyKeptFolders_NamesThemInTheRowItself()
    {
        // The bug this covers, in two rounds: the row first claimed "none linked" while a
        // gallery was being kept, and then explained itself in a note at the very bottom of
        // the dialog, too far from the row to say what was kept.
        var label = RemoveInstallationLabels.ComposeCheckbox("Gallery", [], [@"E:\AI\comfy_output\"]);

        label.Should().NotContain("none linked");
        label.Should().Be(
            "Gallery — kept, another installation still uses it:\nE:\\AI\\comfy_output\\");
    }

    [Fact]
    public void ComposeCheckbox_SeveralKeptFolders_AgreeInNumber()
    {
        var label = RemoveInstallationLabels.ComposeCheckbox(
            "Base Model Folder", [], [@"D:\Models", @"E:\Models"]);

        label.Should().StartWith("Base Model Folder — kept, another installation still uses them:");
        label.Should().Contain(@"D:\Models").And.Contain(@"E:\Models");
    }

    [Fact]
    public void ComposeCheckbox_MixOfRemovableAndKept_ShowsBothGroupsInOneRow()
    {
        var label = RemoveInstallationLabels.ComposeCheckbox(
            "Gallery", [@"D:\Output"], [@"E:\AI\comfy_output\"]);

        label.Should().Be(
            "Gallery\nD:\\Output\n\nkept, another installation still uses it:\nE:\\AI\\comfy_output\\",
            "the checkbox removes the first path and keeps the second — both belong in its own label");
    }

    [Fact]
    public void ComposeCheckbox_DeduplicatesKeptPathsCaseInsensitively()
    {
        RemoveInstallationLabels.ComposeCheckbox("Gallery", [], [@"E:\Out", @"e:\out"])
            .Should().Be("Gallery — kept, another installation still uses it:\nE:\\Out");
    }
}
