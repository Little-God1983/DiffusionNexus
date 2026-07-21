using System.Net;
using System.Net.Sockets;
using System.Text;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

/// <summary>
/// Unit tests for <see cref="ComfyUIFeatureBackend"/>.
///
/// <para>
/// The class documents a hard contract: feature readiness must be decided by the same
/// disk-walking <see cref="IWorkloadInstallationChecker"/> the Installer Manager workload
/// dialog uses, so the two surfaces can never disagree (issue #356). These tests pin that —
/// the workload id comes from <see cref="FeatureRegistry"/>, anything short of
/// <see cref="WorkloadCheckSummary.IsFullyInstalled"/> blocks, and the checker's missing
/// items are surfaced verbatim rather than re-derived from the ComfyUI server.
/// </para>
///
/// <para>
/// The live health check hits <c>{serverUrl}/system_stats</c> with a real
/// <see cref="HttpClient"/>, so the "server online" branches are exercised against a
/// throwaway loopback socket rather than being mocked away.
/// </para>
/// </summary>
public class ComfyUIFeatureBackendTests
{
    private readonly Mock<IComfyUIWrapperService> _comfyUi = new();
    private readonly Mock<IAppSettingsService> _settings = new();
    private readonly Mock<IWorkloadInstallationChecker> _workloadChecker = new();

