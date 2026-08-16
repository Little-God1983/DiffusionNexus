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
}
