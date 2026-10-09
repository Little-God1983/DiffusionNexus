using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.Engine;

public class EngineFeatureBackendTests
{
    private const string Root = @"C:\Engine\ComfyUI";
    private readonly Mock<IEngineRootResolver> _root = new();
    private readonly Mock<ICatalog> _catalog = new();
    private readonly Mock<IConfigurationCheckerService> _checker = new();
    private bool _looksInstalled = true;

    public EngineFeatureBackendTests()
    {
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Root);
        _catalog.Setup(c => c.GetWorkloadAsync(EngineFeatureCatalog.InpaintingQwen2512, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InstallationConfiguration { Id = EngineFeatureCatalog.InpaintingQwen2512, Name = "Inpainting-Qwen 2512" });
    }

    private EngineFeatureBackend Sut() =>
        new(_root.Object, _catalog.Object, _checker.Object, unifiedLogger: null, looksInstalled: _ => _looksInstalled);

    private void CheckReturns(params (string Name, bool Installed, bool IsNode)[] items) =>
        _checker.Setup(c => c.CheckConfigurationAsync(It.IsAny<InstallationConfiguration>(), Root,
                It.IsAny<ConfigurationCheckOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConfigurationCheckResult
            {
                OverallStatus = ConfigurationStatus.Partial,
                CustomNodesStatus = ConfigurationStatus.Partial,
                ModelsStatus = ConfigurationStatus.Partial,
                InstallationType = ComfyUIInstallationType.Manual,
                CustomNodeResults = items.Where(i => i.IsNode).Select(i => new CustomNodeCheckResult
                {
                    Id = Guid.NewGuid(), Name = i.Name, Url = "https://example", IsInstalled = i.Installed, ExpectedPath = "x"
                }).ToList(),
                ModelResults = items.Where(i => !i.IsNode).Select(i => new ModelCheckResult
                {
                    Id = Guid.NewGuid(), Name = i.Name, IsInstalled = i.Installed, SearchedPaths = []
                }).ToList()
            });

    [Fact]
    public async Task NotInstalled_ReportsOneRequirement_AndNeverChecksFiles()
    {
        _looksInstalled = false;

        var result = await Sut().CheckFeatureAsync(Feature.Inpainting);

        result.Backend.Should().Be(BackendKind.Engine);
        result.ActiveBackendName.Should().Be("Diffusion Nexus Engine");
        result.IsReady.Should().BeFalse();
        result.MissingRequirements.Should().Equal("Diffusion Nexus Engine is not installed");
        _checker.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingNodesAndModels_AreListed_ScopedToTheEngineRoot()
    {
        CheckReturns(("ComfyUI-GGUF", false, true), ("qwen_image_vae", true, false), ("Qwen-Image-2512-GGUF", false, false));

        var result = await Sut().CheckFeatureAsync(Feature.Outpaint);

        result.IsReady.Should().BeFalse();
        result.IsBackendOnline.Should().BeTrue("an installed Engine counts as reachable; Generate starts it");
        result.MissingRequirements.Should().BeEquivalentTo(
            "Custom node missing on the Engine: ComfyUI-GGUF",
            "Model missing on the Engine: Qwen-Image-2512-GGUF");
    }

    [Fact]
    public async Task EverythingPresent_IsReady()
    {
        CheckReturns(("ComfyUI-GGUF", true, true), ("qwen_image_vae", true, false));

        var result = await Sut().CheckFeatureAsync(Feature.Inpainting);

        result.IsReady.Should().BeTrue();
        result.MissingRequirements.Should().BeEmpty();
    }

    [Fact]
    public async Task OutpaintVision_IsNotOfferedOnTheEngineYet()
    {
        var result = await Sut().CheckFeatureAsync(Feature.OutpaintVision);

        result.IsReady.Should().BeFalse();
        result.MissingRequirements.Should().Equal("Outpaint Vision is not available on the Diffusion Nexus Engine yet");
        _checker.VerifyNoOtherCalls();
    }

    // #606 code review 3 (H6): a throw used to reach FeatureReadinessViewModel's catch, which drops
    // the whole backend line, "change" link included.
    [Fact]
    public async Task CheckerThrows_ReportsNotReady_OnTheEngine_WithTheReason()
    {
        _checker.Setup(c => c.CheckConfigurationAsync(It.IsAny<InstallationConfiguration>(), Root,
                It.IsAny<ConfigurationCheckOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk not ready"));

        var result = await Sut().CheckFeatureAsync(Feature.Inpainting);

        result.Backend.Should().Be(BackendKind.Engine);
        result.ActiveBackendName.Should().Be("Diffusion Nexus Engine");
        result.IsBackendOnline.Should().BeTrue();
        result.IsReady.Should().BeFalse();
        result.MissingRequirements.Should().Equal("Engine readiness check failed: disk not ready");
    }

    [Fact]
    public async Task CatalogThrows_ReportsNotReady_OnTheEngine_WithTheReason()
    {
        _catalog.Setup(c => c.GetWorkloadAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("catalog is corrupt"));

        var result = await Sut().CheckFeatureAsync(Feature.Outpaint);

        result.ActiveBackendName.Should().Be("Diffusion Nexus Engine");
        result.IsReady.Should().BeFalse();
        result.MissingRequirements.Should().Equal("Engine readiness check failed: catalog is corrupt");
    }

    [Fact]
    public async Task CheckerCancellation_Propagates()
    {
        _checker.Setup(c => c.CheckConfigurationAsync(It.IsAny<InstallationConfiguration>(), Root,
                It.IsAny<ConfigurationCheckOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => Sut().CheckFeatureAsync(Feature.Inpainting);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = () => Sut().CheckFeatureAsync(Feature.Inpainting, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
