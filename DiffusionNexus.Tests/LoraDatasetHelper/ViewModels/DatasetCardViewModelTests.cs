using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.LoraDatasetHelper.ViewModels;

/// <summary>
/// Unit tests for <see cref="DatasetCardViewModel"/>.
/// Tests CreateVersionCard, version operations, metadata, and file type detection.
/// </summary>
public class DatasetCardViewModelTests : IDisposable
{
    private readonly string _testTempPath;

    public DatasetCardViewModelTests()
    {
        _testTempPath = Path.Combine(Path.GetTempPath(), $"DatasetCardTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempPath);
    }

    public void Dispose()
    {
        // Clean up test directories
        if (Directory.Exists(_testTempPath))
        {
            try
            {
                Directory.Delete(_testTempPath, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors in tests
            }
        }
    }

    #region Constructor / Default Values Tests

    [Fact]
    public void DatasetCardViewModel_HasCorrectDefaults()
    {
        // Act
        var vm = new DatasetCardViewModel();

        // Assert
        vm.Name.Should().BeEmpty();
        vm.FolderPath.Should().BeEmpty();
        vm.ImageCount.Should().Be(0);
        vm.VideoCount.Should().Be(0);
        vm.CaptionCount.Should().Be(0);
        vm.CurrentVersion.Should().Be(1);
        vm.TotalVersions.Should().Be(1);
        vm.IsVersionedStructure.Should().BeFalse();
        vm.CategoryId.Should().BeNull();
        vm.Type.Should().BeNull();
    }

    #endregion

    #region Name Property Tests

    [Fact]
    public void Name_WhenSet_NotifiesPropertyChanged()
    {
        // Arrange
        var vm = new DatasetCardViewModel();
        var propertyChangedNames = new List<string>();
        vm.PropertyChanged += (_, e) => propertyChangedNames.Add(e.PropertyName!);

        // Act
        vm.Name = "TestDataset";

        // Assert
        propertyChangedNames.Should().Contain(nameof(DatasetCardViewModel.Name));
    }

    [Fact]
    public void ToString_ReturnsName()
    {
        // Arrange
        var vm = new DatasetCardViewModel { Name = "MyDataset" };

        // Assert
        vm.ToString().Should().Be("MyDataset");
    }

    #endregion

    #region Count Properties Tests

    [Fact]
    public void ImageCount_WhenSet_UpdatesRelatedProperties()
    {
        // Arrange
        var vm = new DatasetCardViewModel();
        var propertyChangedNames = new List<string>();
        vm.PropertyChanged += (_, e) => propertyChangedNames.Add(e.PropertyName!);

        // Act
        vm.ImageCount = 10;

        // Assert
        propertyChangedNames.Should().Contain(nameof(DatasetCardViewModel.ImageCount));
        propertyChangedNames.Should().Contain(nameof(DatasetCardViewModel.ImageCountText));
        propertyChangedNames.Should().Contain(nameof(DatasetCardViewModel.MediaCountText));
        propertyChangedNames.Should().Contain(nameof(DatasetCardViewModel.TotalMediaCount));
    }

    [Theory]
    [InlineData(0, "0 images")]
    [InlineData(1, "1 image")]
    [InlineData(2, "2 images")]
    [InlineData(100, "100 images")]
    public void ImageCountText_FormatsCorrectly(int count, string expected)
    {
        var vm = new DatasetCardViewModel { ImageCount = count };
        vm.ImageCountText.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, "0 videos")]
    [InlineData(1, "1 video")]
    [InlineData(2, "2 videos")]
    [InlineData(50, "50 videos")]
    public void VideoCountText_FormatsCorrectly(int count, string expected)
    {
        var vm = new DatasetCardViewModel { VideoCount = count };
        vm.VideoCountText.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, "0 captions")]
    [InlineData(1, "1 caption")]
    [InlineData(2, "2 captions")]
    public void CaptionCountText_FormatsCorrectly(int count, string expected)
    {
        var vm = new DatasetCardViewModel { CaptionCount = count };
        vm.CaptionCountText.Should().Be(expected);
    }

