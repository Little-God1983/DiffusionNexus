using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.LoraDatasetHelper.Services;

/// <summary>
/// Unit tests for <see cref="DatasetStateService"/>.
/// Tests state property changes, StateChanged event firing, and collection management.
/// </summary>
public class DatasetStateServiceTests
{
    private readonly Mock<IDatasetEventAggregator> _mockEventAggregator;
    private readonly DatasetStateService _sut;

    public DatasetStateServiceTests()
    {
        _mockEventAggregator = new Mock<IDatasetEventAggregator>();
        _sut = new DatasetStateService(_mockEventAggregator.Object);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WhenNullEventAggregator_ThrowsArgumentNullException()
    {
        var act = () => new DatasetStateService(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("eventAggregator");
    }

    [Fact]
    public void Constructor_InitializesCollections()
    {
        _sut.Datasets.Should().NotBeNull().And.BeEmpty();
        _sut.GroupedDatasets.Should().NotBeNull().And.BeEmpty();
        _sut.DatasetImages.Should().NotBeNull().And.BeEmpty();
        _sut.AvailableCategories.Should().NotBeNull().And.BeEmpty();
        _sut.AvailableVersions.Should().NotBeNull().And.BeEmpty();
        _sut.EditorVersionItems.Should().NotBeNull().And.BeEmpty();
        _sut.EditorDatasetImages.Should().NotBeNull().And.BeEmpty();
    }

    #endregion

    #region SetActiveDataset Tests

    [Fact]
    public void SetActiveDataset_WhenDatasetProvided_SetsActiveDataset()
    {
        // Arrange
        var dataset = CreateTestDatasetCard();

        // Act
        _sut.SetActiveDataset(dataset);

        // Assert
        _sut.ActiveDataset.Should().Be(dataset);
    }

    [Fact]
    public void SetActiveDataset_WhenDatasetProvided_SetsIsViewingDatasetToTrue()
    {
        // Arrange
        var dataset = CreateTestDatasetCard();

        // Act
        _sut.SetActiveDataset(dataset);

        // Assert
        _sut.IsViewingDataset.Should().BeTrue();
    }

    [Fact]
    public void SetActiveDataset_WhenNull_SetsIsViewingDatasetToFalse()
    {
        // Arrange
        var dataset = CreateTestDatasetCard();
        _sut.SetActiveDataset(dataset);

        // Act
        _sut.SetActiveDataset(null);

        // Assert
        _sut.IsViewingDataset.Should().BeFalse();
        _sut.ActiveDataset.Should().BeNull();
    }

    [Fact]
    public void SetActiveDataset_WhenDatasetProvided_SetsSelectedVersionFromDataset()
    {
        // Arrange
        var dataset = CreateTestDatasetCard();
        dataset.CurrentVersion = 3;

        // Act
        _sut.SetActiveDataset(dataset);

        // Assert
        _sut.SelectedVersion.Should().Be(3);
    }

    [Fact]
    public void SetActiveDataset_PublishesActiveDatasetChangedEvent()
    {
        // Arrange
        var dataset = CreateTestDatasetCard();

        // Act
        _sut.SetActiveDataset(dataset);

        // Assert
        _mockEventAggregator.Verify(
            x => x.PublishActiveDatasetChanged(It.Is<ActiveDatasetChangedEventArgs>(args => 
                args.Dataset == dataset && args.PreviousDataset == null)),
            Times.Once);
    }

    [Fact]
    public void SetActiveDataset_RaisesStateChangedEvent()
    {
        // Arrange
        var stateChangedEvents = new List<string>();
        _sut.StateChanged += (_, e) => stateChangedEvents.Add(e.PropertyName);
        var dataset = CreateTestDatasetCard();

        // Act
        _sut.SetActiveDataset(dataset);

        // Assert
        stateChangedEvents.Should().Contain(nameof(IDatasetState.ActiveDataset));
    }

    #endregion

    #region SetStorageConfigured Tests

    [Fact]
    public void SetStorageConfigured_WhenTrue_SetsIsStorageConfigured()
    {
        // Act
        _sut.SetStorageConfigured(true);

        // Assert
        _sut.IsStorageConfigured.Should().BeTrue();
    }

    [Fact]
    public void SetStorageConfigured_WhenFalse_SetsIsStorageConfigured()
    {
        // Arrange
        _sut.SetStorageConfigured(true);

        // Act
        _sut.SetStorageConfigured(false);

        // Assert
        _sut.IsStorageConfigured.Should().BeFalse();
    }

    [Fact]
    public void SetStorageConfigured_RaisesStateChangedEvent()
    {
        // Arrange
        var stateChangedEvents = new List<string>();
        _sut.StateChanged += (_, e) => stateChangedEvents.Add(e.PropertyName);

        // Act
        _sut.SetStorageConfigured(true);

        // Assert
        stateChangedEvents.Should().Contain(nameof(IDatasetState.IsStorageConfigured));
    }

    #endregion

    #region Selection State Tests

    [Fact]
    public void UpdateSelectionCount_WhenNoSelectedImages_ReturnsZero()
    {
        // Arrange
        _sut.DatasetImages.Add(new DatasetImageViewModel { IsSelected = false });
        _sut.DatasetImages.Add(new DatasetImageViewModel { IsSelected = false });

        // Act
        _sut.UpdateSelectionCount();

        // Assert
        _sut.SelectionCount.Should().Be(0);
        _sut.HasSelection.Should().BeFalse();
    }

    [Fact]
    public void UpdateSelectionCount_WhenSomeSelected_ReturnsCorrectCount()
    {
        // Arrange
        _sut.DatasetImages.Add(new DatasetImageViewModel { IsSelected = true });
        _sut.DatasetImages.Add(new DatasetImageViewModel { IsSelected = false });
        _sut.DatasetImages.Add(new DatasetImageViewModel { IsSelected = true });

        // Act
        _sut.UpdateSelectionCount();

        // Assert
        _sut.SelectionCount.Should().Be(2);
        _sut.HasSelection.Should().BeTrue();
    }

    [Fact]
    public void UpdateSelectionCount_RaisesStateChangedEvent()
    {
        // Arrange
        _sut.DatasetImages.Add(new DatasetImageViewModel { IsSelected = true });
        var stateChangedEvents = new List<string>();
        _sut.StateChanged += (_, e) => stateChangedEvents.Add(e.PropertyName);

        // Act
        _sut.UpdateSelectionCount();

        // Assert
        stateChangedEvents.Should().Contain(nameof(IDatasetState.SelectionCount));
    }

    [Fact]
    public void ClearSelectionSilent_DeselectsAllImages()
    {
        // Arrange
        var image1 = new DatasetImageViewModel { IsSelected = true };
        var image2 = new DatasetImageViewModel { IsSelected = true };
        _sut.DatasetImages.Add(image1);
        _sut.DatasetImages.Add(image2);

        // Act
        _sut.ClearSelectionSilent();

        // Assert
        image1.IsSelected.Should().BeFalse();
        image2.IsSelected.Should().BeFalse();
        _sut.SelectionCount.Should().Be(0);
    }

    [Fact]
    public void LastClickedImage_CanBeSetAndRetrieved()
    {
        // Arrange
        var image = new DatasetImageViewModel();

        // Act
        _sut.LastClickedImage = image;

        // Assert
        _sut.LastClickedImage.Should().Be(image);
    }

    #endregion

    #region UI State Tests

    [Fact]
    public void IsLoading_CanBeSetAndRetrieved()
    {
        // Act
        _sut.IsLoading = true;

        // Assert
        _sut.IsLoading.Should().BeTrue();
    }

    [Fact]
    public void IsLoading_RaisesStateChangedEvent()
    {
        // Arrange
        var stateChangedEvents = new List<string>();
        _sut.StateChanged += (_, e) => stateChangedEvents.Add(e.PropertyName);

        // Act
        _sut.IsLoading = true;

        // Assert
        stateChangedEvents.Should().Contain(nameof(IDatasetState.IsLoading));
    }

    [Fact]
    public void StatusMessage_CanBeSetAndRetrieved()
    {
        // Act
        _sut.StatusMessage = "Test status";

        // Assert
        _sut.StatusMessage.Should().Be("Test status");
    }

    [Fact]
    public void StatusMessage_RaisesStateChangedEvent()
    {
        // Arrange
        var stateChangedEvents = new List<string>();
        _sut.StateChanged += (_, e) => stateChangedEvents.Add(e.PropertyName);

        // Act
        _sut.StatusMessage = "Test status";

        // Assert
        stateChangedEvents.Should().Contain(nameof(IDatasetState.StatusMessage));
    }

    [Fact]
    public void HasUnsavedChanges_CanBeSetAndRetrieved()
    {
        // Act
        _sut.HasUnsavedChanges = true;

        // Assert
        _sut.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void HasUnsavedChanges_RaisesStateChangedEvent()
    {
        // Arrange
        var stateChangedEvents = new List<string>();
        _sut.StateChanged += (_, e) => stateChangedEvents.Add(e.PropertyName);

        // Act
        _sut.HasUnsavedChanges = true;

        // Assert
        stateChangedEvents.Should().Contain(nameof(IDatasetState.HasUnsavedChanges));
    }

    [Fact]
    public void IsFileDialogOpen_CanBeSetAndRetrieved()
    {
        // Act
        _sut.IsFileDialogOpen = true;

        // Assert
        _sut.IsFileDialogOpen.Should().BeTrue();
    }

    [Fact]
    public void SelectedTabIndex_CanBeSetAndRetrieved()
    {
        // Act
        _sut.SelectedTabIndex = 1;

        // Assert
        _sut.SelectedTabIndex.Should().Be(1);
    }

    [Fact]
    public void SelectedTabIndex_RaisesStateChangedEvent()
    {
        // Arrange
        var stateChangedEvents = new List<string>();
        _sut.StateChanged += (_, e) => stateChangedEvents.Add(e.PropertyName);

        // Act
        _sut.SelectedTabIndex = 1;

        // Assert
        stateChangedEvents.Should().Contain(nameof(IDatasetState.SelectedTabIndex));
    }

    #endregion

    #region HasNoImages Tests

    [Fact]
    public void HasNoImages_WhenViewingDatasetAndNoImages_ReturnsTrue()
    {
        // Arrange
        var dataset = CreateTestDatasetCard();
        _sut.SetActiveDataset(dataset);
        _sut.DatasetImages.Clear();

        // Assert
        _sut.HasNoImages.Should().BeTrue();
    }

    [Fact]
    public void HasNoImages_WhenViewingDatasetAndHasImages_ReturnsFalse()
    {
        // Arrange
        var dataset = CreateTestDatasetCard();
        _sut.SetActiveDataset(dataset);
        _sut.DatasetImages.Add(new DatasetImageViewModel());

        // Assert
        _sut.HasNoImages.Should().BeFalse();
    }

    [Fact]
    public void HasNoImages_WhenNotViewingDataset_ReturnsFalse()
    {
        // Arrange
        _sut.SetActiveDataset(null);
        _sut.DatasetImages.Clear();

        // Assert
        _sut.HasNoImages.Should().BeFalse();
    }

    [Fact]
    public void DatasetImages_CollectionChanged_RaisesHasNoImagesStateChanged()
    {
        // Arrange
        var dataset = CreateTestDatasetCard();
        _sut.SetActiveDataset(dataset);
        var stateChangedEvents = new List<string>();
        _sut.StateChanged += (_, e) => stateChangedEvents.Add(e.PropertyName);

        // Act
        _sut.DatasetImages.Add(new DatasetImageViewModel());

        // Assert
        stateChangedEvents.Should().Contain(nameof(IDatasetState.HasNoImages));
    }

    #endregion

    #region FlattenVersions Tests

    [Fact]
    public void FlattenVersions_CanBeSetAndRetrieved()
    {
        // Act
        _sut.FlattenVersions = true;

        // Assert
        _sut.FlattenVersions.Should().BeTrue();
    }

    [Fact]
    public void FlattenVersions_RaisesStateChangedEvent()
    {
        // Arrange
        var stateChangedEvents = new List<string>();
        _sut.StateChanged += (_, e) => stateChangedEvents.Add(e.PropertyName);

        // Act
        _sut.FlattenVersions = true;

        // Assert
        stateChangedEvents.Should().Contain(nameof(IDatasetState.FlattenVersions));
    }

    #endregion

    #region SelectedVersion Tests

    [Fact]
    public void SelectedVersion_CanBeSetAndRetrieved()
    {
        // Act
        _sut.SelectedVersion = 5;

        // Assert
        _sut.SelectedVersion.Should().Be(5);
    }

    [Fact]
    public void SelectedVersion_RaisesStateChangedEvent()
    {
        // Arrange
        var stateChangedEvents = new List<string>();
        _sut.StateChanged += (_, e) => stateChangedEvents.Add(e.PropertyName);

        // Act
        _sut.SelectedVersion = 5;

        // Assert
        stateChangedEvents.Should().Contain(nameof(IDatasetState.SelectedVersion));
    }

    #endregion

    #region Image Edit State Tests

    [Fact]
    public void SelectedEditorDataset_CanBeSetAndRetrieved()
    {
        // Arrange
        var dataset = CreateTestDatasetCard();

        // Act
        _sut.SelectedEditorDataset = dataset;

        // Assert
        _sut.SelectedEditorDataset.Should().Be(dataset);
    }

    [Fact]
    public void SelectedEditorDataset_RaisesStateChangedEvent()
    {
        // Arrange
        var stateChangedEvents = new List<string>();
        _sut.StateChanged += (_, e) => stateChangedEvents.Add(e.PropertyName);
        var dataset = CreateTestDatasetCard();

        // Act
        _sut.SelectedEditorDataset = dataset;

        // Assert
        stateChangedEvents.Should().Contain(nameof(IDatasetState.SelectedEditorDataset));
    }

    [Fact]
    public void SelectedEditorVersion_CanBeSetAndRetrieved()
    {
        // Arrange
        var versionItem = EditorVersionItem.Create(1, 10);

        // Act
        _sut.SelectedEditorVersion = versionItem;

        // Assert
        _sut.SelectedEditorVersion.Should().Be(versionItem);
    }

    [Fact]
    public void SelectedEditorImage_CanBeSetAndRetrieved()
    {
        // Arrange
        var image = new DatasetImageViewModel();

        // Act
        _sut.SelectedEditorImage = image;

        // Assert
        _sut.SelectedEditorImage.Should().Be(image);
    }

    [Fact]
    public void SelectedEditorImage_RaisesStateChangedEvent()
    {
        // Arrange
        var stateChangedEvents = new List<string>();
        _sut.StateChanged += (_, e) => stateChangedEvents.Add(e.PropertyName);
        var image = new DatasetImageViewModel();

        // Act
        _sut.SelectedEditorImage = image;

        // Assert
        stateChangedEvents.Should().Contain(nameof(IDatasetState.SelectedEditorImage));
    }

    #endregion

    #region Property Change Idempotence Tests

    [Fact]
    public void IsLoading_WhenSetToSameValue_DoesNotRaiseStateChanged()
    {
        // Arrange
        _sut.IsLoading = true;
        var stateChangedEvents = new List<string>();
        _sut.StateChanged += (_, e) => stateChangedEvents.Add(e.PropertyName);

        // Act
        _sut.IsLoading = true;

        // Assert
        stateChangedEvents.Should().NotContain(nameof(IDatasetState.IsLoading));
    }

    [Fact]
    public void StatusMessage_WhenSetToSameValue_DoesNotRaiseStateChanged()
    {
        // Arrange
        _sut.StatusMessage = "Test";
        var stateChangedEvents = new List<string>();
        _sut.StateChanged += (_, e) => stateChangedEvents.Add(e.PropertyName);

        // Act
        _sut.StatusMessage = "Test";

        // Assert
        stateChangedEvents.Should().NotContain(nameof(IDatasetState.StatusMessage));
    }

    #endregion

    #region Helper Methods

    private static DatasetCardViewModel CreateTestDatasetCard()
    {
        return new DatasetCardViewModel
        {
            Name = "TestDataset",
            FolderPath = "/test/path",
            CurrentVersion = 1
        };
    }

    #endregion
}
