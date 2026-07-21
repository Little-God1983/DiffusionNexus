using System.Collections;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.Domain.Services;

/// <summary>
/// Unit tests for <see cref="FeatureBackendRouter"/> — the single place that maps
/// "feature X → backend Y". Pins the registration semantics the DI host relies on
/// (last-registration-wins per <see cref="BackendKind"/>, so a test stub can shadow the
/// production backend without unregistering it) and the routing-override seam.
/// </summary>
public class FeatureBackendRouterTests
{
    private static Mock<IFeatureBackend> BackendMock(BackendKind kind, string displayName)
    {
        var mock = new Mock<IFeatureBackend>();
        mock.SetupGet(b => b.Kind).Returns(kind);
        mock.SetupGet(b => b.DisplayName).Returns(displayName);
        return mock;
    }

    #region Construction

    [Fact]
    public void WhenBackendsIsNullThenConstructorThrows()
    {
        var act = () => new FeatureBackendRouter(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenNoBackendsRegisteredThenResolveReturnsNull()
    {
        var router = new FeatureBackendRouter([]);

        router.Resolve(Feature.Captioning).Should().BeNull(
            "the routing table names a kind but nothing was registered to serve it");
    }

    [Fact]
    public void WhenConstructedThenBackendsAreEnumeratedExactlyOnce()
    {
        // The router snapshots the sequence in the constructor. A DI container may hand over a
        // lazily-resolving enumerable, and re-enumerating it per Resolve() call would build a
        // fresh backend instance on every readiness check.
        var source = new CountingEnumerable([BackendMock(BackendKind.ComfyUI, "ComfyUI").Object]);

        var router = new FeatureBackendRouter(source);
        router.Resolve(Feature.Captioning);
        router.Resolve(Feature.Inpainting);
        router.Resolve(Feature.Outpaint);

        source.EnumerationCount.Should().Be(1);
    }

    #endregion

    #region Last write wins

    [Fact]
    public void WhenTwoBackendsShareAKindThenTheLastRegisteredWins()
    {
        var production = BackendMock(BackendKind.ComfyUI, "Production ComfyUI");
        var stub = BackendMock(BackendKind.ComfyUI, "Test Stub");

        var router = new FeatureBackendRouter([production.Object, stub.Object]);

        router.Resolve(Feature.Captioning).Should().BeSameAs(stub.Object);
    }

    [Fact]
    public void WhenThreeBackendsShareAKindThenOnlyTheFinalOneIsReachable()
    {
        var first = BackendMock(BackendKind.ComfyUI, "First");
        var second = BackendMock(BackendKind.ComfyUI, "Second");
        var third = BackendMock(BackendKind.ComfyUI, "Third");

        var router = new FeatureBackendRouter([first.Object, second.Object, third.Object]);

        var resolvedForEveryFeature = FeatureBackendRouter.DefaultRouting.Keys
            .Select(router.Resolve)
            .ToList();

        resolvedForEveryFeature.Should().AllSatisfy(b => b.Should().BeSameAs(third.Object));
    }

    [Fact]
    public void WhenBackendsHaveDistinctKindsThenBothRemainResolvable()
    {
        var comfy = BackendMock(BackendKind.ComfyUI, "ComfyUI");
        var local = BackendMock(BackendKind.LocalInference, "Diffusion Nexus Core");

        var router = new FeatureBackendRouter(
            [comfy.Object, local.Object],
            new Dictionary<Feature, BackendKind>
            {
                [Feature.Captioning] = BackendKind.ComfyUI,
                [Feature.Inpainting] = BackendKind.LocalInference
            });

        router.Resolve(Feature.Captioning).Should().BeSameAs(comfy.Object);
        router.Resolve(Feature.Inpainting).Should().BeSameAs(local.Object);
    }

    #endregion

    #region Routing override

    [Fact]
    public void WhenRoutingIsNullThenDefaultRoutingIsUsed()
    {
        var comfy = BackendMock(BackendKind.ComfyUI, "ComfyUI");
        var local = BackendMock(BackendKind.LocalInference, "Diffusion Nexus Core");

        var router = new FeatureBackendRouter([comfy.Object, local.Object], routing: null);

        // DefaultRouting sends every feature to ComfyUI, so the registered local backend is
        // reachable by nobody.
        foreach (var feature in Enum.GetValues<Feature>())
        {
            router.Resolve(feature).Should().BeSameAs(comfy.Object, "feature {0} defaults to ComfyUI", feature);
        }
    }

    [Fact]
    public void WhenRoutingOverridesAFeatureThenTheOverriddenBackendAnswers()
    {
        var comfy = BackendMock(BackendKind.ComfyUI, "ComfyUI");
        var local = BackendMock(BackendKind.LocalInference, "Diffusion Nexus Core");

        var router = new FeatureBackendRouter(
            [comfy.Object, local.Object],
            new Dictionary<Feature, BackendKind> { [Feature.Captioning] = BackendKind.LocalInference });

        router.Resolve(Feature.Captioning).Should().BeSameAs(local.Object);
    }

    [Fact]
    public void WhenRoutingOmitsAFeatureThenResolveReturnsNullEvenThoughABackendExists()
    {
        var comfy = BackendMock(BackendKind.ComfyUI, "ComfyUI");

        var router = new FeatureBackendRouter(
            [comfy.Object],
            new Dictionary<Feature, BackendKind> { [Feature.Captioning] = BackendKind.ComfyUI });

        router.Resolve(Feature.Captioning).Should().BeSameAs(comfy.Object);
        router.Resolve(Feature.Inpainting).Should().BeNull("Inpainting is absent from the custom routing table");
    }

    [Fact]
    public void WhenRoutingPointsAtAnUnregisteredKindThenResolveReturnsNull()
    {
        var comfy = BackendMock(BackendKind.ComfyUI, "ComfyUI");

        var router = new FeatureBackendRouter(
            [comfy.Object],
            new Dictionary<Feature, BackendKind> { [Feature.Captioning] = BackendKind.LocalInference });

        router.Resolve(Feature.Captioning).Should().BeNull(
            "the feature routes to LocalInference but no LocalInference backend was registered");
    }

    [Fact]
    public void WhenFeatureIsOutsideTheEnumThenResolveReturnsNullInsteadOfThrowing()
    {
        var comfy = BackendMock(BackendKind.ComfyUI, "ComfyUI");
        var router = new FeatureBackendRouter([comfy.Object]);

        router.Resolve((Feature)9999).Should().BeNull();
    }

    #endregion

    #region Default routing policy

    [Theory]
    [InlineData(Feature.Captioning)]
    [InlineData(Feature.Inpainting)]
    [InlineData(Feature.BatchUpscale)]
    [InlineData(Feature.BatchUpscaleVision)]
    [InlineData(Feature.Outpaint)]
    [InlineData(Feature.OutpaintVision)]
    public void WhenReadingDefaultRoutingThenEveryFeatureIsMappedToComfyUI(Feature feature)
    {
        FeatureBackendRouter.DefaultRouting.TryGetValue(feature, out var kind).Should().BeTrue();
        kind.Should().Be(BackendKind.ComfyUI);
    }

    [Fact]
    public void WhenReadingDefaultRoutingThenEveryDeclaredFeatureIsCovered()
    {
        // A new Feature enum member without a routing entry silently resolves to null and the
        // readiness service reports "No backend is registered" — this catches that at build time.
        FeatureBackendRouter.DefaultRouting.Keys.Should().BeEquivalentTo(Enum.GetValues<Feature>());
    }

    #endregion

    /// <summary>Sequence that records how many times it was enumerated.</summary>
    private sealed class CountingEnumerable(IReadOnlyList<IFeatureBackend> items) : IEnumerable<IFeatureBackend>
    {
        public int EnumerationCount { get; private set; }

        public IEnumerator<IFeatureBackend> GetEnumerator()
        {
            EnumerationCount++;
            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
