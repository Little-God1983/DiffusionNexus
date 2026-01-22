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
        viewModel.MediaItems.First().FilePath.Should().Be(olderFile);

        viewModel.SelectedSortOption = "Creation date";
        viewModel.MediaItems.First().FilePath.Should().Be(newerFile);
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
