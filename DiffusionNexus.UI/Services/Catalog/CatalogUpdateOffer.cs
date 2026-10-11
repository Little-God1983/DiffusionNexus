using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.UI.Services.Catalog;

/// <summary>
/// What Settings → Catalog offers, as pure functions of the coordinator's state -- shared by the coordinator
/// and the page's test stub so the page tests see the real rule (installer PR #54 lesson).
/// </summary>
public static class CatalogUpdateOffer
{
    /// <summary>
    /// The followed channel's folder is behind its release, whoever the changes are for. A switch preview
    /// whose apply failed counts too, whatever it said: the SDK found the target folder needs the release
    /// (it tried to fill it), and applying the preview again is the retry. Not while a switch waits for an
    /// answer (the check describes the channel being left), nor once applied.
    /// </summary>
    public static bool ReleaseAvailable(CatalogUpdateCheck? check, CatalogUpdatePhase phase, bool pendingSwitch, bool lastApplyFailed) =>
        !pendingSwitch && phase != CatalogUpdatePhase.Applied && check is not null
        && (check.Outcome == CatalogUpdateOutcome.UpdatesAvailable || (check.IsSwitchPreview && lastApplyFailed));

    /// <summary>
    /// Announced and offered: the release changes something this app shows; or a failed apply is retried
    /// whoever the changes were for; or the installed content is another channel's (an embedded seed after a
    /// switch) and only this release replaces it.
    /// </summary>
    public static bool Offered(CatalogUpdateCheck? check, CatalogUpdatePhase phase, bool pendingSwitch, bool lastApplyFailed, bool contentFromAnotherChannel) =>
        ReleaseAvailable(check, phase, pendingSwitch, lastApplyFailed)
        && (check!.HasChanges || lastApplyFailed || contentFromAnotherChannel);
}
