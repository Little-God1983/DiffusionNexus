using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.UI.ViewModels;

public class DownloadLoraDialogViewModelTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public DownloadLoraDialogViewModelTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task Ok_Is_Enabled_When_Url_And_Target_Selected()
    {
        var downloader = new Mock<ILoraDownloader>(MockBehavior.Strict);
        downloader.Setup(d => d.DownloadAsync(It.IsAny<LoraDownloadRequest>(), It.IsAny<IProgress<LoraDownloadProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoraDownloadResult(LoraDownloadResultStatus.Completed, Path.Combine(_tempDirectory, "file")));

        var sourcesProvider = new Mock<ILoraSourcesProvider>();
        sourcesProvider.Setup(p => p.GetSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LoraSourceInfo> { new("Temp", _tempDirectory) });

        var userSettings = new Mock<IUserSettings>();
        userSettings.Setup(s => s.GetLastDownloadLoraTargetAsync()).ReturnsAsync((string?)null);
        userSettings.Setup(s => s.SetLastDownloadLoraTargetAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        var vm = new DownloadLoraDialogViewModel(new CivitaiUrlParser(), downloader.Object, sourcesProvider.Object, userSettings.Object, string.Empty);
        await vm.InitializeAsync();

        vm.CivitaiUrl = "https://civitai.com/models/372465?modelVersionId=914390";

        vm.IsOkEnabled.Should().BeTrue();

        await vm.OkCommand.ExecuteAsync(null);
        await Task.Delay(50);

        downloader.Verify(d => d.DownloadAsync(It.Is<LoraDownloadRequest>(r => r.ModelId == 372465 && r.ModelVersionId == 914390), It.IsAny<IProgress<LoraDownloadProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        userSettings.Verify(s => s.SetLastDownloadLoraTargetAsync(_tempDirectory), Times.Once);
    }

    [Fact]
    public async Task Invalid_Url_Shows_Error()
    {
        var downloader = new Mock<ILoraDownloader>(MockBehavior.Strict);
        var sourcesProvider = new Mock<ILoraSourcesProvider>();
        sourcesProvider.Setup(p => p.GetSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LoraSourceInfo> { new("Temp", _tempDirectory) });

        var userSettings = new Mock<IUserSettings>();
        userSettings.Setup(s => s.GetLastDownloadLoraTargetAsync()).ReturnsAsync((string?)null);

        var vm = new DownloadLoraDialogViewModel(new CivitaiUrlParser(), downloader.Object, sourcesProvider.Object, userSettings.Object, string.Empty);
        await vm.InitializeAsync();

        vm.CivitaiUrl = "https://notcivitai.com/foo";

        vm.ErrorMessage.Should().NotBeNull();
        vm.IsOkEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Progress_Updates_Set_Speed_And_Eta()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new ImmediateSynchronizationContext());
        try
        {
            var downloader = new Mock<ILoraDownloader>();
            downloader.Setup(d => d.DownloadAsync(It.IsAny<LoraDownloadRequest>(), It.IsAny<IProgress<LoraDownloadProgress>>(), It.IsAny<CancellationToken>()))
                .Returns<LoraDownloadRequest, IProgress<LoraDownloadProgress>, CancellationToken>(async (req, progress, _) =>
                {
                    progress.Report(new LoraDownloadProgress(512 * 1024, 1024 * 1024));
                    await Task.Delay(10);
                    progress.Report(new LoraDownloadProgress(1024 * 1024, 1024 * 1024));
                    return new LoraDownloadResult(LoraDownloadResultStatus.Completed, Path.Combine(_tempDirectory, "file"));
                });

            var sourcesProvider = new Mock<ILoraSourcesProvider>();
            sourcesProvider.Setup(p => p.GetSourcesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<LoraSourceInfo> { new("Temp", _tempDirectory) });

            var userSettings = new Mock<IUserSettings>();
            userSettings.Setup(s => s.GetLastDownloadLoraTargetAsync()).ReturnsAsync((string?)null);
            userSettings.Setup(s => s.SetLastDownloadLoraTargetAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

            var timestamps = new Queue<DateTimeOffset>(new[]
            {
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddSeconds(1)
            });

            var vm = new DownloadLoraDialogViewModel(new CivitaiUrlParser(), downloader.Object, sourcesProvider.Object, userSettings.Object, string.Empty, () => timestamps.Count > 0 ? timestamps.Dequeue() : DateTimeOffset.UtcNow);
            await vm.InitializeAsync();
            vm.CivitaiUrl = "https://civitai.com/models/372465?modelVersionId=914390";

            await vm.OkCommand.ExecuteAsync(null);

            vm.Progress.Should().BeApproximately(100d, 0.1);
            vm.ProgressText.Should().Contain("1.00 MB");
            SpinWait.SpinUntil(() => !string.IsNullOrEmpty(vm.SpeedText), TimeSpan.FromSeconds(1)).Should().BeTrue();
            vm.SpeedText.Should().NotBeNullOrEmpty();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            d(state);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
