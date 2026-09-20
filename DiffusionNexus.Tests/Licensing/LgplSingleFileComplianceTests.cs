using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DiffusionNexus.Tests.Helpers;
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
    public void WhenAnAssemblyIsListedForExclusionThenItsNameMatchesARealAssemblyExactly()
    {
        // The MSBuild target matches with System.String.Contains, which is ORDINAL: a listed name
        // that differs from the real file even in case silently matches nothing, the assembly is
        // welded in unreplaceable, and a case-insensitive test would still pass. These assemblies
        // reach the test output through the ProjectReference to DiffusionNexus.UI, so the file
        // system itself is the check - and the comparison here is ordinal to match MSBuild.
        var present = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in ReadRelinkableAssemblyNames())
        {
            present.Should().Contain(name,
                $"'{name}' is listed for exclusion but no assembly of exactly that name is in the "
                + "build output; MSBuild's ordinal match would silently skip it");
        }
    }

    [Fact]
    public void WhenTheVlcPayloadIsConfiguredThenOnlyTheAuditedArchitectureIsEnabled()
    {
        // THIRD-PARTY-NOTICES.txt states the win-x86 and win-arm64 payloads are excluded "in
        // their entirety", and the payload audit only covers x64. If a later edit re-enables one,
        // ~178 MB of unaudited binaries carrying the same GPL modules ship against that written
        // claim, and nothing else would catch it - the payload audit reads build/x64 only.
        var csproj = File.ReadAllText(RepoRoot.Combine("DiffusionNexus.UI", "DiffusionNexus.UI.csproj"));

        foreach (var (property, expected) in new[]
                 {
                     ("VlcWindowsX64Enabled", "true"),
                     ("VlcWindowsX86Enabled", "false"),
                     ("VlcWindowsArm64Enabled", "false"),
                 })
        {
            var match = Regex.Match(csproj, $"<{property}>([^<]*)</{property}>", RegexOptions.IgnoreCase);

            match.Success.Should().BeTrue(
                $"{property} must be pinned explicitly; left implicit it follows $(Platform) and can flip");
            match.Groups[1].Value.Trim().Should().Be(expected,
                $"{property} is what decides whether that architecture's payload ships and is audited");
        }
    }

    [Fact]
    public void WhenAnLgplComponentShipsThenItIsEitherExcludedFromTheBundleOrShipsLoose()
    {
        var declared = ReadRelinkableNoticesIds();

        // Derived from the notices rather than hardcoded, so a newly introduced LGPL dependency
        // fails here instead of shipping welded into the exe. A licence pin cannot catch this:
        // the obligation arrives with the package, not with a version bump.
        var unaccounted = ThirdPartyNotices.Load()
            .Where(c => c.License.Contains("LGPL", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Id)
            .Where(id => !declared.Contains(id) && !ShipsLooseAsContent.ContainsKey(id))
            .ToList();

        unaccounted.Should().BeEmpty(
            "an LGPL component must either be excluded from the single-file bundle or ship as "
            + "loose native content; anything else is unreplaceable and breaks LGPL s6");
    }

    private static IReadOnlyCollection<string> ReadRelinkableAssemblyNames()
        => ReadItemIncludes("LgplRelinkableAssembly");

    /// <summary>
    /// The notices component ids for the relinkable assemblies: the NoticesId metadata where it
    /// is given, otherwise the Include. NuGet package ids and assembly file names are not the
    /// same namespace and need not agree, so conflating them would eventually make some LGPL
    /// package impossible to declare - no single string could satisfy both the file-exists check
    /// and the notices check, and the pressure would be to weaken one of them.
    /// </summary>
    private static IReadOnlyCollection<string> ReadRelinkableNoticesIds()
    {
        var text = File.ReadAllText(RepoRoot.Combine("DiffusionNexus.UI", "DiffusionNexus.UI.csproj"));

        return Regex.Matches(text, @"<LgplRelinkableAssembly\b([^>]*?)/?>", RegexOptions.IgnoreCase)
            .Select(m =>
            {
                var attributes = m.Groups[1].Value;
                var notices = Regex.Match(attributes, "NoticesId=\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (notices.Success) return notices.Groups[1].Value;
                return Regex.Match(attributes, "Include=\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value;
            })
            .Where(v => !string.IsNullOrEmpty(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyCollection<string> ReadItemIncludes(string itemName)
    {
        var text = File.ReadAllText(RepoRoot.Combine("DiffusionNexus.UI", "DiffusionNexus.UI.csproj"));

        return Regex.Matches(text, $"<{itemName}\\b[^>]*Include=\"([^\"]+)\"", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

}
