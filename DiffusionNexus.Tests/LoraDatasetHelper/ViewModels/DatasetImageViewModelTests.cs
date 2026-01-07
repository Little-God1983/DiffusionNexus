using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.LoraDatasetHelper.ViewModels;

/// <summary>
/// Unit tests for <see cref="DatasetImageViewModel"/>.
/// Tests FromFile factory, caption/rating operations, selection state, and property changes.
/// </summary>
public class DatasetImageViewModelTests
{
    private readonly Mock<IDatasetEventAggregator> _mockEventAggregator;

    public DatasetImageViewModelTests()
    {
        _mockEventAggregator = new Mock<IDatasetEventAggregator>();
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_InitializesWithDefaultValues()
    {
        // Act
        var vm = new DatasetImageViewModel();

        // Assert
        vm.ImagePath.Should().BeEmpty();
        vm.Caption.Should().BeEmpty();
        vm.HasUnsavedChanges.Should().BeFalse();
        vm.IsSelected.Should().BeFalse();
        vm.IsEditorSelected.Should().BeFalse();
        vm.RatingStatus.Should().Be(ImageRatingStatus.Unrated);
    }

    [Fact]
    public void Constructor_WithEventAggregator_AcceptsNullWithoutThrowing()
    {
        // Act
        var act = () => new DatasetImageViewModel(null);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_InitializesCommands()
    {
        // Act
        var vm = new DatasetImageViewModel();

        // Assert
        vm.SaveCaptionCommand.Should().NotBeNull();
        vm.RevertCaptionCommand.Should().NotBeNull();
        vm.DeleteCommand.Should().NotBeNull();
        vm.MarkApprovedCommand.Should().NotBeNull();
        vm.MarkRejectedCommand.Should().NotBeNull();
        vm.ClearRatingCommand.Should().NotBeNull();
    }

    #endregion

    #region ImagePath Property Tests

    [Fact]
    public void ImagePath_WhenSet_NotifiesPropertyChanged()
    {
        // Arrange
        var vm = new DatasetImageViewModel();
        var propertyChangedNames = new List<string>();
        vm.PropertyChanged += (_, e) => propertyChangedNames.Add(e.PropertyName!);

        // Act
        vm.ImagePath = "/path/to/image.png";

        // Assert
        propertyChangedNames.Should().Contain(nameof(DatasetImageViewModel.ImagePath));
        propertyChangedNames.Should().Contain(nameof(DatasetImageViewModel.IsVideo));
        propertyChangedNames.Should().Contain(nameof(DatasetImageViewModel.IsImage));
        propertyChangedNames.Should().Contain(nameof(DatasetImageViewModel.FileName));
        propertyChangedNames.Should().Contain(nameof(DatasetImageViewModel.FullFileName));
        propertyChangedNames.Should().Contain(nameof(DatasetImageViewModel.FileExtension));
    }

    [Fact]
    public void FileName_ReturnsFileNameWithoutExtension()
    {
        // Arrange
        var vm = new DatasetImageViewModel { ImagePath = "/path/to/my_image.png" };

        // Assert
        vm.FileName.Should().Be("my_image");
    }

    [Fact]
    public void FullFileName_ReturnsFileNameWithExtension()
    {
        // Arrange
        var vm = new DatasetImageViewModel { ImagePath = "/path/to/my_image.png" };

        // Assert
        vm.FullFileName.Should().Be("my_image.png");
    }

    [Fact]
    public void FileExtension_ReturnsUppercaseExtensionWithoutDot()
    {
        // Arrange
        var vm = new DatasetImageViewModel { ImagePath = "/path/to/image.jpeg" };

        // Assert
        vm.FileExtension.Should().Be("JPEG");
    }

    #endregion

    #region IsImage / IsVideo Tests

    [Theory]
    [InlineData("image.png", false)]
    [InlineData("image.jpg", false)]
    [InlineData("image.jpeg", false)]
    [InlineData("image.webp", false)]
    [InlineData("image.gif", false)]
    [InlineData("image.bmp", false)]
    public void IsVideoFile_WhenImageExtension_ReturnsFalse(string fileName, bool expectedIsVideo)
    {
        // Note: We can't test IsImage directly via property setting because
        // _isVideo is set in FromFile factory. We'll test the static method instead.
        var isVideo = DatasetImageViewModel.IsVideoFile(fileName);
        isVideo.Should().Be(expectedIsVideo);
    }

    [Theory]
    [InlineData("video.mp4", true)]
    [InlineData("video.mov", true)]
    [InlineData("video.webm", true)]
    [InlineData("video.avi", true)]
    [InlineData("video.mkv", true)]
    public void IsVideoFile_WhenVideoExtension_ReturnsTrue(string fileName, bool expectedIsVideo)
    {
        var isVideo = DatasetImageViewModel.IsVideoFile(fileName);
        isVideo.Should().Be(expectedIsVideo);
    }

    [Fact]
    public void MediaType_WhenImage_ReturnsImage()
    {
        // Arrange - Need to use reflection or test with actual file for _isVideo
        var vm = new DatasetImageViewModel();
        // _isVideo is private, but MediaType defaults to "Image" when _isVideo is false

        // Assert
        vm.MediaType.Should().Be("Image");
    }

    #endregion

    #region Caption Property Tests

    [Fact]
    public void Caption_WhenChanged_SetsHasUnsavedChangesToTrue()
    {
        // Arrange
        var vm = new DatasetImageViewModel();
        vm.Caption = "original";
        // Simulate the original caption being set (normally done via LoadCaption)
        // Since we can't access _originalCaption, we test the behavior
        vm.HasUnsavedChanges = false;

        // Act
        vm.Caption = "modified";

        // Assert
        vm.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void Caption_WhenRevertedToOriginal_SetsHasUnsavedChangesToFalse()
    {
        // Arrange
        var vm = new DatasetImageViewModel();
        // Start with empty (which is the original)
        vm.Caption = "modified";
        vm.HasUnsavedChanges.Should().BeTrue();

        // Act
        vm.Caption = string.Empty;

        // Assert
        vm.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public void CaptionFilePath_ReturnsPathWithTxtExtension()
    {
        // Arrange
        var vm = new DatasetImageViewModel { ImagePath = "/path/to/image.png" };

        // Assert
        vm.CaptionFilePath.Should().Be("/path/to/image.txt");
    }

    #endregion

    #region Rating Property Tests

    [Fact]
    public void RatingStatus_DefaultsToUnrated()
    {
        // Arrange
        var vm = new DatasetImageViewModel();

        // Assert
        vm.RatingStatus.Should().Be(ImageRatingStatus.Unrated);
        vm.IsUnrated.Should().BeTrue();
        vm.IsApproved.Should().BeFalse();
        vm.IsRejected.Should().BeFalse();
    }

    [Fact]
    public void RatingStatus_WhenSetToApproved_UpdatesIsApproved()
    {
        // Arrange
        var vm = new DatasetImageViewModel();

        // Act
        vm.RatingStatus = ImageRatingStatus.Approved;

        // Assert
        vm.IsApproved.Should().BeTrue();
        vm.IsRejected.Should().BeFalse();
        vm.IsUnrated.Should().BeFalse();
    }

    [Fact]
    public void RatingStatus_WhenSetToRejected_UpdatesIsRejected()
    {
        // Arrange
        var vm = new DatasetImageViewModel();

        // Act
        vm.RatingStatus = ImageRatingStatus.Rejected;

        // Assert
        vm.IsRejected.Should().BeTrue();
        vm.IsApproved.Should().BeFalse();
        vm.IsUnrated.Should().BeFalse();
    }

    [Fact]
    public void RatingStatus_WhenChanged_NotifiesPropertyChanged()
    {
        // Arrange
        var vm = new DatasetImageViewModel();
        var propertyChangedNames = new List<string>();
        vm.PropertyChanged += (_, e) => propertyChangedNames.Add(e.PropertyName!);

        // Act
        vm.RatingStatus = ImageRatingStatus.Approved;

        // Assert
        propertyChangedNames.Should().Contain(nameof(DatasetImageViewModel.RatingStatus));
        propertyChangedNames.Should().Contain(nameof(DatasetImageViewModel.IsApproved));
        propertyChangedNames.Should().Contain(nameof(DatasetImageViewModel.IsRejected));
        propertyChangedNames.Should().Contain(nameof(DatasetImageViewModel.IsUnrated));
    }

    [Fact]
    public void RatingFilePath_ReturnsPathWithRatingExtension()
    {
        // Arrange
        var vm = new DatasetImageViewModel { ImagePath = "/path/to/image.png" };

        // Assert
        vm.RatingFilePath.Should().Be("/path/to/image.rating");
    }

    #endregion

    #region Selection Tests

    [Fact]
    public void IsSelected_WhenChanged_NotifiesPropertyChanged()
    {
        // Arrange
        var vm = new DatasetImageViewModel(_mockEventAggregator.Object);
        var propertyChangedNames = new List<string>();
        vm.PropertyChanged += (_, e) => propertyChangedNames.Add(e.PropertyName!);

        // Act
        vm.IsSelected = true;

        // Assert
        propertyChangedNames.Should().Contain(nameof(DatasetImageViewModel.IsSelected));
    }

    [Fact]
    public void IsSelected_WhenChangedWithEventAggregator_PublishesEvent()
    {
        // Arrange
        var vm = new DatasetImageViewModel(_mockEventAggregator.Object);

        // Act
        vm.IsSelected = true;

        // Assert
        _mockEventAggregator.Verify(
            x => x.PublishImageSelectionChanged(It.Is<ImageSelectionChangedEventArgs>(args =>
                args.Image == vm && args.IsSelected == true)),
            Times.Once);
    }

    [Fact]
    public void IsSelected_WhenChangedWithoutEventAggregator_DoesNotThrow()
    {
        // Arrange
        var vm = new DatasetImageViewModel();

        // Act
        var act = () => vm.IsSelected = true;

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void IsEditorSelected_CanBeSetAndRetrieved()
    {
        // Arrange
        var vm = new DatasetImageViewModel();

        // Act
        vm.IsEditorSelected = true;

        // Assert
        vm.IsEditorSelected.Should().BeTrue();
    }

    #endregion

    #region ThumbnailPath Tests

    [Fact]
    public void ThumbnailPath_WhenNotSet_ForImageReturnsImagePath()
    {
        // Arrange
        var vm = new DatasetImageViewModel { ImagePath = "/path/to/image.png" };
        // Note: _isVideo is false by default, so ThumbnailPath returns ImagePath

        // Assert
        vm.ThumbnailPath.Should().Be("/path/to/image.png");
    }

    [Fact]
    public void ThumbnailPath_WhenExplicitlySet_ReturnsSetValue()
    {
        // Arrange
        var vm = new DatasetImageViewModel { ImagePath = "/path/to/image.png" };

        // Act
        vm.ThumbnailPath = "/path/to/custom_thumbnail.webp";

        // Assert
        vm.ThumbnailPath.Should().Be("/path/to/custom_thumbnail.webp");
    }

    #endregion

    #region MarkApproved Command Tests

    [Fact]
    public void MarkApprovedCommand_WhenUnrated_SetsToApproved()
    {
        // Arrange
        var vm = new DatasetImageViewModel(_mockEventAggregator.Object)
        {
            ImagePath = "test.png"
        };
        vm.RatingStatus.Should().Be(ImageRatingStatus.Unrated);

        // Act
        vm.MarkApprovedCommand.Execute(null);

        // Assert
        vm.RatingStatus.Should().Be(ImageRatingStatus.Approved);
    }

    [Fact]
    public void MarkApprovedCommand_WhenAlreadyApproved_TogglesBackToUnrated()
    {
        // Arrange
        var vm = new DatasetImageViewModel(_mockEventAggregator.Object)
        {
            ImagePath = "test.png",
            RatingStatus = ImageRatingStatus.Approved
        };

        // Act
        vm.MarkApprovedCommand.Execute(null);

        // Assert
        vm.RatingStatus.Should().Be(ImageRatingStatus.Unrated);
    }

    [Fact]
    public void MarkApprovedCommand_PublishesRatingChangedEvent()
    {
        // Arrange
        var vm = new DatasetImageViewModel(_mockEventAggregator.Object)
        {
            ImagePath = "test.png"
        };

        // Act
        vm.MarkApprovedCommand.Execute(null);

        // Assert
        _mockEventAggregator.Verify(
            x => x.PublishImageRatingChanged(It.Is<ImageRatingChangedEventArgs>(args =>
                args.Image == vm &&
                args.NewRating == ImageRatingStatus.Approved &&
                args.PreviousRating == ImageRatingStatus.Unrated)),
            Times.Once);
    }

    #endregion

    #region MarkRejected Command Tests

    [Fact]
    public void MarkRejectedCommand_WhenUnrated_SetsToRejected()
    {
        // Arrange
        var vm = new DatasetImageViewModel(_mockEventAggregator.Object)
        {
            ImagePath = "test.png"
        };

        // Act
        vm.MarkRejectedCommand.Execute(null);

        // Assert
        vm.RatingStatus.Should().Be(ImageRatingStatus.Rejected);
    }

    [Fact]
    public void MarkRejectedCommand_WhenAlreadyRejected_TogglesBackToUnrated()
    {
        // Arrange
        var vm = new DatasetImageViewModel(_mockEventAggregator.Object)
        {
            ImagePath = "test.png",
            RatingStatus = ImageRatingStatus.Rejected
        };

        // Act
        vm.MarkRejectedCommand.Execute(null);

        // Assert
        vm.RatingStatus.Should().Be(ImageRatingStatus.Unrated);
    }

    #endregion

    #region ClearRating Command Tests

    [Fact]
    public void ClearRatingCommand_WhenApproved_SetsToUnrated()
    {
        // Arrange
        var vm = new DatasetImageViewModel(_mockEventAggregator.Object)
        {
            ImagePath = "test.png",
            RatingStatus = ImageRatingStatus.Approved
        };

        // Act
        vm.ClearRatingCommand.Execute(null);

        // Assert
        vm.RatingStatus.Should().Be(ImageRatingStatus.Unrated);
    }

    [Fact]
    public void ClearRatingCommand_WhenRejected_SetsToUnrated()
    {
        // Arrange
        var vm = new DatasetImageViewModel(_mockEventAggregator.Object)
        {
            ImagePath = "test.png",
            RatingStatus = ImageRatingStatus.Rejected
        };

        // Act
        vm.ClearRatingCommand.Execute(null);

        // Assert
        vm.RatingStatus.Should().Be(ImageRatingStatus.Unrated);
    }

    #endregion

    #region RevertCaption Command Tests

    [Fact]
    public void RevertCaptionCommand_ResetsToOriginalCaption()
    {
        // Arrange
        var vm = new DatasetImageViewModel();
        // The original caption is empty string by default
        vm.Caption = "modified caption";
        vm.HasUnsavedChanges.Should().BeTrue();

        // Act
        vm.RevertCaptionCommand.Execute(null);

        // Assert
        vm.Caption.Should().BeEmpty();
        vm.HasUnsavedChanges.Should().BeFalse();
    }

    #endregion

    #region HasUnsavedChanges Tests

    [Fact]
    public void HasUnsavedChanges_WhenExplicitlySet_UpdatesValue()
    {
        // Arrange
        var vm = new DatasetImageViewModel();

        // Act
        vm.HasUnsavedChanges = true;

        // Assert
        vm.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void HasUnsavedChanges_WhenChanged_NotifiesPropertyChanged()
    {
        // Arrange
        var vm = new DatasetImageViewModel();
        var propertyChangedNames = new List<string>();
        vm.PropertyChanged += (_, e) => propertyChangedNames.Add(e.PropertyName!);

        // Act
        vm.HasUnsavedChanges = true;

        // Assert
        propertyChangedNames.Should().Contain(nameof(DatasetImageViewModel.HasUnsavedChanges));
    }

    #endregion
}
