using System.Globalization;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using FluentAssertions;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>An accepted result is now a layer: the properties the layer stack (#594) edits.</summary>
public class GenerationFrameLayerTests
{
    [Fact]
    public void ANewFrameIsAVisibleOpaqueUnlockedRasterLayer()
    {
        var frame = new GenerationFrameViewModel();

        frame.Kind.Should().Be(CanvasLayerKind.Raster);
        frame.IsVisible.Should().BeTrue();
        frame.Opacity.Should().Be(1.0);
        frame.IsLocked.Should().BeFalse();
        frame.Name.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1.5, 1.0)]
    [InlineData(-0.2, 0.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(0.25, 0.25)]
    public void OpacityIsClampedToTheUnitRange(double assigned, double expected)
    {
        var frame = new GenerationFrameViewModel { Opacity = assigned };

        frame.Opacity.Should().Be(expected);
    }

    [Fact]
    public void OpacityPercentRoundTripsAndRaisesTheDerivedProperties()
    {
        var frame = new GenerationFrameViewModel();
        var raised = new List<string?>();
        frame.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        frame.OpacityPercent = 50;

        frame.Opacity.Should().Be(0.5);
        frame.OpacityText.Should().Be("50%");
        raised.Should().Contain([nameof(GenerationFrameViewModel.Opacity),
            nameof(GenerationFrameViewModel.OpacityPercent), nameof(GenerationFrameViewModel.OpacityText)]);
    }

    [Fact]
    public void ProvenanceTextIsInvariantUnderAGermanCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var frame = new GenerationFrameViewModel
            {
                Seed = 1234567, Width = 1024, Height = 768, CanvasX = 1536.5, CanvasY = -64,
            };

            frame.ProvenanceText.Should().Be("Seed 1234567 · 1024×768 at (1537, -64)");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ProvenanceTextSaysWhenTheSeedIsUnknown()
    {
        new GenerationFrameViewModel { Width = 512, Height = 512 }
            .ProvenanceText.Should().StartWith("Seed unknown · 512×512");
    }

    [Theory]
    [InlineData("  Sky  ", "Old", "Sky")]
    [InlineData("", "Old", "Old")]
    [InlineData("   ", "Old", "Old")]
    [InlineData(null, "Old", "Old")]
    [InlineData("two\r\nlines", "Old", "two  lines")]
    public void LayerStackNaming_TrimsAndRevertsBlankNames(string? proposed, string current, string expected)
    {
        LayerStackNaming.Resolve(proposed, current).Should().Be(expected);
    }

    [Fact]
    public void LayerStackNaming_CapsTheLength()
    {
        LayerStackNaming.Resolve(new string('x', 200), "Old").Should().HaveLength(LayerStackNaming.MaxLength);
    }
}
