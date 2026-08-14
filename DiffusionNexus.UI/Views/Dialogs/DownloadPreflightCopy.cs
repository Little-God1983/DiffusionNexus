namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Wording rules for <see cref="DownloadPreflightDialog"/>. Kept out of the Window so
/// they can be unit-tested without initializing Avalonia.
/// <para>
/// The dialog reports up to three groups and they must never share a sentence:
/// temporary Early Access is paywalled <em>for a limited time</em> and ends on a date
/// (waitlistable); permanently paid versions are paywalled indefinitely and never
/// become free; already-installed versions are free but would be downloaded a second
/// time. Copy that describes a permanently paid model as time-limited is simply wrong,
/// so each group gets its own paragraph, sized for its own count.
/// </para>
/// </summary>
internal static class DownloadPreflightCopy
{
    private static bool MoreThanOneKind(int temporary, int permanent, int installed)
        => (temporary > 0 ? 1 : 0) + (permanent > 0 ? 1 : 0) + (installed > 0 ? 1 : 0) > 1;

    /// <summary>Window chrome title, named for whichever kinds are present.</summary>
    public static string WindowTitle(int temporary, int permanent, int installed)
    {
        if (MoreThanOneKind(temporary, permanent, installed)) return "Check your selection";
        if (permanent > 0) return "Paywalled models in selection";
        if (installed > 0) return "Already installed";
        return "Early Access models in selection";
    }

    /// <summary>Headline above the explanation paragraphs.</summary>
    public static string Header(int temporary, int permanent, int installed)
    {
        if (MoreThanOneKind(temporary, permanent, installed)) return "Check your selection";
        if (permanent > 0) return permanent == 1 ? "Paywalled model detected" : "Paywalled models detected";
        if (installed > 0) return installed == 1 ? "Already installed" : "Already in your library";
        return temporary == 1 ? "Early Access model detected" : "Early Access models detected";
    }

    /// <summary>
    /// Sentence opener shared by every paragraph, ending in the verb so the highlighted
    /// phrase ("Early Access" / "permanently paid" / "already installed") can follow it
    /// as its own coloured Run.
    /// </summary>
    public static string SelectionLead(int count) => count == 1
        ? "1 version in your selection is "
        : $"{count} versions in your selection are ";

    /// <summary>Explanation that follows the highlighted "Early Access" phrase.</summary>
    public static string TemporaryTail(int count) => count == 1
        ? " — the creator has paywalled it on Civitai for a limited time. The app cannot download it during that window, even after you buy access and even with your API key set: Civitai only serves paid files through the website. When early access ends, it becomes free for everyone and downloads normally."
        : " — the creator has paywalled them on Civitai for a limited time. The app cannot download them during that window, even after you buy access and even with your API key set: Civitai only serves paid files through the website. When early access ends, they become free for everyone and download normally.";

    /// <summary>Explanation that follows the highlighted "permanently paid" phrase.</summary>
    public static string PermanentTail(int count) => count == 1
        ? " — the creator has paywalled it on Civitai indefinitely, with no end date. It will never become free, so the waitlist can't help: manually buying and downloading it on Civitai is the only way."
        : " — the creator has paywalled them on Civitai indefinitely, with no end date. They will never become free, so the waitlist can't help: manually buying and downloading them on Civitai is the only way.";

    /// <summary>Explanation that follows the highlighted "already installed" phrase.</summary>
    public static string InstalledTail(int count) => count == 1
        ? " — the file is already in your library, so downloading it again just fetches the same bytes a second time."
        : " — the files are already in your library, so downloading them again just fetches the same bytes a second time.";

    /// <summary>
    /// Label for the skip button. It only promises "the rest" when the selection holds
    /// unflagged versions — otherwise skipping is the whole outcome.
    /// </summary>
    public static string SkipButtonText(int temporary, int permanent, int installed, int otherCount)
    {
        var what =
            MoreThanOneKind(temporary, permanent, installed) ? "Skip flagged"
            : permanent > 0 ? "Skip paywalled"
            : installed > 0 ? "Skip installed"
            : "Skip Early Access";
        return otherCount > 0 ? what + ", add the rest" : what;
    }

    /// <summary>Waitlist button tooltip; only promises the immediate download when there is one.</summary>
    public static string WaitlistButtonTooltip(int otherCount) => otherCount > 0
        ? "Track these on the Waitlist tab and download them when early access ends — the rest of the selection downloads now"
        : "Track these on the Waitlist tab and download them when early access ends";
}
