using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.Tests.Catalog;
using DiffusionNexus.Tests.Helpers;
using DiffusionNexus.UI.Services.Catalog;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

public sealed class CatalogSettingsViewModelTests
{
    private readonly StubCatalogUpdateCoordinator _catalog = new();

    private CatalogSettingsViewModel Vm() => new(_catalog, new ImmediateUiScheduler());

    private void Checked(CatalogUpdateCheck check)
    {
        _catalog.LastCheck = check.For(CatalogAudience.DiffusionNexus);
        _catalog.Phase = CatalogUpdatePhase.Checked;
    }

    [Fact]
    public void Shows_the_followed_channel_and_the_installed_version()
    {
        _catalog.Channel = CatalogChannel.Preview;
        _catalog.Installed = CatalogChecks.State(6, CatalogChannel.Preview);

        var vm = Vm();

        vm.IsPreview.Should().BeTrue();
        vm.IsStable.Should().BeFalse();
        vm.StatusText.Should().Be("Catalog v6 · Preview");
        vm.OutcomeText.Should().Be("Not checked yet.");
    }

    [Fact]
    public void Content_from_another_channel_is_named_in_the_status()
    {
        _catalog.Channel = CatalogChannel.Preview;
        _catalog.Installed = CatalogChecks.State(5, CatalogChannel.Stable, SectionOrigin.Embedded);

        Vm().StatusText.Should().Be("Catalog v5 · Preview (content from Stable)");
    }

    [Fact]
    public void An_update_for_the_app_lists_its_changes_and_offers_apply()
    {
        Checked(CatalogChecks.Available(7).Targeting(WorkloadTargetType.DiffusionNexusCore));
        var vm = Vm();

        vm.OutcomeText.Should().Be("Catalog update available – v7");
        vm.ChangeGroups.Should().ContainSingle().Which.Heading.Should().Be("Updated");
        vm.ChangeGroups[0].Rows.Should().ContainSingle().Which.Should().Be("Workload · Upscaling-Z-Image-Turbo · V1.6 → V1.7");
        vm.ShowApply.Should().BeTrue();
        vm.ApplyCommand.CanExecute(null).Should().BeTrue();

        vm.ApplyCommand.Execute(null);
        _catalog.Applies.Should().Be(1);
    }

    [Fact]
    public void A_release_with_only_installer_changes_says_so_and_offers_nothing()
    {
        Checked(CatalogChecks.Available(7, CatalogChannel.Stable, [CatalogChecks.WorkloadAdded("Qwen-Image-Edit", "V1.0")]).Targeting(WorkloadTargetType.Installer));
        var vm = Vm();

        vm.OutcomeText.Should().Be("Catalog v7 has no changes for Diffusion Nexus.");
        vm.ChangeGroups.Should().BeEmpty();
        vm.ShowApply.Should().BeFalse();
    }

    [Fact]
    public void An_install_running_holds_apply_and_says_which()
    {
        Checked(CatalogChecks.Available(7));
        _catalog.InstallRunning = "Krea-2-Turbo";
        var vm = Vm();

        vm.ShowApply.Should().BeFalse();
        vm.ApplyBlockedReason.Should().Be("It can be applied once Krea-2-Turbo has finished.");
    }

    [Theory]
    [InlineData(null, 7L, "Applying the catalog…")]
    [InlineData(1024L * 1024, 2L * 1024 * 1024, "Downloading… 50%")]
    public void Progress_is_shown_while_any_apply_runs(long? received, long? total, string expected)
    {
        _catalog.LastCheck = CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate).For(CatalogAudience.DiffusionNexus);
        _catalog.Phase = CatalogUpdatePhase.Applying;
        _catalog.Progress = received is null ? null : new CatalogDownloadProgress(received.Value, total);