    [Fact]
    public void TotalMediaCount_ReturnsSumOfImagesAndVideos()
    {
        // Arrange
        var vm = new DatasetCardViewModel { ImageCount = 10, VideoCount = 5 };

        // Assert
        vm.TotalMediaCount.Should().Be(15);
    }

    [Fact]
    public void HasVideos_WhenVideoCountGreaterThanZero_ReturnsTrue()
    {
        var vm = new DatasetCardViewModel { VideoCount = 1 };
        vm.HasVideos.Should().BeTrue();
    }

    [Fact]
    public void HasVideos_WhenVideoCountIsZero_ReturnsFalse()
    {
        var vm = new DatasetCardViewModel { VideoCount = 0 };
        vm.HasVideos.Should().BeFalse();
    }

    #endregion

    #region MediaCountText Tests

    [Fact]
    public void MediaCountText_WhenOnlyImages_ReturnsImageText()
    {
        var vm = new DatasetCardViewModel { ImageCount = 10, VideoCount = 0 };
        vm.MediaCountText.Should().Be("10 images");
    }

    [Fact]
    public void MediaCountText_WhenOnlyVideos_ReturnsVideoText()
    {
        var vm = new DatasetCardViewModel { ImageCount = 0, VideoCount = 5 };
        vm.MediaCountText.Should().Be("5 videos");
    }

    [Fact]
    public void MediaCountText_WhenBothImagesAndVideos_ReturnsCombinedText()
    {
        var vm = new DatasetCardViewModel { ImageCount = 10, VideoCount = 5 };
        vm.MediaCountText.Should().Be("10 images, 5 videos");
    }

    #endregion

    #region DetailedCountText Tests

    [Fact]
    public void DetailedCountText_WhenEmpty_ReturnsEmpty()
    {
        var vm = new DatasetCardViewModel { ImageCount = 0, VideoCount = 0, CaptionCount = 0 };
        vm.DetailedCountText.Should().Be("Empty");
    }

    [Fact]
    public void DetailedCountText_WhenOnlyImages_ReturnsImageText()
    {
        var vm = new DatasetCardViewModel { ImageCount = 5, VideoCount = 0, CaptionCount = 0 };
        vm.DetailedCountText.Should().Be("5 Images");
    }

    [Fact]
    public void DetailedCountText_WhenAllTypes_ReturnsCombinedText()
    {
        var vm = new DatasetCardViewModel { ImageCount = 10, VideoCount = 3, CaptionCount = 8 };
        vm.DetailedCountText.Should().Be("10 Images; 3 Videos; 8 Captions");
    }

    [Fact]
    public void DetailedCountText_UsesSingularForms()
    {
        var vm = new DatasetCardViewModel { ImageCount = 1, VideoCount = 1, CaptionCount = 1 };
        vm.DetailedCountText.Should().Be("1 Image; 1 Video; 1 Caption");
    }

    #endregion

    #region Version Properties Tests

    [Fact]
    public void CurrentVersion_WhenSet_UpdatesRelatedProperties()
    {
        // Arrange
        var vm = new DatasetCardViewModel();
        var propertyChangedNames = new List<string>();
        vm.PropertyChanged += (_, e) => propertyChangedNames.Add(e.PropertyName!);

        // Act
        vm.CurrentVersion = 3;

        // Assert
        propertyChangedNames.Should().Contain(nameof(DatasetCardViewModel.CurrentVersion));
        propertyChangedNames.Should().Contain(nameof(DatasetCardViewModel.VersionDisplayText));
        propertyChangedNames.Should().Contain(nameof(DatasetCardViewModel.CurrentVersionFolderPath));
    }

    [Fact]
    public void VersionDisplayText_WhenSingleVersion_ShowsOnlyVersion()
    {
        var vm = new DatasetCardViewModel { CurrentVersion = 1, TotalVersions = 1 };
        vm.VersionDisplayText.Should().Be("V1");
    }

