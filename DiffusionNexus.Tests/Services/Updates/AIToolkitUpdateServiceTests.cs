using DiffusionNexus.Service.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Services.Updates;

/// <summary>
/// Behavioural tests for <see cref="AIToolkitUpdateService"/> through the injected
/// <see cref="IProcessRunner"/> seam (issue #439): the in-flight-collapse race, the
/// pull → package-reinstall ordering, and that the venv isolation profile is still
/// applied to the environment passed to each process.
/// </summary>
public class AIToolkitUpdateServiceTests : IDisposable
{
    private readonly string _root;

    public AIToolkitUpdateServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"aitk_update_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── In-flight collapse ──

    [Fact]
    public async Task WhenManyConcurrentChecksForSamePathThenGitFetchLaunchesExactlyOnce()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        var gate = new TaskCompletionSource();
        var runner = new RecordingProcessRunner(
            responder: (_, args, _) => args == "rev-parse --abbrev-ref HEAD"
                ? new ProcessResult(0, "main", string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty),
            fetchGate: gate);

        var service = new AIToolkitUpdateService(runner);

        var first = service.CheckForUpdatesAsync(_root);
        runner.CountByArguments("fetch --all").Should().Be(1);

        var rest = Enumerable.Range(0, 15)
            .Select(_ => service.CheckForUpdatesAsync(_root))
            .ToArray();

        gate.SetResult();
        await Task.WhenAll(new[] { first }.Concat(rest));

        runner.CountByArguments("fetch --all").Should()
            .Be(1, "16 concurrent same-key checks must collapse to a single git fetch");
    }

    // ── Update ordering: git pull → package reinstalls ──

    [Fact]
    public async Task WhenUpdatePullsChangesThenReinstallsPackagesAfterPull()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        // venv with pip-only python (no uv) so the pip reinstall path is exercised.
        var venvDir = Path.Combine(_root, "venv");
        Directory.CreateDirectory(Path.Combine(venvDir, "Scripts"));
        File.WriteAllText(Path.Combine(venvDir, "Scripts", "python.exe"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "requirements.txt"), "torch");

        var runner = new RecordingProcessRunner(responder: HeadChangesResponder());
        var service = new AIToolkitUpdateService(runner);

        var result = await service.UpdateAsync(_root);
        result.Success.Should().BeTrue();

        var pull = runner.IndexOf(i =>
            i.Arguments == "pull --ff-only origin main" && i.WorkingDirectory == _root);
        var uninstall = runner.IndexOf(i => i.Arguments.Contains("pip uninstall diffusers"));
        var hub = runner.IndexOf(i => i.Arguments.Contains("huggingface-hub"));
        var transformers = runner.IndexOf(i => i.Arguments.Contains("pip install") && i.Arguments.Contains("transformers"));

        pull.Should().BeGreaterThanOrEqualTo(0);
        uninstall.Should().BeGreaterThan(pull, "diffusers is uninstalled after the pull");
        hub.Should().BeGreaterThan(uninstall, "huggingface-hub is pinned after the uninstall");
        transformers.Should().BeGreaterThan(hub, "transformers is upgraded after huggingface-hub");
    }

    [Fact]
    public async Task WhenUpdateFindsNoChangesThenSkipsReinstall()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        // Same pre/post hash → no changes → reinstall must be skipped.
        var runner = new RecordingProcessRunner(
            responder: (_, args, _) => args == "rev-parse --short HEAD"
                ? new ProcessResult(0, "deadbee", string.Empty)
                : args == "rev-parse --abbrev-ref HEAD"
                    ? new ProcessResult(0, "main", string.Empty)
                    : new ProcessResult(0, string.Empty, string.Empty));

        var service = new AIToolkitUpdateService(runner);

        var result = await service.UpdateAsync(_root);

        result.Success.Should().BeTrue();
        result.Message.Should().Be("Already up to date");
        runner.IndexOf(i => i.Arguments.Contains("pip uninstall")).Should().Be(-1);
    }

    // ── Venv isolation profile survives the seam ──

    [Fact]
    public async Task WhenRunningThenGitCallsClearAmbientPythonEnvAndPipCallsActivateVenv()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var venvDir = Path.Combine(_root, "venv");
        Directory.CreateDirectory(Path.Combine(venvDir, "Scripts"));
        File.WriteAllText(Path.Combine(venvDir, "Scripts", "python.exe"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "requirements.txt"), "torch");

        var runner = new RecordingProcessRunner(responder: HeadChangesResponder());
        var service = new AIToolkitUpdateService(runner);

        await service.UpdateAsync(_root);

        // git fetch runs with no venv → ambient Python config cleared, VIRTUAL_ENV removed.
        var fetch = runner.Invocations.First(i => i.Arguments == "fetch --all");
        fetch.Environment.Should().NotBeNull();
        fetch.Environment!.Should().ContainKey("PYTHONUNBUFFERED").WhoseValue.Should().Be("1");
        fetch.Environment.Should().ContainKey("GIT_LFS_SKIP_SMUDGE").WhoseValue.Should().Be("1");
        // Isolation keys are present with a null value, meaning "remove from environment".
        fetch.Environment.Should().ContainKey("VIRTUAL_ENV").WhoseValue.Should().BeNull();
        fetch.Environment.Should().ContainKey("PYTHONPATH").WhoseValue.Should().BeNull();

        // pip runs with the venv "activated": VIRTUAL_ENV set, venv Scripts prepended to PATH.
        var pip = runner.Invocations.First(i => i.Arguments.Contains("pip uninstall diffusers"));
        pip.Environment.Should().NotBeNull();
        pip.Environment!.Should().ContainKey("VIRTUAL_ENV").WhoseValue.Should().Be(venvDir);
        pip.Environment.Should().ContainKey("PATH");
        pip.Environment["PATH"].Should().StartWith(Path.Combine(venvDir, "Scripts"));
    }

    /// <summary>
    /// Responder that returns two different short hashes for successive
    /// <c>rev-parse --short HEAD</c> calls so the service sees the pull as a real change.
    /// </summary>
    private static Func<string, string, string, ProcessResult> HeadChangesResponder()
    {
        var headCalls = 0;
        return (_, args, _) =>
        {
            if (args == "rev-parse --short HEAD")
                return new ProcessResult(0, Interlocked.Increment(ref headCalls) == 1 ? "aaaaaaa" : "bbbbbbb", string.Empty);
            if (args == "rev-parse --abbrev-ref HEAD")
                return new ProcessResult(0, "main", string.Empty);
            return new ProcessResult(0, string.Empty, string.Empty);
        };
    }
}
