using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.Services.CivitaiBrowser;

/// <summary>
/// Lifecycle of a waitlisted early-access version. Waiting/Available flip locally
/// from the stored deadline; the other three are only ever assigned by an API
/// re-check and (except CheckFailed) are terminal — the timer never clears them.
/// </summary>
public enum WaitlistEntryStatus
{
    Waiting,
    Available,
    PermanentlyPaid,
    Unavailable,
    CheckFailed
}

/// <summary>
/// One early-access version the user is waiting on. Carries everything needed to
/// build a <see cref="CivitaiDownloadJob"/> later without re-browsing, plus the
/// deadline captured at add/re-check time. Availability is computed locally
/// (UTC deadline vs UTC now — Civitai timestamps are UTC ISO-8601, so no offset
/// handling); no API call happens outside explicit re-checks.
/// </summary>
public partial class CivitaiWaitlistEntry : ObservableObject
{
    public int ModelId { get; init; }
    public int VersionId { get; init; }
    public string ModelName { get; init; } = string.Empty;
    public string VersionName { get; init; } = string.Empty;
    public string BaseModel { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string SizeDisplay { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string? ExpectedSha256 { get; init; }
    public string? PreviewImageUrl { get; init; }

    /// <summary>Routes "open on Civitai" to civitai.red (full page for NSFW) vs civitai.com.</summary>
    public bool IsNsfw { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    /// <summary>When early access ends (UTC). Null = no end date published, or confirmed free.</summary>
    [ObservableProperty]
    private DateTimeOffset? _earlyAccessDeadline;

    /// <summary>Last successful API re-check. Kept unchanged on CheckFailed so the user sees the last good data's age.</summary>
    [ObservableProperty]
    private DateTimeOffset? _lastCheckedAt;

    [ObservableProperty]
    private WaitlistEntryStatus _status = WaitlistEntryStatus.Waiting;

    /// <summary>Human-readable note from the last re-check (error text, "no longer exists", …).</summary>
    [ObservableProperty]
    private string? _statusDetail;

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private string? _countdownDisplay;

    /// <summary>
    /// Recomputes <see cref="IsAvailable"/>, <see cref="CountdownDisplay"/>, and the
    /// Waiting↔Available flip from the stored deadline. Terminal statuses
    /// (PermanentlyPaid, Unavailable) are never overridden here — only a re-check
    /// assigns or clears them. CheckFailed entries still promote when the deadline
    /// passes: move-to-queue re-verifies before enqueueing anyway.
    /// </summary>
    public void RefreshAvailability(DateTimeOffset? utcNow = null)
    {
        var now = utcNow ?? DateTimeOffset.UtcNow;

        switch (Status)
        {
            case WaitlistEntryStatus.PermanentlyPaid:
                IsAvailable = false;
                CountdownDisplay = "Permanently paid — won't become free";
                return;
            case WaitlistEntryStatus.Unavailable:
                IsAvailable = false;
                CountdownDisplay = "No longer available on Civitai";
                return;
        }

        if (EarlyAccessDeadline is { } deadline)
        {
            if (deadline <= now)
            {
                Status = WaitlistEntryStatus.Available;
                IsAvailable = true;
                CountdownDisplay = "Available now";
            }
            else
            {
                if (Status == WaitlistEntryStatus.Available) Status = WaitlistEntryStatus.Waiting;
                IsAvailable = false;
                CountdownDisplay = FormatCountdown(deadline - now);
            }
        }
        else
        {
            // No deadline stored: Available means a re-check confirmed "free";
            // anything else is a gate whose end date Civitai didn't publish.
            IsAvailable = Status == WaitlistEntryStatus.Available;
            CountdownDisplay = IsAvailable ? "Available now" : "Early access — no end date published";
        }
    }

    private static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining.TotalDays >= 1) return $"free in {(int)remaining.TotalDays}d {remaining.Hours}h";
        if (remaining.TotalHours >= 1) return $"free in {(int)remaining.TotalHours}h {remaining.Minutes}m";
        return $"free in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
    }

    // Same allocate-once brush pattern as CivitaiDownloadJob.StatusForeground.
    private static readonly IBrush AvailableBrush = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush DeadBrush = new SolidColorBrush(Color.Parse("#F87171"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#FBBF24"));
    private static readonly IBrush NeutralBrush = new SolidColorBrush(Color.Parse("#B3B3B3"));

    /// <summary>Green when downloadable, red for dead entries, amber for a failed check, neutral while counting down.</summary>
    public IBrush StatusForeground => Status switch
    {
        WaitlistEntryStatus.Available => AvailableBrush,
        WaitlistEntryStatus.PermanentlyPaid or WaitlistEntryStatus.Unavailable => DeadBrush,
        WaitlistEntryStatus.CheckFailed => WarnBrush,
        _ => NeutralBrush
    };

    partial void OnStatusChanged(WaitlistEntryStatus value) => OnPropertyChanged(nameof(StatusForeground));
}
