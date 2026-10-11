using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Engine;

/// <summary>
/// Start / Stop / Restart on the Engine's tile in the Unified Console's instance row (#610). The Engine
/// is not a launched package (its row has no executable), so the generic launch did nothing.
/// </summary>
public class EngineConsoleControlTests
{
    private const string Root = @"E:\Engine\ComfyUI";

    private readonly FakeEngine _engine = new();
    private readonly Mock<IEngineRootResolver> _roots = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly Mock<IUnifiedLogger> _logger = new();

    public EngineConsoleControlTests()
    {
        _roots.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Root);
    }

    private EngineConsoleControl Create() => new(_engine, _roots.Object, _dialogs.Object, _logger.Object);

    [Fact]
    public async Task Start_StartsTheEngineFromItsInstallFolder()
    {
        await Create().StartAsync();

        _engine.StartedFrom.Should().Equal(Root);
    }

    [Fact]
    public async Task Start_WithNoEngineInstalled_SaysSoInsteadOfDoingNothing()
    {
        _roots.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        await Create().StartAsync();

        _engine.StartedFrom.Should().BeEmpty();
        _logger.Verify(l => l.Warn(LogCategory.InstanceManagement, "Diffusion Nexus Engine",
            It.Is<string>(m => m.Contains("not installed")), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Start_ThatFails_LogsTheEnginesReason()
    {
        _engine.StartResult = new EngineStartResult(false, null, "The engine's Python environment was not found.");

        await Create().StartAsync();

        _logger.Verify(l => l.Warn(LogCategory.InstanceManagement, "Diffusion Nexus Engine",
            It.Is<string>(m => m.Contains("The engine's Python environment was not found.")), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Stop_WhenIdle_StopsWithoutAsking()
    {
        _engine.Running = true;

        var stopped = await Create().StopAsync();

        stopped.Should().BeTrue();
        _engine.Stops.Should().Be(1);
        _dialogs.Verify(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>()), Times.Never);
    }

    [Fact]
    public async Task Stop_WithAGenerationRunning_AsksFirst_AndKeepRunningKeepsIt()
    {
        _engine.Running = true;
        _engine.HasActiveJobs = true;
        _dialogs.Setup(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>())).ReturnsAsync(1);

        var stopped = await Create().StopAsync();

        stopped.Should().BeFalse();
        _engine.Stops.Should().Be(0, "the user chose to keep the generation running");
        _dialogs.Verify(d => d.ShowOptionsAsync(It.IsAny<string>(),
            It.Is<string>(m => m.Contains("generation is running")), "Stop anyway", "Keep running"), Times.Once);
    }

    [Fact]
    public async Task Stop_WithAGenerationRunning_StopAnywayStopsIt()
    {
        _engine.Running = true;
        _engine.HasActiveJobs = true;
        _dialogs.Setup(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>())).ReturnsAsync(0);

        var stopped = await Create().StopAsync();

        stopped.Should().BeTrue();
        _engine.Stops.Should().Be(1);
    }

    [Fact]
    public async Task Stop_WithAGenerationRunning_ADismissedDialogKeepsIt()
    {
        _engine.Running = true;
        _engine.HasActiveJobs = true;
        _dialogs.Setup(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>())).ReturnsAsync(-1);

        (await Create().StopAsync()).Should().BeFalse();
        _engine.Stops.Should().Be(0);
    }

    [Fact]
    public async Task Stop_WithAGenerationRunning_AndNoDialog_SaysWhyItDidNotStop()
    {
        _engine.Running = true;
        _engine.HasActiveJobs = true;
        var control = new EngineConsoleControl(_engine, _roots.Object, dialogs: null, _logger.Object);

        (await control.StopAsync()).Should().BeFalse();

        _engine.Stops.Should().Be(0);
        _logger.Verify(l => l.Warn(LogCategory.InstanceManagement, "Diffusion Nexus Engine",
            It.Is<string>(m => m.Contains("generation is running")), It.IsAny<string?>()), Times.Once,
            "a button that does nothing must say why");
    }

    [Fact]
    public async Task Restart_StopsThenStartsAgain()
    {
        _engine.Running = true;

        await Create().RestartAsync();

        _engine.Calls.Should().Equal("stop", "start");
    }

    [Fact]
    public async Task Restart_WithAGenerationRunning_KeepRunningRestartsNothing()
    {
        _engine.Running = true;
        _engine.HasActiveJobs = true;
        _dialogs.Setup(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>())).ReturnsAsync(1);

        await Create().RestartAsync();

        _engine.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AStartThatThrows_IsLogged_NotThrown()
    {
        _roots.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("database locked"));

        var act = () => Create().StartAsync();

        await act.Should().NotThrowAsync("a console button must never crash the app");
        _logger.Verify(l => l.Error(LogCategory.InstanceManagement, "Diffusion Nexus Engine",
            It.Is<string>(m => m.Contains("database locked")), It.IsAny<Exception?>()), Times.Once);
    }

    private sealed class FakeEngine : IManagedEngineControl
    {
        public bool Running { get; set; }
        public bool IsRunning => Running;
        public string? BaseUrl => Running ? "http://127.0.0.1:50123" : null;
        public bool HasActiveJobs { get; set; }
        public EngineStartResult StartResult { get; set; } = new(true, "http://127.0.0.1:50123", null);
        public List<string> StartedFrom { get; } = [];
        public List<string> Calls { get; } = [];
        public int Stops { get; private set; }

#pragma warning disable CS0067 // the control does not subscribe; the console does
        public event Action<bool>? RunningStateChanged;
#pragma warning restore CS0067

        public Task<EngineStartResult> EnsureRunningAsync(string installRoot, CancellationToken ct)
        {
            StartedFrom.Add(installRoot);
            Calls.Add("start");
            Running = StartResult.IsRunning;
            return Task.FromResult(StartResult);
        }

        public Task StopAsync()
        {
            Stops++;
            Calls.Add("stop");
            Running = false;
            return Task.CompletedTask;
        }

        public void RequestRestart() { }
        public bool IsRestartPending => false;
        public IDisposable BeginJob() => throw new NotSupportedException("not used by the console control");
    }
}
