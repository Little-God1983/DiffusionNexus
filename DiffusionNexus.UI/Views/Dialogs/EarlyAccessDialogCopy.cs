namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Wording rules for <see cref="EarlyAccessConfirmDialog"/>. Kept out of the Window so
/// they can be unit-tested without initializing Avalonia.
/// <para>
/// The two gated kinds must never share a sentence: temporary Early Access is paywalled
/// <em>for a limited time</em> and ends on a date (waitlistable), while permanently paid
/// versions are paywalled indefinitely and never become free. Copy that describes a
/// permanently paid model as time-limited is simply wrong, so each group gets its own
/// paragraph, sized for its own count.
/// </para>
/// </summary>
internal static class EarlyAccessDialogCopy
{
    /// <summary>Window chrome title, named for whichever kinds are present.</summary>
    public static string WindowTitle(int temporary, int permanent) => (temporary, permanent) switch
    {
        (> 0, > 0) => "Paid models in selection",
        (0, > 0) => "Paywalled models in selection",
        _ => "Early Access models in selection"
    };

    /// <summary>Headline above the explanation paragraphs.</summary>
    public static string Header(int temporary, int permanent) => (temporary, permanent) switch
    {
        (> 0, > 0) => "Paid access detected",
        (0, > 0) => permanent == 1 ? "Paywalled model detected" : "Paywalled models detected",
        _ => temporary == 1 ? "Early Access model detected" : "Early Access models detected"
    };

    /// <summary>
    /// Sentence opener shared by both paragraphs, ending in the verb so the highlighted
    /// phrase ("Early Access" / "permanently paid") can follow it as its own coloured Run.
    /// </summary>
    public static string SelectionLead(int count) => count == 1
        ? "1 version in your selection is "
        : $"{count} versions in your selection are ";

    /// <summary>Explanation that follows the highlighted "Early Access" phrase.</summary>
    public static string TemporaryTail(int count) => count == 1
        ? " — the creator has paywalled it on Civitai for a limited time. Until that period ends, downloading requires buying access on the website (the app's download would fail with HTTP 401). When early access ends, it becomes free for everyone."
        : " — the creator has paywalled them on Civitai for a limited time. Until that period ends, downloading requires buying access on the website (the app's download would fail with HTTP 401). When early access ends, they become free for everyone.";

    /// <summary>Explanation that follows the highlighted "permanently paid" phrase.</summary>
    public static string PermanentTail(int count) => count == 1
        ? " — the creator has paywalled it on Civitai indefinitely, with no end date. It will never become free, so the waitlist can't help: buying it on Civitai is the only way to download it."
        : " — the creator has paywalled them on Civitai indefinitely, with no end date. They will never become free, so the waitlist can't help: buying them on Civitai is the only way to download them.";

    /// <summary>
    /// Label for the skip button. Only shown when the selection actually holds
    /// ungated items — with nothing else to add, "add the rest" names nothing.
    /// </summary>
    public static string SkipButtonText(int temporary, int permanent) => (temporary, permanent) switch
    {
        (> 0, > 0) => "Skip paid items, add the rest",
        (0, > 0) => "Skip paywalled, add the rest",
        _ => "Skip Early Access, add the rest"
    };

    /// <summary>Waitlist button tooltip; only promises the immediate download when there is one.</summary>
    public static string WaitlistButtonTooltip(int otherCount) => otherCount > 0
        ? "Track these on the Waitlist tab and download them when early access ends — the rest of the selection downloads now"
        : "Track these on the Waitlist tab and download them when early access ends";
}
