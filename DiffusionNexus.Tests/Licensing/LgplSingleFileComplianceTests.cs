using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DiffusionNexus.UI.Services.Licensing;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Licensing;

/// <summary>
/// LGPL-2.1 section 6 lets a work use the library only if the recipient can relink it against a
/// modified version of that library. The app publishes with <c>PublishSingleFile=true</c>, which
/// welds every managed assembly into one executable - so an LGPL assembly left in the bundle is
/// one the recipient physically cannot replace, and the permission to use it lapses.
/// <para>
/// The native side falls into the same trap, which is easy to get wrong: MSBuild <c>Content</c>
/// lands loose in an ordinary build, so <c>VideoLAN.LibVLC.Windows</c> looks replaceable - but
/// <c>_ComputeFilesToBundle</c> takes every <c>ResolvedFileToPublish</c> regardless of where it
/// came from, so a single-file publish welds <c>libvlc.dll</c>, <c>libvlccore.dll</c> and the whole
/// plugin tree in as well. Verified by publishing: before this fix the output was one 866 MB
/// executable and nothing beside it.
/// </para>
/// </summary>
public class LgplSingleFileComplianceTests
{
    /// <summary>
    /// Maps an LGPL package whose payload is native to the published directory it is kept loose
    /// in. The directory must also appear in the csproj's <c>LgplRelinkableContent</c> list, so
    /// "this one ships loose" is enforced rather than merely asserted here - that assumption is
    /// exactly what was wrong before this fix.
    /// </summary>
    private static readonly Dictionary<string, string> ShipsLooseAsContent = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VideoLAN.LibVLC.Windows"] = "libvlc",
    };

    [Fact]
    public void WhenPublishedAsSingleFileThenTheNativeLgplPayloadIsExcludedFromTheBundle()
    {
        var content = ReadItemIncludes("LgplRelinkableContent");

        foreach (var (package, directory) in ShipsLooseAsContent)
        {
            content.Should().Contain(directory,
                $"{package} ships its LGPL payload as content under '{directory}', and content is "
                + "bundled like everything else unless excluded");
        }
    }

    [Fact]
    public void WhenPublishedAsSingleFileThenTheLgplManagedAssembliesAreExcludedFromTheBundle()
    {
        var excluded = ReadRelinkableAssemblyNames();

        excluded.Should().BeEquivalentTo(
            ["HPPH", "HPPH.SkiaSharp", "LibVLCSharp", "LibVLCSharp.Avalonia"],
            "every managed LGPL assembly must stay loose beside the executable so a recipient can "
            + "replace it; a bundled one cannot be replaced and LGPL s6 is not satisfied");
    }

    [Fact]
    public void WhenAnLgplComponentShipsThenItIsEitherExcludedFromTheBundleOrShipsLoose()
    {
        var excluded = ReadRelinkableAssemblyNames();

        // Derived from the notices rather than hardcoded, so a newly introduced LGPL dependency
        // fails here instead of shipping welded into the exe. A licence pin cannot catch this:
        // the obligation arrives with the package, not with a version bump.
        var unaccounted = ThirdPartyNotices.Load()
            .Where(c => c.License.Contains("LGPL", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Id)
            .Where(id => !excluded.Contains(id) && !ShipsLooseAsContent.ContainsKey(id))
            .ToList();

        unaccounted.Should().BeEmpty(
            "an LGPL component must either be excluded from the single-file bundle or ship as "
            + "loose native content; anything else is unreplaceable and breaks LGPL s6");
    }

    private static IReadOnlyCollection<string> ReadRelinkableAssemblyNames()
        => ReadItemIncludes("LgplRelinkableAssembly");

    private static IReadOnlyCollection<string> ReadItemIncludes(string itemName)
    {
        var csproj = Path.Combine(FindRepoRoot(), "DiffusionNexus.UI", "DiffusionNexus.UI.csproj");
        var text = File.ReadAllText(csproj);

        return Regex.Matches(text, $"<{itemName}\\b[^>]*Include=\"([^\"]+)\"", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
