using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Covers the remove-installation dialog's wording. Each folder kind gets exactly one row,
/// and it has to be self-contained: a row reading "none linked" while a folder of that kind
/// is being kept is a contradiction, and an explanation collected at the bottom of the
/// dialog cannot say which row it belongs to. The kept half is returned separately so the
/// view can tint it — it describes what the checkbox will NOT do.
/// </summary>
public sealed class RemoveInstallationLabelsTests
{
    [Fact]
    public void Compose_ListsRemovableFolders()
    {
        var label = RemoveInstallationLabels.Compose("Gallery", [@"D:\Output"]);

        label.Text.Should().Be("Gallery\nD:\\Output");
        label.KeptText.Should().BeEmpty();
        label.HasKept.Should().BeFalse();
    }

    [Fact]
    public void Compose_NoFoldersAtAll_SaysNoneLinked()
    {
        var label = RemoveInstallationLabels.Compose("LoRA Source", []);

        label.Text.Should().Be("LoRA Source — none linked");
        label.HasKept.Should().BeFalse();
    }

    [Fact]
    public void Compose_OnlyKeptFolders_NamesThemInTheRowAndNeverSaysNoneLinked()
    {
        // The bug this covers, in two rounds: the row first claimed "none linked" while a
        // gallery was being kept, then explained itself in a note at the very bottom of the
        // dialog, too far from the row to say which folder kind it meant.
        var label = RemoveInstallationLabels.Compose("Gallery", [], [@"E:\AI\comfy_output\"]);

        label.Text.Should().Be("Gallery");
        label.Text.Should().NotContain("none linked");
        label.KeptText.Should().Be(
            "kept — another installation still uses it:\nE:\\AI\\comfy_output\\");
        label.HasKept.Should().BeTrue();
    }

    [Fact]
    public void Compose_SeveralKeptFolders_AgreesInNumber()
    {
        var label = RemoveInstallationLabels.Compose(
            "Base Model Folder", [], [@"D:\Models", @"E:\Models"]);

        label.KeptText.Should().StartWith("kept — another installation still uses them:");
        label.KeptText.Should().Contain(@"D:\Models").And.Contain(@"E:\Models");
    }

    [Fact]
    public void Compose_MixOfRemovableAndKept_SplitsThemIntoTheTwoHalves()
    {
        var label = RemoveInstallationLabels.Compose(
            "Gallery", [@"D:\Output"], [@"E:\AI\comfy_output\"]);

        label.Text.Should().Be("Gallery\nD:\\Output", "only these are removed by the checkbox");
        label.KeptText.Should().Contain(@"E:\AI\comfy_output\");
    }

    [Fact]
    public void Compose_DeduplicatesKeptPathsCaseInsensitively()
    {
        RemoveInstallationLabels.Compose("Gallery", [], [@"E:\Out", @"e:\out"])
            .KeptText.Should().Be("kept — another installation still uses it:\nE:\\Out");
    }
}
