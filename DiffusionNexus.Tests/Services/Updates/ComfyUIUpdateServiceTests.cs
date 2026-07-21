using DiffusionNexus.Service.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Services.Updates;

/// <summary>
/// Behavioural tests for <see cref="ComfyUIUpdateService"/> driven through the injected
/// <see cref="IProcessRunner"/> seam (issue #439). Covers the two behaviours that had no
/// coverage and are the whole point of the issue: the in-flight-collapse race and the
/// backend → pip → legacy-frontend update ordering.
/// </summary>
public class ComfyUIUpdateServiceTests : IDisposable
{
    private readonly string _root;

    public ComfyUIUpdateServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"comfy_update_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── THE test that matters most: concurrent same-key checks collapse to ONE launch ──

    [Fact]
    public async Task WhenManyConcurrentChecksForSamePathThenGitFetchLaunchesExactlyOnce()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        // Gate the first "git fetch --all" so the first check is parked in-flight while
        // every other caller for the same key arrives and joins the same task.
        var gate = new TaskCompletionSource();
        var runner = new RecordingProcessRunner(
            responder: (_, args, _) => args == "rev-parse --abbrev-ref HEAD"
                ? new ProcessResult(0, "main", string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty),
            fetchGate: gate);

        var service = new ComfyUIUpdateService(runner);

        // The first call runs synchronously through the Lazy factory up to the gated
        // fetch await, so by the time it returns the fetch has already been launched and
        // the in-flight entry is published.
        var first = service.CheckForUpdatesAsync(_root);
        runner.CountByArguments("fetch --all").Should().Be(1, "the first check should have started exactly one fetch");

        // Fan out more callers for the SAME path while the first is still parked.
        var rest = Enumerable.Range(0, 15)
            .Select(_ => service.CheckForUpdatesAsync(_root))
            .ToArray();

        // Release and let them all finish.
        gate.SetResult();
        await Task.WhenAll(new[] { first }.Concat(rest));

        runner.CountByArguments("fetch --all").Should()
            .Be(1, "16 concurrent same-key checks must collapse to a single git fetch");
    }

    [Fact]
    public async Task WhenChecksForSamePathRunSequentiallyThenEachLaunchesItsOwnFetch()
    {
        // No gate: each call completes and removes itself from the in-flight map before
        // the next starts, so collapse does NOT apply — proves the map is released.
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var runner = new RecordingProcessRunner(
            responder: (_, args, _) => args == "rev-parse --abbrev-ref HEAD"
                ? new ProcessResult(0, "main", string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty));

        var service = new ComfyUIUpdateService(runner);

        await service.CheckForUpdatesAsync(_root);
        await service.CheckForUpdatesAsync(_root);

        runner.CountByArguments("fetch --all").Should().Be(2);
    }

    // ── Update ordering: backend git → pip → legacy frontend git ──

    [Fact]
    public async Task WhenUpdatingThenRunsBackendGitThenPipThenLegacyFrontendGitInOrder()
    {
        // Backend repo at root.
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        // requirements.txt + a portable python so the pip step actually runs.
        File.WriteAllText(Path.Combine(_root, "requirements.txt"), "comfyui-frontend-package");
        var pythonDir = Path.Combine(_root, "python_embeded");
        Directory.CreateDirectory(pythonDir);
        var pythonExe = Path.Combine(pythonDir, "python.exe");
        File.WriteAllText(pythonExe, string.Empty);
        // Legacy git-based frontend at web/.
        var webDir = Path.Combine(_root, "web");
        Directory.CreateDirectory(Path.Combine(webDir, ".git"));

        var runner = new RecordingProcessRunner(
            responder: (_, args, _) => args == "rev-parse --abbrev-ref HEAD"
                ? new ProcessResult(0, "main", string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty));

        var service = new ComfyUIUpdateService(runner);

        var result = await service.UpdateAsync(_root);

        result.Success.Should().BeTrue();

        var backendPull = runner.IndexOf(i =>
            i.Arguments == "pull --ff-only origin main" && i.WorkingDirectory == _root);
        var pip = runner.IndexOf(i =>
            i.FileName == pythonExe && i.Arguments.Contains("-m pip install"));
        var frontendPull = runner.IndexOf(i =>
            i.Arguments == "pull --ff-only origin main" && i.WorkingDirectory == webDir);

        backendPull.Should().BeGreaterThanOrEqualTo(0, "backend must be pulled");
        pip.Should().BeGreaterThan(backendPull, "pip must run after the backend git pull");
        frontendPull.Should().BeGreaterThan(pip, "the legacy frontend git pull must run after pip");
    }

    [Fact]
    public async Task WhenNoGitRepositoryThenReportsFailureWithoutLaunchingAnything()
    {
        var runner = new RecordingProcessRunner();
        var service = new ComfyUIUpdateService(runner);

        var result = await service.CheckForUpdatesAsync(_root);

        result.IsUpdateAvailable.Should().BeFalse();
        result.Summary.Should().Be("No git repository found");
        runner.TotalInvocations.Should().Be(0);
    }
}