    private void GivenServerUrl(string url) =>
        _settings.Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AppSettings { ComfyUiServerUrl = url });

    private void GivenWorkloadSummary(bool fullyInstalled, params string[] missingItems) =>
        _workloadChecker.Setup(c => c.CheckAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync((Guid id, CancellationToken _) => new WorkloadCheckSummary
                        {
                            WorkloadId = id,
                            WorkloadName = "Captioning-Qwen-3-VL",
                            IsFullyInstalled = fullyInstalled,
                            MissingItems = missingItems,
                            CheckedAgainstPath = @"C:\ComfyUI"
                        });

    private ComfyUIFeatureBackend CreateBackend(IUnifiedLogger? logger = null) =>
        new(_comfyUi.Object, _settings.Object, _workloadChecker.Object, logger);

    /// <summary>Returns a loopback URL that is guaranteed to have nothing listening on it.</summary>
    private static string ClosedPortUrl()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return $"http://127.0.0.1:{port}";
    }

    #region Construction / identity

    [Fact]
    public void WhenAnyRequiredDependencyIsNullThenConstructorThrows()
    {
        var withoutComfy = () => new ComfyUIFeatureBackend(null!, _settings.Object, _workloadChecker.Object);
        var withoutSettings = () => new ComfyUIFeatureBackend(_comfyUi.Object, null!, _workloadChecker.Object);
        var withoutChecker = () => new ComfyUIFeatureBackend(_comfyUi.Object, _settings.Object, null!);

        withoutComfy.Should().Throw<ArgumentNullException>();
        withoutSettings.Should().Throw<ArgumentNullException>();
        withoutChecker.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenInspectingIdentityThenBackendDeclaresComfyUI()
    {
        var backend = CreateBackend();

        backend.Kind.Should().Be(BackendKind.ComfyUI);
        backend.DisplayName.Should().Be("ComfyUI");
    }

    #endregion

    #region Unregistered feature

    [Fact]
    public async Task WhenFeatureHasNoRegisteredRequirementsThenResultIsNotReadyAndNoNetworkCallIsMade()
    {
        var backend = CreateBackend();

        var result = await backend.CheckFeatureAsync((Feature)9999);

        result.IsReady.Should().BeFalse();
        result.IsBackendOnline.Should().BeFalse();
        result.ActiveBackendName.Should().Be("ComfyUI");
        result.MissingRequirements.Should().ContainSingle()
            .Which.Should().Contain("not registered in the readiness system");
        _settings.Verify(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()), Times.Never);
        _workloadChecker.Verify(c => c.CheckAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Server URL resolution failures

    [Fact]
    public async Task WhenSettingsLookupThrowsThenResultIsBackendOfflineWithUnknownEndpoint()
    {
        _settings.Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new InvalidOperationException("settings database unavailable"));
        var backend = CreateBackend();

        var result = await backend.CheckFeatureAsync(Feature.Captioning);

        result.IsReady.Should().BeFalse();
        result.IsBackendOnline.Should().BeFalse();
        result.Endpoint.Should().Be("(unknown)");
        result.MissingRequirements.Should().ContainSingle()
            .Which.Should().Contain("Could not resolve ComfyUI server URL");
        _workloadChecker.Verify(c => c.CheckAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Installed-but-not-running (server offline)

    [Fact]
    public async Task WhenServerIsUnreachableThenResultIsOfflineAndTheDiskCheckIsSkipped()
    {
        // Everything may well be installed on disk; with the server down the feature still
        // cannot run, and spending time walking the disk would be pointless.
        var url = ClosedPortUrl();
        GivenServerUrl(url);
        GivenWorkloadSummary(fullyInstalled: true);
        var backend = CreateBackend();

        var result = await backend.CheckFeatureAsync(Feature.Captioning);

        result.IsReady.Should().BeFalse();
        result.IsBackendOnline.Should().BeFalse();
        result.Endpoint.Should().Be(url);
        result.MissingRequirements.Should().ContainSingle()
            .Which.Should().Contain("not reachable").And.Contain(url);
        _workloadChecker.Verify(c => c.CheckAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhenServerAnswersWithAnErrorStatusThenItCountsAsOffline()
    {
        using var server = new LoopbackHttpServer(statusCode: 500, reason: "Internal Server Error");
        GivenServerUrl(server.Url);
        GivenWorkloadSummary(fullyInstalled: true);
        var backend = CreateBackend();

        var result = await backend.CheckFeatureAsync(Feature.Captioning);

        result.IsBackendOnline.Should().BeFalse();
        result.IsReady.Should().BeFalse();
        _workloadChecker.Verify(c => c.CheckAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Readiness merge against the workload checker

    [Fact]
    public async Task WhenServerIsOnlineAndWorkloadIsFullyInstalledThenFeatureIsReady()
    {
        using var server = new LoopbackHttpServer();
        GivenServerUrl(server.Url);
        GivenWorkloadSummary(fullyInstalled: true);
        var backend = CreateBackend();

        var result = await backend.CheckFeatureAsync(Feature.Captioning);

        result.IsReady.Should().BeTrue();
        result.IsBackendOnline.Should().BeTrue();
        result.Backend.Should().Be(BackendKind.ComfyUI);
        result.MissingRequirements.Should().BeEmpty();
        result.Warnings.Should().BeEmpty();
        result.Endpoint.Should().Be(server.Url);
    }

    [Fact]
    public async Task WhenWorkloadIsIncompleteThenMissingItemsAreSurfacedVerbatimWithAnInstallerManagerHint()
    {
        using var server = new LoopbackHttpServer();
        GivenServerUrl(server.Url);
        GivenWorkloadSummary(
            fullyInstalled: false,
            "Custom node missing: ComfyUI-QwenVL",
            "Model missing: qwen3-vl-8b.gguf");
        var backend = CreateBackend();

        var result = await backend.CheckFeatureAsync(Feature.Captioning);

        result.IsReady.Should().BeFalse("anything short of fully installed blocks, matching the Installer Manager");
        result.IsBackendOnline.Should().BeTrue("the server itself answered the health check");
        result.MissingRequirements.Should().HaveCount(3);
        result.MissingRequirements.Should().Contain("Custom node missing: ComfyUI-QwenVL");
        result.MissingRequirements.Should().Contain("Model missing: qwen3-vl-8b.gguf");
        result.MissingRequirements[^1].Should().Contain("Installer Manager").And.Contain("Captioning-Qwen-3-VL");
    }

    [Fact]
    public async Task WhenWorkloadIsIncompleteWithNoNamedItemsThenOnlyTheRemediationHintIsReported()
    {
        using var server = new LoopbackHttpServer();
        GivenServerUrl(server.Url);
        GivenWorkloadSummary(fullyInstalled: false);
        var backend = CreateBackend();

        var result = await backend.CheckFeatureAsync(Feature.Captioning);

        result.IsReady.Should().BeFalse();
        result.MissingRequirements.Should().ContainSingle()
            .Which.Should().Contain("Installer Manager");
    }

    [Theory]
    [InlineData(Feature.Captioning)]
    [InlineData(Feature.Inpainting)]
    [InlineData(Feature.BatchUpscale)]
    [InlineData(Feature.BatchUpscaleVision)]
    [InlineData(Feature.Outpaint)]
    [InlineData(Feature.OutpaintVision)]
    public async Task WhenCheckingAFeatureThenTheWorkloadIdFromTheRegistryIsHandedToTheChecker(Feature feature)
    {
        // This is the Installer-Manager-agreement contract: readiness is decided against the
        // exact SDK workload the installer dialog would install.
        var expectedWorkloadId = FeatureRegistry.GetRequirements(feature)!.WorkloadConfigurationId;
        expectedWorkloadId.Should().NotBeNull();
        expectedWorkloadId!.Value.Should().NotBe(Guid.Empty);

        using var server = new LoopbackHttpServer();
        GivenServerUrl(server.Url);
        GivenWorkloadSummary(fullyInstalled: true);
        var backend = CreateBackend();

        await backend.CheckFeatureAsync(feature);

        _workloadChecker.Verify(
            c => c.CheckAsync(expectedWorkloadId!.Value, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenServerUrlHasATrailingSlashThenTheEndpointIsNormalised()
    {
        using var server = new LoopbackHttpServer();
        GivenServerUrl(server.Url + "/");
        GivenWorkloadSummary(fullyInstalled: true);
        var backend = CreateBackend();

        var result = await backend.CheckFeatureAsync(Feature.Captioning);

        result.Endpoint.Should().Be(server.Url, "a doubled slash before /system_stats would break the health check");
        result.IsBackendOnline.Should().BeTrue();
    }

    [Fact]
    public async Task WhenCheckingThenTheCancellationTokenReachesTheWorkloadChecker()
    {
        using var cts = new CancellationTokenSource();
        using var server = new LoopbackHttpServer();
        GivenServerUrl(server.Url);
        GivenWorkloadSummary(fullyInstalled: true);
        var backend = CreateBackend();

        await backend.CheckFeatureAsync(Feature.Captioning, cts.Token);

        _workloadChecker.Verify(c => c.CheckAsync(It.IsAny<Guid>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task WhenTheFeatureIsReadyThenTheComfyUiWrapperIsNeverQueried()
    {
        // The /object_info fallback was deliberately removed because it disagreed with the
        // Installer Manager; the wrapper must stay out of the readiness path.
        using var server = new LoopbackHttpServer();
        GivenServerUrl(server.Url);
        GivenWorkloadSummary(fullyInstalled: true);
        var backend = CreateBackend();

        await backend.CheckFeatureAsync(Feature.Captioning);

        _comfyUi.VerifyNoOtherCalls();
    }

    #endregion

    #region Logging

    [Fact]
    public async Task WhenNoUnifiedLoggerIsSuppliedThenTheCheckStillCompletes()
    {
        using var server = new LoopbackHttpServer();
        GivenServerUrl(server.Url);
        GivenWorkloadSummary(fullyInstalled: true);
        var backend = CreateBackend(logger: null);

        var act = async () => await backend.CheckFeatureAsync(Feature.Captioning);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WhenNoUnifiedLoggerIsSuppliedThenOfflineAndUnregisteredPathsAlsoSurvive()
    {
        GivenServerUrl(ClosedPortUrl());
        var backend = CreateBackend(logger: null);

        (await backend.CheckFeatureAsync(Feature.Captioning)).IsBackendOnline.Should().BeFalse();
        (await backend.CheckFeatureAsync((Feature)9999)).IsReady.Should().BeFalse();
    }

    [Fact]
    public async Task WhenAUnifiedLoggerIsSuppliedThenTheReadinessOutcomeIsEchoedToTheConsole()
    {
        var logger = new Mock<IUnifiedLogger>();
        using var server = new LoopbackHttpServer();
        GivenServerUrl(server.Url);
        GivenWorkloadSummary(fullyInstalled: true);
        var backend = CreateBackend(logger.Object);

        await backend.CheckFeatureAsync(Feature.Captioning);

        logger.Verify(
            l => l.Info(LogCategory.Configuration, "Readiness",
                        It.Is<string>(m => m.Contains("Ready=True")), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task WhenTheServerIsOfflineThenTheUnifiedLoggerReceivesAWarning()
    {
        var logger = new Mock<IUnifiedLogger>();
        GivenServerUrl(ClosedPortUrl());
        var backend = CreateBackend(logger.Object);

        await backend.CheckFeatureAsync(Feature.Captioning);

        logger.Verify(
            l => l.Warn(LogCategory.Configuration, "Readiness",
                        It.Is<string>(m => m.Contains("offline")), It.IsAny<string?>()),
            Times.Once);
    }

    #endregion

    /// <summary>
    /// Minimal loopback HTTP/1.1 responder. Uses a raw socket rather than
    /// <see cref="HttpListener"/> so no URL ACL registration is required on Windows.
    /// </summary>
    private sealed class LoopbackHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly int _statusCode;
        private readonly string _reason;

        public LoopbackHttpServer(int statusCode = 200, string reason = "OK")
        {
            _statusCode = statusCode;
            _reason = reason;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(AcceptLoopAsync);
        }

        /// <summary>Base URL with no trailing slash, matching the production URL normalisation.</summary>
        public string Url { get; }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => RespondAsync(client));
                }
            }
            catch
            {
                // Listener stopped — expected on Dispose.
            }
        }

        private async Task RespondAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();

                    // Drain the request head. A short read is fine — the content is
                    // irrelevant here, we only need the socket read before replying.
                    var scratch = new byte[4096];
                    int bytesRead = await stream.ReadAsync(scratch, _cts.Token);
                    if (bytesRead == 0)
                    {
                        return; // client hung up before sending a request
                    }

                    const string body = "{\"system\":{}}";
                    var response =
                        $"HTTP/1.1 {_statusCode} {_reason}\r\n" +
                        "Content-Type: application/json\r\n" +
                        $"Content-Length: {body.Length}\r\n" +
                        "Connection: close\r\n" +
                        "\r\n" + body;

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                }
            }
            catch
            {
                // Client vanished or the server is shutting down.
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); }
            catch { /* already stopped */ }
            _cts.Dispose();
        }
    }
}
