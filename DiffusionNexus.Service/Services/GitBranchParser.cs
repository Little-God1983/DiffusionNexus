namespace DiffusionNexus.Service.Services;

/// <summary>
/// Pure parsing helpers for the git output the backend update services inspect when
/// resolving which branch to track. Extracted so the detached-HEAD vs attached-branch
/// decision can be unit-tested directly against captured <c>git</c> stdout, without
/// launching git. Shared by <see cref="ComfyUIUpdateService"/> and
/// <see cref="AIToolkitUpdateService"/> — the branch-resolution logic was identical in
/// both.
/// </summary>
internal static class GitBranchParser
{
    /// <summary>
    /// Interprets the output of <c>git rev-parse --abbrev-ref HEAD</c>.
    /// Returns the branch name when the repo is on a named branch, or <c>null</c> when
    /// the command failed or the repo is in detached-HEAD state (git prints the literal
    /// <c>HEAD</c> in that case).
    /// </summary>
    public static string? ParseAttachedBranch(bool success, string abbrevRefOutput)
    {
        if (!success)
            return null;

        var branch = abbrevRefOutput.Trim();
        return branch.Length > 0 && branch != "HEAD" ? branch : null;
    }

    /// <summary>
    /// Interprets the output of <c>git symbolic-ref refs/remotes/origin/HEAD --short</c>
    /// (e.g. <c>origin/main</c>) into the bare branch name (<c>main</c>).
    /// Returns <c>null</c> when the command failed or produced no output.
    /// </summary>
    /// <remarks>
    /// Preserves the original behaviour of stripping <c>origin/</c> via a plain
    /// <see cref="string.Replace(string, string)"/> (all occurrences, not just the
    /// prefix). This is intentional parity with the pre-refactor implementation.
    /// </remarks>
    public static string? ParseSymbolicRefBranch(bool success, string symbolicRefOutput)
    {
        if (!success)
            return null;

        var trimmed = symbolicRefOutput.Trim();
        return trimmed.Length > 0 ? trimmed.Replace("origin/", "") : null;
    }
}
