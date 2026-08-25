using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Covers <see cref="DownloadDestinationViewModel.BuildTargetDirectory"/> now that it delegates
/// to <c>LoraPathBuilder</c> (spec §4.4) instead of hand-rolling the same base-model/category
/// combine the sorter already got right. Three behaviors changed on purpose — see each test.
/// </summary>
public class DownloadDestinationViewModelTests
{
    private static DownloadDestinationViewModel CreateVm(string sourceFolder = @"C:\root")
    {
        var vm = new DownloadDestinationViewModel();
        vm.SourceFolders.Add(sourceFolder);
        vm.SelectedSourceFolder = sourceFolder;
        vm.IsDownloadToExisting = true;
        vm.IsDownloadToFolder = false;
        return vm;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("???")]
    [InlineData("")]
    public void BuildTargetDirectory_BaseModelToggleOnWithUnresolvedBaseModel_LandsInUnknownFolder(string? baseModel)
        // Delta 1: previously the picker skipped the base-model segment entirely for a
        // blank/placeholder value even with the toggle ON, silently dropping the file one
        // level up from where the toggle promised it would go. LoraPathBuilder always gives an
        // unresolved base model its Unknown\ bucket, matching the sorter.
        => CreateVm().BuildTargetDirectory(baseModel, "Style")
            .Should().Be(@"C:\root\Unknown\Style");

    [Fact]
    public void BuildTargetDirectory_SanitizesFolderSegments()
        // Delta 2: the download path never sanitized folder names; a base model or category
        // carrying a Windows-invalid character used to be handed straight to Path.Combine.
        => CreateVm().BuildTargetDirectory("SD 3.5?", "Chara<cter")
            .Should().Be(@"C:\root\SD 3.5_\Chara_cter");

    [Fact]
    public void BuildTargetDirectory_CategoryLiterallyNamedUnknown_CreatesNoSegment()
        // Delta 3: a category resolved to the literal string "Unknown" used to still create an
        // Unknown\ subfolder under the base model, same drift SorterPathBuilder/LoraPathBuilder's
        // IsUnresolvedCategory already fixed on the sorter side (spec §4.4 exists to kill it here
        // too).
        => CreateVm().BuildTargetDirectory("SDXL 1.0", "Unknown")
            .Should().Be(@"C:\root\SDXL 1.0");

    [Fact]
    public void BuildTargetDirectory_BothTogglesOff_ReturnsBareSourceFolder()
    {
        var vm = CreateVm();
        vm.CreateBaseModelFolder = false;
        vm.CreateCategoryFolder = false;

        vm.BuildTargetDirectory("SDXL 1.0", "Character").Should().Be(@"C:\root");
    }

    [Fact]
    public void BuildTargetDirectory_DownloadToCustomFolder_IgnoresBaseModelAndCategory()
    {
        var vm = CreateVm();
        vm.IsDownloadToFolder = true;
        vm.CustomFolderPath = @"D:\custom";

        vm.BuildTargetDirectory("SDXL 1.0", "Character").Should().Be(@"D:\custom");
    }
}
