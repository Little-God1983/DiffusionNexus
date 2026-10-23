using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.UI.Views.Controls;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Views;

public sealed class CommunityLinkIconTests
{
    [Fact]
    public void EveryCompiledInLink_HasItsOwnGlyph()
    {
        // A default that falls back would show the generic link glyph on a first, offline launch.
        foreach (var link in CommunityLink.Defaults)
        {
            CommunityLinkIcon.ResolveKey(link.Icon).Should().Be(link.Icon, $"'{link.Name}' ships with key '{link.Icon}'");
        }
    }

    [Theory]
    [InlineData("YouTube", "youtube")]
    [InlineData("  civitai ", "civitai")]
    [InlineData("discord", CommunityLinkIcon.FallbackKey)]
    [InlineData("", CommunityLinkIcon.FallbackKey)]
    [InlineData(null, CommunityLinkIcon.FallbackKey)]
    public void ResolveKey_MatchesCaseInsensitively_AndFallsBackForUnknownKeys(string? key, string expected)
    {
        CommunityLinkIcon.ResolveKey(key).Should().Be(expected);
    }
}
