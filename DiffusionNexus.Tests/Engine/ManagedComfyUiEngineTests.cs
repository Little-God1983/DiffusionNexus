using System.Diagnostics;
using System.Reflection;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Engine;

public class ManagedComfyUiEngineTests
{
    [Fact]
    public void AllocateFreePort_NeverReturns8188()
    {
        for (var i = 0; i < 20; i++)
        {
            var port = ManagedComfyUiEngine.AllocateFreePort();
            port.Should().NotBe(8188, "a user's own ComfyUI owns the default port");
            port.Should().BeInRange(1024, 65535);
        }
    }

    // #610: the console's Engine tile follows the engine, whoever started or stopped it (the Canvas
    // starts it on demand). Reported once per change, so a repeated stop is not a second "stopped".
    [Fact]
    public void RunningState_IsReportedOncePerChange()
    {
        var engine = new ManagedComfyUiEngine(unifiedLogger: null);
        var seen = new List<bool>();
        engine.StateChanged += () => seen.Add(engine.IsRunning);

        engine.ReportRunning(true);
        engine.ReportRunning(true);
        engine.ReportRunning(false);
        engine.ReportRunning(false);

        seen.Should().Equal(true, false);
    }

    [Fact]
    public void StartingState_IsReportedOncePerChange()
    {
        var engine = new ManagedComfyUiEngine(unifiedLogger: null);
        var seen = new List<bool>();
        engine.StateChanged += () => seen.Add(engine.IsStarting);

        engine.ReportStarting(true);
        engine.ReportStarting(true);
        engine.ReportStarting(false);

        seen.Should().Equal(true, false);
    }

    // #621 review: a start that only resolves paths and probes the server before its job begins is a
    // generation getting under way: Stop on the console asks first. A pending restart ignores it, or
    // the generation that carries the restart out would postpone it forever.
    [Fact]
    public async Task APreparingGeneration_CountsForTheConsole_ButNotForAPendingRestart()
    {
        await using var engine = new ManagedComfyUiEngine(unifiedLogger: null);
        var running = StartStandInEngine();
        using var observer = Process.GetProcessById(running.Id);
        try
        {
            PretendRunning(engine, running, "http://127.0.0.1:51234");
            engine.RequestRestart();

            using (engine.BeginPreparing())
            {
                engine.HasActiveJobs.Should().BeTrue("Stop must ask: a generation is getting under way");
                await engine.EnsureRunningAsync(NotInstalledRoot, CancellationToken.None);
            }

            observer.WaitForExit(10_000).Should().BeTrue("the preparing generation carries the restart out");
            engine.HasActiveJobs.Should().BeFalse();
        }
        finally { KillQuietly(running); }
    }

    [Fact]
    public async Task AStartAfterACrash_ReleasesTheDeadEngineFirst()
    {
        var logger = new Mock<IUnifiedLogger>();
        await using var engine = new ManagedComfyUiEngine(logger.Object);
        var exited = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 1") { UseShellExecute = false, CreateNoWindow = true })!;
        await exited.WaitForExitAsync();
        PretendRunning(engine, exited, "http://127.0.0.1:51234");
        engine.ReportRunning(true);   // crashed, and its release has not run yet

        await engine.EnsureRunningAsync(NotInstalledRoot, CancellationToken.None);

        engine.IsRunning.Should().BeFalse("a dead engine must not read as running while the next start runs");
        engine.BaseUrl.Should().BeNull();
        logger.Verify(l => l.Info(It.IsAny<LogCategory>(), It.IsAny<string>(),
            It.Is<string>(m => m.StartsWith("The engine process exited")), It.IsAny<string?>()), Times.Once,
            "the exit handler may come too late to see it: this release reports the crash");
    }

