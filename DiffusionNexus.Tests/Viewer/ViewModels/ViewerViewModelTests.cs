using System;
using System.IO;
using System.Linq;
using System.Threading;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Viewer.ViewModels;

public class ViewerViewModelTests
{
    [Fact]
    public async Task RefreshAsync_LoadsMediaFromEnabledGalleriesAndSubfolders()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(tempRoot, "nested");
        Directory.CreateDirectory(nested);

        var imagePath = Path.Combine(tempRoot, "sample.png");
        var videoPath = Path.Combine(nested, "clip.mp4");
        File.WriteAllText(imagePath, "fake");
        File.WriteAllText(videoPath, "fake");

        var disabledRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(disabledRoot);
        var disabledImage = Path.Combine(disabledRoot, "disabled.png");
        File.WriteAllText(disabledImage, "fake");

        try
        {
            var settings = new AppSettings
            {
                ImageGalleries =
                [
                    new ImageGallery { FolderPath = tempRoot, IsEnabled = true },
                    new ImageGallery { FolderPath = disabledRoot, IsEnabled = false }
                ]
            };

            var settingsService = new Mock<IAppSettingsService>();
            settingsService.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(settings);

            var viewModel = new ViewerViewModel(settingsService.Object);

            await viewModel.RefreshCommand.ExecuteAsync(null);

            viewModel.MediaItems.Should().HaveCount(2);
            viewModel.MediaItems.Select(item => item.FilePath).Should().Contain(imagePath);
            viewModel.MediaItems.Select(item => item.FilePath).Should().Contain(videoPath);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
            Directory.Delete(disabledRoot, true);
        }
    }

    [Fact]
    public async Task RefreshAsync_AssignsFormatTagsForImageAndVideo()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var imagePath = Path.Combine(tempRoot, "sample.png");
        var videoPath = Path.Combine(tempRoot, "clip.mp4");
        File.WriteAllText(imagePath, "fake");
        File.WriteAllText(videoPath, "fake");

        try
        {
            var settings = new AppSettings
            {
                ImageGalleries =
                [
                    new ImageGallery { FolderPath = tempRoot, IsEnabled = true }
                ]
            };

            var settingsService = new Mock<IAppSettingsService>();
            settingsService.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(settings);

            var viewModel = new ViewerViewModel(settingsService.Object);

            await viewModel.RefreshCommand.ExecuteAsync(null);

            var imageItem = viewModel.MediaItems.Single(item => item.FilePath == imagePath);
            var videoItem = viewModel.MediaItems.Single(item => item.FilePath == videoPath);

            imageItem.FormatTag.Should().Be(SupportedMediaTypes.ImageExtensionsDisplay);
            videoItem.FormatTag.Should().Be(SupportedMediaTypes.VideoExtensionsDisplay);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }
}
