using System.Net;

namespace DiffusionNexus.Civitai;

/// <summary>
/// Told, as soon as it happens, that Civitai answered 429 — including a 429 the client's own
/// retry then recovers from.
/// </summary>
/// <remarks>
/// The response is only visible inside <see cref="CivitaiClient"/>, and by the time an exception
/// reaches a caller the limit may already have been in force for a minute. Every other surface
/// wants to know at the moment of the first refusal, not at the end of one caller's retries.
/// </remarks>
public interface ICivitaiRateLimitObserver
{
    /// <param name="retryAfter">The parsed Retry-After, or null when the response carried none.</param>
    /// <param name="isRetryOfReportedCall">
    /// True when this report is <see cref="CivitaiClient"/>'s own in-call retry re-hitting the
    /// limit — i.e. an earlier report already told the observer about a 429 from the SAME logical
    /// call. False (the default) for a call's first 429. See
    /// <see cref="CivitaiRateLimitCooldown.OnRateLimited"/> for why this matters: a purely
    /// time-based "is this still the same episode" check cannot tell the difference between a
    /// caller's own retry landing at (or after) its own cooldown deadline and a genuinely new
    /// rate-limit episode starting at that same moment.
    /// </param>
    void OnRateLimited(TimeSpan? retryAfter, bool isRetryOfReportedCall = false);
}

/// <summary>
/// A 429 the client gave up on. Derives from <see cref="HttpRequestException"/> with
/// <see cref="HttpRequestException.StatusCode"/> set to 429, so the existing
/// <c>catch (HttpRequestException ex) when (ex.StatusCode == TooManyRequests)</c> handlers
/// across the app keep working; it merely adds the wait the server asked for.
/// </summary>
public sealed class CivitaiRateLimitedException : HttpRequestException
{
    public CivitaiRateLimitedException(string message, TimeSpan? retryAfter)
        : base(message, null, HttpStatusCode.TooManyRequests)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>How long Civitai asked us to wait, when it said.</summary>
    public TimeSpan? RetryAfter { get; }
}
