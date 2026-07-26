using DiffusionNexus.UI.Models.Pipelines;
using FluentAssertions;

namespace DiffusionNexus.Tests.Models.Pipelines;

/// <summary>
/// Unit tests for <see cref="HuggingFaceUrl"/>.
/// Covers filename extraction (query/fragment stripping, trailing slashes, percent-unescaping)
/// and <c>/resolve/</c> normalization (host detection, blob/raw rewriting, download flag, idempotency).
/// </summary>
public class HuggingFaceUrlTests
{
    // ---------------------------------------------------------------
    //  GetFileName
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void WhenUrlIsNullOrBlankThenGetFileNameReturnsEmptyString(string? url)
    {
        HuggingFaceUrl.GetFileName(url).Should().BeEmpty();
    }

    [Fact]
    public void WhenUrlHasDownloadQueryThenGetFileNameStripsIt()
    {
        HuggingFaceUrl
            .GetFileName("https://huggingface.co/org/repo/resolve/main/flux-2-klein-9b-Q4_K_M.gguf?download=true")
            .Should().Be("flux-2-klein-9b-Q4_K_M.gguf");
    }

    [Fact]
    public void WhenUrlHasMultipleQueryParametersThenGetFileNameStripsAllOfThem()
    {
        HuggingFaceUrl
            .GetFileName("https://huggingface.co/org/repo/resolve/main/model.safetensors?download=true&foo=1")
            .Should().Be("model.safetensors");
    }

    [Fact]
    public void WhenUrlHasFragmentThenGetFileNameStripsIt()
    {
        HuggingFaceUrl
            .GetFileName("https://huggingface.co/org/repo/resolve/main/model.safetensors#sha256")
            .Should().Be("model.safetensors");
    }

    [Fact]
    public void WhenUrlHasFragmentBeforeQueryThenGetFileNameStripsFromTheFirstDelimiter()
    {
        // Split('?', '#') cuts at whichever delimiter comes first.
        HuggingFaceUrl
            .GetFileName("https://huggingface.co/org/repo/resolve/main/model.gguf#frag?download=true")
            .Should().Be("model.gguf");
    }

    [Fact]
    public void WhenUrlHasTrailingSlashThenGetFileNameReturnsThePrecedingSegment()
    {
        HuggingFaceUrl
            .GetFileName("https://huggingface.co/org/repo/resolve/main/")
            .Should().Be("main");
    }

    [Fact]
    public void WhenUrlHasSeveralTrailingSlashesThenGetFileNameTrimsThemAll()
    {
        HuggingFaceUrl
            .GetFileName("https://huggingface.co/org/repo/resolve/main///")
            .Should().Be("main");
    }

    [Fact]
    public void WhenUrlHasTrailingSlashBeforeQueryThenGetFileNameStillFindsTheSegment()
    {
        // Query is removed first, so the trailing slash is exposed and trimmed afterwards.
        HuggingFaceUrl
            .GetFileName("https://huggingface.co/org/repo/resolve/main/model.gguf/?download=true")
            .Should().Be("model.gguf");
    }

    [Fact]
    public void WhenSegmentIsPercentEncodedThenGetFileNameUnescapesIt()
    {
        HuggingFaceUrl
            .GetFileName("https://huggingface.co/org/repo/resolve/main/my%20model%20v2.safetensors")
            .Should().Be("my model v2.safetensors");
    }

    [Fact]
    public void WhenEncodedSegmentContainsEscapedSlashThenUnescapingReintroducesIt()
    {
        // %2F is not treated as a path separator during the split, but unescaping brings it back.
        HuggingFaceUrl
            .GetFileName("https://huggingface.co/org/repo/resolve/main/sub%2Fdir%20name.safetensors")
            .Should().Be("sub/dir name.safetensors");
    }

    [Fact]
    public void WhenSegmentHasInvalidPercentEscapeThenGetFileNameLeavesItLiteral()
    {
        HuggingFaceUrl
            .GetFileName("https://huggingface.co/org/repo/resolve/main/file%zz.txt")
            .Should().Be("file%zz.txt");
    }

    [Fact]
    public void WhenSegmentContainsPlusThenGetFileNameDoesNotTreatItAsSpace()
    {
        // UnescapeDataString is not form-decoding: '+' stays a '+'.
        HuggingFaceUrl
            .GetFileName("https://huggingface.co/org/repo/resolve/main/a+b.gguf")
            .Should().Be("a+b.gguf");
    }

