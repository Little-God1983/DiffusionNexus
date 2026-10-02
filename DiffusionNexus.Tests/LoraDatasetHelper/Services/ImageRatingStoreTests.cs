using System.Text.Json;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.LoraDatasetHelper.Services;

/// <summary>
/// Unit tests for <see cref="ImageRatingStore"/>: one <c>.ratings.json</c> per folder, the
/// fallback to the legacy per-image <c>.rating</c> files, and the folder conversion that the
/// first rating change in a folder performs (issue #317).
/// </summary>
public class ImageRatingStoreTests : IDisposable
{
    private readonly string _folder;
    private readonly ImageRatingStore _sut = new();

    public ImageRatingStoreTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"ImageRatingStoreTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string Media(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private string Legacy(string baseName, string content)
    {
        var path = Path.Combine(_folder, baseName + ".rating");
        File.WriteAllText(path, content);
        return path;
    }

    private string RatingsFile => Path.Combine(_folder, ImageRatingStore.RatingsFileName);

    private Dictionary<string, string> ReadRatingsFile()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RatingsFile));
        return doc.RootElement.GetProperty("ratings").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    #region Get

    [Fact]
    public void Get_NoFiles_ReturnsUnrated()
    {
        var image = Media("a.png");

        _sut.Get(image).Should().Be(ImageRatingStatus.Unrated);
    }

    [Fact]
    public void Get_EntryInRatingsFile_ReturnsIt()
    {
        var image = Media("a.png");
        _sut.Set(image, ImageRatingStatus.Approved);

        new ImageRatingStore().Get(image).Should().Be(ImageRatingStatus.Approved);
    }

    [Fact]
    public void Get_LegacyFileOnly_FallsBackToIt()
    {
        var image = Media("a.png");
        Legacy("a", "Rejected");

        _sut.Get(image).Should().Be(ImageRatingStatus.Rejected);
    }

    [Fact]
    public void Get_LegacyFileOnly_DoesNotConvertTheFolder()
    {
        var image = Media("a.png");
        var legacy = Legacy("a", "Approved");

        _sut.Get(image);

        File.Exists(legacy).Should().BeTrue();
        File.Exists(RatingsFile).Should().BeFalse();
    }

    [Fact]
    public void Get_RatingsFileEntryWinsOverLegacyFile()
    {
        var image = Media("a.png");
        File.WriteAllText(RatingsFile, """{ "version": 1, "ratings": { "a.png": "Approved" } }""");
        Legacy("a", "Rejected");

        _sut.Get(image).Should().Be(ImageRatingStatus.Approved);
    }

    [Fact]
    public void Get_FileNameMatchIsCaseInsensitive()
    {
        var image = Media("a.png");
        File.WriteAllText(RatingsFile, """{ "version": 1, "ratings": { "A.PNG": "Rejected" } }""");

        _sut.Get(image).Should().Be(ImageRatingStatus.Rejected);
    }

    [Fact]
    public void Get_SameBaseNameDifferentExtension_AreRatedSeparately()
    {
        var png = Media("a.png");
        var jpg = Media("a.jpg");

        _sut.Set(png, ImageRatingStatus.Approved);
        _sut.Set(jpg, ImageRatingStatus.Rejected);

        _sut.Get(png).Should().Be(ImageRatingStatus.Approved);
        _sut.Get(jpg).Should().Be(ImageRatingStatus.Rejected);
    }

    [Fact]
    public void Get_RatingsFileChangedOnDisk_IsReread()
    {
        var image = Media("a.png");
        _sut.Set(image, ImageRatingStatus.Approved);
        _sut.Get(image).Should().Be(ImageRatingStatus.Approved);

        // Another writer (a second app instance, a restored backup) replaces the file.
        File.WriteAllText(RatingsFile, """{ "version": 1, "ratings": { "a.png": "Rejected" } }""");
        File.SetLastWriteTimeUtc(RatingsFile, DateTime.UtcNow.AddMinutes(5));

        _sut.Get(image).Should().Be(ImageRatingStatus.Rejected);
    }

    [Fact]
    public void Get_CorruptRatingsFile_FallsBackToLegacy()
    {
        var image = Media("a.png");
        File.WriteAllText(RatingsFile, "{ not json");
        Legacy("a", "Approved");

        _sut.Get(image).Should().Be(ImageRatingStatus.Approved);
    }

    [Fact]
    public void Get_UnknownValueInRatingsFile_IsUnrated()
    {
        var image = Media("a.png");
        File.WriteAllText(RatingsFile, """{ "version": 1, "ratings": { "a.png": "Maybe" } }""");

        _sut.Get(image).Should().Be(ImageRatingStatus.Unrated);
    }

    #endregion

    #region Set

    [Fact]
    public void Set_WritesOneFilePerFolder_AndNoLegacyFile()
    {
        var a = Media("a.png");
        var b = Media("b.png");

        _sut.Set(a, ImageRatingStatus.Approved);
        _sut.Set(b, ImageRatingStatus.Rejected);

        ReadRatingsFile().Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["a.png"] = "Approved",
            ["b.png"] = "Rejected",
        });
        Directory.GetFiles(_folder, "*.rating").Should().BeEmpty();
    }

    [Fact]
    public void Set_Unrated_RemovesTheEntry()
    {
        var a = Media("a.png");
        var b = Media("b.png");
        _sut.Set(a, ImageRatingStatus.Approved);
        _sut.Set(b, ImageRatingStatus.Approved);

        _sut.Set(a, ImageRatingStatus.Unrated);

        ReadRatingsFile().Keys.Should().BeEquivalentTo(["b.png"]);
        _sut.Get(a).Should().Be(ImageRatingStatus.Unrated);
    }

    [Fact]
    public void Set_LastEntryCleared_DeletesTheRatingsFile()
    {
        var a = Media("a.png");
        _sut.Set(a, ImageRatingStatus.Approved);

        _sut.Set(a, ImageRatingStatus.Unrated);

        File.Exists(RatingsFile).Should().BeFalse();
    }

    [Fact]
    public void Set_UnratedInUntouchedFolder_CreatesNothing()
    {
        var a = Media("a.png");

        _sut.Set(a, ImageRatingStatus.Unrated).Should().BeTrue();

        File.Exists(RatingsFile).Should().BeFalse();
    }

    [Fact]
    public void Set_FolderWithLegacyFiles_ConvertsTheWholeFolder()
    {
        var a = Media("a.png");
        var b = Media("b.png");
        var c = Media("c.png");
        Legacy("b", "Approved");
        Legacy("c", "Rejected");

        _sut.Set(a, ImageRatingStatus.Approved);

        ReadRatingsFile().Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["a.png"] = "Approved",
            ["b.png"] = "Approved",
            ["c.png"] = "Rejected",
        });
        Directory.GetFiles(_folder, "*.rating").Should().BeEmpty();
        _sut.Get(b).Should().Be(ImageRatingStatus.Approved);
        _sut.Get(c).Should().Be(ImageRatingStatus.Rejected);
    }

    [Fact]
    public void Set_UnratedOnAnImageWithoutRating_DoesNotConvertTheFolder()
    {
        var a = Media("a.png");
        Media("b.png");
        var legacy = Legacy("b", "Approved");

        _sut.Set(a, ImageRatingStatus.Unrated).Should().BeTrue();

        File.Exists(legacy).Should().BeTrue("nothing about the folder's ratings changed");
        File.Exists(RatingsFile).Should().BeFalse();
    }

    [Fact]
    public void Set_ConversionAppliesALegacyRatingToEveryImageWithThatBaseName()
    {
        var png = Media("x.png");
        var jpg = Media("x.jpg");
        var other = Media("other.png");
        Legacy("x", "Rejected");

        _sut.Set(other, ImageRatingStatus.Approved);

        _sut.Get(png).Should().Be(ImageRatingStatus.Rejected);
        _sut.Get(jpg).Should().Be(ImageRatingStatus.Rejected);
    }

    [Fact]
    public void Set_ConversionThenChangeOfTheSameImage_KeepsTheNewValue()
    {
        var a = Media("a.png");
        Legacy("a", "Rejected");

        _sut.Set(a, ImageRatingStatus.Approved);

        _sut.Get(a).Should().Be(ImageRatingStatus.Approved);
        File.Exists(Path.Combine(_folder, "a.rating")).Should().BeFalse();
    }

    [Fact]
    public void Set_ClearingAConvertedRating_DoesNotComeBackFromALegacyFile()
    {
        var a = Media("a.png");
        Legacy("a", "Rejected");

        _sut.Set(a, ImageRatingStatus.Unrated);

        _sut.Get(a).Should().Be(ImageRatingStatus.Unrated);
        new ImageRatingStore().Get(a).Should().Be(ImageRatingStatus.Unrated);
    }

    [Fact]
    public void Set_ConversionDropsLegacyFilesWithoutAnImage()
    {
        var a = Media("a.png");
        var orphan = Legacy("gone", "Approved");

        _sut.Set(a, ImageRatingStatus.Approved);

        File.Exists(orphan).Should().BeFalse();
        ReadRatingsFile().Keys.Should().BeEquivalentTo(["a.png"]);
    }

    [Fact]
    public void Set_ConversionIgnoresUnparseableLegacyFiles()
    {
        var a = Media("a.png");
        var b = Media("b.png");
        Legacy("b", "garbage");

        _sut.Set(a, ImageRatingStatus.Approved);

        _sut.Get(b).Should().Be(ImageRatingStatus.Unrated);
    }

    [Fact]
    public void Set_WriteFails_KeepsTheLegacyFilesAndReportsFailure()
    {
        var a = Media("a.png");
        var b = Media("b.png");
        var legacy = Legacy("b", "Approved");
        // A directory where the ratings file should go makes every write fail.
        Directory.CreateDirectory(RatingsFile);

        var result = _sut.Set(a, ImageRatingStatus.Approved);

        result.Should().BeFalse();
        File.Exists(legacy).Should().BeTrue();
        _sut.Get(b).Should().Be(ImageRatingStatus.Approved);
    }

    [Fact]
    public void Set_CorruptRatingsFile_IsSetAsideNotSilentlyOverwritten()
    {
        var a = Media("a.png");
        File.WriteAllText(RatingsFile, "{ not json");

        _sut.Set(a, ImageRatingStatus.Approved);

        File.ReadAllText(RatingsFile + ".bad").Should().Be("{ not json");
        ReadRatingsFile().Keys.Should().BeEquivalentTo(["a.png"]);
    }

    [Fact]
    public void Set_LeavesNoTempFileBehind()
    {
        var a = Media("a.png");

        _sut.Set(a, ImageRatingStatus.Approved);

        Directory.GetFiles(_folder).Select(Path.GetFileName)
            .Should().BeEquivalentTo(["a.png", ImageRatingStore.RatingsFileName]);
    }

    #endregion

    #region Remove / Copy / Move

    [Fact]
    public void Remove_DropsTheEntry()
    {
        var a = Media("a.png");
        var b = Media("b.png");
        _sut.Set(a, ImageRatingStatus.Approved);
        _sut.Set(b, ImageRatingStatus.Approved);

        _sut.Remove(a);

        ReadRatingsFile().Keys.Should().BeEquivalentTo(["b.png"]);
    }

    [Fact]
    public void Remove_DeletesTheImagesLegacyFile_WithoutConvertingTheFolder()
    {
        var a = Media("a.png");
        var b = Media("b.png");
        var legacyA = Legacy("a", "Rejected");
        var legacyB = Legacy("b", "Approved");
        File.Delete(a); // the caller deletes the image, then removes its rating

        _sut.Remove(a);

        File.Exists(legacyA).Should().BeFalse();
        File.Exists(legacyB).Should().BeTrue();
        File.Exists(RatingsFile).Should().BeFalse();
    }

    [Fact]
    public void Remove_KeepsALegacyFileStillSharedByAnotherImage()
    {
        var png = Media("x.png");
        var jpg = Media("x.jpg");
        var legacy = Legacy("x", "Approved");
        File.Delete(png);

        _sut.Remove(png);

        File.Exists(legacy).Should().BeTrue();
        _sut.Get(jpg).Should().Be(ImageRatingStatus.Approved);
    }

    [Fact]
    public void Copy_CarriesTheRatingIntoAnotherFolder()
    {
        var a = Media("a.png");
        _sut.Set(a, ImageRatingStatus.Rejected);
        var destFolder = Path.Combine(_folder, "V2");
        Directory.CreateDirectory(destFolder);
        var dest = Path.Combine(destFolder, "a.png");
        File.WriteAllBytes(dest, [1]);

        _sut.Copy(a, dest);

        _sut.Get(dest).Should().Be(ImageRatingStatus.Rejected);
        _sut.Get(a).Should().Be(ImageRatingStatus.Rejected);
    }

    [Fact]
    public void Copy_FromALegacyRating_WritesTheNewFormatAtTheDestination()
    {
        var a = Media("a.png");
        Legacy("a", "Approved");
        var destFolder = Path.Combine(_folder, "V2");
        Directory.CreateDirectory(destFolder);
        var dest = Path.Combine(destFolder, "a.png");
        File.WriteAllBytes(dest, [1]);

        _sut.Copy(a, dest);

        File.Exists(Path.Combine(destFolder, ImageRatingStore.RatingsFileName)).Should().BeTrue();
        File.Exists(Path.Combine(destFolder, "a.rating")).Should().BeFalse();
        _sut.Get(dest).Should().Be(ImageRatingStatus.Approved);
    }

    [Fact]
    public void Copy_UnratedSource_ClearsAStaleDestinationRating()
    {
        var a = Media("a.png");
        var b = Media("b.png");
        _sut.Set(b, ImageRatingStatus.Rejected);

        _sut.Copy(a, b);

        _sut.Get(b).Should().Be(ImageRatingStatus.Unrated);
    }

    [Fact]
    public void Move_CarriesTheRatingAndRemovesTheSourceEntry()
    {
        var a = Media("a.png");
        var keep = Media("keep.png");
        _sut.Set(a, ImageRatingStatus.Approved);
        _sut.Set(keep, ImageRatingStatus.Approved);
        var dest = Path.Combine(_folder, "renamed.png");
        File.Move(a, dest);

        _sut.Move(a, dest);

        _sut.Get(dest).Should().Be(ImageRatingStatus.Approved);
        ReadRatingsFile().Keys.Should().BeEquivalentTo(["keep.png", "renamed.png"]);
    }

    #endregion
}
