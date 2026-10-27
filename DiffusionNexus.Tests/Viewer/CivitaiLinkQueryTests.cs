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

    /// <summary>
    /// Review finding: a "copy download link" URL carries the VERSION id under <c>/api/download/models/</c>;
    /// the page-link regex read it as a model id and showed an unrelated model.
    /// </summary>
    [Theory]
    [InlineData("https://civitai.com/api/download/models/3175491?type=Model&format=SafeTensor")]
    [InlineData("https://civitai.com/api/v1/model-versions/3175491")]
    public void DownloadAndApiVersionLinks_YieldTheVersionId(string text)
    {
        CivitaiLinkQuery.TryParse(text, out var q).Should().BeTrue();

        q!.ModelId.Should().BeNull();
        q.VersionId.Should().Be(3175491);
    }

    [Fact]
    public void CivitaiLinkWithoutIds_IsALinkThatNamesNoModel()
    {
        // An image link used to be one of these; since #628 it is its own kind (below).
        CivitaiLinkQuery.TryParse("https://civitai.com/user/purplelady", out var q).Should().BeTrue();

        q!.IsLinkWithoutIds.Should().BeTrue();
        q.IsImageLink.Should().BeFalse();
    }

    [Theory]
    [InlineData("https://civitai.red/images/143992601")]
    [InlineData("civitai.com/images/143992601")]
    [InlineData("https://www.civitai.com/images/143992601?postId=31277568")]
    [InlineData("https://civitai.com/images/143992601/")]
    public void ImageLink_YieldsTheImageId(string text)
    {
        CivitaiLinkQuery.TryParse(text, out var q).Should().BeTrue();

        q!.ImageId.Should().Be(143992601);
        q.IsImageLink.Should().BeTrue();
        q.IsLinkWithoutIds.Should().BeFalse();
        q.ModelId.Should().BeNull();
        q.VersionId.Should().BeNull();
        q.ToString().Should().Be("image link (image 143992601)");
    }

    [Fact]
    public void BareNumber_IsNeverAnImage()
    {
        CivitaiLinkQuery.TryParse("143992601", out var q).Should().BeTrue();

        q!.IsImageLink.Should().BeFalse();
        q.ImageId.Should().BeNull();
    }

    [Fact]
    public void ImageLinkWithZeroId_IsALinkWithoutIds()
    {
        CivitaiLinkQuery.TryParse("https://civitai.com/images/0", out var q).Should().BeTrue();

        q!.IsImageLink.Should().BeFalse();
        q.IsLinkWithoutIds.Should().BeTrue();
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
