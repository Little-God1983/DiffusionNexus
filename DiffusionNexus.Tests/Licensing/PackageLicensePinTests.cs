using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Licensing;

/// <summary>
/// Guards the licence pins that a routine "update the packages" pass would silently undo.
/// </summary>
public class PackageLicensePinTests
{
    /// <summary>
    /// FluentAssertions 8.0 relicensed from Apache-2.0 to the Xceed Community licence: free for
    /// open-source and non-commercial use, PAID for commercial use with no revenue threshold.
    /// Being a test-only dependency is not an exemption - it is a use licence, not a distribution
    /// licence, so running the suite commercially is the licensed act.
    /// <para>
    /// This drifted once already: DiffusionNexus.Tests was pinned to 7.2.2 with that rationale in
    /// a comment while DiffusionNexus.IntegrationTests sat on 8.10.0, and nothing noticed. A
    /// comment in one csproj is not a control; this is.
    /// </para>
    /// </summary>
    [Fact]
    public void WhenAnyProjectReferencesFluentAssertionsThenItIsBelowVersion8()
    {
        var offenders = new List<string>();

        foreach (var project in EnumerateProjectFiles())
        {
            var text = File.ReadAllText(project);

            // Two steps rather than one pattern, so attribute order cannot hide a reference:
            // find the element, then read its Version out of it wherever that sits.
            foreach (Match element in Regex.Matches(
                         text,
                         "<PackageReference\\b[^>]*Include=\"FluentAssertions\"[^>]*>",
                         RegexOptions.IgnoreCase))
            {
                var version = Regex.Match(element.Value, "Version=\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (!version.Success)
                    continue;

                var majorText = version.Groups[1].Value.Split('.', '-', '*')[0];
                if (int.TryParse(majorText, out var major) && major >= 8)
                    offenders.Add($"{Path.GetFileName(project)} -> FluentAssertions {version.Groups[1].Value}");
            }
        }

        offenders.Should().BeEmpty(
            "FluentAssertions 8.x is the Xceed licence - paid for commercial use with no revenue " +
            "threshold - and 7.2.2 is the last Apache-2.0 release. Do not bump without buying a licence.");
    }

    private static IEnumerable<string> EnumerateProjectFiles()
    {
        var root = FindRepoRoot();
        var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !ContainsBuildOutputDirectory(p, root))
            .ToList();

        projects.Should().NotBeEmpty("the scan is worthless if it finds no projects to check");
        return projects;
    }

    private static bool ContainsBuildOutputDirectory(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DiffusionNexus.UI", "DiffusionNexus.UI.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root above " + AppContext.BaseDirectory);
    }
}