    [Fact]
    public void VersionDisplayText_WhenMultipleVersions_ShowsVersionOfTotal()
    {
        var vm = new DatasetCardViewModel { CurrentVersion = 2, TotalVersions = 5 };
        vm.VersionDisplayText.Should().Be("V2 of 5");
    }

    [Fact]
    public void HasMultipleVersions_WhenTotalVersionsGreaterThanOne_ReturnsTrue()
    {
        var vm = new DatasetCardViewModel { TotalVersions = 3 };
        vm.HasMultipleVersions.Should().BeTrue();
    }

    [Fact]
    public void HasMultipleVersions_WhenTotalVersionsIsOne_ReturnsFalse()
    {
        var vm = new DatasetCardViewModel { TotalVersions = 1 };
        vm.HasMultipleVersions.Should().BeFalse();
    }

    [Fact]
    public void VersionBadgeText_WhenDisplayVersion_ShowsVersionNumber()
    {
        var vm = new DatasetCardViewModel { DisplayVersion = 2, TotalVersions = 3 };
        vm.VersionBadgeText.Should().Be("V2");
    }

    [Fact]
    public void VersionBadgeText_WhenCollapsedWithMultipleVersions_ShowsVersionCount()
    {
        var vm = new DatasetCardViewModel { DisplayVersion = null, TotalVersions = 3 };
        vm.VersionBadgeText.Should().Be("3 Versions");
    }

    [Fact]
    public void VersionBadgeText_WhenCollapsedWithSingleVersion_IsEmpty()
    {
        var vm = new DatasetCardViewModel { DisplayVersion = null, TotalVersions = 1 };
        vm.VersionBadgeText.Should().BeEmpty();
    }

    [Fact]
    public void ShowVersionBadge_WhenDisplayVersion_ReturnsTrue()
    {
        var vm = new DatasetCardViewModel { DisplayVersion = 1 };
        vm.ShowVersionBadge.Should().BeTrue();
    }

    [Fact]
    public void ShowVersionBadge_WhenMultipleVersions_ReturnsTrue()
    {
        var vm = new DatasetCardViewModel { TotalVersions = 3 };
        vm.ShowVersionBadge.Should().BeTrue();
    }

    [Fact]
    public void ShowVersionBadge_WhenSingleVersionNoDisplay_ReturnsFalse()
    {
        var vm = new DatasetCardViewModel { TotalVersions = 1, DisplayVersion = null };
        vm.ShowVersionBadge.Should().BeFalse();
    }

    [Fact]
    public void IsVersionCard_WhenDisplayVersionSet_ReturnsTrue()
    {
        var vm = new DatasetCardViewModel { DisplayVersion = 1 };
        vm.IsVersionCard.Should().BeTrue();
    }

    [Fact]
    public void IsVersionCard_WhenDisplayVersionNull_ReturnsFalse()
    {
        var vm = new DatasetCardViewModel { DisplayVersion = null };
        vm.IsVersionCard.Should().BeFalse();
    }

    #endregion

    #region Category Tests

    [Fact]
    public void HasCategory_WhenCategoryIdSet_ReturnsTrue()
    {
        var vm = new DatasetCardViewModel { CategoryId = 1 };
        vm.HasCategory.Should().BeTrue();
    }

    [Fact]
    public void HasCategory_WhenCategoryIdNull_ReturnsFalse()
    {
        var vm = new DatasetCardViewModel { CategoryId = null };
        vm.HasCategory.Should().BeFalse();
    }

    [Fact]
    public void CategoryId_WhenChanged_RaisesCategoryChangedEvent()
    {
        // Arrange
        var vm = new DatasetCardViewModel();
        var eventRaised = false;
        vm.CategoryChanged += (_, _) => eventRaised = true;

        // Act
        vm.CategoryId = 1;

        // Assert
        eventRaised.Should().BeTrue();
    }

    #endregion

    #region Type Tests

    [Fact]
    public void HasType_WhenTypeSet_ReturnsTrue()
    {
        var vm = new DatasetCardViewModel { Type = DatasetType.Image };
        vm.HasType.Should().BeTrue();
    }

