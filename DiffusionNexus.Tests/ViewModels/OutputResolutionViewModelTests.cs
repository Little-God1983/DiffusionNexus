using DiffusionNexus.UI.ViewModels.Controls;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

public class OutputResolutionViewModelTests
{
    [Fact]
    public void Default_IsSameAsInput_At1MP()
    {
        var vm = new OutputResolutionViewModel();

        vm.SelectedAspectRatio.Should().Be(OutputAspectRatio.SameAsInput);
        vm.OutputMegapixels.Should().Be(1.0);
    }

    [Fact]
    public void SameAsInput_PreservesSourceAspect_ScaledToMegapixels()
    {
        var vm = new OutputResolutionViewModel();

        var (w, h) = vm.ComputeDimensions(1920, 1080); // 16:9 source

        ((double)w / h).Should().BeApproximately(16.0 / 9.0, 0.05);
        ((long)w * h).Should().BeInRange(900_000, 1_100_000); // ~1 MP
        (w % 16).Should().Be(0);
        (h % 16).Should().Be(0);
    }

    [Theory]
    [InlineData(OutputAspectRatio.R16x9, 16.0 / 9.0)]
    [InlineData(OutputAspectRatio.R1x1, 1.0)]
    [InlineData(OutputAspectRatio.R4x3, 4.0 / 3.0)]
    [InlineData(OutputAspectRatio.R5x4, 5.0 / 4.0)]
    public void FixedRatio_IgnoresSource_UsesChosenAspect(OutputAspectRatio ratio, double expectedAspect)
    {
        var vm = new OutputResolutionViewModel { SelectedAspectRatio = ratio };

        var (w, h) = vm.ComputeDimensions(800, 600); // arbitrary 4:3 source, ignored for fixed ratios

        ((double)w / h).Should().BeApproximately(expectedAspect, 0.05);
    }

    [Fact]
    public void Switch_TransposesOrientation_SameMegapixels()
    {
        var vm = new OutputResolutionViewModel { SelectedAspectRatio = OutputAspectRatio.R16x9 };
        var (lw, lh) = vm.ComputeDimensions(0, 0);
        lw.Should().BeGreaterThan(lh, "16:9 is landscape");

        vm.SwitchOrientation = true;
        var (pw, ph) = vm.ComputeDimensions(0, 0);

        ph.Should().BeGreaterThan(pw, "switched 16:9 is portrait (9:16)");
        pw.Should().Be(lh); // transposed
        ph.Should().Be(lw);
    }

    [Fact]
    public void Megapixels_ScalesTotalPixels()
    {
        var vm = new OutputResolutionViewModel { SelectedAspectRatio = OutputAspectRatio.R1x1, OutputMegapixels = 4.0 };

        var (w, h) = vm.ComputeDimensions(0, 0);

        ((long)w * h).Should().BeInRange(3_600_000, 4_300_000); // ~4 MP
    }

    [Fact]
    public void Megapixels_ClampedToMax()
    {
        var vm = new OutputResolutionViewModel { SelectedAspectRatio = OutputAspectRatio.R1x1, OutputMegapixels = 99.0 };

        var (w, h) = vm.ComputeDimensions(0, 0);

        ((long)w * h).Should().BeInRange(3_600_000, 4_300_000); // clamped to the 4 MP ceiling
    }

    [Theory]
    [InlineData(false, "16:9", "4:3", "5:4")]
    [InlineData(true, "9:16", "3:4", "4:5")]
    public void Labels_FlipWithSwitch(bool switched, string l169, string l43, string l54)
    {
        var vm = new OutputResolutionViewModel { SwitchOrientation = switched };

        vm.Label16x9.Should().Be(l169);
        vm.Label4x3.Should().Be(l43);
        vm.Label5x4.Should().Be(l54);
    }

    [Fact]
    public void SelectAspectRatioCommand_SetsSelection()
    {
        var vm = new OutputResolutionViewModel();

        vm.SelectAspectRatioCommand.Execute(OutputAspectRatio.R4x3);

        vm.SelectedAspectRatio.Should().Be(OutputAspectRatio.R4x3);
    }
}
