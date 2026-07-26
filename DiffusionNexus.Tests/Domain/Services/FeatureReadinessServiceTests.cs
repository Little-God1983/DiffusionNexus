using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.Domain.Services;

/// <summary>
/// Unit tests for <see cref="FeatureReadinessService"/>. The service owns exactly two
/// behaviours: the synthetic "no backend registered" result it manufactures when the router
/// returns <c>null</c>, and pure delegation to the routed backend for everything else.
/// </summary>
public class FeatureReadinessServiceTests
{
    private readonly Mock<IFeatureBackendRouter> _router = new();

    private FeatureReadinessService CreateService(Func<Feature, FeatureRequirements?>? lookup = null) =>
        new(_router.Object, lookup ?? (_ => null));

    private static FeatureReadinessResult ResultFor(
        Feature feature,
        bool isReady,
        IReadOnlyList<string>? missing = null,
        IReadOnlyList<string>? warnings = null) => new()
    {
        Feature = feature,
        Backend = BackendKind.ComfyUI,
        IsBackendOnline = true,
        IsReady = isReady,
        ActiveBackendName = "ComfyUI",
        MissingRequirements = missing ?? [],
        Warnings = warnings ?? []
    };

    #region Construction

    [Fact]
    public void WhenRouterIsNullThenConstructorThrows()
    {
        var act = () => new FeatureReadinessService(null!, _ => null);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenRequirementsLookupIsNullThenConstructorThrows()
    {
        var act = () => new FeatureReadinessService(_router.Object, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region No backend registered

    [Fact]
    public async Task WhenRouterResolvesNoBackendThenResultIsNotReadyAndOffline()
    {
        _router.Setup(r => r.Resolve(Feature.Outpaint)).Returns((IFeatureBackend?)null);
        var service = CreateService();

        var result = await service.CheckAsync(Feature.Outpaint);

        result.Feature.Should().Be(Feature.Outpaint);
        result.IsReady.Should().BeFalse();
        result.IsBackendOnline.Should().BeFalse();
        result.ActiveBackendName.Should().Be("(none)");
        result.Warnings.Should().BeEmpty();
        result.Endpoint.Should().BeNull();
    }

    [Fact]
    public async Task WhenRouterResolvesNoBackendThenMissingRequirementsNamesTheFeature()
    {
        _router.Setup(r => r.Resolve(It.IsAny<Feature>())).Returns((IFeatureBackend?)null);
        var service = CreateService();

        var result = await service.CheckAsync(Feature.BatchUpscaleVision);

        result.MissingRequirements.Should().ContainSingle()
            .Which.Should().Contain(nameof(Feature.BatchUpscaleVision));
    }

    [Fact]
    public async Task WhenRouterResolvesNoBackendThenBackendKindFallsBackToComfyUI()
    {
        // There is no "unknown" BackendKind, so the fallback result reports ComfyUI. Pinned
        // because the UI switches on Backend when rendering the remediation hint.
        _router.Setup(r => r.Resolve(It.IsAny<Feature>())).Returns((IFeatureBackend?)null);
        var service = CreateService();

        var result = await service.CheckAsync(Feature.Captioning);

        result.Backend.Should().Be(BackendKind.ComfyUI);
    }

    #endregion

    #region Delegation to the routed backend

    [Fact]
    public async Task WhenBackendReportsReadyThenItsResultIsReturnedUnmodified()
    {
        var backendResult = ResultFor(Feature.Captioning, isReady: true);
        var backend = new Mock<IFeatureBackend>();
        backend.Setup(b => b.CheckFeatureAsync(Feature.Captioning, It.IsAny<CancellationToken>()))
               .ReturnsAsync(backendResult);
        _router.Setup(r => r.Resolve(Feature.Captioning)).Returns(backend.Object);

        var result = await CreateService().CheckAsync(Feature.Captioning);

        result.Should().BeSameAs(backendResult, "the service must not re-wrap or reinterpret backend results");
        result.IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task WhenBackendReportsMissingRequirementsThenTheyArePropagated()
    {
        var backendResult = ResultFor(
            Feature.Inpainting,
            isReady: false,
            missing: ["Missing model: qwen-image-2512.safetensors", "Open Installer Manager"],
            warnings: ["Model will auto-download on first run"]);
        var backend = new Mock<IFeatureBackend>();
        backend.Setup(b => b.CheckFeatureAsync(Feature.Inpainting, It.IsAny<CancellationToken>()))
               .ReturnsAsync(backendResult);
        _router.Setup(r => r.Resolve(Feature.Inpainting)).Returns(backend.Object);

        var result = await CreateService().CheckAsync(Feature.Inpainting);

        result.IsReady.Should().BeFalse();
        result.MissingRequirements.Should().HaveCount(2)
            .And.Contain("Missing model: qwen-image-2512.safetensors");
        result.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task WhenCheckingThenTheCancellationTokenReachesTheBackend()
    {
        using var cts = new CancellationTokenSource();
        var backend = new Mock<IFeatureBackend>();
        backend.Setup(b => b.CheckFeatureAsync(It.IsAny<Feature>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(ResultFor(Feature.Captioning, isReady: true));
        _router.Setup(r => r.Resolve(It.IsAny<Feature>())).Returns(backend.Object);

        await CreateService().CheckAsync(Feature.Captioning, cts.Token);

        backend.Verify(b => b.CheckFeatureAsync(Feature.Captioning, cts.Token), Times.Once);
    }

    [Fact]
    public async Task WhenBackendThrowsThenTheExceptionIsNotSwallowed()
    {
        var backend = new Mock<IFeatureBackend>();
        backend.Setup(b => b.CheckFeatureAsync(It.IsAny<Feature>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("boom"));
        _router.Setup(r => r.Resolve(It.IsAny<Feature>())).Returns(backend.Object);

        var act = async () => await CreateService().CheckAsync(Feature.Captioning);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public async Task WhenCheckingThenTheRouterIsConsultedPerCall()
    {
        // Re-resolving each time is what lets a routing change take effect without restarting.
        var backend = new Mock<IFeatureBackend>();
        backend.Setup(b => b.CheckFeatureAsync(It.IsAny<Feature>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(ResultFor(Feature.Captioning, isReady: true));
        _router.Setup(r => r.Resolve(Feature.Captioning)).Returns(backend.Object);
        var service = CreateService();

        await service.CheckAsync(Feature.Captioning);
        await service.CheckAsync(Feature.Captioning);

        _router.Verify(r => r.Resolve(Feature.Captioning), Times.Exactly(2));
    }

    #endregion

    #region GetRequirements

    [Fact]
    public void WhenRequirementsExistThenGetRequirementsReturnsThemFromTheLookup()
    {
        var requirements = new FeatureRequirements(Feature.Captioning, "AI Captioning", Guid.NewGuid());
        var service = CreateService(f => f == Feature.Captioning ? requirements : null);

        service.GetRequirements(Feature.Captioning).Should().BeSameAs(requirements);
    }

    [Fact]
    public void WhenLookupReturnsNullThenGetRequirementsReturnsNull()
    {
        var service = CreateService(_ => null);

        service.GetRequirements(Feature.Outpaint).Should().BeNull();
    }

    [Fact]
    public void WhenGetRequirementsIsCalledThenTheRouterIsNeverTouched()
    {
        var service = CreateService(_ => new FeatureRequirements(Feature.Outpaint, "Outpaint"));

        service.GetRequirements(Feature.Outpaint);

        _router.Verify(r => r.Resolve(It.IsAny<Feature>()), Times.Never,
            "GetRequirements is documented as performing no network/backend work");
    }

    #endregion
}
