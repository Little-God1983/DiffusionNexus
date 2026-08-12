using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.ViewModels;

public sealed class BackgroundRemovalViewModelTests
{
    private static Mock<IBackgroundRemovalService> MakeService(bool downloadResult = true)
    {
        var mock = new Mock<IBackgroundRemovalService>();
        mock.Setup(s => s.DownloadModelAsync(It.IsAny<IProgress<ModelDownloadProgress>>(), It.IsAny<CancellationToken>()))
            .Callback<IProgress<ModelDownloadProgress>?, CancellationToken>((p, _) =>
                p?.Report(new ModelDownloadProgress(100, 100, "Download complete")))
            .ReturnsAsync(downloadResult);
        return mock;
    }

    [Fact]
    public async Task DownloadModelCommand_WithCoordinator_EnqueuesThroughIt()
    {
        var service = MakeService();
        var coordinator = new Mock<IDownloadCoordinator>();
        coordinator
            .Setup(c => c.EnqueueAsync(
                It.IsAny<string>(),
                It.IsAny<Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>, CancellationToken>(
                (name, action, ct) => action(new Progress<DownloadTaskProgress>(), ct));

        var vm = new BackgroundRemovalViewModel(() => true, _ => { }, service.Object, coordinator.Object);

        await vm.DownloadModelCommand.ExecuteAsync(null);

        coordinator.Verify(c => c.EnqueueAsync(
            It.Is<string>(n => n.Contains("RMBG")),
            It.IsAny<Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.DownloadModelAsync(It.IsAny<IProgress<ModelDownloadProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DownloadModelCommand_WithoutCoordinator_StillDownloadsDirectly()
    {
        var service = MakeService();
        var vm = new BackgroundRemovalViewModel(() => true, _ => { }, service.Object, downloadCoordinator: null);

        await vm.DownloadModelCommand.ExecuteAsync(null);

        service.Verify(s => s.DownloadModelAsync(It.IsAny<IProgress<ModelDownloadProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
