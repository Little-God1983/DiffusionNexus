using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services.Diffusion;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Engine;

public class ComfyUiClientProviderTests
{
    private ComfyUiServerConnection _current = new(ComfyUiServerMode.Engine, "http://127.0.0.1:8188/");
    private readonly Mock<IEngineRootResolver> _root = new();
    private readonly Mock<IManagedComfyUiEngine> _engine = new();
    private readonly List<string> _clientUrls = [];
    private readonly List<Mock<IComfyUIWrapperService>> _clients = [];
    private bool _looksInstalled = true;
    private Func<CancellationToken, Task<ComfyUiServerConnection>>? _readSettings;
    private Exception? _clientFactoryThrows;

    private void Mode(ComfyUiServerMode mode, string url = "http://127.0.0.1:8188/") =>
        _current = new ComfyUiServerConnection(mode, url);

    private ComfyUiClientProvider Sut() => new(
        _readSettings ?? (_ => Task.FromResult(_current)), _root.Object, _engine.Object, unifiedLogger: null,
        clientFactory: url =>
        {
            if (_clientFactoryThrows is not null) throw _clientFactoryThrows;
            _clientUrls.Add(url);
            var client = new Mock<IComfyUIWrapperService>();
            _clients.Add(client);
            return client.Object;
        },
        looksInstalled: _ => _looksInstalled);

