using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Unit tests verifying IThumbnailAware behavior across ViewModels.
/// </summary>
public class ThumbnailAwareViewModelTests
{
    private readonly Mock<IThumbnailOrchestrator> _mockOrchestrator = new();

    [Fact]
    public void WhenDatasetManagementActivated_ThenSetsActiveOwnerOnOrchestrator()
    {
        var vm = CreateDatasetManagementViewModel();

        vm.OnThumbnailActivated();

        _mockOrchestrator.Verify(o => o.SetActiveOwner(vm.OwnerToken), Times.Once);
    }

    [Fact]
    public void WhenDatasetManagementDeactivated_ThenCancelsRequestsOnOrchestrator()
    {
        var vm = CreateDatasetManagementViewModel();

        vm.OnThumbnailDeactivated();

        _mockOrchestrator.Verify(o => o.CancelRequests(vm.OwnerToken), Times.Once);
    }

    [Fact]
    public void WhenImageEditActivated_ThenSetsActiveOwnerOnOrchestrator()
    {
        var vm = CreateImageEditTabViewModel();

        vm.OnThumbnailActivated();

        _mockOrchestrator.Verify(o => o.SetActiveOwner(vm.OwnerToken), Times.Once);
    }

    [Fact]
    public void WhenImageEditDeactivated_ThenCancelsRequestsOnOrchestrator()
    {
        var vm = CreateImageEditTabViewModel();

        vm.OnThumbnailDeactivated();

        _mockOrchestrator.Verify(o => o.CancelRequests(vm.OwnerToken), Times.Once);
    }

    [Fact]
    public void WhenGenerationGalleryActivated_ThenSetsActiveOwnerOnOrchestrator()
    {
        var vm = CreateGenerationGalleryViewModel();

        vm.OnThumbnailActivated();

        _mockOrchestrator.Verify(o => o.SetActiveOwner(vm.OwnerToken), Times.Once);
    }

    [Fact]
    public void WhenGenerationGalleryDeactivated_ThenCancelsRequestsOnOrchestrator()
    {
        var vm = CreateGenerationGalleryViewModel();

        vm.OnThumbnailDeactivated();

        _mockOrchestrator.Verify(o => o.CancelRequests(vm.OwnerToken), Times.Once);
    }

    [Fact]
    public void WhenImageComparerActivated_ThenSetsActiveOwnerOnOrchestrator()
    {
        var vm = new ImageCompareViewModel(null, _mockOrchestrator.Object);

        vm.OnThumbnailActivated();

        _mockOrchestrator.Verify(o => o.SetActiveOwner(vm.OwnerToken), Times.Once);
    }

    [Fact]
    public void WhenImageComparerDeactivated_ThenCancelsRequestsOnOrchestrator()
    {
        var vm = new ImageCompareViewModel(null, _mockOrchestrator.Object);

        vm.OnThumbnailDeactivated();

        _mockOrchestrator.Verify(o => o.CancelRequests(vm.OwnerToken), Times.Once);
    }

    [Fact]
    public void WhenOrchestratorIsNull_ThenActivationDoesNotThrow()
    {
        // ViewModels with null orchestrator should not throw
        var vm = new DatasetManagementViewModel(
            new Mock<DiffusionNexus.Domain.Services.IAppSettingsService>().Object,
            new Mock<IDatasetStorageService>().Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null, null, null, null, thumbnailOrchestrator: null);

        var act = () => vm.OnThumbnailActivated();
        act.Should().NotThrow();

        var act2 = () => vm.OnThumbnailDeactivated();
        act2.Should().NotThrow();
    }

    [Fact]
    public void OwnerTokens_AreUniquePerViewModelType()
    {
        var datasetMgmt = CreateDatasetManagementViewModel();
        var imageEdit = CreateImageEditTabViewModel();
        var gallery = CreateGenerationGalleryViewModel();
        var comparer = new ImageCompareViewModel(null, _mockOrchestrator.Object);

        // Each ViewModel type should have a distinct owner token name
        var names = new[]
        {
            datasetMgmt.OwnerToken.Name,
            imageEdit.OwnerToken.Name,
            gallery.OwnerToken.Name,
            comparer.OwnerToken.Name
        };

        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void OwnerToken_IsSameAcrossMultipleAccessesOnSameInstance()
    {
        var vm = CreateDatasetManagementViewModel();

        var token1 = vm.OwnerToken;
        var token2 = vm.OwnerToken;

        token1.Should().BeSameAs(token2);
    }

    #region Factory Helpers

    private DatasetManagementViewModel CreateDatasetManagementViewModel()
    {
        return new DatasetManagementViewModel(
            new Mock<DiffusionNexus.Domain.Services.IAppSettingsService>().Object,
            new Mock<IDatasetStorageService>().Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null, null, null, null,
            thumbnailOrchestrator: _mockOrchestrator.Object);
    }

    private ImageEditTabViewModel CreateImageEditTabViewModel()
    {
        var mockState = new Mock<IDatasetState>();
        mockState.Setup(s => s.Datasets).Returns(new System.Collections.ObjectModel.ObservableCollection<DatasetCardViewModel>());

        return new ImageEditTabViewModel(
            new Mock<IDatasetEventAggregator>().Object,
            mockState.Object,
            null, null, null,
            thumbnailOrchestrator: _mockOrchestrator.Object);
    }

    private GenerationGalleryViewModel CreateGenerationGalleryViewModel()
    {
        return new GenerationGalleryViewModel(
            new Mock<DiffusionNexus.Domain.Services.IAppSettingsService>().Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            _mockOrchestrator.Object);
    }

    #endregion
}