    // An exit right after a start became ready is reported once: by the exit handler (the start has
    // already ended its "starting" and reported running, so the exit is no longer the start's).
    [Fact]
    public async Task AnExitRightAfterAStartBecameReady_IsLoggedOnce()
    {
        var logger = new Mock<IUnifiedLogger>();
        await using var engine = new ManagedComfyUiEngine(logger.Object);
        var running = StartStandInEngine();
        try
        {
            engine.ReportStarting(true);
            PretendRunning(engine, running, null!);
            engine.WatchProcess(running);
            var handled = ExitHandlersRan(running);
            (await engine.CompleteReadyAsync(running, "http://127.0.0.1:51234")).IsRunning.Should().BeTrue();

            running.Kill(entireProcessTree: true);
            await handled;
            for (var i = 0; i < 50 && engine.BaseUrl is not null; i++) await Task.Delay(100);

            logger.Verify(l => l.Info(It.IsAny<LogCategory>(), It.IsAny<string>(),
                It.Is<string>(m => m.StartsWith("The engine process exited")), It.IsAny<string?>()), Times.Once);
            engine.IsRunning.Should().BeFalse();
        }
        finally
        {
            KillQuietly(running);
        }
    }

    [Fact]
    public async Task StoppingAnEngineThatNeverRan_ReportsNothing()
    {
        var engine = new ManagedComfyUiEngine(unifiedLogger: null);
        var seen = new List<bool>();
        engine.StateChanged += () => seen.Add(engine.IsRunning);

        await engine.StopAsync();

        seen.Should().BeEmpty();
        engine.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void HasActiveJobs_FollowsTheJobTokens()
    {
        var engine = new ManagedComfyUiEngine(unifiedLogger: null);
        engine.HasActiveJobs.Should().BeFalse();

        var job = engine.BeginJob();
        engine.HasActiveJobs.Should().BeTrue();

        job.Dispose();
        engine.HasActiveJobs.Should().BeFalse();
    }

    [Fact]
    public void BuildArguments_BindsLoopbackOnlyAndDisablesTheBrowser()
    {
        var args = ManagedComfyUiEngine.BuildArguments(@"C:\Engine\ComfyUI\main.py", 51234);

        args.Should().Contain("--listen 127.0.0.1");
        args.Should().Contain("--port 51234");
        args.Should().Contain("--disable-auto-launch");
        args.Should().Contain("\"C:\\Engine\\ComfyUI\\main.py\"",
            "the script path must be quoted so folders with spaces work");
    }

    // #607 smoke: ComfyUI's main.py disables Hugging Face telemetry, and with that `kernels` 0.17.x
    // builds an empty user-agent part; huggingface_hub turns it into a header ending in "; ", which
    // httpx rejects, so the FP8 kernel's publisher trust check fails and Qwen3-VL (Outpaint Vision)
    // cannot run. An origin is appended after that part, so the header no longer ends in whitespace.
    [Fact]
    public void EngineEnvironment_SetsAUserAgentOrigin_WithoutWhitespace()
    {
        ManagedComfyUiEngine.EngineEnvironment.Should().ContainKey("HF_HUB_USER_AGENT_ORIGIN");
        var origin = ManagedComfyUiEngine.EngineEnvironment["HF_HUB_USER_AGENT_ORIGIN"];
        origin.Should().NotBeNullOrWhiteSpace();
        origin.Should().NotContainAny(" ", ";", "	");
    }

    [Fact]
    public void ResolveVenvPython_FindsTheEngineVenvInterpreter()
    {
        var root = Path.Combine(Path.GetTempPath(), "dn-engine-" + Guid.NewGuid());
        var scripts = Path.Combine(root, "venv", "Scripts");
        Directory.CreateDirectory(scripts);
        try
        {
            ManagedComfyUiEngine.ResolveVenvPython(root).Should().BeNull("no interpreter exists yet");

            File.WriteAllText(Path.Combine(scripts, "python.exe"), "");
            ManagedComfyUiEngine.ResolveVenvPython(root).Should().Be(Path.Combine(scripts, "python.exe"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureRunning_FailsClearlyWhenTheEngineIsNotInstalled()
    {
        await using var engine = new ManagedComfyUiEngine(unifiedLogger: null);

        var result = await engine.EnsureRunningAsync(
            Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid()),
            CancellationToken.None);

        result.IsRunning.Should().BeFalse();
        result.BaseUrl.Should().BeNull();
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task StopAsync_IsSafeToCallRepeatedlyWhenNothingIsRunning()
    {
        await using var engine = new ManagedComfyUiEngine(unifiedLogger: null);

        await engine.StopAsync();
        await engine.StopAsync();
    }

    [Fact]
    public async Task EnsureRunningAndStopAsync_ShareTheStartLockWithoutDeadlocking()
    {
        // Regression for the review finding: StopAsync used to touch _process without taking
        // _startLock at all, so a stop landing while a start was in flight (anywhere in the up-to-
        // ~120s readiness poll) could dispose the process/HttpClient out from under it. Now both
        // serialize on the same lock. This doesn't exercise the readiness poll itself (that needs
        // a real spawned process, which unit tests here deliberately avoid) — it proves the two
        // entry points can run concurrently, contending for the same lock, without deadlocking.
        await using var engine = new ManagedComfyUiEngine(unifiedLogger: null);
        var notInstalledRoot = Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid());

        var ensureTask = engine.EnsureRunningAsync(notInstalledRoot, CancellationToken.None);
        var stopTask = engine.StopAsync();
        var both = Task.WhenAll(ensureTask, stopTask);

        var completed = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.Should().BeSameAs(both, "concurrent start/stop must not deadlock");

        var result = await ensureTask;
        result.IsRunning.Should().BeFalse("the install root doesn't exist");
    }

    [Fact]
    public async Task EnsureRunningAsync_DoesNotThrow_WhenTheProcessFieldIsInAnUnusableState()
    {
        // Regression for the review finding: the fast path used to read _process.HasExited
        // outside both the lock and any try/catch. A concurrent StopAsync disposing the Process
        // in that window turns that read into an uncaught InvalidOperationException. A Process
        // that was never Process.Start()-ed throws the exact same exception from .HasExited,
        // which lets this test reproduce the crash without needing a real spawned engine.
        await using var engine = new ManagedComfyUiEngine(unifiedLogger: null);

        using var unusableProcess = new Process();
        var processField = typeof(ManagedComfyUiEngine)
            .GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!;
        processField.SetValue(engine, unusableProcess);

        var act = async () => await engine.EnsureRunningAsync(
            Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid()),
            CancellationToken.None);

        // Must fail gracefully (a false EngineStartResult), not throw past the caller.
        await act.Should().NotThrowAsync();
    }

    // #606 code review 2 (G1). A Features install that adds node packs no longer stops the engine
    // (a job may be running on it); it asks for a restart on the next use. These hand the engine a
    // stand-in "running engine" (a long-lived cmd.exe) through the same private fields a real start
    // sets, and an install root without main.py, so the fresh start after a restart fails cleanly
    // instead of spawning Python.

    private static readonly string NotInstalledRoot =
        Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid());

    private static Process StartStandInEngine() =>
        Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 120 127.0.0.1 > nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

    /// <summary>Makes the engine believe <paramref name="process"/> is its running engine.</summary>
    private static void PretendRunning(ManagedComfyUiEngine engine, Process process, string baseUrl)
    {
        typeof(ManagedComfyUiEngine).GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, process);
        typeof(ManagedComfyUiEngine).GetField("_baseUrl", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, baseUrl);
    }

    // Completes once every Exited handler subscribed before it (the engine's) has run: handlers run
    // in subscription order, so a check made after this sees what the engine's handler did.
    private static Task ExitHandlersRan(Process process)
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => ran.TrySetResult();
        return ran.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    // #621 review round 3: the Engine is not started from a folder an update is changing, whoever asks.
    [Fact]
    public async Task WhileTheEngineIsUpdated_NoStartRuns()
    {
        await using var engine = new ManagedComfyUiEngine(unifiedLogger: null);

        EngineStartResult result;
        using (engine.TryBeginMaintenance()!)
            result = await engine.EnsureRunningAsync(NotInstalledRoot, CancellationToken.None);

        result.IsRunning.Should().BeFalse();
        result.FailureReason.Should().Contain("being updated");
    }

    [Fact]
    public async Task NoMaintenanceHold_UnderARunningOrStartingEngine()
    {
        await using var engine = new ManagedComfyUiEngine(unifiedLogger: null);

        // A start in progress holds the start lock (up to two minutes, BaseUrl still null).
        var startLock = (SemaphoreSlim)typeof(ManagedComfyUiEngine)
            .GetField("_startLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
        await startLock.WaitAsync();
        try { engine.TryBeginMaintenance().Should().BeNull("a start is booting from that folder"); }
        finally { startLock.Release(); }

        var running = StartStandInEngine();
        try
        {
            PretendRunning(engine, running, "http://127.0.0.1:51234");
            engine.TryBeginMaintenance().Should().BeNull("the engine runs from that folder");
        }
        finally { KillQuietly(running); }
    }

    // Finding 6: a start that ends is never published as running and starting at once.
    [Fact]
    public async Task AReadyStart_IsNeverRunningAndStartingAtOnce()
    {
        await using var engine = new ManagedComfyUiEngine(unifiedLogger: null);
        var states = new List<(bool Running, bool Starting)>();
        engine.StateChanged += () => states.Add((engine.IsRunning, engine.IsStarting));
        var running = StartStandInEngine();
        try
        {
            engine.ReportStarting(true);
            PretendRunning(engine, running, null!);

            var result = await engine.CompleteReadyAsync(running, "http://127.0.0.1:51234");

            result.IsRunning.Should().BeTrue();
            states.Should().NotContain((true, true), "the tile would show Starting... with Stop, Restart and Web UI");
            engine.IsStarting.Should().BeFalse();
        }
        finally { KillQuietly(running); }
    }

    // #621 review round 3: the console's Stop decides "idle" under the start lock, so a generation
    // that got under way after the console looked is not killed without asking.
    [Fact]
    public async Task StopIfIdle_LeavesAnEngineWithAGenerationUnderWayRunning()
    {
        await using var engine = new ManagedComfyUiEngine(unifiedLogger: null);
        var running = StartStandInEngine();
        using var observer = Process.GetProcessById(running.Id);
        try
        {
            PretendRunning(engine, running, "http://127.0.0.1:51234");
            engine.ReportRunning(true);

            using (engine.BeginPreparing())
                (await engine.StopIfIdleAsync()).Should().BeFalse();
            observer.HasExited.Should().BeFalse();

            (await engine.StopIfIdleAsync()).Should().BeTrue();
            observer.WaitForExit(10_000).Should().BeTrue();
        }
        finally { KillQuietly(running); }
    }

    private static void KillQuietly(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already gone */ }
        process.Dispose();
    }

    // #610 review: an engine that dies after it was ready (a crash, a kill from outside) is no longer
    // running: the console tile must show Start again, BaseUrl must be null, and its handles released.
    [Fact]
    public async Task AnEngineThatExitsAfterBeingReady_ReportsNotRunning_AndLetsGoOfIt()
    {
        await using var engine = new ManagedComfyUiEngine(unifiedLogger: null);
        var seen = new List<bool>();
        engine.StateChanged += () => seen.Add(engine.IsRunning);
        var running = StartStandInEngine();
        try
        {
            PretendRunning(engine, running, "http://127.0.0.1:51234");
            engine.WatchProcess(running);
            engine.ReportRunning(true);

            running.Kill(entireProcessTree: true);
            for (var i = 0; i < 50 && (engine.IsRunning || engine.BaseUrl is not null); i++) await Task.Delay(100);

            engine.IsRunning.Should().BeFalse();
            engine.BaseUrl.Should().BeNull("the interface promises null when it is not running");
            seen.Should().Equal(true, false);
        }
        finally { KillQuietly(running); }
    }

    [Fact]
    public async Task AnExitDuringStartup_IsLeftToTheStart_NotLoggedTwice()
    {
        var logger = new Mock<IUnifiedLogger>();
        await using var engine = new ManagedComfyUiEngine(logger.Object);
        var starting = StartStandInEngine();
        try
        {
            PretendRunning(engine, starting, null!);   // a start in progress: not ready, not reported
            engine.WatchProcess(starting);
            var handled = ExitHandlersRan(starting);

            starting.Kill(entireProcessTree: true);
            await handled;

            logger.Verify(l => l.Info(It.IsAny<LogCategory>(), It.IsAny<string>(),
                It.Is<string>(m => m.StartsWith("The engine process exited")), It.IsAny<string?>()), Times.Never,
                "the readiness poll reports a startup exit itself");
        }
        finally { KillQuietly(starting); }
    }

    [Fact]
    public async Task ReadyButAlreadyGone_IsAFailedStart_NotARunningEngine()
    {
        await using var engine = new ManagedComfyUiEngine(unifiedLogger: null);
        var seen = new List<bool>();
        engine.StateChanged += () => seen.Add(engine.IsRunning);
        var exited = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 3") { UseShellExecute = false, CreateNoWindow = true })!;
        await exited.WaitForExitAsync();
        PretendRunning(engine, exited, null!);

        var result = await engine.CompleteReadyAsync(exited, "http://127.0.0.1:51234");

        result.IsRunning.Should().BeFalse("it answered the readiness check and died before it was reported");
        engine.IsRunning.Should().BeFalse();
        engine.BaseUrl.Should().BeNull();
        seen.Should().NotContain(true);
    }

    [Fact]
    public async Task RequestRestart_DoesNotStopTheRunningEngine()
    {
        await using var engine = new ManagedComfyUiEngine(unifiedLogger: null);
        var running = StartStandInEngine();
        using var observer = Process.GetProcessById(running.Id);
        try
        {
            PretendRunning(engine, running, "http://127.0.0.1:51234");

            engine.RequestRestart();

            observer.HasExited.Should().BeFalse("a job may be running on the engine right now");
            engine.BaseUrl.Should().Be("http://127.0.0.1:51234");
        }
        finally
        {
            KillQuietly(observer);
        }
    }

    [Fact]
    public async Task EnsureRunningAsync_AfterRequestRestart_StopsTheRunningEngineOnce_AndStartsFresh()
    {
        await using var engine = new ManagedComfyUiEngine(unifiedLogger: null);
        var first = StartStandInEngine();
        using var firstObserver = Process.GetProcessById(first.Id);
        var second = StartStandInEngine();
        using var secondObserver = Process.GetProcessById(second.Id);
        try
        {
            PretendRunning(engine, first, "http://127.0.0.1:51234");
            engine.RequestRestart();

            var restarted = await engine.EnsureRunningAsync(NotInstalledRoot, CancellationToken.None);

            firstObserver.WaitForExit(TimeSpan.FromSeconds(10)).Should()
                .BeTrue("the old process is stopped so the fresh one loads the new node packs");
            restarted.IsRunning.Should().BeFalse("the fresh start ran (and found no install here)");
            restarted.FailureReason.Should().Contain("main.py");

            // The request is used up: the next running engine is reused, not restarted again.
            PretendRunning(engine, second, "http://127.0.0.1:51235");
            var reused = await engine.EnsureRunningAsync(NotInstalledRoot, CancellationToken.None);

            reused.Should().Be(new EngineStartResult(true, "http://127.0.0.1:51235", null));
            secondObserver.HasExited.Should().BeFalse();
        }
        finally
        {
            KillQuietly(firstObserver);
            KillQuietly(secondObserver);
        }
    }

    [Fact]
    public async Task RequestRestart_WhileNotRunning_IsUsedUpByTheNextColdStart()
    {
        await using var engine = new ManagedComfyUiEngine(unifiedLogger: null);
        engine.RequestRestart();

        var cold = await engine.EnsureRunningAsync(NotInstalledRoot, CancellationToken.None);
        cold.IsRunning.Should().BeFalse();

        var running = StartStandInEngine();
        using var observer = Process.GetProcessById(running.Id);
        try
        {
            PretendRunning(engine, running, "http://127.0.0.1:51234");

            var reused = await engine.EnsureRunningAsync(NotInstalledRoot, CancellationToken.None);

            reused.Should().Be(new EngineStartResult(true, "http://127.0.0.1:51234", null),
                "a cold start already loads every node pack, so the request was cleared by it");
            observer.HasExited.Should().BeFalse();
        }
        finally
        {
            KillQuietly(observer);
        }
    }

    // #606 code review 3 (H1). Any caller's EnsureRunningAsync (the Canvas checks availability often)
    // used to carry out a pending restart, even with another consumer's job running on the engine.

    [Theory]
    [InlineData(true, true, 0, true)]
    [InlineData(true, true, 1, false)] // a job is running: postponed
    [InlineData(true, true, 2, false)]
    [InlineData(true, false, 0, false)] // nothing pending
    [InlineData(false, true, 0, false)] // not running: the cold start loads everything anyway
    public void ShouldRestartNow_OnlyWhenRunning_Pending_AndIdle(bool isRunning, bool pending, int jobs, bool expected) =>
        ManagedComfyUiEngine.ShouldRestartNow(isRunning, pending, jobs).Should().Be(expected);

    [Fact]
    public async Task EnsureRunningAsync_WhileAJobRuns_PostponesThePendingRestart_UntilTheJobEnds()
    {
        var unified = new Mock<IUnifiedLogger>();
        await using var engine = new ManagedComfyUiEngine(unified.Object);
        var running = StartStandInEngine();
        using var observer = Process.GetProcessById(running.Id);
        try
        {
            PretendRunning(engine, running, "http://127.0.0.1:51234");
            var job = engine.BeginJob();
            engine.RequestRestart();

            // Two availability checks while the job runs: the engine is left alone both times.
            (await engine.EnsureRunningAsync(NotInstalledRoot, CancellationToken.None))
                .Should().Be(new EngineStartResult(true, "http://127.0.0.1:51234", null));
            (await engine.EnsureRunningAsync(NotInstalledRoot, CancellationToken.None))
                .Should().Be(new EngineStartResult(true, "http://127.0.0.1:51234", null));

            observer.HasExited.Should().BeFalse("a job is still running on the engine");
            engine.IsRestartPending.Should().BeTrue("the restart is postponed, not dropped");
            unified.Verify(u => u.Info(It.IsAny<LogCategory>(), It.IsAny<string>(),
                "Engine restart postponed: a job is still running.", It.IsAny<string?>()), Times.Once,
                "logged once per deferral, not on every check");

            job.Dispose();
            job.Dispose(); // idempotent: must not end a second job

            var restarted = await engine.EnsureRunningAsync(NotInstalledRoot, CancellationToken.None);

            observer.WaitForExit(TimeSpan.FromSeconds(10)).Should().BeTrue("the engine is idle now");
            restarted.IsRunning.Should().BeFalse("the fresh start ran (and found no install here)");
            engine.IsRestartPending.Should().BeFalse();
        }
        finally
        {
            KillQuietly(observer);
        }
    }

    [Fact]
    public async Task BeginJob_DisposingOneTokenTwice_DoesNotEndAnotherJob()
    {
        await using var engine = new ManagedComfyUiEngine(unifiedLogger: null);
        var running = StartStandInEngine();
        using var observer = Process.GetProcessById(running.Id);
        try
        {
            PretendRunning(engine, running, "http://127.0.0.1:51234");
            var first = engine.BeginJob();
            using var second = engine.BeginJob();
            engine.RequestRestart();

            first.Dispose();
            first.Dispose();
            await engine.EnsureRunningAsync(NotInstalledRoot, CancellationToken.None);

            observer.HasExited.Should().BeFalse("the second job is still running");
            engine.IsRestartPending.Should().BeTrue();
        }
        finally
        {
            KillQuietly(observer);
        }
    }
}
