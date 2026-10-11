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
    private readonly Mock<IDialogService> _dialogs = new();

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
            .AddSingleton(_dialogs.Object)
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
        first.DetectedWebUrl.Should().BeNull(
            "no Web UI for the app-owned Engine: work queued in that page is invisible to Stop's \"ask first\"");
    }

    [Fact]
    public void EngineTabs_ShowTheEnginesStart_WhoeverStartedIt()
    {
        // Starting is the engine's state, not the tab's: the Canvas starting it on demand, a pending
        // restart carried out, a Restart from the tile and a reload of the tabs all show it.
        var console = CreateConsole();
        var tab = EngineTab();
        console.InstanceTabs.Add(tab);

        _engine.Starting = true;
        console.ApplyEngineState();

        tab.IsStarting.Should().BeTrue();
        tab.ShowStartButton.Should().BeFalse("a second Start would only queue behind the first");
        tab.ShowStopButton.Should().BeTrue("a start can be aborted: the engine stops it within seconds");

        _engine.Starting = false;
        console.ApplyEngineState();
        tab.IsStarting.Should().BeFalse();
        tab.ShowStartButton.Should().BeTrue();
    }

    [Fact]
    public void TheEnginesStopDialog_DoesNotGreyOutStopOnEveryOtherTab()
    {
        // Stop and Restart are one command each for all tabs: awaiting the Engine's dialog (or its
        // two-minute restart) inside them would disable those buttons on every package's tab.
        var console = CreateConsole();
        var tab = EngineTab();
        console.InstanceTabs.Add(tab);
        _engine.Busy = true;
        var answer = new TaskCompletionSource<int>();
        _dialogs.Setup(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>())).Returns(answer.Task);

        console.StopInstanceCommand.Execute(tab);
        console.RestartInstanceCommand.Execute(tab);

        console.StopInstanceCommand.IsRunning.Should().BeFalse("the dialog is still open");
        console.RestartInstanceCommand.IsRunning.Should().BeFalse();
        answer.SetResult(1);
    }

    [Fact]
    public void AnUpdatingTab_HidesStart()
    {
        var tab = EngineTab();

        tab.IsUpdating = true;

        tab.ShowStartButton.Should().BeFalse("the Engine must not start from a folder being updated");
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
        public bool Starting { get; set; }
        public bool IsRunning => Running;
        public bool IsStarting => Starting;
        public string? BaseUrl => Running ? "http://127.0.0.1:50123" : null;
        public bool Busy { get; set; }
        public bool HasActiveJobs => Busy;
        public bool IsRestartPending => false;

#pragma warning disable CS0067 // raising it would post to the UI dispatcher; the tests call ApplyEngineState
        public event Action? StateChanged;
#pragma warning restore CS0067

        public Task<EngineStartResult> EnsureRunningAsync(string installRoot, CancellationToken ct) =>
            Task.FromResult(new EngineStartResult(true, BaseUrl, null));
        public IDisposable BeginPreparing() => throw new NotSupportedException();

        public Task StopAsync() => Task.CompletedTask;
        public Task<bool> StopIfIdleAsync() => Task.FromResult(true);
        public void RequestRestart() { }
        public IDisposable BeginJob() => throw new NotSupportedException();
    }
}
