using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public class EngineFeatureCatalogTests
{
    [Fact]
    public void Rows_MapToTheirCatalogWorkloads()
    {
        EngineFeatureCatalog.Get(EngineFeature.InpaintOutpaint).WorkloadIds
            .Should().Equal(Guid.Parse("4C486765-A4C1-4E94-ACC2-BBAC0E405B6A"));
        EngineFeatureCatalog.Get(EngineFeature.OutpaintVision).WorkloadIds
            .Should().Equal(Guid.Parse("137929E4-5C05-4304-80D4-5D785D45FD3F"));
        EngineFeatureCatalog.Get(EngineFeature.Canvas).WorkloadIds
            .Should().Equal(Guid.Parse("E79C079A-2FD7-4FE7-8086-23731092555D"));
        EngineFeatureCatalog.AllWorkloadIds.Should().HaveCount(3);
    }

    [Fact]
    public void OutpaintVision_BringsTheQwen3VLFolderModel_OtherRowsNone()
    {
        EngineFeatureCatalog.Get(EngineFeature.OutpaintVision).FolderModels
            .Should().Equal(EngineFolderModels.Qwen3VL4BInstructFp8);
        EngineFeatureCatalog.Get(EngineFeature.InpaintOutpaint).FolderModels.Should().BeEmpty();
        EngineFeatureCatalog.Get(EngineFeature.Canvas).FolderModels.Should().BeEmpty();
    }

    [Fact]
    public void Rows_AreListedInDisplayOrder_WithTheirLabels()
    {
        EngineFeatureCatalog.All.Select(r => r.DisplayName)
            .Should().Equal("Inpaint & Outpaint", "Outpaint Vision", "Canvas · Krea 2 Turbo");
    }

    [Theory]
    [InlineData(Feature.Inpainting, EngineFeature.InpaintOutpaint)]
    [InlineData(Feature.Outpaint, EngineFeature.InpaintOutpaint)]
    [InlineData(Feature.OutpaintVision, EngineFeature.OutpaintVision)]
    public void ForAppFeature_MapsEditorToolsToTheirRow(Feature feature, EngineFeature expected)
    {
        EngineFeatureCatalog.ForAppFeature(feature).Should().Be(expected);
    }

    [Theory]
    [InlineData(Feature.BatchUpscale)]
    [InlineData(Feature.BatchUpscaleVision)]
    [InlineData(Feature.Captioning)]
    public void ForAppFeature_IsNull_ForFeaturesTheEngineDoesNotOfferYet(Feature feature)
    {
        EngineFeatureCatalog.ForAppFeature(feature).Should().BeNull();
    }

    [Theory]
    [InlineData("8,12,16,24,32", new[] { 8, 12, 16, 24, 32 })]
    [InlineData("8GB, 16GB, 24+", new[] { 8, 16, 24 })]
    [InlineData("", new int[0])]
    [InlineData(null, new int[0])]
    public void ParseVramProfiles_ReadsTheCatalogFormat(string? raw, int[] expected)
    {
        EngineFeatureCatalog.ParseVramProfiles(raw).Should().Equal(expected);
    }

    [Theory]
    [InlineData(8192, 8)]     // 8 GB card -> smallest tier
    [InlineData(12288, 12)]
    [InlineData(16384, 16)]
    [InlineData(24576, 24)]
    [InlineData(49152, 32)]   // above the top tier -> top tier
    [InlineData(6144, 8)]     // below the smallest tier -> smallest tier, never 0
    [InlineData(0, 8)]        // unknown VRAM -> smallest tier
    public void SuggestVramTier_PicksTheLargestTierThatFits(long vramMb, int expected)
    {
        int[] tiers = [8, 12, 16, 24, 32];

        EngineFeatureCatalog.SuggestVramTier(vramMb, tiers).Should().Be(expected);
    }

    [Fact]
    public void SuggestVramTier_ReturnsZeroWhenTheWorkloadDeclaresNoTiers()
    {
        EngineFeatureCatalog.SuggestVramTier(24576, []).Should().Be(0,
            "0 means 'no VRAM filtering' to the workload installer");
    }
}
