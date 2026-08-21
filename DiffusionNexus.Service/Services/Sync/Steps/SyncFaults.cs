using System.Net;
using System.Text.Json;
using DiffusionNexus.DataAccess.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.Service.Services.Sync.Steps;

/// <summary>
/// Which exceptions a sync step answers for itself, shared by all of them so the three catch
/// filters cannot drift apart.
/// </summary>
internal static class SyncFaults
{
    /// <summary>
    /// A fault of one item — recorded against that item, after which the run carries on. Anything
    /// else escapes to <see cref="LibrarySyncService"/>, which treats it as a bug and stops.
    /// </summary>
    /// <remarks>
    /// Transport and payload faults (network, disk, HttpClient's own timeout, a changed response
    /// shape) were always here. The database faults were not, and that was wrong in the most
    /// expensive way available: one model whose graph the database refuses — a creator renamed into
    /// a taken username, a tag list Civitai spells two ways — aborted the entire run, so every
    /// model queued behind it went unchecked and the user saw a sync that "stopped working".
    /// <para>
    /// A rejected save reaches us as <see cref="DatabaseOperationException"/> /
    /// <see cref="ConcurrencyConflictException"/> (the unit of work translates EF's
    /// <see cref="DbUpdateException"/>, which is still listed for any path that does not), and the
    /// change tracker reports its own conflicts as a plain <see cref="InvalidOperationException"/>
    /// with no distinguishing type. Including that last one is deliberate and does cost something:
    /// a genuine programming error of the same type now surfaces as one failed item rather than a
    /// stopped run. It is not swallowed — every caller logs a warning with the message and the full
    /// exception at debug level.
    /// </para>
    /// </remarks>
    public static bool IsItemFault(Exception ex) => ex
        is HttpRequestException
        or IOException
        or TaskCanceledException
        or JsonException
        or DbUpdateException
        or DatabaseOperationException
        or ConcurrencyConflictException
        or InvalidOperationException;

    /// <summary>
    /// Whether Civitai has refused this id for good. <c>GetAsync</c> returns null on 404 and throws
    /// with the status attached for everything else in the 4xx range — 401/403 on early-access and
    /// restricted pages, above all — and that is an answer, not a hiccup: it will be the same answer
    /// tomorrow. 429 is the exception, being the server asking us to come back later, and 5xx and a
    /// bare connection failure (no status at all) are not answers about the resource.
    /// </summary>
    public static bool IsFinalRefusal(HttpRequestException ex)
        => ex.StatusCode is { } status
           && (int)status is >= 400 and < 500
           && status != HttpStatusCode.TooManyRequests;

    /// <summary>The status as the user sees it in a log line: <c>403</c>.</summary>
    public static int RefusalCode(HttpRequestException ex) => (int)(ex.StatusCode ?? HttpStatusCode.BadRequest);
}