    [Fact]
    public void WhenValueHasNoSlashThenGetFileNameReturnsTheWholeValue()
    {
        HuggingFaceUrl.GetFileName("model.safetensors").Should().Be("model.safetensors");
    }

    [Fact]
    public void WhenUrlIsOnlyAQueryStringThenGetFileNameReturnsEmptyString()
    {
        HuggingFaceUrl.GetFileName("?download=true").Should().BeEmpty();
    }

    [Fact]
    public void WhenUrlIsOnlySlashesThenGetFileNameReturnsEmptyString()
    {
        HuggingFaceUrl.GetFileName("///").Should().BeEmpty();
    }

    // ---------------------------------------------------------------
    //  NormalizeResolveUrl — guard clauses
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenUrlIsNullOrBlankThenNormalizeReturnsEmptyString(string? url)
    {
        HuggingFaceUrl.NormalizeResolveUrl(url).Should().BeEmpty();
    }

    // ---------------------------------------------------------------
    //  NormalizeResolveUrl — non-HuggingFace passthrough
    // ---------------------------------------------------------------

    [Fact]
    public void WhenUrlIsNotHuggingFaceThenNormalizeReturnsItUnchanged()
    {
        const string url = "https://civitai.com/api/download/models/12345";

        HuggingFaceUrl.NormalizeResolveUrl(url).Should().Be(url);
    }

    [Fact]
    public void WhenNonHuggingFaceUrlContainsBlobThenNormalizeDoesNotRewriteIt()
    {
        const string url = "https://github.com/org/repo/blob/main/file.txt";

        HuggingFaceUrl.NormalizeResolveUrl(url).Should().Be(url);
    }

    [Fact]
    public void WhenNonHuggingFaceUrlIsPaddedThenNormalizeStillTrimsIt()
    {
        HuggingFaceUrl.NormalizeResolveUrl("  https://example.com/file.bin  ")
            .Should().Be("https://example.com/file.bin");
    }

