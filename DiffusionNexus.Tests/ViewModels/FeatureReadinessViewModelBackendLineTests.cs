using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

public class FeatureReadinessViewModelBackendLineTests
{
    private readonly Mock<IFeatureReadinessService> _service = new();
    private readonly Mock<IDatasetEventAggregator> _events = new();

    private void Returns(Feature feature, BackendKind kind, string name, params string[] missing) =>
        _service.Setup(s => s.CheckAsync(feature, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FeatureReadinessResult
            {
                Feature = feature, Backend = kind, ActiveBackendName = name,
                IsBackendOnline = true, IsReady = missing.Length == 0,
                MissingRequirements = missing, Warnings = []
            });

    [Fact]
    public async Task ReadyOnTheEngine_ShowsRunningOn_WithTheChangeLink()
    {
        Returns(Feature.Inpainting, BackendKind.Engine, "Diffusion Nexus Engine");
        var vm = new FeatureReadinessViewModel(_service.Object, Feature.Inpainting, _events.Object);

        await vm.CheckReadinessAsync();

        vm.ShowBackendLine.Should().BeTrue();
        vm.ActiveBackendName.Should().Be("Diffusion Nexus Engine");
        vm.ShowChangeLink.Should().BeTrue();
        vm.ShowInstallOnEngine.Should().BeFalse();
    }

    [Fact]
    public async Task MissingOnTheEngine_OffersTheInstallLink_ForThatFeaturesRow()
    {
        Returns(Feature.Outpaint, BackendKind.Engine, "Diffusion Nexus Engine", "Model missing on the Engine: qwen_image_vae");
        var vm = new FeatureReadinessViewModel(_service.Object, Feature.Outpaint, _events.Object);

        await vm.CheckReadinessAsync();

        vm.ShowInstallOnEngine.Should().BeTrue();
        vm.InstallOnEngineLabel.Should().Be("Install Inpaint & Outpaint");

        vm.InstallOnEngineCommand.Execute(null);
        _events.Verify(e => e.PublishNavigateToEngineFeatures(
            It.Is<NavigateToEngineFeaturesEventArgs>(a => a.Preselect == EngineFeature.InpaintOutpaint)), Times.Once);
    }

    [Fact]
    public async Task OutpaintVisionOnTheEngine_HasNoInstallLink_BecauseNoRowOffersItYet()
    {
        Returns(Feature.OutpaintVision, BackendKind.Engine, "Diffusion Nexus Engine",
            "Outpaint Vision is not available on the Diffusion Nexus Engine yet");
        var vm = new FeatureReadinessViewModel(_service.Object, Feature.OutpaintVision, _events.Object);

        await vm.CheckReadinessAsync();

        vm.ShowInstallOnEngine.Should().BeFalse();
        vm.ShowChangeLink.Should().BeTrue("the user can still switch to their own ComfyUI");
    }

    [Fact]
    public void ChangeLink_OpensSettingsAtTheComfyUiServerSection()
    {
        var vm = new FeatureReadinessViewModel(_service.Object, Feature.Inpainting, _events.Object);

        vm.ChangeBackendCommand.Execute(null);

        _events.Verify(e => e.PublishNavigateToSettings(
            It.Is<NavigateToSettingsEventArgs>(a => a.Section == SettingsSection.ComfyUiServer)), Times.Once);
    }

    [Theory]
    [InlineData(Feature.Captioning)]
    [InlineData(Feature.BatchUpscale)]
    public async Task FeaturesTheDropdownDoesNotGovern_ShowTheirBackend_WithoutAChangeLink(Feature feature)
    {
        Returns(feature, BackendKind.ComfyUI, "ComfyUI");
        var vm = new FeatureReadinessViewModel(_service.Object, feature, _events.Object);

        await vm.CheckReadinessAsync();

        vm.ShowBackendLine.Should().BeTrue();
        vm.ShowChangeLink.Should().BeFalse();
    }
}
