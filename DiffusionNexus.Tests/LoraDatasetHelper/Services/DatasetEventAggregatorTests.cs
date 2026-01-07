using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.LoraDatasetHelper.Services;

/// <summary>
/// Unit tests for <see cref="DatasetEventAggregator"/> service.
/// Tests event publishing, subscription, and thread safety.
/// </summary>
public class DatasetEventAggregatorTests
{
    private readonly DatasetEventAggregator _sut;

    public DatasetEventAggregatorTests()
    {
        _sut = new DatasetEventAggregator();
    }

    #region ActiveDatasetChanged Tests

    [Fact]
    public void PublishActiveDatasetChanged_WhenSubscribed_RaisesEvent()
    {
        // Arrange
        ActiveDatasetChangedEventArgs? receivedArgs = null;
        _sut.ActiveDatasetChanged += (_, args) => receivedArgs = args;

        var dataset = CreateTestDatasetCard();
        var args = new ActiveDatasetChangedEventArgs { Dataset = dataset };

        // Act
        _sut.PublishActiveDatasetChanged(args);

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.Dataset.Should().Be(dataset);
    }

    [Fact]
    public void PublishActiveDatasetChanged_WhenNotSubscribed_DoesNotThrow()
    {
        // Arrange
        var args = new ActiveDatasetChangedEventArgs { Dataset = null };

        // Act
        var act = () => _sut.PublishActiveDatasetChanged(args);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void PublishActiveDatasetChanged_WhenNullArgs_ThrowsArgumentNullException()
    {
        // Act
        var act = () => _sut.PublishActiveDatasetChanged(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region DatasetCreated Tests

    [Fact]
    public void PublishDatasetCreated_WhenSubscribed_RaisesEvent()
    {
        // Arrange
        DatasetCreatedEventArgs? receivedArgs = null;
        _sut.DatasetCreated += (_, args) => receivedArgs = args;

        var dataset = CreateTestDatasetCard();
        var args = new DatasetCreatedEventArgs { Dataset = dataset };

        // Act
        _sut.PublishDatasetCreated(args);

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.Dataset.Should().Be(dataset);
    }

    [Fact]
    public void PublishDatasetCreated_WhenNullArgs_ThrowsArgumentNullException()
    {
        var act = () => _sut.PublishDatasetCreated(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region DatasetDeleted Tests

    [Fact]
    public void PublishDatasetDeleted_WhenSubscribed_RaisesEvent()
    {
        // Arrange
        DatasetDeletedEventArgs? receivedArgs = null;
        _sut.DatasetDeleted += (_, args) => receivedArgs = args;

        var dataset = CreateTestDatasetCard();
        var args = new DatasetDeletedEventArgs { Dataset = dataset, DeletedVersion = 2 };

        // Act
        _sut.PublishDatasetDeleted(args);

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.Dataset.Should().Be(dataset);
        receivedArgs.DeletedVersion.Should().Be(2);
    }

    [Fact]
    public void PublishDatasetDeleted_WhenNullArgs_ThrowsArgumentNullException()
    {
        var act = () => _sut.PublishDatasetDeleted(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region DatasetMetadataChanged Tests

    [Fact]
    public void PublishDatasetMetadataChanged_WhenSubscribed_RaisesEvent()
    {
        // Arrange
        DatasetMetadataChangedEventArgs? receivedArgs = null;
        _sut.DatasetMetadataChanged += (_, args) => receivedArgs = args;

        var dataset = CreateTestDatasetCard();
        var args = new DatasetMetadataChangedEventArgs
        {
            Dataset = dataset,
            ChangeType = DatasetMetadataChangeType.Category
        };

        // Act
        _sut.PublishDatasetMetadataChanged(args);

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.ChangeType.Should().Be(DatasetMetadataChangeType.Category);
    }

    [Fact]
    public void PublishDatasetMetadataChanged_WhenNullArgs_ThrowsArgumentNullException()
    {
        var act = () => _sut.PublishDatasetMetadataChanged(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region DatasetImagesLoaded Tests

    [Fact]
    public void PublishDatasetImagesLoaded_WhenSubscribed_RaisesEvent()
    {
        // Arrange
        DatasetImagesLoadedEventArgs? receivedArgs = null;
        _sut.DatasetImagesLoaded += (_, args) => receivedArgs = args;

        var dataset = CreateTestDatasetCard();
        var images = new List<DatasetImageViewModel>();
        var args = new DatasetImagesLoadedEventArgs { Dataset = dataset, Images = images };

        // Act
        _sut.PublishDatasetImagesLoaded(args);

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.Images.Should().BeSameAs(images);
    }

    [Fact]
    public void PublishDatasetImagesLoaded_WhenNullArgs_ThrowsArgumentNullException()
    {
        var act = () => _sut.PublishDatasetImagesLoaded(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region ImageAdded Tests

    [Fact]
    public void PublishImageAdded_WhenSubscribed_RaisesEvent()
    {
        // Arrange
        ImageAddedEventArgs? receivedArgs = null;
        _sut.ImageAdded += (_, args) => receivedArgs = args;

        var dataset = CreateTestDatasetCard();
        var addedImages = new List<DatasetImageViewModel>();
        var args = new ImageAddedEventArgs { Dataset = dataset, AddedImages = addedImages };

        // Act
        _sut.PublishImageAdded(args);

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.AddedImages.Should().BeSameAs(addedImages);
    }

    [Fact]
    public void PublishImageAdded_WhenNullArgs_ThrowsArgumentNullException()
    {
        var act = () => _sut.PublishImageAdded(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region ImageDeleted Tests

    [Fact]
    public void PublishImageDeleted_WhenSubscribed_RaisesEvent()
    {
        // Arrange
        ImageDeletedEventArgs? receivedArgs = null;
        _sut.ImageDeleted += (_, args) => receivedArgs = args;

        var dataset = CreateTestDatasetCard();
        var args = new ImageDeletedEventArgs { Dataset = dataset, ImagePath = "/path/to/image.png" };

        // Act
        _sut.PublishImageDeleted(args);

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.ImagePath.Should().Be("/path/to/image.png");
    }

    [Fact]
    public void PublishImageDeleted_WhenNullArgs_ThrowsArgumentNullException()
    {
        var act = () => _sut.PublishImageDeleted(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region ImageSaved Tests

    [Fact]
    public void PublishImageSaved_WhenSubscribed_RaisesEvent()
    {
        // Arrange
        ImageSavedEventArgs? receivedArgs = null;
        _sut.ImageSaved += (_, args) => receivedArgs = args;

        var args = new ImageSavedEventArgs { ImagePath = "/path/to/saved.png", OriginalPath = "/path/to/original.png" };

        // Act
        _sut.PublishImageSaved(args);

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.ImagePath.Should().Be("/path/to/saved.png");
        receivedArgs.OriginalPath.Should().Be("/path/to/original.png");
        receivedArgs.IsNewFile.Should().BeTrue();
    }

    [Fact]
    public void PublishImageSaved_WhenOverwrite_IsNewFileIsFalse()
    {
        // Arrange
        ImageSavedEventArgs? receivedArgs = null;
        _sut.ImageSaved += (_, args) => receivedArgs = args;

        var args = new ImageSavedEventArgs { ImagePath = "/path/to/saved.png", OriginalPath = null };

        // Act
        _sut.PublishImageSaved(args);

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.IsNewFile.Should().BeFalse();
    }

    [Fact]
    public void PublishImageSaved_WhenNullArgs_ThrowsArgumentNullException()
    {
        var act = () => _sut.PublishImageSaved(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region ImageRatingChanged Tests

    [Fact]
    public void PublishImageRatingChanged_WhenSubscribed_RaisesEvent()
    {
        // Arrange
        ImageRatingChangedEventArgs? receivedArgs = null;
        _sut.ImageRatingChanged += (_, args) => receivedArgs = args;

        var image = new DatasetImageViewModel();
        var args = new ImageRatingChangedEventArgs
        {
            Image = image,
            NewRating = ImageRatingStatus.Approved,
            PreviousRating = ImageRatingStatus.Unrated
        };

        // Act
        _sut.PublishImageRatingChanged(args);

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.Image.Should().Be(image);
        receivedArgs.NewRating.Should().Be(ImageRatingStatus.Approved);
        receivedArgs.PreviousRating.Should().Be(ImageRatingStatus.Unrated);
    }

    [Fact]
    public void PublishImageRatingChanged_WhenNullArgs_ThrowsArgumentNullException()
    {
        var act = () => _sut.PublishImageRatingChanged(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region CaptionChanged Tests

    [Fact]
    public void PublishCaptionChanged_WhenSubscribed_RaisesEvent()
    {
        // Arrange
        CaptionChangedEventArgs? receivedArgs = null;
        _sut.CaptionChanged += (_, args) => receivedArgs = args;

        var image = new DatasetImageViewModel();
        var args = new CaptionChangedEventArgs { Image = image, WasSaved = true };

        // Act
        _sut.PublishCaptionChanged(args);

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.WasSaved.Should().BeTrue();
    }

    [Fact]
    public void PublishCaptionChanged_WhenNullArgs_ThrowsArgumentNullException()
    {
        var act = () => _sut.PublishCaptionChanged(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region ImageSelectionChanged Tests

    [Fact]
    public void PublishImageSelectionChanged_WhenSubscribed_RaisesEvent()
    {
        // Arrange
        ImageSelectionChangedEventArgs? receivedArgs = null;
        _sut.ImageSelectionChanged += (_, args) => receivedArgs = args;

        var image = new DatasetImageViewModel();
        var args = new ImageSelectionChangedEventArgs { Image = image, IsSelected = true };

        // Act
        _sut.PublishImageSelectionChanged(args);

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void PublishImageSelectionChanged_WhenNullArgs_ThrowsArgumentNullException()
    {
        var act = () => _sut.PublishImageSelectionChanged(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region VersionCreated Tests

    [Fact]
    public void PublishVersionCreated_WhenSubscribed_RaisesEvent()
    {
        // Arrange
        VersionCreatedEventArgs? receivedArgs = null;
        _sut.VersionCreated += (_, args) => receivedArgs = args;

        var dataset = CreateTestDatasetCard();
        var args = new VersionCreatedEventArgs { Dataset = dataset, NewVersion = 2, BranchedFromVersion = 1 };

        // Act
        _sut.PublishVersionCreated(args);

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.NewVersion.Should().Be(2);
        receivedArgs.BranchedFromVersion.Should().Be(1);
    }

    [Fact]
    public void PublishVersionCreated_WhenNullArgs_ThrowsArgumentNullException()
    {
        var act = () => _sut.PublishVersionCreated(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region NavigateToImageEditor Tests

    [Fact]
    public void PublishNavigateToImageEditor_WhenSubscribed_RaisesEvent()
    {
        // Arrange
        NavigateToImageEditorEventArgs? receivedArgs = null;
        _sut.NavigateToImageEditorRequested += (_, args) => receivedArgs = args;

        var dataset = CreateTestDatasetCard();
        var image = new DatasetImageViewModel { ImagePath = "/path/to/image.png" };
        var args = new NavigateToImageEditorEventArgs { Dataset = dataset, Image = image };

        // Act
        _sut.PublishNavigateToImageEditor(args);

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.Image.Should().Be(image);
        receivedArgs.Dataset.Should().Be(dataset);
    }

    [Fact]
    public void PublishNavigateToImageEditor_WhenNullArgs_ThrowsArgumentNullException()
    {
        var act = () => _sut.PublishNavigateToImageEditor(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region RefreshDatasetsRequested Tests

    [Fact]
    public void PublishRefreshDatasetsRequested_WhenSubscribed_RaisesEvent()
    {
        // Arrange
        RefreshDatasetsRequestedEventArgs? receivedArgs = null;
        _sut.RefreshDatasetsRequested += (_, args) => receivedArgs = args;

        var args = new RefreshDatasetsRequestedEventArgs();

        // Act
        _sut.PublishRefreshDatasetsRequested(args);

        // Assert
        receivedArgs.Should().NotBeNull();
    }

    [Fact]
    public void PublishRefreshDatasetsRequested_WhenNullArgs_ThrowsArgumentNullException()
    {
        var act = () => _sut.PublishRefreshDatasetsRequested(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Unsubscription Tests

    [Fact]
    public void Event_WhenUnsubscribed_DoesNotReceiveEvents()
    {
        // Arrange
        var eventCount = 0;
        void Handler(object? sender, ActiveDatasetChangedEventArgs args) => eventCount++;

        _sut.ActiveDatasetChanged += Handler;
        _sut.PublishActiveDatasetChanged(new ActiveDatasetChangedEventArgs { Dataset = null });
        eventCount.Should().Be(1);

        // Act
        _sut.ActiveDatasetChanged -= Handler;
        _sut.PublishActiveDatasetChanged(new ActiveDatasetChangedEventArgs { Dataset = null });

        // Assert
        eventCount.Should().Be(1, "because handler was unsubscribed");
    }

    #endregion

    #region Multiple Subscribers Tests

    [Fact]
    public void Event_WhenMultipleSubscribers_AllReceiveEvent()
    {
        // Arrange
        var subscriber1Called = false;
        var subscriber2Called = false;

        _sut.DatasetCreated += (_, _) => subscriber1Called = true;
        _sut.DatasetCreated += (_, _) => subscriber2Called = true;

        var args = new DatasetCreatedEventArgs { Dataset = CreateTestDatasetCard() };

        // Act
        _sut.PublishDatasetCreated(args);

        // Assert
        subscriber1Called.Should().BeTrue();
        subscriber2Called.Should().BeTrue();
    }

    #endregion

    #region Event Args Tests

    [Fact]
    public void DatasetEventArgs_Timestamp_IsSetOnCreation()
    {
        // Arrange
        var before = DateTime.UtcNow;

        // Act
        var args = new DatasetCreatedEventArgs { Dataset = CreateTestDatasetCard() };

        // Assert
        var after = DateTime.UtcNow;
        args.Timestamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    #endregion

    #region Helper Methods

    private static DatasetCardViewModel CreateTestDatasetCard()
    {
        return new DatasetCardViewModel
        {
            Name = "TestDataset",
            FolderPath = "/test/path"
        };
    }

    #endregion
}
