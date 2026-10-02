using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.ViewModels;

public sealed class CommunityLinkSlotsTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(3, 3, 0)]
    [InlineData(4, 4, 0)] // fits exactly: no "More" hiding a link that has its own slot
    [InlineData(5, 3, 2)]
    [InlineData(6, 3, 3)]
    [InlineData(12, 3, 9)] // the SDK's MaxLinks
    public void Split_UsesEverySlotWhenTheListFits_AndGivesTheLastToMoreWhenItDoesNot(int count, int inline, int overflow)
    {
        var links = Enumerable.Range(1, count).ToList();

        var (inlineLinks, overflowLinks) = CommunityLinkSlots.Split(links);

        inlineLinks.Should().HaveCount(inline);
        overflowLinks.Should().HaveCount(overflow);
        (inlineLinks.Count + (overflowLinks.Count > 0 ? 1 : 0)).Should().BeLessThanOrEqualTo(CommunityLinkSlots.Count);
    }

    [Fact]
    public void Split_KeepsTheDocumentOrderAcrossInlineAndOverflow()
    {
        var links = new[] { "a", "b", "c", "d", "e" };

        var (inline, overflow) = CommunityLinkSlots.Split(links);

        inline.Should().Equal("a", "b", "c");
        overflow.Should().Equal("d", "e");
    }

    [Fact]
    public void Split_RejectsFewerThanOneSlot()
    {
        var act = () => CommunityLinkSlots.Split(new[] { "a" }, slots: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
