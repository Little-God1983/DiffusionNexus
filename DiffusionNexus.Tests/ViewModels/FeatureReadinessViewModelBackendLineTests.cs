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
    public async Task OutpaintVisionMissingOnTheEngine_OffersItsOwnRow()
    {
        Returns(Feature.OutpaintVision, BackendKind.Engine, "Diffusion Nexus Engine",
            "Model missing on the Engine: Qwen3-VL-4B-Instruct-FP8");
        var vm = new FeatureReadinessViewModel(_service.Object, Feature.OutpaintVision, _events.Object);

        await vm.CheckReadinessAsync();
        vm.InstallOnEngineCommand.Execute(null);

        vm.ShowInstallOnEngine.Should().BeTrue();
        vm.InstallOnEngineLabel.Should().Be("Install Outpaint Vision");
        _events.Verify(e => e.PublishNavigateToEngineFeatures(It.Is<NavigateToEngineFeaturesEventArgs>(a =>
            a.Preselect == EngineFeature.OutpaintVision && !a.InstallEngineOnly)), Times.Once);
    }

    [Fact]
    public async Task ACheckThatThrows_DropsTheStaleBackendLine()
    {
        Returns(Feature.Outpaint, BackendKind.Engine, "Diffusion Nexus Engine", "Model missing on the Engine: qwen_image_vae");
        var vm = new FeatureReadinessViewModel(_service.Object, Feature.Outpaint, _events.Object);
        await vm.CheckReadinessAsync();
        vm.ShowInstallOnEngine.Should().BeTrue();

        _service.Setup(s => s.CheckAsync(Feature.Outpaint, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        await vm.CheckReadinessAsync();

        vm.ShowInstallOnEngine.Should().BeFalse();
        vm.ShowBackendLine.Should().BeFalse();
    }

    [Fact]
    public void ChangeLink_OpensSettingsAtTheComfyUiServerSection()
    {
        var vm = new FeatureReadinessViewModel(_service.Object, Feature.Inpainting, _events.Object);

        vm.ChangeBackendCommand.Execute(null);

        _events.Verify(e => e.PublishNavigateToSettings(
            It.Is<NavigateToSettingsEventArgs>(a => a.Section == SettingsSection.ComfyUiServer)), Times.Once);
    }

    private static FeatureReadinessResult Result(Feature feature, BackendKind kind, string name, bool online = true,
        params string[] missing) => new()
    {
        Feature = feature, Backend = kind, ActiveBackendName = name,
        IsBackendOnline = online, IsReady = missing.Length == 0,
        MissingRequirements = missing, Warnings = []
    };

    [Fact]
    public async Task OverlappingChecks_TheOlderOneFinishingLast_DoesNotOverwriteTheNewerResult()
    {
        var older = new TaskCompletionSource<FeatureReadinessResult>();
        var newer = new TaskCompletionSource<FeatureReadinessResult>();
        _service.SetupSequence(s => s.CheckAsync(Feature.Inpainting, It.IsAny<CancellationToken>()))
            .Returns(older.Task)
            .Returns(newer.Task);
        var vm = new FeatureReadinessViewModel(_service.Object, Feature.Inpainting, _events.Object);

        var first = vm.CheckReadinessAsync();
        var second = vm.CheckReadinessAsync();

        newer.SetResult(Result(Feature.Inpainting, BackendKind.ComfyUI, "Custom URL"));
        await second;
        older.SetResult(Result(Feature.Inpainting, BackendKind.Engine, "Diffusion Nexus Engine",
            missing: "Model missing on the Engine: qwen_image_vae"));
        await first;

        vm.ActiveBackendName.Should().Be("Custom URL");
        vm.IsEngineBackend.Should().BeFalse();
        vm.IsReady.Should().BeTrue();
        vm.MissingRequirements.Should().BeEmpty();
        vm.IsChecking.Should().BeFalse();
    }

    [Fact]
    public async Task OverlappingChecks_TheOlderOneFinishingFirst_LeavesIsCheckingOn_UntilTheNewerFinishes()
    {
        var older = new TaskCompletionSource<FeatureReadinessResult>();
        var newer = new TaskCompletionSource<FeatureReadinessResult>();
        _service.SetupSequence(s => s.CheckAsync(Feature.Inpainting, It.IsAny<CancellationToken>()))
            .Returns(older.Task)
            .Returns(newer.Task);
        var vm = new FeatureReadinessViewModel(_service.Object, Feature.Inpainting, _events.Object);

        var first = vm.CheckReadinessAsync();
        var second = vm.CheckReadinessAsync();

        older.SetResult(Result(Feature.Inpainting, BackendKind.Engine, "Diffusion Nexus Engine"));
        await first;
        vm.IsChecking.Should().BeTrue("the newer check is still running");
        vm.ActiveBackendName.Should().BeNull("an outdated check must not write its result");

        newer.SetResult(Result(Feature.Inpainting, BackendKind.ComfyUI, "Custom URL"));
        await second;
        vm.IsChecking.Should().BeFalse();
        vm.ActiveBackendName.Should().Be("Custom URL");
        vm.HasChecked.Should().BeTrue();
    }

    [Fact]
    public async Task OverlappingChecks_AnOlderCheckThatThrows_DoesNotResetTheNewerResult()
    {
        var older = new TaskCompletionSource<FeatureReadinessResult>();
        var newer = new TaskCompletionSource<FeatureReadinessResult>();
        _service.SetupSequence(s => s.CheckAsync(Feature.Inpainting, It.IsAny<CancellationToken>()))
            .Returns(older.Task)
            .Returns(newer.Task);
        var vm = new FeatureReadinessViewModel(_service.Object, Feature.Inpainting, _events.Object);

        var first = vm.CheckReadinessAsync();
        var second = vm.CheckReadinessAsync();

        newer.SetResult(Result(Feature.Inpainting, BackendKind.ComfyUI, "Custom URL"));
        await second;
        older.SetException(new InvalidOperationException("boom"));
        await first;

        vm.ActiveBackendName.Should().Be("Custom URL");
        vm.IsReady.Should().BeTrue();
        vm.MissingRequirements.Should().BeEmpty();
    }

    [Fact]
    public async Task EngineNotInstalled_OffersToInstallTheEngine_AndAsksForTheEngineOnly()
    {
        _service.Setup(s => s.CheckAsync(Feature.Inpainting, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(Feature.Inpainting, BackendKind.Engine, "Diffusion Nexus Engine", online: false,
                missing: "Diffusion Nexus Engine is not installed"));
        var vm = new FeatureReadinessViewModel(_service.Object, Feature.Inpainting, _events.Object);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.CheckReadinessAsync();

        vm.ShowInstallOnEngine.Should().BeTrue();
        vm.InstallOnEngineLabel.Should().Be("Install the Diffusion Nexus Engine");
        changed.Should().Contain(nameof(FeatureReadinessViewModel.InstallOnEngineLabel));

        vm.InstallOnEngineCommand.Execute(null);
        _events.Verify(e => e.PublishNavigateToEngineFeatures(
            It.Is<NavigateToEngineFeaturesEventArgs>(a =>
                a.Preselect == EngineFeature.InpaintOutpaint && a.InstallEngineOnly)), Times.Once);
    }

    [Fact]
    public async Task InstalledButMissingItems_KeepsTheFeatureLabel_AndOpensTheFeaturesDialog()
    {
        Returns(Feature.Inpainting, BackendKind.Engine, "Diffusion Nexus Engine", "Model missing on the Engine: qwen_image_vae");
        var vm = new FeatureReadinessViewModel(_service.Object, Feature.Inpainting, _events.Object);

        await vm.CheckReadinessAsync();

        vm.InstallOnEngineLabel.Should().Be("Install Inpaint & Outpaint");
        vm.InstallOnEngineCommand.Execute(null);
        _events.Verify(e => e.PublishNavigateToEngineFeatures(
            It.Is<NavigateToEngineFeaturesEventArgs>(a =>
                a.Preselect == EngineFeature.InpaintOutpaint && !a.InstallEngineOnly)), Times.Once);
    }

    [Theory]
    [InlineData(Feature.Captioning)]
    public async Task FeaturesTheDropdownDoesNotGovern_ShowTheirBackend_WithoutAChangeLink(Feature feature)
    {
        Returns(feature, BackendKind.ComfyUI, "ComfyUI");
        var vm = new FeatureReadinessViewModel(_service.Object, feature, _events.Object);

        await vm.CheckReadinessAsync();

        vm.ShowBackendLine.Should().BeTrue();
        vm.ShowChangeLink.Should().BeFalse();
    }
}
