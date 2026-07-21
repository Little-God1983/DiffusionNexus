using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

/// <summary>
/// Unit tests for <see cref="ComfyUICaptioningBackend.IsAvailableAsync"/> — the adapter that
/// projects the unified <see cref="IFeatureReadinessService"/> onto the older
/// <c>IsAvailableAsync / MissingRequirements / Warnings</c> surface. Both failure modes have
/// to leave the two mutable properties in a coherent state, because the captioning UI reads
/// them straight after the call.
/// </summary>
public class ComfyUICaptioningBackendTests
{
    private readonly Mock<IComfyUIWrapperService> _comfyUi = new();
    private readonly Mock<IFeatureReadinessService> _readiness = new();

    private ComfyUICaptioningBackend CreateBackend(bool withReadinessService = true) =>
        new(_comfyUi.Object, withReadinessService ? _readiness.Object : null);

    private static FeatureReadinessResult Result(
        bool isReady,
        IReadOnlyList<string>? missing = null,
        IReadOnlyList<string>? warnings = null) => new()
    {
        Feature = Feature.Captioning,
        Backend = BackendKind.ComfyUI,
        IsBackendOnline = true,
        IsReady = isReady,
        ActiveBackendName = "ComfyUI",
        MissingRequirements = missing ?? [],
        Warnings = warnings ?? []
    };

    #region Construction / identity

