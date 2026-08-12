using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Tests for the module-level refresh button of the LoRA Dataset Helper:
/// the shell dispatches to the active tab via <see cref="IRefreshableTab"/>,
/// and only tabs implementing the interface enable the button.
/// </summary>
public class LoraDatasetHelperRefreshTests
{
    private readonly Mock<IAppSettingsService> _settings = new();
    private readonly Mock<IDatasetStorageService> _storage = new();
    private readonly Mock<IDatasetState> _state = new();

    public LoraDatasetHelperRefreshTests()
    {
        _settings.Setup(s => s.GetSettingsAsync()).ReturnsAsync(new AppSettings());
        _state.Setup(s => s.Datasets).Returns([]);
        _state.Setup(s => s.GroupedDatasets).Returns([]);
        _state.Setup(s => s.DatasetImages).Returns([]);
        _state.Setup(s => s.AvailableCategories).Returns([]);
        _state.Setup(s => s.AvailableVersions).Returns([]);
    }

    private LoraDatasetHelperViewModel CreateShell()
        => new(_settings.Object, _storage.Object, new DatasetEventAggregator(), _state.Object);

    private DatasetManagementViewModel CreateDatasetManagement()
        => new(_settings.Object, _storage.Object, new DatasetEventAggregator(), _state.Object);

    [Fact]
    public void WhenDatasetManagementTabIsActive_ThenRefreshIsEnabled()
    {
        var shell = CreateShell();

        shell.SelectedTabIndex = 0;

        shell.RefreshCurrentTabCommand.CanExecute(null).Should().BeTrue();
    }

    [Theory]
    [InlineData(1)] // Image Edit
    [InlineData(2)] // Captioning
    [InlineData(3)] // Batch Crop/Scale
    [InlineData(4)] // Batch Upscale
    public void WhenActiveTabDoesNotSupportRefresh_ThenRefreshIsDisabled(int tabIndex)
    {
        var shell = CreateShell();

        shell.SelectedTabIndex = tabIndex;

        shell.RefreshCurrentTabCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void WhenDatasetManagementIsLoading_ThenRefreshIsDisabled()
    {
        _state.Setup(s => s.IsLoading).Returns(true);
        var shell = CreateShell();

        shell.SelectedTabIndex = 0;

        shell.RefreshCurrentTabCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task WhenRefreshExecutesOnDatasetManagement_ThenTheDatasetListIsReloaded()
    {
        var shell = CreateShell();
        shell.SelectedTabIndex = 0;

        await shell.RefreshCurrentTabCommand.ExecuteAsync(null);

        // The list-view refresh path re-reads settings to re-scan storage
        _settings.Verify(s => s.GetSettingsAsync(), Times.AtLeastOnce);
        _state.VerifySet(s => s.StatusMessage = "Refreshed.");
    }

    [Fact]
    public async Task WhenRefreshFails_ThenTheErrorIsSurfacedAndTheButtonReEnables()
    {
        _settings.Setup(s => s.GetSettingsAsync()).ThrowsAsync(new InvalidOperationException("boom"));
        var shell = CreateShell();
        shell.SelectedTabIndex = 0;

        await shell.RefreshCurrentTabCommand.ExecuteAsync(null);

        _state.VerifySet(s => s.StatusMessage = "Refresh failed: boom");
        shell.RefreshCurrentTabCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task WhenDatasetManagementRefreshesInListView_ThenItRescansStorage()
    {
        _state.Setup(s => s.IsViewingDataset).Returns(false);
        var vm = CreateDatasetManagement();

        await vm.RefreshAsync();

        _settings.Verify(s => s.GetSettingsAsync(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task WhenDatasetManagementIsLoading_ThenRefreshIsANoOp()
    {
        _state.Setup(s => s.IsLoading).Returns(true);
        var vm = CreateDatasetManagement();

        vm.CanRefresh.Should().BeFalse();
        await vm.RefreshAsync();

        _settings.Verify(s => s.GetSettingsAsync(), Times.Never);
    }
}
