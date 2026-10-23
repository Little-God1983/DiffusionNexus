namespace DiffusionNexus.Civitai.Models;

/// <summary>
/// Single source of truth for "is this version early-access gated right now?".
/// Civitai has signaled EA three different ways over time and payloads in the
/// wild (API responses, cached sidecar files) carry any of them:
/// <list type="number">
/// <item><c>earlyAccessTimeFrame</c> — legacy day counter, absent from current responses.</item>
/// <item><c>availability: "EarlyAccess"</c> — replaced the counter, but current
/// responses say "Public" even for gated versions.</item>
/// <item><c>earlyAccessDeadline</c> + <c>paidAccess {permanent, endsAt}</c> — the
/// current signal (live-verified 2026-08-12).</item>
/// </list>
/// Deciding from any single field silently breaks when Civitai migrates again —
/// every consumer must call this instead.
/// </summary>
public static class CivitaiEarlyAccessExtensions
{
    /// <summary>
    /// True when the version is gated at <paramref name="utcNow"/> (defaults to
    /// the current time). Time-aware: an expired deadline reads as free, since
    /// early access lapses into a normal public download. A <c>paidAccess</c>
    /// object with neither <c>permanent</c> nor an end date is treated as gated —
    /// better a spurious confirmation prompt than a silent 401.
    /// </summary>
    public static bool IsEarlyAccessActive(this CivitaiModelVersion? version, DateTimeOffset? utcNow = null)
    {
        if (version is null) return false;

        var now = utcNow ?? DateTimeOffset.UtcNow;

        if (version.EarlyAccessTimeFrame > 0) return true;
        if (string.Equals(version.Availability, "EarlyAccess", StringComparison.OrdinalIgnoreCase)) return true;
        if (version.EarlyAccessDeadline is { } deadline && deadline > now) return true;

        return version.PaidAccess is { } paid
            && (paid.Permanent == true || paid.EndsAt is null || paid.EndsAt > now);
    }

    /// <summary>
    /// True when the version is paywalled forever (<c>paidAccess.permanent</c>) —
    /// it will never lapse into a free download, so waiting for it is pointless.
    /// Companion to <see cref="IsEarlyAccessActive"/>: that answers "gated right
    /// now?", this answers "gated forever?". Keep both here so no consumer reads
    /// the raw fields.
    /// </summary>
    public static bool IsPermanentlyPaid(this CivitaiModelVersion? version)
        => version?.PaidAccess?.Permanent == true;
}