    [Fact]
    public void HasType_WhenTypeNull_ReturnsFalse()
    {
        var vm = new DatasetCardViewModel { Type = null };
        vm.HasType.Should().BeFalse();
    }

    [Fact]
    public void TypeDisplayName_WhenTypeSet_ReturnsDisplayName()
    {
        var vm = new DatasetCardViewModel { Type = DatasetType.Image };
        vm.TypeDisplayName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TypeDisplayName_WhenTypeNull_ReturnsNull()
    {
        var vm = new DatasetCardViewModel { Type = null };
        vm.TypeDisplayName.Should().BeNull();
    }

    #endregion

    #region Description Tests

    [Fact]
    public void Description_WhenSet_UpdatesVersionDescriptions()
    {
        // Arrange
        var vm = new DatasetCardViewModel { CurrentVersion = 1 };

        // Act
        vm.Description = "Test description";

        // Assert
        vm.Description.Should().Be("Test description");
        vm.HasDescription.Should().BeTrue();
        vm.VersionDescriptions[1].Should().Be("Test description");
    }

    [Fact]
    public void HasDescription_WhenDescriptionEmpty_ReturnsFalse()
    {
        var vm = new DatasetCardViewModel { Description = "" };
        vm.HasDescription.Should().BeFalse();
    }

    [Fact]
    public void HasDescription_WhenDescriptionWhitespace_ReturnsFalse()
    {
        var vm = new DatasetCardViewModel { Description = "   " };
        vm.HasDescription.Should().BeFalse();
    }

    #endregion

    #region Path Tests

    [Fact]
    public void CurrentVersionFolderPath_WhenVersioned_ReturnsVersionPath()
    {
        var vm = new DatasetCardViewModel
        {
            FolderPath = "/dataset",
            IsVersionedStructure = true,
            CurrentVersion = 2
        };
        vm.CurrentVersionFolderPath.Should().Be(Path.Combine("/dataset", "V2"));
    }

    [Fact]
    public void CurrentVersionFolderPath_WhenLegacy_ReturnsRootPath()
    {
        var vm = new DatasetCardViewModel
        {
            FolderPath = "/dataset",
            IsVersionedStructure = false
        };
        vm.CurrentVersionFolderPath.Should().Be("/dataset");
    }

    [Fact]
    public void ConfigFolderPath_ReturnsDatasetConfigPath()
    {
        var vm = new DatasetCardViewModel { FolderPath = "/dataset" };
        vm.ConfigFolderPath.Should().Be(Path.Combine("/dataset", ".dataset"));
    }

    [Fact]
    public void MetadataFilePath_ReturnsConfigJsonPath()
    {
        var vm = new DatasetCardViewModel { FolderPath = "/dataset" };
        vm.MetadataFilePath.Should().Be(Path.Combine("/dataset", ".dataset", "config.json"));
    }

    [Fact]
    public void GetVersionFolderPath_ReturnsCorrectPath()
    {
        var vm = new DatasetCardViewModel { FolderPath = "/dataset" };
        vm.GetVersionFolderPath(3).Should().Be(Path.Combine("/dataset", "V3"));
    }

    #endregion

    #region RecordBranch / GetBranchedFrom Tests

    [Fact]
    public void RecordBranch_StoresVersionRelationship()
    {
        // Arrange
        var vm = new DatasetCardViewModel();

        // Act
        vm.RecordBranch(2, 1);
        vm.RecordBranch(3, 2);

        // Assert
        vm.VersionBranchedFrom[2].Should().Be(1);
        vm.VersionBranchedFrom[3].Should().Be(2);
    }

    [Fact]
    public void GetBranchedFrom_WhenVersionTracked_ReturnsParent()
    {
        // Arrange
        var vm = new DatasetCardViewModel();
        vm.RecordBranch(2, 1);

        // Act
        var result = vm.GetBranchedFrom(2);

        // Assert
        result.Should().Be(1);
    }

    [Fact]
    public void GetBranchedFrom_WhenVersionNotTracked_ReturnsNull()
    {
        // Arrange
        var vm = new DatasetCardViewModel();

        // Act
        var result = vm.GetBranchedFrom(1);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region CanIncrementVersion Tests

    [Fact]
    public void CanIncrementVersion_WhenHasMedia_ReturnsTrue()
    {
        var vm = new DatasetCardViewModel { ImageCount = 1 };
        vm.CanIncrementVersion.Should().BeTrue();
    }

    [Fact]
    public void CanIncrementVersion_WhenHasVideos_ReturnsTrue()
    {
        var vm = new DatasetCardViewModel { VideoCount = 1 };
        vm.CanIncrementVersion.Should().BeTrue();
    }

    [Fact]
    public void CanIncrementVersion_WhenEmpty_ReturnsFalse()
    {
        var vm = new DatasetCardViewModel { ImageCount = 0, VideoCount = 0 };
        vm.CanIncrementVersion.Should().BeFalse();
    }

    #endregion

    #region HasThumbnail Tests

    [Fact]
    public void HasThumbnail_WhenThumbnailPathSet_ReturnsTrue()
    {
        var vm = new DatasetCardViewModel { ThumbnailPath = "/path/to/thumb.png" };
        vm.HasThumbnail.Should().BeTrue();
    }

    [Fact]
    public void HasThumbnail_WhenThumbnailPathEmpty_ReturnsFalse()
    {
        var vm = new DatasetCardViewModel { ThumbnailPath = "" };
        vm.HasThumbnail.Should().BeFalse();
    }

    [Fact]
    public void HasThumbnail_WhenThumbnailPathNull_ReturnsFalse()
    {
        var vm = new DatasetCardViewModel { ThumbnailPath = null };
        vm.HasThumbnail.Should().BeFalse();
    }

    #endregion

    #region Static File Type Detection Tests

    [Theory]
    [InlineData("image.png", true)]
    [InlineData("image.jpg", true)]
    [InlineData("image.jpeg", true)]
    [InlineData("image.webp", true)]
    [InlineData("image.gif", true)]
    [InlineData("image.bmp", true)]
    [InlineData("video.mp4", false)]
    [InlineData("document.txt", false)]
    public void IsImageFile_DetectsCorrectly(string filePath, bool expected)
    {
        DatasetCardViewModel.IsImageFile(filePath).Should().Be(expected);
    }

    [Theory]
    [InlineData("video.mp4", true)]
    [InlineData("video.mov", true)]
    [InlineData("video.webm", true)]
    [InlineData("video.avi", true)]
    [InlineData("video.mkv", true)]
    [InlineData("image.png", false)]
    [InlineData("document.txt", false)]
    public void IsVideoFile_DetectsCorrectly(string filePath, bool expected)
    {
        DatasetCardViewModel.IsVideoFile(filePath).Should().Be(expected);
    }

    [Theory]
    [InlineData("image.png", true)]
    [InlineData("video.mp4", true)]
    [InlineData("document.txt", false)]
    [InlineData("data.json", false)]
    public void IsMediaFile_DetectsCorrectly(string filePath, bool expected)
    {
        DatasetCardViewModel.IsMediaFile(filePath).Should().Be(expected);
    }

    [Theory]
    [InlineData("caption.txt", true)]
    [InlineData("caption.caption", true)]
    [InlineData("image.png", false)]
    [InlineData("data.json", false)]
    public void IsCaptionFile_DetectsCorrectly(string filePath, bool expected)
    {
        DatasetCardViewModel.IsCaptionFile(filePath).Should().Be(expected);
    }

    [Theory]
    [InlineData("video_thumb.webp", true)]
    [InlineData("video_thumb.jpg", true)]
    [InlineData("video_thumb.png", true)]
    [InlineData("video.webp", false)]
    [InlineData("thumb_video.webp", false)]
    public void IsVideoThumbnailFile_DetectsCorrectly(string filePath, bool expected)
    {
        DatasetCardViewModel.IsVideoThumbnailFile(filePath).Should().Be(expected);
    }

    [Fact]
    public void GetVideoThumbnailPath_ReturnsCorrectPath()
    {
        var result = DatasetCardViewModel.GetVideoThumbnailPath(Path.Combine("folder", "video.mp4"));
        result.Should().Be(Path.Combine("folder", "video_thumb.webp"));
    }

    #endregion

    #region GetMediaExtensions / GetImageExtensions / GetVideoExtensions Tests

    [Fact]
    public void GetMediaExtensions_ReturnsNonEmptyList()
    {
        var extensions = DatasetCardViewModel.GetMediaExtensions();
        extensions.Should().NotBeEmpty();
        extensions.Should().Contain(".png");
        extensions.Should().Contain(".mp4");
    }

    [Fact]
    public void GetImageExtensions_ReturnsNonEmptyList()
    {
        var extensions = DatasetCardViewModel.GetImageExtensions();
        extensions.Should().NotBeEmpty();
        extensions.Should().Contain(".png");
        extensions.Should().NotContain(".mp4");
    }

    [Fact]
    public void GetVideoExtensions_ReturnsNonEmptyList()
    {
        var extensions = DatasetCardViewModel.GetVideoExtensions();
        extensions.Should().NotBeEmpty();
        extensions.Should().Contain(".mp4");
        extensions.Should().NotContain(".png");
    }

    #endregion

    #region GetNextVersionNumber Tests

    [Fact]
    public void GetNextVersionNumber_WhenNonExistentFolder_Returns1()
    {
        var vm = new DatasetCardViewModel { FolderPath = "/nonexistent/path" };
        vm.GetNextVersionNumber().Should().Be(1);
    }

    [Fact]
    public void GetNextVersionNumber_WhenVersionedWithV1_Returns2()
    {
        // Arrange
        var datasetPath = Path.Combine(_testTempPath, "TestDataset");
        Directory.CreateDirectory(datasetPath);
        Directory.CreateDirectory(Path.Combine(datasetPath, "V1"));

        var vm = new DatasetCardViewModel { FolderPath = datasetPath, IsVersionedStructure = true };

        // Act
        var result = vm.GetNextVersionNumber();

        // Assert
        result.Should().Be(2);
    }

    [Fact]
    public void GetNextVersionNumber_WhenVersionedWithV1AndV2_Returns3()
    {
        // Arrange
        var datasetPath = Path.Combine(_testTempPath, "TestDataset2");
        Directory.CreateDirectory(datasetPath);
        Directory.CreateDirectory(Path.Combine(datasetPath, "V1"));
        Directory.CreateDirectory(Path.Combine(datasetPath, "V2"));

        var vm = new DatasetCardViewModel { FolderPath = datasetPath, IsVersionedStructure = true };

        // Act
        var result = vm.GetNextVersionNumber();

        // Assert
        result.Should().Be(3);
    }

    #endregion

    #region GetAllVersionNumbers Tests

    [Fact]
    public void GetAllVersionNumbers_WhenNonExistentFolder_ReturnsList1()
    {
        var vm = new DatasetCardViewModel { FolderPath = "/nonexistent/path" };
        vm.GetAllVersionNumbers().Should().Equal([1]);
    }

    [Fact]
    public void GetAllVersionNumbers_WhenVersionedFolder_ReturnsVersions()
    {
        // Arrange
        var datasetPath = Path.Combine(_testTempPath, "TestDataset3");
        Directory.CreateDirectory(datasetPath);
        Directory.CreateDirectory(Path.Combine(datasetPath, "V1"));
        Directory.CreateDirectory(Path.Combine(datasetPath, "V3"));
        Directory.CreateDirectory(Path.Combine(datasetPath, "V2"));

        var vm = new DatasetCardViewModel { FolderPath = datasetPath };

        // Act
        var result = vm.GetAllVersionNumbers();

        // Assert
        result.Should().Equal([1, 2, 3]); // Should be sorted
    }

    #endregion

    #region CreateVersionCard Tests

    [Fact]
    public void CreateVersionCard_CopiesBasicProperties()
    {
        // Arrange
        var datasetPath = Path.Combine(_testTempPath, "TestDataset4");
        Directory.CreateDirectory(datasetPath);
        Directory.CreateDirectory(Path.Combine(datasetPath, "V1"));

        var vm = new DatasetCardViewModel
        {
            Name = "TestDataset",
            FolderPath = datasetPath,
            CategoryId = 1,
            CategoryName = "Test Category",
            Type = DatasetType.Image,
            IsVersionedStructure = true,
            TotalVersions = 2
        };

        // Act
        var versionCard = vm.CreateVersionCard(1);

        // Assert
        versionCard.Name.Should().Be("TestDataset");
        versionCard.FolderPath.Should().Be(datasetPath);
        versionCard.CategoryId.Should().Be(1);
        versionCard.CategoryName.Should().Be("Test Category");
        versionCard.Type.Should().Be(DatasetType.Image);
        versionCard.IsVersionedStructure.Should().BeTrue();
        versionCard.TotalVersions.Should().Be(2);
        versionCard.DisplayVersion.Should().Be(1);
        versionCard.CurrentVersion.Should().Be(1);
        versionCard.IsVersionCard.Should().BeTrue();
    }

    [Fact]
    public void CreateVersionCard_CountsMediaFilesInVersion()
    {
        // Arrange
        var datasetPath = Path.Combine(_testTempPath, "TestDataset5");
        Directory.CreateDirectory(datasetPath);
        var v1Path = Path.Combine(datasetPath, "V1");
        Directory.CreateDirectory(v1Path);

        // Create test files
        File.WriteAllText(Path.Combine(v1Path, "image1.png"), "");
        File.WriteAllText(Path.Combine(v1Path, "image2.jpg"), "");
        File.WriteAllText(Path.Combine(v1Path, "video1.mp4"), "");
        File.WriteAllText(Path.Combine(v1Path, "caption1.txt"), "");

        var vm = new DatasetCardViewModel
        {
            Name = "TestDataset",
            FolderPath = datasetPath,
            IsVersionedStructure = true,
            TotalVersions = 1
        };

        // Act
        var versionCard = vm.CreateVersionCard(1);

        // Assert
        versionCard.ImageCount.Should().Be(2);
        versionCard.VideoCount.Should().Be(1);
        versionCard.CaptionCount.Should().Be(1);
    }

    [Fact]
    public void CreateVersionCard_ExcludesVideoThumbnailsFromImageCount()
    {
        // Arrange
        var datasetPath = Path.Combine(_testTempPath, "TestDataset6");
        Directory.CreateDirectory(datasetPath);
        var v1Path = Path.Combine(datasetPath, "V1");
        Directory.CreateDirectory(v1Path);

        // Create test files including a video thumbnail
        File.WriteAllText(Path.Combine(v1Path, "image1.png"), "");
        File.WriteAllText(Path.Combine(v1Path, "video1.mp4"), "");
        File.WriteAllText(Path.Combine(v1Path, "video1_thumb.webp"), ""); // Video thumbnail - should be excluded

        var vm = new DatasetCardViewModel
        {
            Name = "TestDataset",
            FolderPath = datasetPath,
            IsVersionedStructure = true,
            TotalVersions = 1
        };

        // Act
        var versionCard = vm.CreateVersionCard(1);

        // Assert
        versionCard.ImageCount.Should().Be(1); // Only image1.png, not video1_thumb.webp
        versionCard.VideoCount.Should().Be(1);
    }

    #endregion

    #region FromFolder Tests

    [Fact]
    public void FromFolder_WhenEmptyFolder_CreatesCardWithZeroCounts()
    {
        // Arrange
        var datasetPath = Path.Combine(_testTempPath, "EmptyDataset");
        Directory.CreateDirectory(datasetPath);

        // Act
        var card = DatasetCardViewModel.FromFolder(datasetPath);

        // Assert
        card.Name.Should().Be("EmptyDataset");
        card.FolderPath.Should().Be(datasetPath);
        card.ImageCount.Should().Be(0);
        card.VideoCount.Should().Be(0);
        card.ThumbnailPath.Should().BeNull();
    }

    [Fact]
    public void FromFolder_WhenVersionedStructure_DetectsVersions()
    {
        // Arrange
        var datasetPath = Path.Combine(_testTempPath, "VersionedDataset");
        Directory.CreateDirectory(datasetPath);
        Directory.CreateDirectory(Path.Combine(datasetPath, "V1"));
        Directory.CreateDirectory(Path.Combine(datasetPath, "V2"));

        // Act
        var card = DatasetCardViewModel.FromFolder(datasetPath);

        // Assert
        card.IsVersionedStructure.Should().BeTrue();
        card.TotalVersions.Should().Be(2);
    }

    [Fact]
    public void FromFolder_WhenLegacyStructure_DetectsAsNonVersioned()
    {
        // Arrange
        var datasetPath = Path.Combine(_testTempPath, "LegacyDataset");
        Directory.CreateDirectory(datasetPath);
        File.WriteAllText(Path.Combine(datasetPath, "image.png"), "");

        // Act
        var card = DatasetCardViewModel.FromFolder(datasetPath);

        // Assert
        card.IsVersionedStructure.Should().BeFalse();
        card.TotalVersions.Should().Be(1);
    }

    [Fact]
    public void FromFolder_SetsFirstImageAsThumbnail()
    {
        // Arrange
        var datasetPath = Path.Combine(_testTempPath, "ThumbnailDataset");
        Directory.CreateDirectory(datasetPath);
        var imagePath = Path.Combine(datasetPath, "image.png");
        File.WriteAllText(imagePath, "");

        // Act
        var card = DatasetCardViewModel.FromFolder(datasetPath);

        // Assert
        card.ThumbnailPath.Should().Be(imagePath);
        card.HasThumbnail.Should().BeTrue();
    }

    #endregion

    #region SaveMetadata / LoadMetadata Tests

    [Fact]
    public void SaveMetadata_CreatesConfigFolder()
    {
        // Arrange
        var datasetPath = Path.Combine(_testTempPath, "MetadataDataset");
        Directory.CreateDirectory(datasetPath);
        var vm = new DatasetCardViewModel
        {
            FolderPath = datasetPath,
            CategoryId = 1,
            Type = DatasetType.Video,
            CurrentVersion = 2
        };

        // Act
        vm.SaveMetadata();

        // Assert
        Directory.Exists(vm.ConfigFolderPath).Should().BeTrue();
        File.Exists(vm.MetadataFilePath).Should().BeTrue();
    }

    [Fact]
    public void LoadMetadata_RestoresProperties()
    {
        // Arrange
        var datasetPath = Path.Combine(_testTempPath, "LoadMetadataDataset");
        Directory.CreateDirectory(datasetPath);
        Directory.CreateDirectory(Path.Combine(datasetPath, "V1"));

        var original = new DatasetCardViewModel
        {
            FolderPath = datasetPath,
            CategoryOrder = 42,  // Use CategoryOrder (persisted to config.json) instead of CategoryId (resolved at runtime)
            Type = DatasetType.Instruction,
            CurrentVersion = 1
        };
        original.VersionDescriptions[1] = "First version";
        original.RecordBranch(2, 1);
        original.SaveMetadata();

        // Act
        var loaded = new DatasetCardViewModel { FolderPath = datasetPath };
        loaded.LoadMetadata();

        // Assert
        loaded.CategoryOrder.Should().Be(42);  // Check CategoryOrder instead of CategoryId
        loaded.Type.Should().Be(DatasetType.Instruction);
        loaded.VersionDescriptions.Should().ContainKey(1);
        loaded.VersionDescriptions[1].Should().Be("First version");
        loaded.VersionBranchedFrom.Should().ContainKey(2);
        loaded.VersionBranchedFrom[2].Should().Be(1);
    }

    #endregion
}
