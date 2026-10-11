using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Infrastructure.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Catalog;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Catalog;

public sealed partial class CatalogUpdateCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dn-catalog-coordinator-{Guid.NewGuid():N}");
    private readonly FakeCatalogUpdateService _service = new();
    private readonly Mock<ICatalog> _catalog = new();
    private readonly InstallActivity _installs = new();
    private readonly UnifiedLogger _log = new();
    private readonly FakePreferences _preferences = new();
    private readonly CatalogOptions _options;
    private string? _environment;

    public CatalogUpdateCoordinatorTests()
    {
        Directory.CreateDirectory(_dir);
        // SDK 2.3.0 shared folders, what the app runs with: one folder per channel under a shared root.
        _options = new CatalogOptions { SharedRoot = Path.Combine(_dir, "shared"), Channel = CatalogChannel.Stable };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private CatalogUpdateCoordinator Create() =>
        new(_service, _catalog.Object, _options, _preferences, _installs, () => _environment, _log);

    private void WriteState(CatalogChannel folder, int version, CatalogChannel? recorded = null, SectionOrigin origin = SectionOrigin.Remote) =>
        CatalogChecks.State(version, recorded ?? folder, origin).Save(_options.WriteFolderFor(folder, 1));

    private static void Settle(CatalogUpdateCoordinator coordinator) =>
        SpinWait.SpinUntil(() => coordinator.Phase is not (CatalogUpdatePhase.Applying or CatalogUpdatePhase.Checking), 10_000).Should().BeTrue();

    private IEnumerable<string> Logged => _log.GetEntries().Where(e => e.Source == "Catalog").Select(e => e.Message);

    /// <summary>The change lines are logged after the phase flips (review M3): wait for them, never race them.</summary>
    private void WaitLogged(Func<string, bool> line) =>
        SpinWait.SpinUntil(() => Logged.Any(line), 10_000).Should().BeTrue("the line is logged by the apply task");

    internal sealed class FakePreferences : ICatalogPreferenceStore
    {
        public CatalogPreferences Stored { get; set; } = new("Stable", false);
        public Exception? LoadFailure { get; set; }
        public Exception? SaveFailure { get; set; }
        public List<CatalogChannel> SavedChannels { get; } = [];

        public Task<CatalogPreferences> LoadAsync(CancellationToken ct = default) =>
            LoadFailure is not null ? Task.FromException<CatalogPreferences>(LoadFailure) : Task.FromResult(Stored);

        public Task SaveChannelAsync(CatalogChannel channel, CancellationToken ct = default)
        {
            if (SaveFailure is not null) return Task.FromException(SaveFailure);
            SavedChannels.Add(channel);
            Stored = Stored with { Channel = channel.ToString() };
            return Task.CompletedTask;
        }

        public Task SaveApplyAutomaticallyAsync(bool on, CancellationToken ct = default)
        {
            if (SaveFailure is not null) return Task.FromException(SaveFailure);
            Stored = Stored with { ApplyAutomatically = on };
            return Task.CompletedTask;
        }
    }

    // ----- channel -----

    [Fact]
    public async Task The_stored_channel_is_followed_and_written_into_the_sdk_options()
    {
        _preferences.Stored = new CatalogPreferences("Preview", false);
        using var coordinator = Create();

        (await coordinator.ResolveChannelAsync()).Should().Be(CatalogChannel.Preview);

        _options.Channel.Should().Be(CatalogChannel.Preview);
        coordinator.ChannelSource.Should().Be(CatalogChannelSource.Setting);
        Logged.Should().Contain("Following the Preview catalog channel (Setting)");
    }

    [Fact]
    public async Task The_environment_variable_wins_for_this_run()
    {
        _preferences.Stored = new CatalogPreferences("Stable", false);
        _environment = "preview";
        using var coordinator = Create();

        await coordinator.ResolveChannelAsync();

        coordinator.Channel.Should().Be(CatalogChannel.Preview);
        coordinator.ChannelSource.Should().Be(CatalogChannelSource.Environment);
    }

    [Fact]
    public async Task Unreadable_preferences_follow_Stable_apply_nothing_and_are_read_again_next_time()
    {
        _preferences.LoadFailure = new IOException("database locked");
        using var coordinator = Create();

        await coordinator.ResolveChannelAsync();
        coordinator.Channel.Should().Be(CatalogChannel.Stable);
        coordinator.AutoApply.Should().BeFalse();

        _preferences.LoadFailure = null;
        _preferences.Stored = new CatalogPreferences("Preview", true);
        await coordinator.ResolveChannelAsync();
        coordinator.Channel.Should().Be(CatalogChannel.Preview, "a failed read never latches");
        coordinator.AutoApply.Should().BeTrue();
    }

    // ----- check: per-app notices (spec §4) -----

    [Fact]
    public async Task A_release_with_only_installer_changes_is_not_announced()
    {
        _service.NextCheck = () => CatalogChecks.Available(7, CatalogChannel.Stable, [CatalogChecks.WorkloadAdded("Qwen-Image-Edit", "V1.0")])
            .Targeting(WorkloadTargetType.Installer);
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.LastCheck!.Outcome.Should().Be(CatalogUpdateOutcome.UpdatesAvailable, "the folder is behind; releases are full snapshots");
        coordinator.LastCheck.HasChanges.Should().BeFalse();
        coordinator.LastCheck.Workloads.Should().BeEmpty();
        coordinator.UpdateAvailable.Should().BeFalse();
        coordinator.CanApply.Should().BeFalse();
        coordinator.LastCheckedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_release_for_the_app_is_announced_with_only_its_rows()
    {
        var app = CatalogChecks.WorkloadUpdated("Upscaling-Z-Image-Turbo", "V1.6", "V1.7");
        var installer = CatalogChecks.WorkloadAdded("Qwen-Image-Edit", "V1.0");
        _service.NextCheck = () => CatalogChecks.Available(7, CatalogChannel.Stable, [app, installer]) with
        {
            Index = new CatalogAudienceIndex(new Dictionary<Guid, WorkloadAudienceInfo>
            {
                [app.Id] = new(WorkloadTargetType.DiffusionNexusCore, true),
                [installer.Id] = new(WorkloadTargetType.Installer, true),
            }, new Dictionary<Guid, IReadOnlyList<Guid>>()),
        };
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.UpdateAvailable.Should().BeTrue();
        coordinator.LastCheck!.Workloads.Should().ContainSingle().Which.Name.Should().Be("Upscaling-Z-Image-Turbo");
    }

    [Fact]
    public async Task A_check_without_targets_counts_for_the_app()
    {
        _service.NextCheck = () => CatalogChecks.Available(6);   // a manifest packed before 2.3.0: Index empty
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.UpdateAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_release_that_replaces_content_from_another_channel_is_offered()
    {
        // Following Preview; the Preview folder holds the embedded Stable seed (a switch that never landed).
        _preferences.Stored = new CatalogPreferences("Preview", false);
        WriteState(CatalogChannel.Preview, 5, recorded: CatalogChannel.Stable, origin: SectionOrigin.Embedded);
        _service.NextCheck = () => CatalogChecks.Available(6, CatalogChannel.Preview, [CatalogChecks.WorkloadAdded("Qwen-Image-Edit", "V1.0")])
            .Targeting(WorkloadTargetType.Installer);
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.LastCheck!.HasChanges.Should().BeFalse();
        coordinator.UpdateAvailable.Should().BeTrue("applying it is the only thing that replaces the other channel's content");
    }

    [Fact]
    public async Task A_check_that_throws_is_a_filtered_failed_check()
    {
        _service.NextCheck = () => throw new InvalidOperationException("boom");
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.LastCheck!.Outcome.Should().Be(CatalogUpdateOutcome.Failed);
        coordinator.LastCheck.Audience.Should().Be(CatalogAudience.DiffusionNexus);
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
    }

    // ----- apply -----

    [Fact]
    public async Task Apply_sends_the_whole_release_and_ends_applied()
    {
        _service.NextCheck = () => CatalogChecks.Available(7).Targeting(WorkloadTargetType.DiffusionNexusCore);
        _service.OnApply = () => WriteState(CatalogChannel.Stable, 7);
        using var coordinator = Create();
        await coordinator.CheckAsync();

        await coordinator.ApplyAsync();
        Settle(coordinator);

        _service.AppliedSections.Should().Be(CatalogSections.All);
        _service.AppliedCheck!.Unfiltered.Should().NotBeNull("the SDK sees the whole release");
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Applied);
        coordinator.Installed!.HighestCatalogVersion.Should().Be(7);
        WaitLogged(m => m.StartsWith("Updated: Upscaling-Z-Image-Turbo"));
    }

    [Fact]
    public async Task Apply_waits_while_an_install_runs_and_says_which()
    {
        _service.NextCheck = () => CatalogChecks.Available(7);
        using var coordinator = Create();
        await coordinator.CheckAsync();
        var running = _installs.Begin("Krea-2-Turbo");
        var changed = 0;
        coordinator.Changed += () => changed++;

        await coordinator.ApplyAsync();

        _service.ApplyCalls.Should().Be(0, "never swap the catalog under a running plan");
        coordinator.CanApply.Should().BeFalse();
        coordinator.ApplyBlockedReason.Should().Be("It can be applied once Krea-2-Turbo has finished.");

        running.Dispose();
        changed.Should().BeGreaterThan(0, "the page re-renders when the install ends");
        coordinator.CanApply.Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_apply_keeps_the_error_and_apply_is_the_retry()
    {
        _service.NextCheck = () => CatalogChecks.Available(7);
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.None, CatalogSections.All, "The download stalled.");
        using var coordinator = Create();
        await coordinator.CheckAsync();

        await coordinator.ApplyAsync();
        Settle(coordinator);

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
        coordinator.LastApplyFailed.Should().BeTrue();
        coordinator.CanApply.Should().BeTrue();
    }

    [Fact]
    public async Task An_apply_that_throws_reports_the_error_and_checks_again_what_is_left()
    {
        _service.NextCheck = () => CatalogChecks.Available(7);
        using var coordinator = Create();
        await coordinator.CheckAsync();
        _service.ApplyFailure = new InvalidOperationException("unexpected");
        _service.NextCheck = () => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate);   // both sections had landed before the throw

        await coordinator.ApplyAsync();
        Settle(coordinator);

        coordinator.LastApply!.Error.Should().Be("unexpected");
        coordinator.LastCheck!.Outcome.Should().Be(CatalogUpdateOutcome.UpToDate);
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
        _catalog.Verify(c => c.Invalidate(), Times.Once);
    }

    // ----- startup (spec §6 table) -----

    [Fact]
    public async Task Startup_with_the_toggle_off_logs_one_line_and_applies_nothing()
    {
        _service.NextCheck = () => CatalogChecks.Available(7).Targeting(WorkloadTargetType.DiffusionNexusCore);
        using var coordinator = Create();

        await coordinator.CheckAtStartupAsync();

        _service.ApplyCalls.Should().Be(0);
        Logged.Count(m => m == "Catalog update available – see Settings → Catalog").Should().Be(1);
    }

    [Fact]
    public async Task Startup_with_the_toggle_on_applies_silently_and_logs_the_changes()
    {
        _preferences.Stored = new CatalogPreferences("Stable", true);
        _service.NextCheck = () => CatalogChecks.Available(7).Targeting(WorkloadTargetType.DiffusionNexusCore);
        _service.OnApply = () => WriteState(CatalogChannel.Stable, 7);
        using var coordinator = Create();

        await coordinator.CheckAtStartupAsync();
        Settle(coordinator);

        _service.ApplyCalls.Should().Be(1);
        coordinator.LastApplyAutomatic.Should().BeTrue();
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Applied);
        Logged.Should().Contain("Applying the catalog update automatically (setting on)");
        WaitLogged(m => m.StartsWith("Updated: Upscaling-Z-Image-Turbo"));
    }

    [Fact]
    public async Task A_release_with_only_installer_changes_is_still_applied_automatically()
    {
        _preferences.Stored = new CatalogPreferences("Stable", true);
        _service.NextCheck = () => CatalogChecks.Available(7, CatalogChannel.Stable, [CatalogChecks.WorkloadAdded("Qwen-Image-Edit", "V1.0")])
            .Targeting(WorkloadTargetType.Installer);
        _service.OnApply = () => WriteState(CatalogChannel.Stable, 7);
        using var coordinator = Create();

        await coordinator.CheckAtStartupAsync();
        Settle(coordinator);

        _service.ApplyCalls.Should().Be(1, "full snapshots: the folder both apps read stays current");
        Logged.Should().NotContain("Catalog update available – see Settings → Catalog");
    }

    [Fact]
    public async Task Startup_with_the_toggle_off_and_only_installer_changes_does_nothing_visible()
    {
        _service.NextCheck = () => CatalogChecks.Available(7, CatalogChannel.Stable, [CatalogChecks.WorkloadAdded("Qwen-Image-Edit", "V1.0")])
            .Targeting(WorkloadTargetType.Installer);
        using var coordinator = Create();

        await coordinator.CheckAtStartupAsync();

        _service.ApplyCalls.Should().Be(0);
        Logged.Should().NotContain("Catalog update available – see Settings → Catalog");
    }

    // Review M6: toggle on, an install running at startup: the apply waits and the notice was missing.
    [Fact]
    public async Task Startup_with_the_toggle_on_and_an_install_running_announces_the_update()
    {
        _preferences.Stored = new CatalogPreferences("Stable", true);
        _service.NextCheck = () => CatalogChecks.Available(7).Targeting(WorkloadTargetType.DiffusionNexusCore);
        using var running = _installs.Begin("Krea-2-Turbo");
        using var coordinator = Create();

        await coordinator.CheckAtStartupAsync();

        _service.ApplyCalls.Should().Be(0);
        Logged.Should().Contain("Catalog update available – see Settings → Catalog");
    }

    [Fact]
    public async Task A_failed_automatic_apply_can_be_retried()
    {
        _preferences.Stored = new CatalogPreferences("Stable", true);
        _service.NextCheck = () => CatalogChecks.Available(7, CatalogChannel.Stable, [CatalogChecks.WorkloadAdded("Qwen-Image-Edit", "V1.0")])
            .Targeting(WorkloadTargetType.Installer);
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.None, CatalogSections.All, "The download stalled.");
        using var coordinator = Create();

        await coordinator.CheckAtStartupAsync();
        Settle(coordinator);

        coordinator.LastApplyFailed.Should().BeTrue();
        coordinator.CanApply.Should().BeTrue("Apply is the retry, whoever the changes were for");
    }

    // ----- the setting -----

    [Fact]
    public async Task The_toggle_is_saved_and_refused_while_an_apply_runs()
    {
        _service.NextCheck = () => CatalogChecks.Available(7);
        _service.HoldApply = new TaskCompletionSource();
        using var coordinator = Create();
        await coordinator.CheckAsync();
        _ = coordinator.ApplyAsync();

        (await coordinator.SetAutoApplyAsync(true)).Should().BeFalse();
        _preferences.Stored.ApplyAutomatically.Should().BeFalse();

        _service.HoldApply.SetResult();
        Settle(coordinator);
        (await coordinator.SetAutoApplyAsync(true)).Should().BeTrue();
        _preferences.Stored.ApplyAutomatically.Should().BeTrue();
        coordinator.AutoApply.Should().BeTrue();
    }

    [Fact]
    public async Task A_throwing_changed_handler_never_breaks_a_check()
    {
        using var coordinator = Create();
        coordinator.Changed += () => throw new InvalidOperationException("disposed view");

        await coordinator.CheckAsync();

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
    }
}
