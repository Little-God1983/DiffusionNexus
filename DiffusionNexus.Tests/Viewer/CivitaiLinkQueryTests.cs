using DiffusionNexus.UI.Services.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Issue #630: the Browse Civitai search box takes a Civitai link, a bare model ID or a bare
/// version ID; everything else stays a name search.
/// </summary>
public sealed class CivitaiLinkQueryTests
{
    [Fact]
    public void FullVersionLink_YieldsBothIds()
    {
        CivitaiLinkQuery.TryParse("https://civitai.red/models/2815542/079-glitch-photo-effects?modelVersionId=3175491", out var q)
            .Should().BeTrue();

        q!.ModelId.Should().Be(2815542);
        q.VersionId.Should().Be(3175491);
        q.IsBareId.Should().BeFalse();
    }

    [Theory]
    [InlineData("https://civitai.com/models/2815542")]
    [InlineData("civitai.com/models/2815542/some-slug")]
    [InlineData("  https://www.civitai.com/models/2815542?tab=gallery  ")]
    public void ModelLink_YieldsTheModelIdOnly(string text)
    {
        CivitaiLinkQuery.TryParse(text, out var q).Should().BeTrue();

        q!.ModelId.Should().Be(2815542);
        q.VersionId.Should().BeNull();
    }

    [Fact]
    public void BareNumber_IsBothAModelAndAVersionCandidate()
    {
        CivitaiLinkQuery.TryParse(" 3175491 ", out var q).Should().BeTrue();

        q!.IsBareId.Should().BeTrue();
        q.ModelId.Should().Be(3175491);
        q.VersionId.Should().Be(3175491);
    }

    [Fact]
    public void CivitaiLinkWithoutIds_IsALinkThatNamesNoModel()
    {
        CivitaiLinkQuery.TryParse("https://civitai.com/images/123456", out var q).Should().BeTrue();

        q!.IsLinkWithoutIds.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("latex")]
    [InlineData("civitai")]                           // a name search, not a host
    [InlineData("anime style 2077")]
    [InlineData("https://huggingface.co/models/123")] // not a Civitai host
    [InlineData("0")]
    [InlineData("99999999999")]                       // does not fit an id
    public void NameSearchesAndForeignText_AreNotLinks(string? text)
    {
        CivitaiLinkQuery.TryParse(text, out var q).Should().BeFalse();
        q.Should().BeNull();
    }
}
