using System;
using System.IO;

namespace DiffusionNexus.Tests.Helpers;

/// <summary>
/// Locates the repository root from the test binary's location, for tests that assert on files
/// in the repository rather than on behaviour - licence pins, XAML lints, csproj guards.
/// <para>
/// One copy: root detection was duplicated across five test files, so any change to it had to
/// land in five places and a drift between them would be invisible.
/// </para>
/// </summary>
public static class RepoRoot
{
    private static readonly Lazy<string> Located = new(Locate);

    /// <summary>The repository root directory.</summary>
    public static string Path => Located.Value;

    /// <summary>Combines <see cref="Path"/> with the given repo-relative segments.</summary>
    public static string Combine(params string[] segments)
        => System.IO.Path.Combine(Path, System.IO.Path.Combine(segments));

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            // The UI csproj is the marker rather than .git, so the tests still work from a
            // source export or a worktree that has no .git directory of its own.
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "DiffusionNexus.UI", "DiffusionNexus.UI.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root above " + AppContext.BaseDirectory);
    }
}
