using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

public class EditorEngineGenerateTests
{
    private static string TempImage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dn-test-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        return path;
    }

    private static Mock<IComfyUiClientProvider> Unavailable(string message)
    {
        var provider = new Mock<IComfyUiClientProvider>();
        provider.Setup(p => p.AcquireAsync(It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ComfyUiUnavailableException(message));
        return provider;
    }

    [Fact]
    public async Task Inpaint_EngineFailsToStart_ShowsTheReason_AndIsNotLeftBusy()
    {
        var messages = new List<string?>();
        var vm = new InpaintingViewModel(() => true, _ => { },
            Unavailable("Diffusion Nexus Engine failed to start: exit code 1").Object, eventAggregator: null);
        vm.StatusMessageChanged += (_, m) => messages.Add(m);

        await vm.ProcessInpaintAsync(TempImage());

        vm.HasError.Should().BeTrue();
        vm.IsBusy.Should().BeFalse();
        vm.ProgressDisplayText.Should().Be("Diffusion Nexus Engine failed to start: exit code 1");
        messages.Should().Contain("Diffusion Nexus Engine failed to start: exit code 1");
    }

    [Fact]
    public async Task Outpaint_EngineNotInstalled_ShowsTheReason_AndIsNotLeftBusy()
    {
        var vm = new OutpaintingViewModel(() => true, () => 512, () => 512, _ => { },
            Unavailable("Diffusion Nexus Engine is not installed. Install it in the Installation Manager.").Object);

        await vm.ProcessOutpaintAsync(TempImage(), useVision: false, 64, 0, 64, 0);

        vm.HasError.Should().BeTrue();
        vm.IsBusy.Should().BeFalse();
        vm.ProgressDisplayText.Should().StartWith("Diffusion Nexus Engine is not installed");
    }

    [Fact]
    public async Task Inpaint_FailureAfterTheEngineStarted_UsesTheEngineWording()
    {
        var client = new Mock<IComfyUIWrapperService>();
        client.Setup(c => c.UploadImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var vm = new InpaintingViewModel(() => true, _ => { },
            InpaintingViewModelGGUFResolutionTests.Provider(client.Object, ComfyUiServerMode.Engine), eventAggregator: null);

        await vm.ProcessInpaintAsync(TempImage());

        vm.ProgressDisplayText.Should().Be("Generation failed – is the Diffusion Nexus Engine running?");
    }

    [Fact]
    public async Task SwitchingTheServerInSettings_WhileThePanelIsOpen_RechecksReadiness()
    {
        var events = new Mock<IDatasetEventAggregator>();
        var readiness = new Mock<IFeatureReadinessService>();
        readiness.Setup(r => r.CheckAsync(Feature.Inpainting, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FeatureReadinessResult
            {
                Feature = Feature.Inpainting, Backend = BackendKind.Engine, ActiveBackendName = "Diffusion Nexus Engine",
                IsBackendOnline = true, IsReady = true, MissingRequirements = [], Warnings = []
            });
        var vm = new InpaintingViewModel(() => true, _ => { }, comfyUiClientProvider: null, events.Object, readiness.Object);
        vm.IsPanelOpen = true;
        await Task.Delay(50); // the open-panel check is fire-and-forget

        events.Raise(e => e.SettingsSaved += null, events.Object, new SettingsSavedEventArgs());
        await Task.Delay(50);

        readiness.Verify(r => r.CheckAsync(Feature.Inpainting, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SettingsSaved_WithThePanelClosed_DoesNotCheck()
    {
        var events = new Mock<IDatasetEventAggregator>();
        var readiness = new Mock<IFeatureReadinessService>();
        _ = new OutpaintingViewModel(() => true, () => 512, () => 512, _ => { },
            readinessService: readiness.Object, eventAggregator: events.Object);

        events.Raise(e => e.SettingsSaved += null, events.Object, new SettingsSavedEventArgs());
        await Task.Delay(50);

        readiness.Verify(r => r.CheckAsync(It.IsAny<Feature>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