    // ---------------------------------------------------------------
    //  NormalizeResolveUrl — host detection
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("https://huggingface.co/org/repo/resolve/main/f.gguf")]
    [InlineData("https://HuggingFace.CO/org/repo/resolve/main/f.gguf")]
    [InlineData("https://hf.co/org/repo/resolve/main/f.gguf")]
    [InlineData("https://HF.co/org/repo/resolve/main/f.gguf")]
    public void WhenHostIsHuggingFaceInAnyCasingThenNormalizeAppendsDownloadFlag(string url)
    {
        HuggingFaceUrl.NormalizeResolveUrl(url).Should().EndWith("?download=true");
    }

    [Fact]
    public void WhenHuggingFaceUrlIsPaddedThenNormalizeTrimsBeforeRewriting()
    {
        HuggingFaceUrl.NormalizeResolveUrl("  https://huggingface.co/org/repo/blob/main/f.gguf  ")
            .Should().Be("https://huggingface.co/org/repo/resolve/main/f.gguf?download=true");
    }

    // ---------------------------------------------------------------
    //  NormalizeResolveUrl — blob/raw rewriting
    // ---------------------------------------------------------------

    [Fact]
    public void WhenUrlUsesBlobThenNormalizeRewritesItToResolve()
    {
        HuggingFaceUrl.NormalizeResolveUrl("https://huggingface.co/org/repo/blob/main/model.safetensors")
            .Should().Be("https://huggingface.co/org/repo/resolve/main/model.safetensors?download=true");
    }

    [Fact]
    public void WhenUrlUsesRawThenNormalizeRewritesItToResolve()
    {
        HuggingFaceUrl.NormalizeResolveUrl("https://huggingface.co/org/repo/raw/main/config.json")
            .Should().Be("https://huggingface.co/org/repo/resolve/main/config.json?download=true");
    }

    [Fact]
    public void WhenBlobSegmentIsUpperCaseThenNormalizeStillRewritesIt()
    {
        HuggingFaceUrl.NormalizeResolveUrl("https://huggingface.co/org/repo/BLOB/main/model.safetensors")
            .Should().Be("https://huggingface.co/org/repo/resolve/main/model.safetensors?download=true");
    }

    // ---------------------------------------------------------------
    //  NormalizeResolveUrl — download=true placement
    // ---------------------------------------------------------------

    [Fact]
    public void WhenUrlHasNoQueryThenNormalizeAppendsDownloadFlagWithQuestionMark()
    {
        HuggingFaceUrl.NormalizeResolveUrl("https://huggingface.co/org/repo/resolve/main/f.gguf")
            .Should().Be("https://huggingface.co/org/repo/resolve/main/f.gguf?download=true");
    }

    [Fact]
    public void WhenUrlAlreadyHasAQueryThenNormalizeAppendsDownloadFlagWithAmpersand()
    {
        HuggingFaceUrl.NormalizeResolveUrl("https://huggingface.co/org/repo/resolve/main/f.gguf?revision=abc")
            .Should().Be("https://huggingface.co/org/repo/resolve/main/f.gguf?revision=abc&download=true");
    }

    [Fact]
    public void WhenUrlAlreadyHasDownloadFlagThenNormalizeDoesNotDuplicateIt()
    {
        const string url = "https://huggingface.co/org/repo/resolve/main/f.gguf?download=true";

        HuggingFaceUrl.NormalizeResolveUrl(url).Should().Be(url);
    }

    [Fact]
    public void WhenDownloadFlagUsesDifferentCasingThenNormalizeDoesNotAppendASecondOne()
    {
        const string url = "https://huggingface.co/org/repo/resolve/main/f.gguf?Download=True";

        HuggingFaceUrl.NormalizeResolveUrl(url).Should().Be(url);
    }

    [Fact]
    public void WhenBlobUrlAlreadyHasDownloadFlagThenNormalizeRewritesPathOnly()
    {
        HuggingFaceUrl.NormalizeResolveUrl("https://huggingface.co/org/repo/blob/main/f.gguf?download=true")
            .Should().Be("https://huggingface.co/org/repo/resolve/main/f.gguf?download=true");
    }

    [Fact]
    public void WhenResolveSegmentIsUpperCaseThenNormalizeStillAppendsDownloadFlag()
    {
        HuggingFaceUrl.NormalizeResolveUrl("https://huggingface.co/org/repo/RESOLVE/main/f.gguf")
            .Should().Be("https://huggingface.co/org/repo/RESOLVE/main/f.gguf?download=true");
    }

    [Fact]
    public void WhenHuggingFaceUrlHasNoResolveSegmentThenNormalizeLeavesItAlone()
    {
        // A model landing page is not a file download — no download flag should be added.
        const string url = "https://huggingface.co/org/repo";

        HuggingFaceUrl.NormalizeResolveUrl(url).Should().Be(url);
    }

    [Fact]
    public void WhenHuggingFaceTreeUrlIsGivenThenNormalizeLeavesItAlone()
    {
        const string url = "https://huggingface.co/org/repo/tree/main";

        HuggingFaceUrl.NormalizeResolveUrl(url).Should().Be(url);
    }

    // ---------------------------------------------------------------
    //  NormalizeResolveUrl — idempotency
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("https://huggingface.co/org/repo/blob/main/f.gguf")]
    [InlineData("https://huggingface.co/org/repo/raw/main/config.json")]
    [InlineData("https://huggingface.co/org/repo/resolve/main/f.gguf?revision=abc")]
    [InlineData("https://huggingface.co/org/repo")]
    [InlineData("https://civitai.com/api/download/models/1")]
    public void WhenNormalizeIsAppliedTwiceThenTheResultIsUnchanged(string url)
    {
        var once = HuggingFaceUrl.NormalizeResolveUrl(url);

        HuggingFaceUrl.NormalizeResolveUrl(once).Should().Be(once);
    }

    // ---------------------------------------------------------------
    //  Cross-method behavior
    // ---------------------------------------------------------------

    [Fact]
    public void WhenNormalizedUrlIsFedToGetFileNameThenTheDownloadFlagDoesNotLeakIntoTheName()
    {
        var normalized = HuggingFaceUrl.NormalizeResolveUrl(
            "https://huggingface.co/org/repo/blob/main/flux-2-klein-9b-Q4_K_M.gguf");

        HuggingFaceUrl.GetFileName(normalized).Should().Be("flux-2-klein-9b-Q4_K_M.gguf");
    }
}
