using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.UI.ViewModels;

public class DownloadLoraDialogViewModelTests
{
    [Fact]
    public void OkEnabledRequiresValidUrlAndTarget()
    {
        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            var targets = new[] { new DownloadTargetOption("Test", tempDir.FullName) };
            var vm = new DownloadLoraDialogViewModel(new CivitaiUrlParser(), Mock.Of<ILoraDownloader>(), targets, null, null, null);

            vm.IsOkEnabled.Should().BeFalse();

            vm.CivitaiUrl = "https://civitai.com/models/1";
            vm.SelectedTarget = vm.Targets.First();

            vm.IsOkEnabled.Should().BeTrue();
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExecutesDownloadUsingParsedIdentifiers()
    {
        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            var parser = new CivitaiUrlParser();
            var downloader = new Mock<ILoraDownloader>(MockBehavior.Strict);
            var plan = new LoraDownloadPlan(372465, 914390, "model.safetensors", new Uri("https://example.com"), 1_024);

            downloader
                .Setup(d => d.PrepareAsync(372465, 914390, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(plan);

            downloader
                .Setup(d => d.DownloadAsync(plan, It.IsAny<string>(), It.IsAny<IProgress<LoraDownloadProgress>>(), It.IsAny<CancellationToken>()))
                .Callback<LoraDownloadPlan, string, IProgress<LoraDownloadProgress>, CancellationToken>((_, _, progress, _) =>
                {
                    progress.Report(new LoraDownloadProgress(512, 1_024, 50, 2.5, TimeSpan.FromSeconds(1)));
                })
                .ReturnsAsync(new LoraDownloadResult(true, Path.Combine(tempDir.FullName, plan.FileName)));

            string? savedPath = null;
            var vm = new DownloadLoraDialogViewModel(parser, downloader.Object, [new DownloadTargetOption("Target", tempDir.FullName)], null, null, path =>
            {
                savedPath = path;
                return Task.CompletedTask;
            });

            vm.SelectedTarget = vm.Targets.First();
            vm.CivitaiUrl = "https://civitai.com/models/372465?modelVersionId=914390";

            await vm.OkCommand.ExecuteAsync(null);

            vm.WasSuccessful.Should().BeTrue();
            savedPath.Should().Be(tempDir.FullName);

            downloader.VerifyAll();
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CancelCommandStopsInFlightDownload()
    {
        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            var parser = new CivitaiUrlParser();
            var downloader = new Mock<ILoraDownloader>(MockBehavior.Strict);
            var plan = new LoraDownloadPlan(1, 2, "model.safetensors", new Uri("https://example.com"), null);

            downloader
                .Setup(d => d.PrepareAsync(1, 2, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(plan);

            downloader
                .Setup(d => d.DownloadAsync(plan, It.IsAny<string>(), It.IsAny<IProgress<LoraDownloadProgress>>(), It.IsAny<CancellationToken>()))
                .Returns<LoraDownloadPlan, string, IProgress<LoraDownloadProgress>, CancellationToken>(async (p, path, progress, token) =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                    return new LoraDownloadResult(true, path);
                });

            var vm = new DownloadLoraDialogViewModel(parser, downloader.Object, [new DownloadTargetOption("Target", tempDir.FullName)], null, null, null);
            vm.SelectedTarget = vm.Targets.First();
            vm.CivitaiUrl = "https://civitai.com/models/1?modelVersionId=2";

            var execution = vm.OkCommand.ExecuteAsync(null);
            await Task.Delay(100);
            vm.CancelCommand.Execute(null);

            await execution;

            vm.WasSuccessful.Should().BeFalse();
            downloader.Verify(d => d.DownloadAsync(plan, It.IsAny<string>(), It.IsAny<IProgress<LoraDownloadProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
