using System.Diagnostics;
using System.Reflection;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

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

    private static void KillQuietly(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already gone */ }
        process.Dispose();
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
}