    [Fact]
    public void WhenComfyUiServiceIsNullThenConstructorThrows()
    {
        var act = () => new ComfyUICaptioningBackend(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenFreshlyConstructedThenRequirementsAndWarningsAreEmpty()
    {
        var backend = CreateBackend();

        backend.MissingRequirements.Should().BeEmpty();
        backend.Warnings.Should().BeEmpty();
        backend.DisplayName.Should().Contain("ComfyUI").And.Contain("Qwen3-VL");
    }

    #endregion

    #region No readiness service configured

    [Fact]
    public async Task WhenNoReadinessServiceIsConfiguredThenBackendIsUnavailable()
    {
        var backend = CreateBackend(withReadinessService: false);

        var available = await backend.IsAvailableAsync();

        available.Should().BeFalse();
        backend.MissingRequirements.Should().ContainSingle()
            .Which.Should().Contain("readiness service is not configured");
        backend.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenNoReadinessServiceIsConfiguredThenTheComfyUiWrapperIsNeverCalled()
    {
        var backend = CreateBackend(withReadinessService: false);

        await backend.IsAvailableAsync();

        _comfyUi.VerifyNoOtherCalls();
    }

    #endregion

    #region Delegation to the readiness service

    [Fact]
    public async Task WhenReadinessReportsReadyThenBackendIsAvailableWithNoBlockers()
    {
        _readiness.Setup(r => r.CheckAsync(Feature.Captioning, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Result(isReady: true));
        var backend = CreateBackend();

        var available = await backend.IsAvailableAsync();

        available.Should().BeTrue();
        backend.MissingRequirements.Should().BeEmpty();
        backend.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenReadinessReportsBlockersThenTheyAreCopiedOntoTheBackend()
    {
        _readiness.Setup(r => r.CheckAsync(Feature.Captioning, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Result(
                      isReady: false,
                      missing: ["Custom node missing: ComfyUI-QwenVL"],
                      warnings: ["Model downloads on first run"]));
        var backend = CreateBackend();

        var available = await backend.IsAvailableAsync();

        available.Should().BeFalse();
        backend.MissingRequirements.Should().ContainSingle()
            .Which.Should().Be("Custom node missing: ComfyUI-QwenVL");
        backend.Warnings.Should().ContainSingle()
            .Which.Should().Be("Model downloads on first run");
    }

    [Fact]
    public async Task WhenReadinessReportsReadyWithWarningsThenWarningsDoNotBlockAvailability()
    {
        _readiness.Setup(r => r.CheckAsync(Feature.Captioning, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Result(isReady: true, warnings: ["Auto-download pending"]));
        var backend = CreateBackend();

        var available = await backend.IsAvailableAsync();

        available.Should().BeTrue();
        backend.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task WhenCheckingAvailabilityThenTheCaptioningFeatureAndTokenAreForwarded()
    {
        using var cts = new CancellationTokenSource();
        _readiness.Setup(r => r.CheckAsync(It.IsAny<Feature>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Result(isReady: true));
        var backend = CreateBackend();

        await backend.IsAvailableAsync(cts.Token);

        _readiness.Verify(r => r.CheckAsync(Feature.Captioning, cts.Token), Times.Once);
    }

    [Fact]
    public async Task WhenReadinessStateChangesThenTheSecondCallReplacesTheFirstResult()
    {
        _readiness.SetupSequence(r => r.CheckAsync(Feature.Captioning, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Result(isReady: false, missing: ["Model missing"], warnings: ["A warning"]))
                  .ReturnsAsync(Result(isReady: true));
        var backend = CreateBackend();

        (await backend.IsAvailableAsync()).Should().BeFalse();
        (await backend.IsAvailableAsync()).Should().BeTrue();

        backend.MissingRequirements.Should().BeEmpty("stale blockers must not linger after a successful check");
        backend.Warnings.Should().BeEmpty();
    }

    #endregion

    #region Readiness check failure

    [Fact]
    public async Task WhenTheReadinessCheckThrowsThenTheExceptionBecomesAMissingRequirement()
    {
        _readiness.Setup(r => r.CheckAsync(Feature.Captioning, It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new HttpRequestException("connection refused"));
        var backend = CreateBackend();

        var available = await backend.IsAvailableAsync();

        available.Should().BeFalse("a failed check is never reported as ready");
        backend.MissingRequirements.Should().ContainSingle()
            .Which.Should().Be("Readiness check failed: connection refused");
        backend.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenTheReadinessCheckThrowsThenPreviouslyCapturedWarningsAreCleared()
    {
        _readiness.SetupSequence(r => r.CheckAsync(Feature.Captioning, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Result(isReady: true, warnings: ["Auto-download pending"]))
                  .ThrowsAsync(new InvalidOperationException("router exploded"));
        var backend = CreateBackend();

        await backend.IsAvailableAsync();
        backend.Warnings.Should().ContainSingle();

        await backend.IsAvailableAsync();

        backend.Warnings.Should().BeEmpty();
        backend.MissingRequirements.Should().ContainSingle()
            .Which.Should().Contain("router exploded");
    }

    [Fact]
    public async Task WhenTheReadinessCheckIsCancelledThenTheCancellationPropagatesInsteadOfBecomingABlocker()
    {
        // Cancellation is the caller's business, not a backend availability problem -- swallowing
        // it into MissingRequirements would show the user a bogus "readiness check failed"
        // blocker every time they navigate away. Matches LocalInferenceFeatureBackend (#434).
        _readiness.Setup(r => r.CheckAsync(Feature.Captioning, It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new OperationCanceledException("cancelled"));
        var backend = CreateBackend();

        var act = async () => await backend.IsAvailableAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region Single-caption generation

    [Fact]
    public async Task WhenTheImagePathIsBlankThenGenerationThrows()
    {
        var backend = CreateBackend();

        var act = async () => await backend.GenerateSingleCaptionAsync("   ", "describe this");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task WhenTheImageDoesNotExistThenGenerationFailsWithoutCallingComfyUI()
    {
        var backend = CreateBackend();
        var missingPath = Path.Combine(Path.GetTempPath(), $"dn-missing-{Guid.NewGuid():N}.png");

        var result = await backend.GenerateSingleCaptionAsync(missingPath, "describe this");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Image file not found.");
        _comfyUi.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WhenComfyUIReturnsNoTextThenGenerationFails()
    {
        var imagePath = CreateTempImage();
        try
        {
            _comfyUi.Setup(c => c.GenerateCaptionAsync(
                        imagePath, It.IsAny<string>(), It.IsAny<float>(),
                        It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((string?)null);
            var backend = CreateBackend();

            var result = await backend.GenerateSingleCaptionAsync(imagePath, "describe this");

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("no caption text");
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task WhenComfyUIThrowsThenTheErrorIsReturnedInsteadOfPropagating()
    {
        var imagePath = CreateTempImage();
        try
        {
            _comfyUi.Setup(c => c.GenerateCaptionAsync(
                        imagePath, It.IsAny<string>(), It.IsAny<float>(),
                        It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new HttpRequestException("server gone"));
            var backend = CreateBackend();

            var result = await backend.GenerateSingleCaptionAsync(imagePath, "describe this");

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("ComfyUI error").And.Contain("server gone");
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task WhenComfyUIReturnsACaptionThenTheTriggerWordIsAppliedByPostProcessing()
    {
        var imagePath = CreateTempImage();
        try
        {
            _comfyUi.Setup(c => c.GenerateCaptionAsync(
                        imagePath, It.IsAny<string>(), It.IsAny<float>(),
                        It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync("a woman standing in a field");
            var backend = CreateBackend();

            var result = await backend.GenerateSingleCaptionAsync(
                imagePath, "describe this", triggerWord: "mytoken");

            result.Success.Should().BeTrue();
            result.Caption.Should().StartWith("mytoken");
            result.Caption.Should().Contain("a woman standing in a field");
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    private static string CreateTempImage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dn-caption-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        return path;
    }

    #endregion
}
