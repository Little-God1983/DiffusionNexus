using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.UI.Services.Catalog;
using FluentAssertions;

namespace DiffusionNexus.Tests.Catalog;

public sealed partial class CatalogUpdateCoordinatorTests
{
    private static CatalogUpdateCheck PreviewRemovesAppWorkload(CatalogChannel channel) =>
        CatalogChecks.Available(6, channel, [CatalogChecks.WorkloadRemoved("Upscaling-Z-Image-Turbo Vision", "V1.0")])
            .Targeting(WorkloadTargetType.DiffusionNexusCore);

    [Fact]
    public async Task A_switch_that_removes_an_app_workload_waits_for_an_answer_and_saves_nothing()
    {
        _service.NextPreview = PreviewRemovesAppWorkload;
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);

        coordinator.PendingSwitch!.Warning.Removed.Should().Equal("Upscaling-Z-Image-Turbo Vision");
        _service.PreviewedSwitches.Should().Equal((CatalogChannel.Stable, CatalogChannel.Preview));
        _preferences.SavedChannels.Should().BeEmpty();
        coordinator.Channel.Should().Be(CatalogChannel.Stable);
        coordinator.UpdateAvailable.Should().BeFalse("the last check describes the channel being left");

        coordinator.KeepChannel();
        coordinator.PendingSwitch.Should().BeNull();
        _preferences.SavedChannels.Should().BeEmpty();
    }

    [Fact]
    public async Task A_switch_that_only_changes_installer_workloads_goes_ahead_without_asking()
    {
        _service.NextPreview = channel => CatalogChecks.Available(6, channel, [CatalogChecks.WorkloadRemoved("Qwen-Image-Edit", "V1.0")])
            .Targeting(WorkloadTargetType.Installer);
        _service.OnApply = () => WriteState(CatalogChannel.Preview, 6);
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        Settle(coordinator);

        coordinator.PendingSwitch.Should().BeNull();
        _preferences.SavedChannels.Should().Equal(CatalogChannel.Preview);
        _options.Channel.Should().Be(CatalogChannel.Preview);
        _service.ApplyCalls.Should().Be(1);
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Applied);
    }

    [Fact]
    public async Task Confirming_saves_the_channel_before_the_download_and_applies_the_preview()
    {
        _service.NextPreview = PreviewRemovesAppWorkload;
        var savedBeforeApply = false;
        _service.OnApply = () => { savedBeforeApply = _preferences.SavedChannels.Count == 1; WriteState(CatalogChannel.Preview, 6); };
        using var coordinator = Create();
        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        var preview = coordinator.PendingSwitch!.Preview;

        await coordinator.ConfirmSwitchAsync();
        Settle(coordinator);

        savedBeforeApply.Should().BeTrue("a failed download must not lose the choice");
        _service.AppliedCheck.Should().BeSameAs(preview);
        coordinator.Installed!.HighestCatalogVersion.Should().Be(6);
        coordinator.LastCheck!.Remote!.CatalogVersion.Should().Be(6, "landed everything: the preview names the release");
    }

    [Fact]
    public async Task A_switch_whose_apply_lands_nothing_is_up_to_date_and_offers_nothing()
    {
        WriteState(CatalogChannel.Preview, 6);
        _service.NextPreview = channel => CatalogChecks.Available(6, channel, [CatalogChecks.WorkloadAdded("Ernie", "V1.0")])
            .Targeting(WorkloadTargetType.DiffusionNexusCore);   // additions only: no warning
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.None, CatalogSections.None, null);
        _service.NextCheck = () => throw new InvalidOperationException("no second fetch: the SDK just checked that folder");
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        Settle(coordinator);

        coordinator.LastCheck!.Outcome.Should().Be(CatalogUpdateOutcome.UpToDate);
        coordinator.UpdateAvailable.Should().BeFalse("otherwise Apply is offered and applies nothing, forever");
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
    }

    [Fact]
    public async Task A_same_content_switch_still_lets_the_sdk_fill_the_target_folder()
    {
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate, channel) with { Remote = CatalogChecks.Remote(6, channel) };
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null);
        _service.OnApply = () => WriteState(CatalogChannel.Preview, 6);
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        Settle(coordinator);

        _service.ApplyCalls.Should().Be(1);
        _service.AppliedCheck!.IsSwitchPreview.Should().BeTrue();
        coordinator.Installed!.HighestCatalogVersion.Should().Be(6);
    }

    [Fact]
    public async Task A_failed_switch_apply_offers_apply_as_the_retry()
    {
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate, channel) with { Remote = CatalogChecks.Remote(6, channel) };
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.None, CatalogSections.All, "The download stalled.");
        _service.NextCheck = () => throw new InvalidOperationException("no check expected");
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        Settle(coordinator);

        coordinator.LastApplyFailed.Should().BeTrue();
        coordinator.UpdateAvailable.Should().BeTrue("the SDK found the folder needs the release");
        coordinator.CanApply.Should().BeTrue();

        _service.NextApply = () => new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null);
        _service.OnApply = () => WriteState(CatalogChannel.Preview, 6);
        await coordinator.ApplyAsync();
        Settle(coordinator);
        _service.AppliedCheck!.IsSwitchPreview.Should().BeTrue("the retry lets the SDK fill the target folder again");
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Applied);
    }

    // PR #619 round 1: a switch changes the folder ICatalog reads at once; mid-install that swaps the catalog
    // under the running plan. The channel can be changed once the install has finished.
    [Fact]
    public async Task A_switch_is_refused_while_an_install_runs()
    {
        using var running = _installs.Begin("Krea-2-Turbo");
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);

        _service.PreviewedSwitches.Should().BeEmpty();
        _preferences.SavedChannels.Should().BeEmpty();
        coordinator.Channel.Should().Be(CatalogChannel.Stable);
        _options.Channel.Should().Be(CatalogChannel.Stable);
        coordinator.ChannelChangeBlockedReason.Should().Be("The channel can be changed once Krea-2-Turbo has finished.");

        running.Dispose();
        coordinator.ChannelChangeBlockedReason.Should().BeNull();
    }

    // PR #619 round 1: after a switch that started no apply, Check joined the background seed read and ran nothing.
    [Fact]
    public async Task Check_right_after_a_switch_runs_a_check()
    {
        var gate = new TaskCompletionSource();
        _catalog.SetupGet(c => c.State).Returns(() => { gate.Task.Wait(); return CatalogChecks.State(5, CatalogChannel.Stable, SectionOrigin.Embedded); });
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.Failed, channel, "offline");
        _service.NextCheck = () => CatalogChecks.Available(6, CatalogChannel.Preview);
        using var coordinator = Create();
        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);

        var check = coordinator.CheckAsync();
        var done = check.Wait(5000);
        gate.SetResult();

        done.Should().BeTrue("the check does not wait for the seed read");
        _service.CheckCalls.Should().Be(1);
    }

    // PR #619 round 3: with the channel unknown (a failed read), picking the shown channel did nothing.
    [Fact]
    public async Task Picking_the_shown_channel_while_it_is_unknown_saves_it()
    {
        _preferences.LoadFailure = new IOException("database locked");   // the setting could not be read
        using var coordinator = Create();
        await coordinator.CheckAsync();
        coordinator.LastCheck!.Outcome.Should().Be(CatalogUpdateOutcome.Failed);

        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);

        _preferences.SavedChannels.Should().Equal(CatalogChannel.Stable);
        _service.NextCheck = () => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate);
        await coordinator.CheckAsync();
        _service.CheckCalls.Should().Be(1, "the channel is known now");
    }

    // PR #619 round 3: while the preview ran, the page showed the old channel's dot.
    [Fact]
    public async Task While_the_preview_runs_the_target_is_shown()
    {
        var hold = new TaskCompletionSource();
        _service.HoldCheck = hold;
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate, channel);
        using var coordinator = Create();

        var switching = coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        SpinWait.SpinUntil(() => _service.PreviewedSwitches.Count == 1, 10_000).Should().BeTrue();

        coordinator.SwitchingTo.Should().Be(CatalogChannel.Preview);
        hold.SetResult();
        await switching;
        coordinator.SwitchingTo.Should().BeNull();
    }

    // PR #619 round 5: a channel whose release needs a newer app can never be read; switching to it left the app
    // following a channel it cannot read, for good.
    [Fact]
    public async Task A_switch_to_a_channel_that_needs_a_newer_app_is_refused()
    {
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.RequiresNewerSoftware, channel) with { Remote = CatalogChecks.Remote(9, channel) };
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);

        _preferences.SavedChannels.Should().BeEmpty();
        coordinator.Channel.Should().Be(CatalogChannel.Stable);
        _options.Channel.Should().Be(CatalogChannel.Stable);
        coordinator.LastCheck!.Outcome.Should().Be(CatalogUpdateOutcome.RequiresNewerSoftware);
        coordinator.UpdateAvailable.Should().BeFalse();
    }

    // PR #619 round 5: a failed save on "Switch to …" dropped the warning; the answer could not be given again.
    [Fact]
    public async Task A_failed_confirmation_keeps_the_warning_for_another_try()
    {
        _service.NextPreview = PreviewRemovesAppWorkload;
        using var coordinator = Create();
        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        _preferences.SaveFailure = new IOException("database locked");

        var act = () => coordinator.ConfirmSwitchAsync();

        await act.Should().ThrowAsync<IOException>();
        coordinator.PendingSwitch.Should().NotBeNull();

        _preferences.SaveFailure = null;
        _service.OnApply = () => WriteState(CatalogChannel.Preview, 6);
        await coordinator.ConfirmSwitchAsync();
        Settle(coordinator);
        coordinator.Channel.Should().Be(CatalogChannel.Preview);
    }

    [Fact]
    public async Task A_switch_into_a_missing_folder_keeps_the_last_known_state()
    {
        WriteState(CatalogChannel.Stable, 5);
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.Failed, channel, "No such host is known.");
        using var coordinator = Create();
        await coordinator.CheckAsync();
        coordinator.Installed!.HighestCatalogVersion.Should().Be(5);

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        SpinWait.SpinUntil(() => coordinator.Phase == CatalogUpdatePhase.Checked, 10_000);

        coordinator.Installed!.HighestCatalogVersion.Should().Be(5, "an empty Preview folder is no description of what the app will read");
        _preferences.SavedChannels.Should().ContainSingle().Which.Should().Be(CatalogChannel.Preview, "a failed preview still saves the choice");
    }

    [Fact]
    public async Task A_switch_into_a_missing_folder_describes_the_seed_the_catalog_reads()
    {
        _catalog.SetupGet(c => c.State).Returns(CatalogChecks.State(5, CatalogChannel.Stable, SectionOrigin.Embedded));
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.Failed, channel, "offline");
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);

        SpinWait.SpinUntil(() => coordinator.Installed?.HighestCatalogVersion == 5, 10_000).Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_save_changes_nothing_and_propagates()
    {
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate, channel);
        _preferences.SaveFailure = new IOException("database locked");
        using var coordinator = Create();

        var act = () => coordinator.SwitchChannelAsync(CatalogChannel.Preview);

        await act.Should().ThrowAsync<IOException>();
        coordinator.Channel.Should().Be(CatalogChannel.Stable);
        _options.Channel.Should().Be(CatalogChannel.Stable);
        _service.ApplyCalls.Should().Be(0);
    }

    [Fact]
    public async Task Under_the_environment_variable_a_switch_only_saves_the_preference()
    {
        _environment = "Stable";
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);

        _preferences.SavedChannels.Should().Equal(CatalogChannel.Preview);
        coordinator.Channel.Should().Be(CatalogChannel.Stable, "the environment decides this run");
        _service.PreviewedSwitches.Should().BeEmpty();
    }

    [Fact]
    public async Task Checks_and_other_switches_are_refused_while_a_switch_waits_for_an_answer()
    {
        _service.NextPreview = PreviewRemovesAppWorkload;
        using var coordinator = Create();
        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);

        await coordinator.CheckAsync();
        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);

        _service.CheckCalls.Should().Be(0);
        _service.PreviewedSwitches.Should().HaveCount(1);
        (await coordinator.SetAutoApplyAsync(true)).Should().BeFalse();
    }
}
