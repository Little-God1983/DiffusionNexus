namespace DiffusionNexus.Service.Services;

/// <summary>
/// Result of running an external process to completion: the raw exit code plus the
/// fully captured stdout/stderr streams. Callers own the success semantics — the
/// update services treat exit code 0 as success and fall back to stderr for the
/// error message, so both streams are returned verbatim rather than pre-judged.
/// </summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="StandardOutput">Everything written to stdout (untrimmed, line endings preserved).</param>
/// <param name="StandardError">Everything written to stderr (untrimmed, line endings preserved).</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Seam over external process execution (git / pip / uv) so the backend update
/// services (<see cref="ComfyUIUpdateService"/>, <see cref="AIToolkitUpdateService"/>)
/// can be unit-tested without launching real processes. The default implementation
/// (<c>DefaultProcessRunner</c>) preserves the exact behaviour the services relied on
/// before this seam existed:
/// <list type="bullet">
///   <item>Live per-line streaming of stdout <b>and</b> stderr via
///     <paramref name="onOutputLine"/> as output arrives — git and pip both write
///     progress to stderr, so long-running pulls don't look frozen.</item>
///   <item>Whole process-<b>tree</b> kill when <paramref name="ct"/> fires before exit,
///     so an aborted git/pip run can't leave child builds alive mid-operation and
///     corrupt the user's installation. After killing, the implementation surfaces the
///     cancellation as an <see cref="OperationCanceledException"/>.</item>
/// </list>
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Starts <paramref name="fileName"/> with <paramref name="arguments"/> in
    /// <paramref name="workingDirectory"/>, streaming each stdout/stderr line to
    /// <paramref name="onOutputLine"/> as it arrives, and returns the exit code plus the
    /// fully captured streams once the process exits.
    /// </summary>
    /// <param name="fileName">Executable to launch (e.g. <c>git</c> or a python path).</param>
    /// <param name="arguments">Command-line arguments passed verbatim.</param>
    /// <param name="workingDirectory">Working directory for the process.</param>
    /// <param name="environment">
    /// Optional environment overrides applied on top of the inherited environment:
    /// a non-null value <b>sets</b> the variable, a <c>null</c> value <b>removes</b> it
    /// (used by the AI-Toolkit isolation profile to clear ambient Python config).
    /// </param>
    /// <param name="onOutputLine">
    /// Optional callback invoked once per non-null stdout/stderr line, in arrival order.
    /// Callers add any log prefix themselves. May be invoked on a thread-pool thread.
    /// </param>
    /// <param name="ct">
    /// Cancellation token. When it fires before the process exits, implementations MUST
    /// kill the entire process tree and then throw <see cref="OperationCanceledException"/>.
    /// </param>
    // TODO: Linux Implementation - process-tree kill semantics differ on Linux; the
    // default implementation relies on Process.Kill(entireProcessTree: true), which is
    // supported cross-platform but sends SIGKILL rather than a graceful signal.
    Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default);
}
