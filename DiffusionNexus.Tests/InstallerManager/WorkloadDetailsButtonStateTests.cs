using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Covers <see cref="WorkloadDetailsButtonState"/> — the workload details dialog's footer rules.
/// The reported bug: after an install finished, the button still read "Installing..." and stayed
/// greyed out, so a skipped or failed download could only be retried by closing and reopening.
/// </summary>
public sealed class WorkloadDetailsButtonStateTests
{
    [Fact]
    public void AfterAnInstall_TheButtonStopsSayingInstalling()
    {
        // The bug verbatim: the label was only assigned on the way into a run, so it never
        // changed back and the dialog looked permanently busy.
        var state = WorkloadDetailsButtonState.Resolve(isBusy: false, hasRun: true, hasMissingItems: false);

        state.InstallContent.Should().Be(WorkloadDetailsButtonState.InstallLabel);
        state.InstallContent.Should().NotBe(WorkloadDetailsButtonState.BusyLabel);
    }

    [Fact]
    public void AfterAnInstall_ItemsStillMissing_CanBeRetried()
    {
        // A skipped or failed download leaves rows missing. Enablement used to hang off "has an
        // install run", which locked the user out of retrying inside the same dialog.
        var state = WorkloadDetailsButtonState.Resolve(isBusy: false, hasRun: true, hasMissingItems: true);

        state.IsInstallEnabled.Should().BeTrue();
        state.InstallContent.Should().Be(WorkloadDetailsButtonState.InstallLabel);
    }

    [Fact]
    public void AfterAnInstall_NothingLeftMissing_LeavesInstallDisabled()
    {
        var state = WorkloadDetailsButtonState.Resolve(isBusy: false, hasRun: true, hasMissingItems: false);

        state.IsInstallEnabled.Should().BeFalse("there is nothing left to install");
    }

    [Fact]
    public void WhileBusy_EverythingThatCouldStartASecondRunIsDisabled()
    {
        var state = WorkloadDetailsButtonState.Resolve(isBusy: true, hasRun: false, hasMissingItems: true);

        state.InstallContent.Should().Be(WorkloadDetailsButtonState.BusyLabel);
        state.IsInstallEnabled.Should().BeFalse();
        state.IsRepairEnabled.Should().BeFalse(
            "repair pip-installs into the venv the install is writing to, and its handler has no re-entry guard");
        state.IsGridEnabled.Should().BeFalse();
        state.IsProgressVisible.Should().BeTrue();
    }

    [Fact]
    public void BeforeAnythingRuns_NothingMissing_MeansNothingToDo()
    {
        var state = WorkloadDetailsButtonState.Resolve(isBusy: false, hasRun: false, hasMissingItems: false);

        state.IsInstallEnabled.Should().BeFalse();
        state.IsRepairEnabled.Should().BeTrue("repair targets already-installed nodes");
        state.IsProgressVisible.Should().BeFalse();
        state.CloseContent.Should().Be("Close");
    }

    [Fact]
    public void BeforeAnythingRuns_ItemsMissing_OffersTheInstall()
    {
        var state = WorkloadDetailsButtonState.Resolve(isBusy: false, hasRun: false, hasMissingItems: true);

        state.IsInstallEnabled.Should().BeTrue();
        state.IsGridEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, "Done")]
    [InlineData(false, "Close")]
    public void CloseLabel_BecomesDone_OnceSomethingHasRun(bool hasRun, string expected)
    {
        WorkloadDetailsButtonState.Resolve(isBusy: false, hasRun: hasRun, hasMissingItems: false)
            .CloseContent.Should().Be(expected);
    }

    [Fact]
    public void ProgressLog_StaysVisibleAfterARun()
    {
        // The log is the only record of what happened; hiding it when the run ends would throw
        // away the reason a download failed.
        WorkloadDetailsButtonState.Resolve(isBusy: false, hasRun: true, hasMissingItems: false)
            .IsProgressVisible.Should().BeTrue();
    }
}
