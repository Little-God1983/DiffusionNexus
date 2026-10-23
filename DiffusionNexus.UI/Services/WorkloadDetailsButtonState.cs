namespace DiffusionNexus.UI.Services;

/// <summary>
/// How the workload details dialog's footer and grid should look for a given state.
/// </summary>
/// <param name="InstallContent">Label for the install button.</param>
/// <param name="IsInstallEnabled">Whether the install button can be clicked.</param>
/// <param name="IsRepairEnabled">Whether the repair button can be clicked.</param>
/// <param name="CloseContent">Label for the close button.</param>
/// <param name="IsGridEnabled">Whether the item grid accepts input.</param>
/// <param name="IsProgressVisible">Whether the progress panel is shown.</param>
public sealed record WorkloadDetailsButtons(
    string InstallContent,
    bool IsInstallEnabled,
    bool IsRepairEnabled,
    string CloseContent,
    bool IsGridEnabled,
    bool IsProgressVisible);

/// <summary>
/// The single rule for the workload details dialog's footer state.
///
/// <para>
/// Extracted from the dialog's code-behind because it is the whole of a real bug: the label was
/// only ever assigned on the way <em>into</em> a run, so a finished install left the button reading
/// "Installing..." forever, and enablement was tied to "has an install happened" rather than "is
/// there anything left to install" — which permanently disabled retrying a download that was
/// skipped or failed, with reopening the dialog as the only way back.
/// </para>
///
/// <para>
/// Lives here rather than in the window so it can be tested: exercising the dialog itself would
/// mean initialising Avalonia, which this test suite deliberately never does.
/// </para>
/// </summary>
public static class WorkloadDetailsButtonState
{
    /// <summary>The install button's resting label.</summary>
    public const string InstallLabel = "Install Selected";

    /// <summary>The install button's label while a run is in flight.</summary>
    public const string BusyLabel = "Installing...";

    /// <param name="isBusy">True while an install or repair is running.</param>
    /// <param name="hasRun">
    /// True once an install or repair has finished in this dialog. Affects only the close button's
    /// label and whether the progress log stays visible — never whether install is available,
    /// because "something already ran" says nothing about whether anything is still missing.
    /// </param>
    /// <param name="hasMissingItems">True while any row is still missing from disk.</param>
    public static WorkloadDetailsButtons Resolve(bool isBusy, bool hasRun, bool hasMissingItems) => new(
        InstallContent: isBusy ? BusyLabel : InstallLabel,
        IsInstallEnabled: !isBusy && hasMissingItems,
        // Repair spawns pip installs into the same venv an install is writing to, and its own
        // handler does not guard against re-entry — so it has to be disabled while busy, which the
        // pre-extraction code never did for either the install path or its own.
        IsRepairEnabled: !isBusy,
        CloseContent: hasRun ? "Done" : "Close",
        IsGridEnabled: !isBusy,
        IsProgressVisible: isBusy || hasRun);
}
