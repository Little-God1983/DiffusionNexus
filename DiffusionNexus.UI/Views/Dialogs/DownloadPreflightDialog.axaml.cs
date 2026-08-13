using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Single pre-download confirmation shown when an enqueue would hit something the user
/// probably wants to know about: cancel, skip the flagged versions and download the
/// rest, download everything anyway, waitlist the Early Access ones, or open the
/// model's Civitai page.
/// <para>
/// It reports all flagged kinds at once — <see cref="EarlyAccessTitles"/> (temporary,
/// waitlistable), <see cref="PermanentTitles"/> (paid forever, never free, excluded
/// from the waitlist) and <see cref="InstalledTitles"/> (already in the library) —
/// specifically so a mixed selection produces one prompt instead of a queue of them.
/// </para>
/// </summary>
public enum DownloadPreflightResult
{
    Cancel,
    SkipFlagged,
    DownloadAnyway,
    AddToWaitlist,
    OpenWebsite
}

public partial class DownloadPreflightDialog : Window
{
    public DownloadPreflightResult Result { get; private set; } = DownloadPreflightResult.Cancel;

    /// <summary>Temporary early-access titles — these CAN be waitlisted.</summary>
    public IReadOnlyList<string> EarlyAccessTitles { get; }

    /// <summary>Permanently paid titles — never free, excluded from the waitlist.</summary>
    public IReadOnlyList<string> PermanentTitles { get; }

    /// <summary>Titles already present in the local library — downloading repeats work.</summary>
    public IReadOnlyList<string> InstalledTitles { get; }

    /// <summary>Unflagged versions in the same selection — what "add the rest" refers to.</summary>
    public int OtherCount { get; }

    public bool HasWaitlistable => EarlyAccessTitles.Count > 0;
    public bool HasPermanent => PermanentTitles.Count > 0;
    public bool HasInstalled => InstalledTitles.Count > 0;
    public bool HasOthers => OtherCount > 0;

    /// <summary>True when anything in the selection is paywalled — drives the Civitai buttons.</summary>
    public bool HasPaywalled => HasWaitlistable || HasPermanent;

    /// <summary>
    /// Skipping is offered whenever it changes the outcome: either there is something
    /// else to download, or there are paywalled versions to leave behind.
    /// </summary>
    public bool ShowSkip => HasOthers || HasPaywalled;

    /// <summary>
    /// Downloading regardless is offered ONLY for already-installed files. Civitai serves
    /// paid files through the website alone — the app appends the API key on a 401 retry
    /// and still gets refused — so a "download anyway" for paywalled versions would be an
    /// option that cannot work.
    /// </summary>
    public bool ShowDownloadAnyway => !HasPaywalled;

    public string WindowTitle => DownloadPreflightCopy.WindowTitle(EarlyAccessTitles.Count, PermanentTitles.Count, InstalledTitles.Count);
    public string HeaderText => DownloadPreflightCopy.Header(EarlyAccessTitles.Count, PermanentTitles.Count, InstalledTitles.Count);
    public string TemporaryLead => DownloadPreflightCopy.SelectionLead(EarlyAccessTitles.Count);
    public string TemporaryTail => DownloadPreflightCopy.TemporaryTail(EarlyAccessTitles.Count);
    public string PermanentLead => DownloadPreflightCopy.SelectionLead(PermanentTitles.Count);
    public string PermanentTail => DownloadPreflightCopy.PermanentTail(PermanentTitles.Count);
    public string InstalledLead => DownloadPreflightCopy.SelectionLead(InstalledTitles.Count);
    public string InstalledTail => DownloadPreflightCopy.InstalledTail(InstalledTitles.Count);
    public string SkipButtonText => DownloadPreflightCopy.SkipButtonText(EarlyAccessTitles.Count, PermanentTitles.Count, InstalledTitles.Count, OtherCount);
    public string WaitlistButtonTooltip => DownloadPreflightCopy.WaitlistButtonTooltip(OtherCount);

    /// <summary>Design-time / XAML loader constructor.</summary>
    public DownloadPreflightDialog() : this([], [], []) { }

    public DownloadPreflightDialog(
        IReadOnlyList<string> earlyAccessTitles,
        IReadOnlyList<string>? permanentTitles = null,
        IReadOnlyList<string>? installedTitles = null,
        int otherCount = 0)
    {
        EarlyAccessTitles = earlyAccessTitles;
        PermanentTitles = permanentTitles ?? [];
        InstalledTitles = installedTitles ?? [];
        OtherCount = otherCount;
        DataContext = this;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = DownloadPreflightResult.Cancel;
        Close();
    }

    private void OnSkipClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = DownloadPreflightResult.SkipFlagged;
        Close();
    }

    private void OnDownloadAnywayClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = DownloadPreflightResult.DownloadAnyway;
        Close();
    }

    private void OnAddToWaitlistClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = DownloadPreflightResult.AddToWaitlist;
        Close();
    }

    private void OnOpenWebsiteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = DownloadPreflightResult.OpenWebsite;
        Close();
    }
}
