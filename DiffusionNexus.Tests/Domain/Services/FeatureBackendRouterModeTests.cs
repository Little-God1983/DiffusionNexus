using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.Domain.Services;

public class FeatureBackendRouterModeTests
{
    private static IFeatureBackend Backend(BackendKind kind)
    {
        var b = new Mock<IFeatureBackend>();
        b.SetupGet(x => x.Kind).Returns(kind);
        return b.Object;
    }

    private readonly IFeatureBackend _comfy = Backend(BackendKind.ComfyUI);
    private readonly IFeatureBackend _engine = Backend(BackendKind.Engine);

    [Theory]
    [InlineData(Feature.Inpainting)]
    [InlineData(Feature.Outpaint)]
    [InlineData(Feature.OutpaintVision)]
    public void GovernedFeatures_FollowTheServerMode(Feature feature)
    {
        var mode = ComfyUiServerMode.Engine;
        var router = new FeatureBackendRouter([_comfy, _engine], serverMode: () => mode);

        router.Resolve(feature).Should().BeSameAs(_engine);

        mode = ComfyUiServerMode.CustomUrl;
        router.Resolve(feature).Should().BeSameAs(_comfy, "the mode is read on every call");
    }

    [Theory]
    [InlineData(Feature.Captioning)]
    [InlineData(Feature.BatchUpscale)]
    [InlineData(Feature.BatchUpscaleVision)]
    public void OtherFeatures_StayOnComfyUi_InEngineMode(Feature feature)
    {
        var router = new FeatureBackendRouter([_comfy, _engine], serverMode: () => ComfyUiServerMode.Engine);

        router.Resolve(feature).Should().BeSameAs(_comfy);
    }

    [Theory]
    [InlineData(Feature.Inpainting)]
    [InlineData(Feature.Outpaint)]
    [InlineData(Feature.OutpaintVision)]
    public async Task ResolveAsync_FollowsTheAsyncServerModeSource_OnEveryCall(Feature feature)
    {
        var mode = ComfyUiServerMode.Engine;
        var calls = 0;
        var router = new FeatureBackendRouter([_comfy, _engine],
            serverMode: () => throw new InvalidOperationException("the blocking source must not be read"),
            serverModeAsync: _ => { calls++; return Task.FromResult(mode); });

        (await router.ResolveAsync(feature)).Should().BeSameAs(_engine);

        mode = ComfyUiServerMode.CustomUrl;
        (await router.ResolveAsync(feature)).Should().BeSameAs(_comfy, "the mode is read on every call");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task ResolveAsync_PassesTheCancellationTokenToTheModeSource()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken seen = default;
        var router = new FeatureBackendRouter([_comfy, _engine],
            serverModeAsync: ct => { seen = ct; return Task.FromResult(ComfyUiServerMode.Engine); });

        await router.ResolveAsync(Feature.Inpainting, cts.Token);

        seen.Should().Be(cts.Token);
    }

    [Fact]
    public async Task ResolveAsync_OtherFeatures_NeverReadTheModeSource()
    {
        var router = new FeatureBackendRouter([_comfy, _engine],
            serverModeAsync: _ => throw new InvalidOperationException("not governed"));

        (await router.ResolveAsync(Feature.Captioning)).Should().BeSameAs(_comfy);
    }

    [Fact]
    public async Task ResolveAsync_WithOnlyTheSyncModeSource_FallsBackToIt()
    {
        var router = new FeatureBackendRouter([_comfy, _engine], serverMode: () => ComfyUiServerMode.Engine);

        (await router.ResolveAsync(Feature.Outpaint)).Should().BeSameAs(_engine);
    }

    [Fact]
    public void WithoutAModeSource_TheStaticRoutingStillApplies()
    {
        new FeatureBackendRouter([_comfy, _engine]).Resolve(Feature.Inpainting).Should().BeSameAs(_comfy);
    }
}
