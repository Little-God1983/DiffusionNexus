using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Inference;
using DiffusionNexus.Inference.Abstractions;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.Inference;

/// <summary>
/// Unit tests for <see cref="LocalInferenceFeatureBackend"/>. Both wrapped backends are
/// optional, so the class has to answer every feature sensibly across all four registration
/// combinations (neither / captioning only / diffusion only / both) — a null backend must
/// produce a "not registered" blocker rather than a <see cref="NullReferenceException"/>.
/// </summary>
public class LocalInferenceFeatureBackendTests
{
    private static readonly Feature[] DiffusionFeatures =
    [
        Feature.Inpainting,
        Feature.Outpaint,
        Feature.OutpaintVision,
        Feature.BatchUpscale,
        Feature.BatchUpscaleVision
    ];

    private static Mock<ICaptioningBackend> CaptioningMock(
        bool available,
        IReadOnlyList<string>? missing = null,
        IReadOnlyList<string>? warnings = null,
        string displayName = "Local Captioning")
    {
        var mock = new Mock<ICaptioningBackend>();
        mock.SetupGet(b => b.DisplayName).Returns(displayName);
        mock.SetupGet(b => b.MissingRequirements).Returns(missing ?? []);
        mock.SetupGet(b => b.Warnings).Returns(warnings ?? []);
        mock.Setup(b => b.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(available);
        return mock;
    }

    private static Mock<IDiffusionBackend> DiffusionMock(
        bool available,
        IReadOnlyList<string>? missing = null,
        IReadOnlyList<string>? warnings = null,
        string displayName = "Local Diffusion")
    {
        var mock = new Mock<IDiffusionBackend>();
        mock.SetupGet(b => b.DisplayName).Returns(displayName);
        mock.SetupGet(b => b.MissingRequirements).Returns(missing ?? []);
        mock.SetupGet(b => b.Warnings).Returns(warnings ?? []);
        mock.Setup(b => b.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(available);
        return mock;
    }

    #region Identity

    [Fact]
    public void WhenInspectingIdentityThenBackendDeclaresLocalInference()
    {
        var backend = new LocalInferenceFeatureBackend();

        backend.Kind.Should().Be(BackendKind.LocalInference);
        backend.DisplayName.Should().Be("Diffusion Nexus Core");
    }

    #endregion

    #region Neither backend registered

    [Fact]
    public async Task WhenNoBackendsAreRegisteredThenCaptioningReportsTheMissingCaptioningBackend()
    {
        var backend = new LocalInferenceFeatureBackend();

        var result = await backend.CheckFeatureAsync(Feature.Captioning);

        result.IsReady.Should().BeFalse();
        result.IsBackendOnline.Should().BeFalse();
        result.Backend.Should().Be(BackendKind.LocalInference);
        result.ActiveBackendName.Should().Be("Diffusion Nexus Core");
        result.MissingRequirements.Should().ContainSingle()
            .Which.Should().Be("Local captioning backend is not registered.");
        result.Warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData(Feature.Inpainting)]
    [InlineData(Feature.Outpaint)]
    [InlineData(Feature.OutpaintVision)]
    [InlineData(Feature.BatchUpscale)]
    [InlineData(Feature.BatchUpscaleVision)]
    public async Task WhenNoBackendsAreRegisteredThenImageFeaturesReportTheMissingDiffusionBackend(Feature feature)
    {
        var backend = new LocalInferenceFeatureBackend();

        var result = await backend.CheckFeatureAsync(feature);

        result.Feature.Should().Be(feature);
        result.IsReady.Should().BeFalse();
        result.IsBackendOnline.Should().BeFalse();
        result.MissingRequirements.Should().ContainSingle()
            .Which.Should().Be("Local diffusion backend is not registered.");
    }

    #endregion

    #region Captioning backend only

    [Fact]
    public async Task WhenOnlyCaptioningIsRegisteredThenCaptioningIsAnsweredByThatBackend()
    {
        var captioning = CaptioningMock(available: true, displayName: "LlamaSharp Qwen3-VL");
        var backend = new LocalInferenceFeatureBackend(captioning.Object);

        var result = await backend.CheckFeatureAsync(Feature.Captioning);

        result.IsReady.Should().BeTrue();
        result.IsBackendOnline.Should().BeTrue();
        result.ActiveBackendName.Should().Be("LlamaSharp Qwen3-VL");
        result.MissingRequirements.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenOnlyCaptioningIsRegisteredThenImageFeaturesStillReportTheMissingDiffusionBackend()
    {
        var captioning = CaptioningMock(available: true);
        var backend = new LocalInferenceFeatureBackend(captioning.Object);

        var result = await backend.CheckFeatureAsync(Feature.Inpainting);

        result.IsReady.Should().BeFalse();
        result.MissingRequirements.Should().ContainSingle()
            .Which.Should().Be("Local diffusion backend is not registered.");
        captioning.Verify(b => b.IsAvailableAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Diffusion backend only

    [Fact]
    public async Task WhenOnlyDiffusionIsRegisteredThenImageFeaturesAreAnsweredByThatBackend()
    {
        var diffusion = DiffusionMock(available: true, displayName: "stable-diffusion.cpp");
        var backend = new LocalInferenceFeatureBackend(diffusion: diffusion.Object);

        foreach (var feature in DiffusionFeatures)
        {
            var result = await backend.CheckFeatureAsync(feature);

            result.IsReady.Should().BeTrue("feature {0} routes to the diffusion backend", feature);
            result.ActiveBackendName.Should().Be("stable-diffusion.cpp");
        }
    }

    [Fact]
    public async Task WhenOnlyDiffusionIsRegisteredThenCaptioningStillReportsTheMissingCaptioningBackend()
    {
        var diffusion = DiffusionMock(available: true);
        var backend = new LocalInferenceFeatureBackend(diffusion: diffusion.Object);

        var result = await backend.CheckFeatureAsync(Feature.Captioning);

        result.IsReady.Should().BeFalse();
        result.MissingRequirements.Should().ContainSingle()
            .Which.Should().Be("Local captioning backend is not registered.");
        diffusion.Verify(b => b.IsAvailableAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Both backends registered

    [Fact]
    public async Task WhenBothBackendsAreRegisteredThenEachFeatureGoesToItsOwnBackend()
    {
        var captioning = CaptioningMock(available: true, displayName: "LlamaSharp Qwen3-VL");
        var diffusion = DiffusionMock(available: true, displayName: "stable-diffusion.cpp");
        var backend = new LocalInferenceFeatureBackend(captioning.Object, diffusion.Object);

        var captionResult = await backend.CheckFeatureAsync(Feature.Captioning);
        var inpaintResult = await backend.CheckFeatureAsync(Feature.Inpainting);

        captionResult.ActiveBackendName.Should().Be("LlamaSharp Qwen3-VL");
        inpaintResult.ActiveBackendName.Should().Be("stable-diffusion.cpp");
        captioning.Verify(b => b.IsAvailableAsync(It.IsAny<CancellationToken>()), Times.Once);
        diffusion.Verify(b => b.IsAvailableAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenBothBackendsAreRegisteredButUnavailableThenBothReportTheirOwnBlockers()
    {
        var captioning = CaptioningMock(available: false, missing: ["Caption model not downloaded"]);
        var diffusion = DiffusionMock(available: false, missing: ["No GGUF checkpoint found"]);
        var backend = new LocalInferenceFeatureBackend(captioning.Object, diffusion.Object);

        var captionResult = await backend.CheckFeatureAsync(Feature.Captioning);
        var upscaleResult = await backend.CheckFeatureAsync(Feature.BatchUpscale);

        captionResult.MissingRequirements.Should().ContainSingle().Which.Should().Be("Caption model not downloaded");
        upscaleResult.MissingRequirements.Should().ContainSingle().Which.Should().Be("No GGUF checkpoint found");
    }

    #endregion

    #region IsBackendOnline derivation

    [Fact]
    public async Task WhenTheBackendIsUnavailableWithNamedBlockersThenItIsReportedOffline()
    {
        var captioning = CaptioningMock(available: false, missing: ["Native library failed to load"]);
        var backend = new LocalInferenceFeatureBackend(captioning.Object);

        var result = await backend.CheckFeatureAsync(Feature.Captioning);

        result.IsBackendOnline.Should().BeFalse();
        result.IsReady.Should().BeFalse();
    }

    [Fact]
    public async Task WhenTheBackendIsUnavailableWithNoNamedBlockersThenItIsStillConsideredOnline()
    {
        // Documented behaviour: with nothing in MissingRequirements there is no evidence the
        // engine itself is down, so the result stays "online but not ready".
        var captioning = CaptioningMock(available: false, missing: []);
        var backend = new LocalInferenceFeatureBackend(captioning.Object);

        var result = await backend.CheckFeatureAsync(Feature.Captioning);

        result.IsBackendOnline.Should().BeTrue();
        result.IsReady.Should().BeFalse();
    }

    [Fact]
    public async Task WhenTheDiffusionBackendIsUnavailableWithNoNamedBlockersThenItIsStillConsideredOnline()
    {
        var diffusion = DiffusionMock(available: false, missing: []);
        var backend = new LocalInferenceFeatureBackend(diffusion: diffusion.Object);

        var result = await backend.CheckFeatureAsync(Feature.Outpaint);

        result.IsBackendOnline.Should().BeTrue();
        result.IsReady.Should().BeFalse();
    }

    [Fact]
    public async Task WhenTheBackendReportsWarningsThenTheyArePassedThroughWithoutBlocking()
    {
        var captioning = CaptioningMock(
            available: true,
            warnings: ["Model will be downloaded on first run"]);
        var backend = new LocalInferenceFeatureBackend(captioning.Object);

        var result = await backend.CheckFeatureAsync(Feature.Captioning);

        result.IsReady.Should().BeTrue();
        result.Warnings.Should().ContainSingle().Which.Should().Be("Model will be downloaded on first run");
    }

    #endregion

    #region Unsupported features and failures

    [Fact]
    public async Task WhenTheFeatureIsOutsideTheEnumThenItIsReportedAsUnsupported()
    {
        var backend = new LocalInferenceFeatureBackend(
            CaptioningMock(available: true).Object,
            DiffusionMock(available: true).Object);

        var result = await backend.CheckFeatureAsync((Feature)9999);

        result.IsReady.Should().BeFalse();
        result.IsBackendOnline.Should().BeFalse();
        result.ActiveBackendName.Should().Be("Diffusion Nexus Core");
        result.MissingRequirements.Should().ContainSingle()
            .Which.Should().Contain("does not handle feature");
    }

    [Fact]
    public async Task WhenTheWrappedBackendThrowsThenTheFailureIsReportedNotPropagated()
    {
        var captioning = new Mock<ICaptioningBackend>();
        captioning.Setup(b => b.IsAvailableAsync(It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new DllNotFoundException("llama.dll"));
        var backend = new LocalInferenceFeatureBackend(captioning.Object);

        var result = await backend.CheckFeatureAsync(Feature.Captioning);

        result.IsReady.Should().BeFalse();
        result.IsBackendOnline.Should().BeFalse();
        result.ActiveBackendName.Should().Be("Diffusion Nexus Core");
        result.MissingRequirements.Should().ContainSingle()
            .Which.Should().Be("Local readiness check failed: llama.dll");
    }

    [Fact]
    public async Task WhenTheWrappedDiffusionBackendThrowsThenTheFailureIsReportedNotPropagated()
    {
        var diffusion = new Mock<IDiffusionBackend>();
        diffusion.Setup(b => b.IsAvailableAsync(It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new InvalidOperationException("model catalog corrupt"));
        var backend = new LocalInferenceFeatureBackend(diffusion: diffusion.Object);

        var result = await backend.CheckFeatureAsync(Feature.Inpainting);

        result.IsReady.Should().BeFalse();
        result.MissingRequirements.Should().ContainSingle()
            .Which.Should().Contain("model catalog corrupt");
    }

    [Fact]
    public async Task WhenTheCheckIsCancelledThenCancellationPropagatesInsteadOfBecomingABlocker()
    {
        // Cancellation is the caller's business — swallowing it would show a bogus
        // "readiness check failed" blocker every time the user navigates away.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var captioning = new Mock<ICaptioningBackend>();
        captioning.Setup(b => b.IsAvailableAsync(It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new OperationCanceledException(cts.Token));
        var backend = new LocalInferenceFeatureBackend(captioning.Object);

        var act = async () => await backend.CheckFeatureAsync(Feature.Captioning, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WhenCheckingThenTheCancellationTokenReachesTheWrappedBackend()
    {
        using var cts = new CancellationTokenSource();
        var captioning = CaptioningMock(available: true);
        var backend = new LocalInferenceFeatureBackend(captioning.Object);

        await backend.CheckFeatureAsync(Feature.Captioning, cts.Token);

        captioning.Verify(b => b.IsAvailableAsync(cts.Token), Times.Once);
    }

    #endregion
}
