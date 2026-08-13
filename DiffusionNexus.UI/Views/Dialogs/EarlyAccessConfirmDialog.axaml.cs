using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Five-option confirmation dialog shown when the user tries to enqueue Civitai
/// versions flagged as Early Access: cancel, skip the Early Access items and
/// download the rest, add them anyway, add them to the waitlist, or open the
/// model's Civitai page. Lists the affected titles split into two groups —
/// <see cref="EarlyAccessTitles"/> (temporary, waitlistable) and
/// <see cref="PermanentTitles"/> (permanently paid, never free, excluded from the
/// waitlist) — so the dialog can explain why only some titles are waitlistable.
/// </summary>
public enum EarlyAccessConfirmResult
{
    Cancel,
    SkipEarlyAccess,
    AddAnyway,
    AddToWaitlist,
    OpenWebsite
}

public partial class EarlyAccessConfirmDialog : Window
{
    public EarlyAccessConfirmResult Result { get; private set; } = EarlyAccessConfirmResult.Cancel;

    /// <summary>Temporary early-access titles — these CAN be waitlisted.</summary>
    public IReadOnlyList<string> EarlyAccessTitles { get; }

    /// <summary>Permanently paid titles — never free, excluded from the waitlist.</summary>
    public IReadOnlyList<string> PermanentTitles { get; }

    /// <summary>Ungated versions in the same selection — what "add the rest" refers to.</summary>
    public int OtherCount { get; }

    public bool HasWaitlistable => EarlyAccessTitles.Count > 0;
    public bool HasPermanent => PermanentTitles.Count > 0;
    public bool HasOthers => OtherCount > 0;

    public string WindowTitle => EarlyAccessDialogCopy.WindowTitle(EarlyAccessTitles.Count, PermanentTitles.Count);
    public string HeaderText => EarlyAccessDialogCopy.Header(EarlyAccessTitles.Count, PermanentTitles.Count);
    public string TemporaryLead => EarlyAccessDialogCopy.SelectionLead(EarlyAccessTitles.Count);
    public string TemporaryTail => EarlyAccessDialogCopy.TemporaryTail(EarlyAccessTitles.Count);
    public string PermanentLead => EarlyAccessDialogCopy.SelectionLead(PermanentTitles.Count);
    public string PermanentTail => EarlyAccessDialogCopy.PermanentTail(PermanentTitles.Count);
    public string SkipButtonText => EarlyAccessDialogCopy.SkipButtonText(EarlyAccessTitles.Count, PermanentTitles.Count);
    public string WaitlistButtonTooltip => EarlyAccessDialogCopy.WaitlistButtonTooltip(OtherCount);

    /// <summary>Design-time / XAML loader constructor.</summary>
    public EarlyAccessConfirmDialog() : this([], []) { }

    public EarlyAccessConfirmDialog(
        IReadOnlyList<string> earlyAccessTitles,
        IReadOnlyList<string>? permanentTitles = null,
        int otherCount = 0)
    {
        EarlyAccessTitles = earlyAccessTitles;
        PermanentTitles = permanentTitles ?? [];
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
        Result = EarlyAccessConfirmResult.Cancel;
        Close();
    }

    private void OnSkipClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = EarlyAccessConfirmResult.SkipEarlyAccess;
        Close();
    }

    private void OnAddAnywayClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = EarlyAccessConfirmResult.AddAnyway;
        Close();
    }

    private void OnAddToWaitlistClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = EarlyAccessConfirmResult.AddToWaitlist;
        Close();
    }

    private void OnOpenWebsiteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = EarlyAccessConfirmResult.OpenWebsite;
        Close();
    }
}
