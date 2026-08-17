using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public class EngineWorkloadCatalogTests
{
    [Fact]
    public void Catalog_ContainsKrea2Turbo()
    {
        var krea = Guid.Parse("E79C079A-2FD7-4FE7-8086-23731092555D");

        EngineWorkloadCatalog.WorkloadIds.Should().Contain(krea);
        EngineWorkloadCatalog.Contains(krea).Should().BeTrue();
        EngineWorkloadCatalog.Contains(Guid.NewGuid()).Should().BeFalse();
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

        EngineWorkloadCatalog.SuggestVramTier(vramMb, tiers).Should().Be(expected);
    }

    [Fact]
    public void SuggestVramTier_ReturnsZeroWhenTheWorkloadDeclaresNoTiers()
    {
        EngineWorkloadCatalog.SuggestVramTier(24576, []).Should().Be(0,
            "0 means 'no VRAM filtering' to the workload installer");
    }
}
