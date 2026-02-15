using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Unit tests for <see cref="GenerationGalleryViewModel"/>.
/// </summary>
public class GenerationGalleryViewModelTests : IDisposable
{
    private readonly List<string> _tempPaths = [];

    [Fact]
    public async Task LoadMediaAsync_NoEnabledSources_ShowsConfigurationMessage()
    {
        var settings = new AppSettings
        {
            ImageGalleries = new List<ImageGallery>
            {
                new() { FolderPath = "C:\\Missing", IsEnabled = false }
            }
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        var mockDatasetState = new Mock<IDatasetState>();
        var viewModel = new GenerationGalleryViewModel(mockSettings.Object, mockEventAggregator.Object, mockDatasetState.Object, null);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.HasNoMedia.Should().BeTrue();
        viewModel.MediaItems.Should().BeEmpty();
        viewModel.NoMediaMessage.Should().Contain("Settings");
    }

    [Fact]
    public async Task LoadMediaAsync_LoadsMediaFromEnabledSourcesOnly()
    {
        var enabledPath = CreateTempDirectory();
        var disabledPath = CreateTempDirectory();

        var enabledImage = Path.Combine(enabledPath, "alpha.png");
        var enabledVideo = Path.Combine(enabledPath, "beta.mp4");
        var disabledImage = Path.Combine(disabledPath, "ignored.jpg");

        File.WriteAllText(enabledImage, "test");
        File.WriteAllText(enabledVideo, "test");
        File.WriteAllText(disabledImage, "test");

        var settings = new AppSettings
        {
            ImageGalleries = new List<ImageGallery>
            {
                new() { FolderPath = enabledPath, IsEnabled = true, Order = 0 },
                new() { FolderPath = disabledPath, IsEnabled = false, Order = 1 }
            }
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        var mockDatasetState = new Mock<IDatasetState>();
        var viewModel = new GenerationGalleryViewModel(mockSettings.Object, mockEventAggregator.Object, mockDatasetState.Object, null);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.MediaItems.Select(item => item.FilePath).Should().Contain(enabledImage);
        viewModel.MediaItems.Select(item => item.FilePath).Should().Contain(enabledVideo);
        viewModel.MediaItems.Select(item => item.FilePath).Should().NotContain(disabledImage);
    }

    [Fact]
    public async Task SelectedSortOption_SortsByNameAndCreationDate()
    {
        var galleryPath = CreateTempDirectory();
        var olderFile = Path.Combine(galleryPath, "alpha.png");
        var newerFile = Path.Combine(galleryPath, "beta.png");

        File.WriteAllText(olderFile, "test");
        File.WriteAllText(newerFile, "test");

        var oldTime = DateTime.UtcNow.AddDays(-2);
        var newTime = DateTime.UtcNow.AddDays(-1);
        File.SetCreationTimeUtc(olderFile, oldTime);
        File.SetCreationTimeUtc(newerFile, newTime);

        var settings = new AppSettings
        {
            ImageGalleries = new List<ImageGallery>
            {
                new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }
            }
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        var mockDatasetState = new Mock<IDatasetState>();
        var viewModel = new GenerationGalleryViewModel(mockSettings.Object, mockEventAggregator.Object, mockDatasetState.Object, null);
        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.SelectedSortOption = "Name";
        await viewModel.WaitForSortingAsync();
        viewModel.MediaItems.First().FilePath.Should().Be(olderFile);

        viewModel.SelectedSortOption = "Creation date";
        await viewModel.WaitForSortingAsync();
        viewModel.MediaItems.First().FilePath.Should().Be(newerFile);
    }

    [Fact]
    public void OpenFolderInExplorerCommand_WhenNoSelection_CannotExecute()
    {
        var galleryPath = CreateTempDirectory();
        var image = Path.Combine(galleryPath, "test.png");
        File.WriteAllText(image, "test");

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null);

        viewModel.OpenFolderInExplorerCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task OpenFolderInExplorerCommand_WhenImageSelected_CanExecute()
    {
        var galleryPath = CreateTempDirectory();
        var image = Path.Combine(galleryPath, "test.png");
        File.WriteAllText(image, "test");

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.SelectWithModifiers(viewModel.MediaItems[0], false, false);

        viewModel.OpenFolderInExplorerCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task OpenFolderInExplorerCommand_MoreThan3Origins_ShowsConfirmDialog()
    {
        // Create 4 distinct folders to trigger the warning
        var folders = Enumerable.Range(0, 4).Select(_ => CreateTempDirectory()).ToList();
        foreach (var folder in folders)
        {
            File.WriteAllText(Path.Combine(folder, "img.png"), "test");
        }

        var settings = new AppSettings
        {
            ImageGalleries = folders.Select((f, i) => new ImageGallery
            {
                FolderPath = f, IsEnabled = true, Order = i
            }).ToList()
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var mockDialog = new Mock<IDialogService>();
        mockDialog.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null);
        viewModel.DialogService = mockDialog.Object;
        viewModel.ProcessLauncher = new Mock<IProcessLauncher>().Object;

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        // Select all items (one per folder = 4 distinct origins)
        viewModel.SelectWithModifiers(viewModel.MediaItems[0], false, false);
        for (var i = 1; i < viewModel.MediaItems.Count; i++)
        {
            viewModel.SelectWithModifiers(viewModel.MediaItems[i], false, true);
        }

        await viewModel.OpenFolderInExplorerCommand.ExecuteAsync(null);

        mockDialog.Verify(d => d.ShowConfirmAsync(
            "Open Multiple Folders",
            It.Is<string>(msg => msg.Contains("4"))), Times.Once);
    }

    [Fact]
    public async Task OpenFolderInExplorerCommand_3OrFewerOrigins_NoConfirmDialog()
    {
        // Create 2 distinct folders — should not trigger the warning
        var folders = Enumerable.Range(0, 2).Select(_ => CreateTempDirectory()).ToList();
        foreach (var folder in folders)
        {
            File.WriteAllText(Path.Combine(folder, "img.png"), "test");
        }

        var settings = new AppSettings
        {
            ImageGalleries = folders.Select((f, i) => new ImageGallery
            {
                FolderPath = f, IsEnabled = true, Order = i
            }).ToList()
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var mockDialog = new Mock<IDialogService>();

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null);
        viewModel.DialogService = mockDialog.Object;
        viewModel.ProcessLauncher = new Mock<IProcessLauncher>().Object;

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.SelectWithModifiers(viewModel.MediaItems[0], false, false);
        for (var i = 1; i < viewModel.MediaItems.Count; i++)
        {
            viewModel.SelectWithModifiers(viewModel.MediaItems[i], false, true);
        }

        await viewModel.OpenFolderInExplorerCommand.ExecuteAsync(null);

        mockDialog.Verify(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    private string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiffusionNexusTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempPaths.Add(root);
        return root;
    }
}
