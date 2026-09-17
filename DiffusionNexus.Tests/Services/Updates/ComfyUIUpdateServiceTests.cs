using DiffusionNexus.Domain.Services;
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
        runner.CountByArguments(ComfyUIUpdateService.FetchArguments).Should().Be(1, "the first check should have started exactly one fetch");

        // Fan out more callers for the SAME path while the first is still parked.
        var rest = Enumerable.Range(0, 15)
            .Select(_ => service.CheckForUpdatesAsync(_root))
            .ToArray();

        // Release and let them all finish.
        gate.SetResult();
        await Task.WhenAll(new[] { first }.Concat(rest));

        runner.CountByArguments(ComfyUIUpdateService.FetchArguments).Should()
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

        runner.CountByArguments(ComfyUIUpdateService.FetchArguments).Should().Be(2);
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

    // ── ComfyUI-Manager parity: the stable channel targets the newest release tag ──

    private const string HeadHash = "aaaaaaa";
    private const string TagHash = "bbbbbbb";

    /// <summary>A checkout with ComfyUI-Manager installed and never configured = stable.</summary>
    private void InstallManager()
        => Directory.CreateDirectory(Path.Combine(_root, "custom_nodes", "ComfyUI-Manager"));

    private void WriteManagerConfig(string policy, bool legacyLocation = false)
    {
        var dir = legacyLocation
            ? Path.Combine(_root, "user", "default", "ComfyUI-Manager")
            : Path.Combine(_root, "user", "__manager");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.ini"), $"[default]\nupdate_policy = {policy}\n");
    }

    /// <summary>
    /// A repo sitting on <see cref="HeadHash"/> whose newest release v0.36.0 is
    /// <see cref="TagHash"/>. <paramref name="newerMigrations"/> is what
    /// <c>git diff --diff-filter=A</c> reports for alembic_db/versions.
    /// </summary>
    private static RecordingProcessRunner StableRepoRunner(
        string newerMigrations = "", bool headIsOlderThanTag = true, bool checkoutSucceeds = true)
        => new((_, args, _) => args switch
        {
            "tag -l v*" => new ProcessResult(0, "v0.9.0\nv0.36.0\nv0.35.2\nv0.37.0-rc1\nlatest", string.Empty),
            "rev-parse --short HEAD" or "rev-parse HEAD" => new ProcessResult(0, HeadHash, string.Empty),
            "rev-parse --short v0.36.0^{commit}" or "rev-parse v0.36.0^{commit}" => new ProcessResult(0, TagHash, string.Empty),
            "merge-base --is-ancestor HEAD v0.36.0" => new ProcessResult(headIsOlderThanTag ? 0 : 1, string.Empty, string.Empty),
            "rev-parse --abbrev-ref HEAD" => new ProcessResult(0, "master", string.Empty),
            _ when args.StartsWith("diff --name-only", StringComparison.Ordinal) => new ProcessResult(0, newerMigrations, string.Empty),
            _ when args.EndsWith("checkout v0.36.0", StringComparison.Ordinal) => new ProcessResult(checkoutSucceeds ? 0 : 1, string.Empty, "checkout failed"),
            _ => new ProcessResult(0, string.Empty, string.Empty),
        });

    [Fact]
    public async Task WhenManagerIsOnStableThenCheckComparesAgainstNewestReleaseTag()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        InstallManager();
        var runner = StableRepoRunner();

        var result = await new ComfyUIUpdateService(runner).CheckForUpdatesAsync(_root);

        result.IsUpdateAvailable.Should().BeTrue();
        result.RemoteHash.Should().Be(TagHash);
        result.Summary.Should().Be("Release v0.36.0 available");
        runner.Invocations.Should().NotContain(i => i.Arguments.StartsWith("rev-list"),
            "the branch tip is irrelevant on the stable channel");
    }

    [Fact]
    public async Task WhenInstalledBuildIsNewerThanReleaseThenCheckStillReportsTheSwitchTheManagerWouldMake()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        InstallManager();
        var runner = StableRepoRunner(headIsOlderThanTag: false);

        var result = await new ComfyUIUpdateService(runner).CheckForUpdatesAsync(_root);

        result.IsUpdateAvailable.Should().BeTrue();
        result.Summary.Should().Contain("newer than release v0.36.0");
    }

    [Fact]
    public async Task WhenManagerIsOnStableThenUpdateChecksOutTheTagAndNeverPulls()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        WriteManagerConfig("stable-comfyui");
        var runner = StableRepoRunner();

        var result = await new ComfyUIUpdateService(runner).UpdateAsync(_root);

        result.Success.Should().BeTrue();
        runner.Invocations.Should().Contain(i => i.Arguments == "-c advice.detachedHead=false checkout v0.36.0");
        runner.Invocations.Should().NotContain(i => i.Arguments.StartsWith("pull"),
            "pulling master is what made this app and ComfyUI-Manager overwrite each other");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenManagerConfigSaysNightlyThenUpdatePullsTheBranch(bool legacyLocation)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        InstallManager();
        WriteManagerConfig("nightly-comfyui", legacyLocation);
        var runner = StableRepoRunner();

        var result = await new ComfyUIUpdateService(runner).UpdateAsync(_root);

        result.Success.Should().BeTrue();
        runner.Invocations.Should().Contain(i => i.Arguments == "pull --ff-only origin master");
        runner.Invocations.Should().NotContain(i => i.Arguments.Contains("checkout v0.36.0"));
    }

    [Fact]
    public async Task WhenOnlyAheadOfTheReleaseThenDatabaseIsLeftAlone()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        InstallManager();
        var database = CreateDatabase();
        var runner = StableRepoRunner(newerMigrations: "");

        await new ComfyUIUpdateService(runner).UpdateAsync(_root);

        File.Exists(database).Should().BeTrue("identical migrations mean the release can open the database");
        Directory.GetFiles(Path.GetDirectoryName(database)!, "*.bak").Should().BeEmpty();
    }

    [Fact]
    public async Task WhenInstalledCodeHasNewerMigrationsThenDatabaseIsMovedAside()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        InstallManager();
        var database = CreateDatabase();
        var runner = StableRepoRunner(newerMigrations: "alembic_db/versions/0008_future.py");

        var result = await new ComfyUIUpdateService(runner).UpdateAsync(_root);

        result.Success.Should().BeTrue();
        File.Exists(database).Should().BeFalse();
        Directory.GetFiles(Path.GetDirectoryName(database)!, "comfyui.db.*.bak").Should().ContainSingle();
    }

    [Fact]
    public async Task WhenSwitchFailsThenMovedDatabaseIsPutBack()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        InstallManager();
        var database = CreateDatabase();
        var runner = StableRepoRunner(newerMigrations: "alembic_db/versions/0008_future.py", checkoutSucceeds: false);

        var result = await new ComfyUIUpdateService(runner).UpdateAsync(_root);

        result.Success.Should().BeFalse();
        File.Exists(database).Should().BeTrue("the installed code still needs its database");
        Directory.GetFiles(Path.GetDirectoryName(database)!, "*.bak").Should().BeEmpty();
    }

    [Fact]
    public async Task WhenDatabaseIsInUseThenUpdateStopsBeforeTouchingTheCheckout()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        InstallManager();
        var database = CreateDatabase();
        var runner = StableRepoRunner(newerMigrations: "alembic_db/versions/0008_future.py");

        UpdateResult result;
        using (new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await new ComfyUIUpdateService(runner).UpdateAsync(_root);
        }

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("ComfyUI is still running");
        runner.Invocations.Should().NotContain(i => i.Arguments.Contains("checkout"));
    }

    [Fact]
    public async Task WhenTrackedFilesWereEditedThenTheyAreStashedBeforeTheSwitch()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        InstallManager();
        var inner = StableRepoRunner();
        var runner = new RecordingProcessRunner((file, args, dir) => args == "diff --quiet HEAD"
            ? new ProcessResult(1, string.Empty, string.Empty)
            : inner.RunAsync(file, args, dir).GetAwaiter().GetResult());

        await new ComfyUIUpdateService(runner).UpdateAsync(_root);

        var stash = runner.IndexOf(i => i.Arguments.Contains("stash push"));
        var checkout = runner.IndexOf(i => i.Arguments.EndsWith("checkout v0.36.0"));
        stash.Should().BeGreaterThanOrEqualTo(0);
        checkout.Should().BeGreaterThan(stash);
    }

    private string CreateDatabase()
    {
        var userDir = Path.Combine(_root, "user");
        Directory.CreateDirectory(userDir);
        var database = Path.Combine(userDir, "comfyui.db");
        File.WriteAllText(database, "db");
        return database;
    }

    // ── Review wave on #578 ──

    /// <summary>Wraps <see cref="StableRepoRunner"/> and overrides the answer for some commands.</summary>
    private static RecordingProcessRunner StableRepoRunnerWith(
        Func<string, ProcessResult?> overrides,
        string newerMigrations = "", bool headIsOlderThanTag = true, bool checkoutSucceeds = true)
    {
        var inner = StableRepoRunner(newerMigrations, headIsOlderThanTag, checkoutSucceeds);
        return new RecordingProcessRunner((file, args, dir) =>
            overrides(args) ?? inner.RunAsync(file, args, dir).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task WhenUpdateMovesForwardThenNoConfirmationIsRequired()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        InstallManager();

        var result = await new ComfyUIUpdateService(StableRepoRunner()).CheckForUpdatesAsync(_root);

        result.IsUpdateAvailable.Should().BeTrue();
        result.ConfirmationMessage.Should().BeNull();
    }

    [Fact]
    public async Task WhenUpdateSwitchesBackToAnOlderReleaseThenConfirmationSpellsOutTheDatabaseConsequence()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        InstallManager();
        CreateDatabase();
        var runner = StableRepoRunner(newerMigrations: "alembic_db/versions/0008_future.py", headIsOlderThanTag: false);

        var result = await new ComfyUIUpdateService(runner).CheckForUpdatesAsync(_root);

        result.ConfirmationMessage.Should().Contain("BACK to v0.36.0");
        result.ConfirmationMessage.Should().Contain("user/comfyui.db");
    }

    [Fact]
    public async Task WhenSwitchingBackButMigrationsAreIdenticalThenConfirmationDoesNotThreatenTheDatabase()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        InstallManager();
        CreateDatabase();
        var runner = StableRepoRunner(newerMigrations: "", headIsOlderThanTag: false);

        var result = await new ComfyUIUpdateService(runner).CheckForUpdatesAsync(_root);

        result.ConfirmationMessage.Should().Contain("BACK to v0.36.0");
        result.ConfirmationMessage.Should().NotContain("comfyui.db");
    }

    [Fact]
    public async Task WhenMigrationComparisonFailsThenTheSwitchIsNotAttempted()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        InstallManager();
        var database = CreateDatabase();
        var runner = StableRepoRunnerWith(args => args.StartsWith("diff --name-only", StringComparison.Ordinal)
            ? new ProcessResult(128, string.Empty, "fatal: bad revision")
            : null);

        var result = await new ComfyUIUpdateService(runner).UpdateAsync(_root);

        result.Success.Should().BeFalse("a failed comparison must never read as 'no newer migrations'");
        File.Exists(database).Should().BeTrue();
        runner.Invocations.Should().NotContain(i => i.Arguments.Contains("checkout"));
    }

    [Fact]
    public async Task WhenCheckoutFailsAfterStashingThenTheFailureMessageSaysWhereTheEditsAre()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        InstallManager();
        var runner = StableRepoRunnerWith(
            args => args == "diff --quiet HEAD" ? new ProcessResult(1, string.Empty, string.Empty) : null,
            checkoutSucceeds: false);

        var result = await new ComfyUIUpdateService(runner).UpdateAsync(_root);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("git stash pop");
        result.Message.Should().Contain("checkout failed", "the original git error must stay visible");
    }

    [Fact]
    public async Task WhenABackupWithTheSameTimestampExistsThenItIsNeverOverwrittenOrReportedAsInUse()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        InstallManager();
        var database = CreateDatabase();
        var userDir = Path.GetDirectoryName(database)!;
        // Occupy every name this second and the next could produce.
        var now = DateTime.Now;
        foreach (var stamp in new[] { now, now.AddSeconds(1), now.AddSeconds(2) })
            File.WriteAllText($"{database}.{stamp:yyyyMMdd_HHmmss}.bak", "earlier backup");
        var runner = StableRepoRunner(newerMigrations: "alembic_db/versions/0008_future.py");

        var result = await new ComfyUIUpdateService(runner).UpdateAsync(_root);

        result.Success.Should().BeTrue(result.Message);
        Directory.GetFiles(userDir, "*.bak").Select(File.ReadAllText)
            .Should().Contain("db").And.Contain("earlier backup");
    }

    [Fact]
    public async Task WhenFetchingThenOnlyOriginIsAsked()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var runner = StableRepoRunner();

        await new ComfyUIUpdateService(runner).CheckForUpdatesAsync(_root);

        runner.Invocations.Should().Contain(i => i.Arguments == "fetch origin --tags --force");
        runner.Invocations.Should().NotContain(i => i.Arguments.Contains("--all"));
    }
}
