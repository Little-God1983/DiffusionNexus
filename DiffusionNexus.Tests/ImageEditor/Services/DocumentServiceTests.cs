using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor.Services;

/// <summary>
/// Unit tests for the pure helpers on <see cref="DocumentService"/>:
/// extension → <see cref="SKEncodedImageFormat"/> mapping and the
/// <c>_edited_NNN</c> unique-path generator (including its 999-attempt cap).
/// <see cref="DocumentService.Save"/> is not covered here — it needs a real bitmap encode.
/// </summary>
public class DocumentServiceTests : IDisposable
{
    private readonly DocumentService _sut = new();
    private readonly DirectoryInfo _tempDir;

    public DocumentServiceTests()
    {
        _tempDir = Directory.CreateTempSubdirectory();
    }

    public void Dispose()
    {
        try { _tempDir.Delete(recursive: true); }
        catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    private string Existing(string fileName)
    {
        var path = Path.Combine(_tempDir.FullName, fileName);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    #region GetFormatFromExtension

    [Theory]
    [InlineData("photo.jpg", SKEncodedImageFormat.Jpeg)]
    [InlineData("photo.jpeg", SKEncodedImageFormat.Jpeg)]
    [InlineData("photo.png", SKEncodedImageFormat.Png)]
    [InlineData("photo.webp", SKEncodedImageFormat.Webp)]
    [InlineData("photo.bmp", SKEncodedImageFormat.Bmp)]
    [InlineData("photo.gif", SKEncodedImageFormat.Gif)]
    public void WhenExtensionIsSupportedThenTheMatchingFormatIsReturned(
        string fileName, SKEncodedImageFormat expected)
    {
        _sut.GetFormatFromExtension(fileName).Should().Be(expected);
    }

    [Theory]
    [InlineData("PHOTO.JPG", SKEncodedImageFormat.Jpeg)]
    [InlineData("PHOTO.JPEG", SKEncodedImageFormat.Jpeg)]
    [InlineData("PHOTO.PNG", SKEncodedImageFormat.Png)]
    [InlineData("Photo.WebP", SKEncodedImageFormat.Webp)]
    [InlineData("Photo.Bmp", SKEncodedImageFormat.Bmp)]
    [InlineData("Photo.GIF", SKEncodedImageFormat.Gif)]
    public void WhenExtensionCasingVariesThenTheMatchingFormatIsStillReturned(
        string fileName, SKEncodedImageFormat expected)
    {
        _sut.GetFormatFromExtension(fileName).Should().Be(expected);
    }

    [Theory]
    [InlineData("photo.tiff")]
    [InlineData("photo.avif")]
    [InlineData("archive.zip")]
    [InlineData("noextension")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("trailing.")]
    public void WhenExtensionIsUnknownThenPngIsTheFallback(string fileName)
    {
        _sut.GetFormatFromExtension(fileName).Should().Be(SKEncodedImageFormat.Png);
    }

    [Fact]
    public void WhenGivenAFullPathThenOnlyTheExtensionIsConsidered()
    {
        var path = Path.Combine(_tempDir.FullName, "nested.png.d", "image.webp");

        _sut.GetFormatFromExtension(path).Should().Be(SKEncodedImageFormat.Webp);
    }

    #endregion

    #region GenerateUniqueFilePath

    [Fact]
    public void WhenNoFileExistsThenTheFirstCandidateIsReturned()
    {
        var path = _sut.GenerateUniqueFilePath(_tempDir.FullName, "photo", ".png");

        path.Should().Be(Path.Combine(_tempDir.FullName, "photo_edited_001.png"));
    }

    [Fact]
    public void WhenTheFirstCandidateExistsThenTheCounterAdvances()
    {
        Existing("photo_edited_001.png");

        var path = _sut.GenerateUniqueFilePath(_tempDir.FullName, "photo", ".png");

        path.Should().Be(Path.Combine(_tempDir.FullName, "photo_edited_002.png"));
    }

    [Fact]
    public void WhenSeveralCandidatesExistThenTheFirstFreeSlotIsReturned()
    {
        Existing("photo_edited_001.png");
        Existing("photo_edited_002.png");
        Existing("photo_edited_003.png");

        var path = _sut.GenerateUniqueFilePath(_tempDir.FullName, "photo", ".png");

        path.Should().Be(Path.Combine(_tempDir.FullName, "photo_edited_004.png"));
    }

    [Fact]
    public void WhenAGapExistsThenTheGapIsReusedRatherThanAppending()
    {
        Existing("photo_edited_001.png");
        Existing("photo_edited_003.png");

        var path = _sut.GenerateUniqueFilePath(_tempDir.FullName, "photo", ".png");

        path.Should().Be(Path.Combine(_tempDir.FullName, "photo_edited_002.png"));
    }

    [Fact]
    public void WhenTakenNamesBelongToAnotherBaseNameThenTheyDoNotAffectTheResult()
    {
        Existing("other_edited_001.png");

        var path = _sut.GenerateUniqueFilePath(_tempDir.FullName, "photo", ".png");

        path.Should().Be(Path.Combine(_tempDir.FullName, "photo_edited_001.png"));
    }

    [Fact]
    public void WhenTakenNamesUseAnotherExtensionThenTheyDoNotAffectTheResult()
    {
        Existing("photo_edited_001.jpg");

        var path = _sut.GenerateUniqueFilePath(_tempDir.FullName, "photo", ".png");

        path.Should().Be(Path.Combine(_tempDir.FullName, "photo_edited_001.png"));
    }

    [Fact]
    public void WhenCounterIsBelowOneThousandThenTheSuffixIsZeroPaddedToThreeDigits()
    {
        for (var i = 1; i <= 9; i++)
        {
            Existing($"photo_edited_{i:D3}.png");
        }

        var path = _sut.GenerateUniqueFilePath(_tempDir.FullName, "photo", ".png");

        path.Should().Be(Path.Combine(_tempDir.FullName, "photo_edited_010.png"));
    }

    [Fact]
    public void WhenEveryCandidateUpToNineNineNineIsTakenThenAnIOExceptionIsThrownInsteadOfReturningAnOccupiedPath()
    {
        // All 999 numbered slots are taken, so there is no unoccupied path left to hand
        // back. Returning "_edited_999" anyway would silently overwrite that file, so the
        // method must fail loudly instead of returning a path that already exists.
        var dir = Path.Combine(_tempDir.FullName, "full");
        Directory.CreateDirectory(dir);
        for (var i = 1; i <= 999; i++)
        {
            File.WriteAllBytes(Path.Combine(dir, $"photo_edited_{i:D3}.png"), Array.Empty<byte>());
        }

        var act = () => _sut.GenerateUniqueFilePath(dir, "photo", ".png");

        act.Should().Throw<IOException>();
    }

    [Fact]
    public void WhenNineNineEightOfNineNineNineCandidatesAreTakenThenTheOneFreeSlotIsReturned()
    {
        // One slot short of the cap: the normal "first free suffix" behavior must still
        // apply right up to the boundary, not just far away from it.
        var dir = Path.Combine(_tempDir.FullName, "almost-full");
        Directory.CreateDirectory(dir);
        for (var i = 1; i <= 999; i++)
        {
            if (i == 999) continue;
            File.WriteAllBytes(Path.Combine(dir, $"photo_edited_{i:D3}.png"), Array.Empty<byte>());
        }

        var path = _sut.GenerateUniqueFilePath(dir, "photo", ".png");

        path.Should().Be(Path.Combine(dir, "photo_edited_999.png"));
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void WhenTargetDirectoryDoesNotExistThenThePathIsStillComposed()
    {
        var missing = Path.Combine(_tempDir.FullName, "not-created-yet");

        var path = _sut.GenerateUniqueFilePath(missing, "photo", ".png");

        path.Should().Be(Path.Combine(missing, "photo_edited_001.png"));
    }

    #endregion
}
