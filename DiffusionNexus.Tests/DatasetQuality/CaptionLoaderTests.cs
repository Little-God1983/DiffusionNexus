using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Service.Services.DatasetQuality;
using FluentAssertions;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Unit tests for <see cref="CaptionLoader"/>.
/// </summary>
public class CaptionLoaderTests : IDisposable
{
    private readonly string _testFolder;
    private readonly CaptionLoader _sut;

    public CaptionLoaderTests()
    {
        _testFolder = Path.Combine(
            Path.GetTempPath(),
            "DiffusionNexus_Tests",
            $"CaptionLoader_{Guid.NewGuid():N}");

        Directory.CreateDirectory(_testFolder);
        _sut = new CaptionLoader();
    }

    public void Dispose()
    {
        // Best-effort cleanup; tests should not depend on this
        try { Directory.Delete(_testFolder, recursive: true); } catch { /* intentional */ }
    }

    #region Load — Happy Path

    [Fact]
    public void WhenFolderContainsPairedCaptionAndImageThenBothArePaired()
    {
        // Arrange
        CreateFile("photo1.png", "imagedata");
        CreateFile("photo1.txt", "a woman standing in a park.");

        // Act
        var (captions, imageCount) = _sut.Load(_testFolder);

        // Assert
        captions.Should().HaveCount(1);
        imageCount.Should().Be(1);

        var caption = captions[0];
        caption.BaseName.Should().Be("photo1");
        caption.RawText.Should().Be("a woman standing in a park.");
        caption.PairedImagePath.Should().EndWith("photo1.png");
    }

    [Fact]
    public void WhenCaptionHasNoMatchingImageThenPairedImagePathIsNull()
    {
        // Arrange
        CreateFile("orphan.txt", "some tags");

        // Act
        var (captions, imageCount) = _sut.Load(_testFolder);

        // Assert
        captions.Should().HaveCount(1);
        captions[0].PairedImagePath.Should().BeNull();
        imageCount.Should().Be(0);
    }

    [Fact]
    public void WhenFolderContainsOnlyImagesThenNoCaptionsAreReturned()
    {
        // Arrange
        CreateFile("img1.jpg", "data");
        CreateFile("img2.webp", "data");

        // Act
        var (captions, imageCount) = _sut.Load(_testFolder);

        // Assert
        captions.Should().BeEmpty();
        imageCount.Should().Be(2);
    }

    [Fact]
    public void WhenMultiplePairsExistThenAllAreLoaded()
    {
        // Arrange
        CreateFile("a.png", "img");
        CreateFile("a.txt", "caption a");
        CreateFile("b.jpg", "img");
        CreateFile("b.txt", "caption b");
        CreateFile("c.webp", "img");
        CreateFile("c.caption", "caption c");

        // Act
        var (captions, imageCount) = _sut.Load(_testFolder);

        // Assert
        captions.Should().HaveCount(3);
        imageCount.Should().Be(3);
        captions.Should().AllSatisfy(c => c.PairedImagePath.Should().NotBeNull());
    }

    [Fact]
    public void WhenFolderIsEmptyThenReturnsEmptyResults()
    {
        // Act
        var (captions, imageCount) = _sut.Load(_testFolder);

        // Assert
        captions.Should().BeEmpty();
        imageCount.Should().Be(0);
    }

    #endregion

    #region Load — Edge Cases & Validation

    [Fact]
    public void WhenFolderPathIsEmptyThenThrowsArgumentException()
    {
        // Act
        var act = () => _sut.Load("");

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WhenFolderPathIsNullThenThrowsArgumentException()
    {
        // Act
        var act = () => _sut.Load(null!);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WhenFolderDoesNotExistThenThrowsDirectoryNotFoundException()
    {
        // Act
        var act = () => _sut.Load(Path.Combine(_testFolder, "nonexistent"));

        // Assert
        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void WhenNonMediaFilesExistThenTheyAreIgnored()
    {
        // Arrange
        CreateFile("readme.md", "# readme");
        CreateFile("config.json", "{}");
        CreateFile("photo.txt", "caption");
        CreateFile("photo.png", "img");

        // Act
        var (captions, imageCount) = _sut.Load(_testFolder);

        // Assert
        captions.Should().HaveCount(1);
        imageCount.Should().Be(1);
    }

    #endregion

    #region DetectCaptionStyle (delegates to TextHelpers)

    [Fact]
    public void WhenTextIsNaturalLanguageThenReturnsNaturalLanguage()
    {
        var text = "A woman with brown hair standing in a sunlit park, wearing a blue dress.";

        var result = TextHelpers.DetectCaptionStyle(text);

        result.Should().Be(CaptionStyle.NaturalLanguage);
    }

    [Fact]
    public void WhenTextIsBooruTagsThenReturnsBooruTags()
    {
        var text = "1girl, brown hair, blue dress, park, sunlight, standing, solo";

        var result = TextHelpers.DetectCaptionStyle(text);

        result.Should().Be(CaptionStyle.BooruTags);
    }

    [Fact]
    public void WhenTextIsEmptyThenReturnsUnknown()
    {
        var result = TextHelpers.DetectCaptionStyle("");

        result.Should().Be(CaptionStyle.Unknown);
    }

    [Fact]
    public void WhenTextIsWhitespaceThenReturnsUnknown()
    {
        var result = TextHelpers.DetectCaptionStyle("   ");

        result.Should().Be(CaptionStyle.Unknown);
    }

    #endregion

    private void CreateFile(string name, string content) =>
        File.WriteAllText(Path.Combine(_testFolder, name), content);
}