        Vm().ProgressText.Should().Be(expected);
    }

    [Fact]
    public void A_failed_apply_shows_the_error_even_when_nothing_is_offered()
    {
        _catalog.LastCheck = CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate).For(CatalogAudience.DiffusionNexus);
        _catalog.Phase = CatalogUpdatePhase.Checked;
        _catalog.LastApply = new CatalogApplyResult(CatalogSections.None, CatalogSections.All, "The download stalled.");

        Vm().ErrorText.Should().Be("The catalog update failed: The download stalled.");
    }

    [Fact]
    public void A_silent_apply_with_no_app_changes_says_so_once_it_landed()
    {
        Checked(CatalogChecks.Available(7, CatalogChannel.Stable, [CatalogChecks.WorkloadAdded("Qwen-Image-Edit", "V1.0")]).Targeting(WorkloadTargetType.Installer));
        _catalog.Phase = CatalogUpdatePhase.Applied;
        _catalog.LastApplyAutomatic = true;
        _catalog.Installed = CatalogChecks.State(7, CatalogChannel.Stable);

        Vm().OutcomeText.Should().Be("Catalog v7 applied automatically; no changes for Diffusion Nexus.");
    }

    [Fact]
    public void Picking_the_other_channel_switches_and_a_warning_shows_switch_and_keep()
    {
        var preview = CatalogChecks.Available(6, CatalogChannel.Preview, [CatalogChecks.WorkloadRemoved("Upscaling-Z-Image-Turbo Vision", "V1.0")])
            .Targeting(WorkloadTargetType.DiffusionNexusCore).For(CatalogAudience.DiffusionNexus);
        _catalog.PendingOnSwitch = new CatalogChannelSwitch(preview, ChannelSwitchWarning.For(preview)!);
        var vm = Vm();

        vm.IsPreview = true;

        _catalog.Switches.Should().Equal(CatalogChannel.Preview);
        vm.ShowWarning.Should().BeTrue();
        vm.WarningText.Should().Be("Preview is at v6. Switching removes Upscaling-Z-Image-Turbo Vision. Software you have already installed is not affected.");
        vm.SwitchButtonText.Should().Be("Switch to Preview");
        vm.KeepButtonText.Should().Be("Keep Stable");
        vm.IsPreview.Should().BeTrue("the dot shows the channel being decided");

        vm.KeepChannelCommand.Execute(null);
        vm.ShowWarning.Should().BeFalse();
        vm.IsStable.Should().BeTrue();
    }

    [Fact]
    public void Under_the_environment_variable_the_radios_are_disabled_with_a_hint()
    {
        _catalog.ChannelSource = CatalogChannelSource.Environment;

        var vm = Vm();

        vm.CanChooseChannel.Should().BeFalse();
        vm.ChannelHint.Should().Be("Set by DIFFUSIONNEXUS_CATALOG_CHANNEL for this run.");
    }

    [Fact]
    public void A_refused_toggle_snaps_back()
    {
        _catalog.RefuseAutoApply = true;
        var vm = Vm();

        vm.AutoApply = true;

        _catalog.AutoApplySet.Should().Equal(true);
        vm.AutoApply.Should().BeFalse();
    }

    // Review I1: a failed save was cleared by the next Refresh (and by the coordinator's own Changed).
    [Fact]
    public void A_failed_channel_save_is_shown_and_the_dot_returns()
    {
        _catalog.SwitchFailure = new IOException("database locked");
        var vm = Vm();

        vm.IsPreview = true;
        _catalog.RaiseChanged();   // the coordinator raises once more in its finally

        vm.ErrorText.Should().Be("The channel could not be saved: database locked");
        vm.IsStable.Should().BeTrue();
    }

    [Fact]
    public void A_failed_toggle_save_is_shown_and_the_toggle_snaps_back()
    {
        _catalog.AutoApplyFailure = new IOException("database locked");
        var vm = Vm();

        vm.AutoApply = true;
        _catalog.RaiseChanged();

        vm.ErrorText.Should().Be("The setting could not be saved: database locked");
        vm.AutoApply.Should().BeFalse();
    }

    [Fact]
    public void The_save_error_goes_once_the_user_acts_again()
    {
        _catalog.SwitchFailure = new IOException("database locked");
        var vm = Vm();
        vm.IsPreview = true;
        _catalog.SwitchFailure = null;

        vm.IsPreview = true;

        vm.ErrorText.Should().BeNull();
    }

    // Review I2: ConfirmSwitchAsync lets a save error through; through an async command that ends the app.
    [Fact]
    public async Task A_failed_switch_confirmation_is_shown_and_never_escapes()
    {
        var preview = CatalogChecks.Available(6, CatalogChannel.Preview, [CatalogChecks.WorkloadRemoved("Upscaling-Z-Image-Turbo Vision", "V1.0")])
            .Targeting(WorkloadTargetType.DiffusionNexusCore).For(CatalogAudience.DiffusionNexus);
        _catalog.PendingSwitch = new CatalogChannelSwitch(preview, ChannelSwitchWarning.For(preview)!);
        _catalog.ConfirmFailure = new IOException("database locked");
        var vm = Vm();

        var act = () => vm.ConfirmSwitchCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync();
        vm.ErrorText.Should().Be("The channel could not be saved: database locked");
        vm.IsStable.Should().BeTrue();
    }

    // Review M7: "Up to date." next to "Downloading… 40%", or "update available" with nothing to press.
    [Theory]
    [InlineData(true, 6, "Catalog v6 is being applied.")]
    [InlineData(false, 7, "Catalog v7 is being applied.")]
    public void While_an_apply_runs_the_outcome_says_so(bool switchPreview, int version, string expected)
    {
        _catalog.LastCheck = switchPreview
            ? (CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate, CatalogChannel.Preview) with { Remote = CatalogChecks.Remote(version, CatalogChannel.Preview), IsSwitchPreview = true })
                .For(CatalogAudience.DiffusionNexus)
            : CatalogChecks.Available(version, CatalogChannel.Stable, [CatalogChecks.WorkloadAdded("Qwen-Image-Edit", "V1.0")])
                .Targeting(WorkloadTargetType.Installer).For(CatalogAudience.DiffusionNexus);
        _catalog.Phase = CatalogUpdatePhase.Applying;

        Vm().OutcomeText.Should().Be(expected);
    }

    // PR #619 round 1: no channel change while an install runs; the page says why.
    [Fact]
    public void While_an_install_runs_the_radios_are_disabled_with_a_hint()
    {
        _catalog.InstallRunning = "Krea-2-Turbo";

        var vm = Vm();

        vm.CanChooseChannel.Should().BeFalse();
        vm.ChannelHint.Should().Be("The channel can be changed once Krea-2-Turbo has finished.");
    }

    // PR #619 round 1: a save error stayed next to "Catalog updated to v7." after Keep, Check and Apply.
    [Fact]
    public async Task Keep_check_and_apply_clear_the_save_error()
    {
        _catalog.SwitchFailure = new IOException("database locked");
        var vm = Vm();
        vm.IsPreview = true;
        vm.ErrorText.Should().NotBeNull();

        vm.KeepChannelCommand.Execute(null);
        vm.ErrorText.Should().BeNull();

        vm.IsPreview = true;
        await vm.CheckCommand.ExecuteAsync(null);
        vm.ErrorText.Should().BeNull();
    }

    // PR #619 round 3: the clicked radio jumped back to the old channel while the preview ran.
    [Fact]
    public void While_a_switch_is_previewed_the_target_dot_stays()
    {
        _catalog.SwitchingTo = CatalogChannel.Preview;
        _catalog.Phase = CatalogUpdatePhase.Checking;

        var vm = Vm();

        vm.IsPreview.Should().BeTrue();
        vm.IsStable.Should().BeFalse();
    }

    [Fact]
    public void Coordinator_changes_refresh_the_page()
    {
        var vm = Vm();
        Checked(CatalogChecks.Available(7));

        _catalog.RaiseChanged();

        vm.OutcomeText.Should().Be("Catalog update available – v7");
    }
}
