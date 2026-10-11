using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>The Engine's tile in the Unified Console's instance row (#610).</summary>
public class UnifiedConsoleEngineTabTests
{
    private readonly FakeEngine _engine = new();

    private UnifiedConsoleViewModel CreateConsole()
    {
        var logger = new Mock<IUnifiedLogger>();
        logger.Setup(l => l.LogStream).Returns(Mock.Of<IObservable<LogEntry>>());
        logger.Setup(l => l.GetEntries(It.IsAny<LogCategory?>(), It.IsAny<LogLevel?>())).Returns([]);
        var roots = new Mock<IEngineRootResolver>();
        roots.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(@"E:\Engine\ComfyUI");

        // No IUnitOfWork: the instance load fails quietly (logged) and leaves the tabs to the test.
        var services = new ServiceCollection()
            .AddSingleton<IManagedEngineControl>(_engine)
            .AddSingleton(roots.Object)
            .BuildServiceProvider();

        var console = new UnifiedConsoleViewModel(logger.Object, Mock.Of<ITaskTracker>());
        console.InitializeInstanceManagement(new PackageProcessManager(), services);
        return console;
    }

    private static InstanceTabItem EngineTab(int id = 35) =>
        new(id, "Diffusion Nexus Engine", InstallerType.ComfyUI) { IsEngine = true };

    [Fact]
    public void EngineTabs_TakeTheEnginesCurrentState_NotAStaleEvent()
    {
        // Start and an exit report on different threads; the last event to arrive may be the older
        // state. Every Engine tab (an old build could leave two app-managed rows) reads the engine.
        var console = CreateConsole();
        var first = EngineTab(35);
        var stale = EngineTab(36);
        first.IsRunning = true;
        stale.IsRunning = true;
        console.InstanceTabs.Add(first);
        console.InstanceTabs.Add(stale);

        _engine.Running = false;
        console.ApplyEngineState();
        first.IsRunning.Should().BeFalse();
        stale.IsRunning.Should().BeFalse();
        first.DetectedWebUrl.Should().BeNull();

        _engine.Running = true;
        console.ApplyEngineState();
        first.IsRunning.Should().BeTrue();
        stale.IsRunning.Should().BeTrue();
        first.DetectedWebUrl.Should().Be(_engine.BaseUrl);
    }

    [Fact]
    public async Task StartFromTheTile_SaysStarting_UntilTheStartEnds()
    {
        var console = CreateConsole();
        var tab = EngineTab();
        console.InstanceTabs.Add(tab);
        var gate = new TaskCompletionSource<EngineStartResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.StartGate = gate.Task;

        console.LaunchInstanceCommand.Execute(tab);

        tab.IsStarting.Should().BeTrue("a start takes up to two minutes; the tile must show it is under way");
        tab.ShowStartButton.Should().BeFalse("a second Start would only queue behind the first");

        gate.SetResult(new EngineStartResult(false, null, "no venv"));
        for (var i = 0; i < 50 && tab.IsStarting; i++) await Task.Delay(20);
        tab.IsStarting.Should().BeFalse();
        tab.ShowStartButton.Should().BeTrue("the start failed: Start again is the way forward");
    }

    [Fact]
    public void AStartingTab_HidesStartAndUpdate()
    {
        var tab = EngineTab();
        tab.IsUpdateAvailable = true;

        tab.IsStarting = true;

        tab.ShowStartButton.Should().BeFalse();
        tab.ShowUpdateButton.Should().BeFalse("an update must not run in the folder the Engine is starting from");
    }

    private sealed class FakeEngine : IManagedEngineControl
    {
        public bool Running { get; set; }
        public Task<EngineStartResult>? StartGate { get; set; }
        public bool IsRunning => Running;
        public string? BaseUrl => Running ? "http://127.0.0.1:50123" : null;
        public bool HasActiveJobs => false;
        public bool IsRestartPending => false;

#pragma warning disable CS0067 // raising it would post to the UI dispatcher; the tests call ApplyEngineState
        public event Action<bool>? RunningStateChanged;
#pragma warning restore CS0067

        public Task<EngineStartResult> EnsureRunningAsync(string installRoot, CancellationToken ct) =>
            StartGate ?? Task.FromResult(new EngineStartResult(true, BaseUrl, null));

        public Task StopAsync() => Task.CompletedTask;
        public void RequestRestart() { }
        public IDisposable BeginJob() => throw new NotSupportedException();
    }
}
