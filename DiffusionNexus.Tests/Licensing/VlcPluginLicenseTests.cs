using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Licensing;

/// <summary>
/// VideoLAN.LibVLC.Windows declares LGPL-2.1-or-later, and that is true of libvlc itself - but it
/// ships 323 plugin binaries for win-x64 and a handful of those are GPL, not LGPL. A package's
/// declared licence covers the package, not everything it carries; the same trap as
/// Avalonia.Fonts.Inter shipping OFL fonts under an MIT wrapper.
/// <para>
/// The GPL set is derived here from the binaries rather than hardcoded, so a VLC version bump
/// that introduces a new GPL module fails this test instead of shipping unattributed. Evidence
/// is a "General Public License" string that is not "Lesser General Public License" (VLC's own
/// LGPL boilerplate contains the former as a substring, which is why a naive scan matches all
/// 323), or an FFmpeg --enable-gpl build stamp.
/// </para>
/// </summary>
public class VlcPluginLicenseTests
{
    [Fact]
    public void WhenAPluginIsGplThenItIsEitherExcludedFromTheBuildOrAttributedInTheNotices()
    {
        var gpl = ScanForGplPlugins();

        gpl.Should().NotBeEmpty(
            "the scan is worthless if it finds nothing - libavcodec is built --enable-gpl and must always be found");

        var accountedFor = ReadExcludedPluginFileNames()
            .Concat(ReadAttributedPluginFileNames())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        gpl.Except(accountedFor).Should().BeEmpty(
            "a GPL plugin must either be excluded from the shipped payload or named in the GPL "
            + "supplement in Scripts/license-data/supplements.json; anything else ships GPL "
            + "binaries with no attribution and no source offer");
    }

    [Fact]
    public void WhenAPluginIsAttributedAsGplThenItIsStillGpl()
    {
        var gpl = ScanForGplPlugins();

        // Guards the other direction: an attribution claim that has quietly stopped being true
        // is as misleading as a missing one, and nothing else would notice.
        ReadAttributedPluginFileNames().Except(gpl).Should().BeEmpty(
            "the GPL supplement names plugins that the binaries no longer show as GPL");
    }

    private static IReadOnlyCollection<string> ScanForGplPlugins()
    {
        var found = new List<string>();

        foreach (var dll in Directory.EnumerateFiles(FindPluginDirectory(), "*.dll", SearchOption.AllDirectories))
        {
            var text = Encoding.ASCII.GetString(File.ReadAllBytes(dll));

            var all = Regex.Matches(text, "General Public License").Count;
            var lesser = Regex.Matches(text, "Lesser General Public License").Count;
            var bareGpl = all - lesser;

            if (bareGpl > 0 || Regex.IsMatch(text, "--enable-gpl|GPL version 2|GPLv2"))
                found.Add(Path.GetFileName(dll));
        }

        return found;
    }

    private static string FindPluginDirectory()
    {
        var version = Regex.Match(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "DiffusionNexus.UI", "DiffusionNexus.UI.csproj")),
            "<PackageReference\\b[^>]*Include=\"VideoLAN\\.LibVLC\\.Windows\"[^>]*Version=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);

        version.Success.Should().BeTrue("the test must scan the version actually referenced, not a guess");

        var root = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        var plugins = Path.Combine(root, "videolan.libvlc.windows", version.Groups[1].Value, "build", "x64", "plugins");

        Directory.Exists(plugins).Should().BeTrue(
            $"the VLC plugin payload must be restored to be audited (looked in '{plugins}')");

        return plugins;
    }

    /// <summary>Plugin file names the csproj keeps out of the build output entirely.</summary>
    private static IEnumerable<string> ReadExcludedPluginFileNames()
        => Regex.Matches(
                File.ReadAllText(Path.Combine(FindRepoRoot(), "DiffusionNexus.UI", "DiffusionNexus.UI.csproj")),
                "<VlcWindowsX64ExcludeFiles\\b[^>]*Include=\"([^\"]+)\"",
                RegexOptions.IgnoreCase)
            .SelectMany(m => m.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Select(path => Path.GetFileName(path.Replace('\\', '/')));

    /// <summary>Plugin file names the notices attribute under the GPL supplement.</summary>
    private static IEnumerable<string> ReadAttributedPluginFileNames()
    {
        var json = File.ReadAllText(Path.Combine(FindRepoRoot(), "Scripts", "license-data", "supplements.json"));

        // Named in the supplement's prose so the notices state exactly which binaries the GPL
        // text applies to. Matching the file names keeps the document and this test in step.
        return Regex.Matches(json, @"lib[A-Za-z0-9_]+_plugin\.dll")
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase);
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
