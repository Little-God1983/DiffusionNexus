using System.Diagnostics;
using System.Runtime.Versioning;
using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// Covers the Windows Job Object wrapper that <c>ManagedComfyUiEngine</c> now relies on (review
/// item: the engine process was not job-object-protected and could outlive an app crash) and that
/// <c>PackageProcessManager</c> already used for every installer process it launches.
///
/// Exercises the mechanism with a short-lived, harmless <c>cmd.exe</c>/<c>ping</c> process rather
/// than a real ComfyUI process — actually spawning/killing the engine itself is a manual smoke,
/// not something this suite does.
/// </summary>
[SupportedOSPlatform("windows")] // ChildProcessJobObject itself is Windows-only; matches its own attribute.
public class ChildProcessJobObjectTests
{
    private static Process StartPingLoop(int pings)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c ping -n {pings} 127.0.0.1 >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the test helper process.");
    }

    [Fact]
    public async Task AssignProcess_ThenTerminate_KillsTheProcess()
    {
        using var process = StartPingLoop(pings: 10);
        using var job = new ChildProcessJobObject(name: null);

        job.AssignProcess(process).Should().BeTrue();
        process.HasExited.Should().BeFalse("the ping loop should still be running");

        job.Terminate().Should().BeTrue();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cts.Token);
        process.HasExited.Should().BeTrue("Terminate() must kill everything assigned to the job");
    }

    [Fact]
    public async Task Dispose_WithoutExplicitTerminate_StillKillsTheAssignedProcess()
    {
        // This is the actual guarantee ManagedComfyUiEngine now depends on: even when the app
        // exits without an orderly StopAsync (crash, Environment.FailFast, Task Manager kill),
        // the OS kernel kills everything still assigned to the job the moment its handle closes
        // (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) — no explicit Terminate() call required.
        using var process = StartPingLoop(pings: 15);
        var job = new ChildProcessJobObject(name: null);

        job.AssignProcess(process).Should().BeTrue();

        job.Dispose();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cts.Token);
        process.HasExited.Should().BeTrue(
            "closing the job handle must kill the child even without Terminate()");
    }

    [Fact]
    public void AssignProcess_NullProcess_ThrowsArgumentNullException()
    {
        using var job = new ChildProcessJobObject(name: null);

        var act = () => job.AssignProcess(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