    [Fact]
    public async Task CustomUrl_ReturnsClientOnTheSettingsUrl_AndNeverTouchesTheEngine()
    {
        Mode(ComfyUiServerMode.CustomUrl, "http://192.168.1.20:8188/");

        using var lease = await Sut().AcquireAsync();

        lease.Mode.Should().Be(ComfyUiServerMode.CustomUrl);
        lease.BaseUrl.Should().Be("http://192.168.1.20:8188/");
        _clientUrls.Should().Equal("http://192.168.1.20:8188/");
        _engine.Verify(e => e.EnsureRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _root.Verify(r => r.ResolveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("", "(empty)")]
    [InlineData("   ", "(empty)")]
    [InlineData("127.0.0.1:8188", "127.0.0.1:8188")]
    [InlineData("192.168.1.5:8188/", "192.168.1.5:8188/")]
    public async Task CustomUrl_InvalidUrl_Throws_WithoutCreatingAClient(string url, string shown)
    {
        Mode(ComfyUiServerMode.CustomUrl, url);

        var act = () => Sut().AcquireAsync();

        await act.Should().ThrowAsync<ComfyUiUnavailableException>()
            .WithMessage($"The ComfyUI server URL in Settings is not valid: '{shown}'. Fix it in Settings → ComfyUI Server.");
        _clientUrls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://127.0.0.1:8188", true)]
    [InlineData("https://comfy.example.com/", true)]
    [InlineData("127.0.0.1:8188", false)]
    [InlineData("192.168.1.5:8188/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("ftp://127.0.0.1:8188", false)]
    public void ComfyUiUrl_IsValid_AcceptsOnlyAbsoluteHttpOrHttps(string? url, bool expected) =>
        ComfyUiUrl.IsValid(url).Should().Be(expected);

    [Theory]
    [InlineData("http://192.168.1.20:8188/", "http://192.168.1.20:8188/")]
    [InlineData("127.0.0.1:8188", "http://127.0.0.1:8188")]
    [InlineData(null, "http://127.0.0.1:8188")]
    public void ComfyUiUrl_OrDefault_FallsBackToLocal8188(string? url, string expected) =>
        ComfyUiUrl.OrDefault(url).Should().Be(expected);

    [Fact]
    public async Task Engine_StartsTheEngine_ReportsProgress_AndReturnsClientOnItsPort()
    {
        Mode(ComfyUiServerMode.Engine);
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(@"C:\Engine\ComfyUI");
        _engine.Setup(e => e.EnsureRunningAsync(@"C:\Engine\ComfyUI", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStartResult(true, "http://127.0.0.1:51234", null));
        var reported = new List<string>();

        using var lease = await Sut().AcquireAsync(new SyncProgress(reported.Add));

        lease.Mode.Should().Be(ComfyUiServerMode.Engine);
        lease.BaseUrl.Should().Be("http://127.0.0.1:51234");
        _clientUrls.Should().Equal("http://127.0.0.1:51234");
        reported.Should().Contain("Starting Diffusion Nexus Engine…");
        _engine.Verify(e => e.EnsureRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Engine_NotInstalled_Throws_WithoutStartingAnything()
    {
        Mode(ComfyUiServerMode.Engine);
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _looksInstalled = false;

        var act = () => Sut().AcquireAsync();

        await act.Should().ThrowAsync<ComfyUiUnavailableException>()
            .WithMessage("Diffusion Nexus Engine is not installed*");
        _engine.Verify(e => e.EnsureRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Engine_StartFailure_Throws_WithTheEnginesReason()
    {
        Mode(ComfyUiServerMode.Engine);
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(@"C:\Engine\ComfyUI");
        _engine.Setup(e => e.EnsureRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStartResult(false, null, "The engine process exited on its own during startup (exit code 1)."));

        var act = () => Sut().AcquireAsync();

        await act.Should().ThrowAsync<ComfyUiUnavailableException>()
            .WithMessage("Diffusion Nexus Engine failed to start: The engine process exited on its own during startup (exit code 1).");
        _clientUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task SettingsReadThrows_BecomesUnavailable_WithTheSettingsHint()
    {
        var cause = new InvalidOperationException("database is locked");
        _readSettings = _ => throw cause;

        var act = () => Sut().AcquireAsync();

        (await act.Should().ThrowAsync<ComfyUiUnavailableException>()
            .WithMessage("Could not read the ComfyUI server setting: database is locked"))
            .Which.InnerException.Should().BeSameAs(cause);
    }

    [Fact]
    public async Task RootResolverThrows_InEngineMode_BecomesUnavailable_WithTheEngineHint()
    {
        Mode(ComfyUiServerMode.Engine);
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("database is locked"));

        var act = () => Sut().AcquireAsync();

        await act.Should().ThrowAsync<ComfyUiUnavailableException>()
            .WithMessage("Diffusion Nexus Engine could not be prepared: database is locked");
    }

    [Fact]
    public async Task EngineStartThrows_BecomesUnavailable_WithTheEngineHint()
    {
        Mode(ComfyUiServerMode.Engine);
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(@"C:\Engine\ComfyUI");
        _engine.Setup(e => e.EnsureRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.ComponentModel.Win32Exception("Access is denied"));

        var act = () => Sut().AcquireAsync();

        await act.Should().ThrowAsync<ComfyUiUnavailableException>()
            .WithMessage("Diffusion Nexus Engine could not be prepared: Access is denied");
    }

    [Fact]
    public async Task CustomUrlClientCreationThrows_BecomesUnavailable_WithTheUrl()
    {
        Mode(ComfyUiServerMode.CustomUrl, "http://192.168.1.20:8188/");
        _clientFactoryThrows = new InvalidOperationException("boom");

        var act = () => Sut().AcquireAsync();

        await act.Should().ThrowAsync<ComfyUiUnavailableException>()
            .WithMessage("Could not connect to your ComfyUI at http://192.168.1.20:8188/: boom");
    }

    [Fact]
    public async Task Cancellation_PropagatesUnchanged()
    {
        var cancelled = new OperationCanceledException();
        _readSettings = _ => throw cancelled;
        (await ((Func<Task>)(() => Sut().AcquireAsync())).Should().ThrowAsync<OperationCanceledException>())
            .Which.Should().BeSameAs(cancelled);

        _readSettings = null;
        Mode(ComfyUiServerMode.Engine);
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new TaskCanceledException());
        await ((Func<Task>)(() => Sut().AcquireAsync())).Should().ThrowExactlyAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task ModeIsReadOnEveryCall_SoASettingsChangeAppliesToTheNextGenerate()
    {
        Mode(ComfyUiServerMode.CustomUrl);
        var sut = Sut();
        (await sut.AcquireAsync()).Dispose();

        Mode(ComfyUiServerMode.Engine);
        _root.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(@"C:\Engine\ComfyUI");
        _engine.Setup(e => e.EnsureRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineStartResult(true, "http://127.0.0.1:51234", null));
        using var second = await sut.AcquireAsync();

        second.Mode.Should().Be(ComfyUiServerMode.Engine);
    }

    [Fact]
    public void Lease_DisposesTheClientOnlyWhenItOwnsIt()
    {
        var owned = new Mock<IComfyUIWrapperService>();
        var borrowed = new Mock<IComfyUIWrapperService>();

        new ComfyUiClientLease(owned.Object, ComfyUiServerMode.Engine, "http://x", ownsClient: true).Dispose();
        new ComfyUiClientLease(borrowed.Object, ComfyUiServerMode.CustomUrl, "http://y", ownsClient: false).Dispose();

        owned.Verify(c => c.Dispose(), Times.Once);
        borrowed.Verify(c => c.Dispose(), Times.Never);
    }

    /// <summary>Synchronous IProgress — Progress&lt;T&gt; posts to a sync context and races the assertion.</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
