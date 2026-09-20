using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiffusionNexus.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Licensing;

/// <summary>
/// VideoLAN.LibVLC.Windows declares LGPL-2.1-or-later. That is true of libvlc and libvlccore, and
/// false of several files the package carries: a package's declared licence covers the package,
/// not its payload - the same trap as Avalonia.Fonts.Inter shipping OFL fonts under an MIT wrapper.
/// <para>
/// This scans everything that actually ships and requires every file carrying GPL evidence to be
/// classified in Scripts/license-data/vlc-payload-audit.json. Three earlier shapes of this test
/// were inert by construction, which is why it is built the way it is: reading the "attributed"
/// set out of notice prose matched the EXCLUDED plugins too; modelling the shipped set by parsing
/// the csproj read the same declaration the build reads, and so could not see the build ignoring
/// it; and scanning only <c>plugins/**/*.dll</c> missed the GPL <c>lua/**</c> tree entirely.
/// </para>
/// <para>
/// TODO: Linux Implementation - the audited payload is the win-x64 one, because
/// VideoLAN.LibVLC.Windows is Windows-only and its PackageReference is conditioned accordingly.
/// A Linux build would use VideoLAN.LibVLC.Linux, whose payload needs its own audit file; the
/// shipped-file model below is the part that would be reused.
/// </para>
/// </summary>
public class VlcPayloadLicenseTests
{
    /// <summary>
    /// Scanning the payload reads ~100 MB across 525 files, so it runs once per test session
    /// rather than once per assertion.
    /// </summary>
    private static readonly Lazy<PayloadScan> Scan = new(PayloadScan.Run, isThreadSafe: true);

    private static readonly Lazy<JsonDocument> AuditDocument = new(() =>
        JsonDocument.Parse(File.ReadAllText(
            RepoRoot.Combine("Scripts", "license-data", "vlc-payload-audit.json"))), isThreadSafe: true);

    private static JsonElement Audit => AuditDocument.Value.RootElement;

    [Fact]
    public void WhenAShippedFileCarriesGplEvidenceThenItIsClassifiedInTheAudit()
    {
        var scan = RequirePayload();
        if (scan is null) return;

        scan.GplEvidence.Should().NotBeEmpty(
            "the scan is worthless if it finds nothing - libavcodec declares itself GPL and must always be found");

        var classified = AuditedFiles("shippedGpl")
            .Concat(AuditedFiles("notLicenceDeclarations"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unclassified = scan.GplEvidence.Keys
            .Where(f => scan.Shipped.Contains(f))
            .Where(f => !classified.Contains(f))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        unclassified.Should().BeEmpty(
            "every shipped file carrying GPL evidence must be attributed as GPL or recorded as a "
            + "false positive with its reason in Scripts/license-data/vlc-payload-audit.json");
    }

    [Fact]
    public void WhenAFileIsAttributedAsGplThenItStillShipsAndIsStillGpl()
    {
        var scan = RequirePayload();
        if (scan is null) return;

        // A stale attribution claim misleads as much as a missing one, and an entry for a file
        // that no longer ships quietly inflates what we appear to have audited.
        foreach (var file in AuditedFiles("shippedGpl"))
        {
            scan.Shipped.Should().Contain(file,
                $"'{file}' is attributed as shipped GPL but the build no longer ships it");
            scan.GplEvidence.Should().ContainKey(file,
                $"'{file}' is attributed as GPL but the binary no longer shows GPL evidence");
        }
    }

    [Fact]
    public void WhenAFileIsRecordedAsExcludedThenItStillExistsAndIsStillGpl()
    {
        var scan = RequirePayload();
        if (scan is null) return;

        // Without this the exclusion test below goes vacuous exactly when it matters. A plugin
        // that moves out of plugins/codec/ in a VLC bump stops matching the path-based csproj
        // exclusion AND stops matching this audit entry - so it would silently start shipping
        // while both tests stayed green.
        foreach (var pattern in AuditedFiles("excludedGpl"))
        {
            var matches = scan.PackageFiles.Where(f => MatchesAuditPattern(pattern, f)).ToList();

            matches.Should().NotBeEmpty(
                $"'{pattern}' is recorded as excluded GPL but nothing in the package matches it "
                + "any more - the exclusion may have stopped matching too, which is the case this "
                + "audit exists to catch");

            matches.Should().Contain(f => scan.GplEvidence.ContainsKey(f),
                $"'{pattern}' is recorded as GPL but no file it matches shows GPL evidence");
        }
    }

    [Fact]
    public void WhenAFileIsRecordedAsExcludedThenTheBuildActuallyExcludesIt()
    {
        var scan = RequirePayload();
        if (scan is null) return;

        foreach (var pattern in AuditedFiles("excludedGpl"))
        {
            scan.Shipped.Should().NotContain(
                f => MatchesAuditPattern(pattern, f),
                $"'{pattern}' is recorded as excluded GPL but the build still ships it");
        }
    }

    [Fact]
    public void WhenAGplHitIsRecordedAsAFalsePositiveThenThatClaimIsReVerified()
    {
        var scan = RequirePayload();
        if (scan is null) return;

        // An allowlist entry that has stopped being true must throw rather than wave the file
        // through: the file could have gained a real licence declaration since it was recorded.
        foreach (var entry in AuditSection("notLicenceDeclarations"))
        {
            var file = entry.GetProperty("file").GetString()!;
            var matched = entry.GetProperty("matched").GetString()!;
            var expected = entry.GetProperty("occurrences").GetInt32();

            scan.GplEvidence.Should().ContainKey(file,
                $"'{file}' is recorded as a GPL false positive but no longer matches at all - remove the entry");

            var bytes = File.ReadAllBytes(Path.Combine(scan.Root!, file.Replace('/', Path.DirectorySeparatorChar)));

            // Asserted as a bool: handing the binary to Should().Contain() renders the whole
            // file into the failure message.
            PayloadScan.Contains(bytes, matched).Should().BeTrue(
                $"the recorded justification for '{file}' quotes a string that is no longer in the file");
            PayloadScan.CountBareGpl(bytes).Should().Be(expected,
                $"'{file}' has a different number of non-Lesser GPL mentions than when it was audited; "
                + "re-read it before trusting the recorded reason");
        }
    }

    [Fact]
    public void WhenTheAuditNamesShippedGplThenTheNoticesNameTheSameFiles()
    {
        if (RequirePayload() is null) return;

        // The notices are the document a reader actually gets. If the audit and the shipped
        // notice disagree about which binaries the GPL text covers, one of them is lying.
        var notices = File.ReadAllText(RepoRoot.Combine("THIRD-PARTY-NOTICES.txt"));

        foreach (var file in AuditedFiles("shippedGpl"))
        {
            notices.Should().Contain(Path.GetFileName(file),
                $"'{file}' is attributed as shipped GPL in the audit but is not named in THIRD-PARTY-NOTICES.txt");
        }
    }

    [Fact]
    public void WhenTheNoticesPointAtASourceFileThenThatFileExists()
    {
        // The notices named DiffusionNexus.Tests/Licensing/VlcPluginLicenseTests.cs after the
        // class was renamed, so the shipped legal document - embedded in the exe - pointed at a
        // file that did not exist. Exactly the drift this PR exists to remove, in the one
        // document whose whole job is being true.
        var notices = File.ReadAllText(RepoRoot.Combine("THIRD-PARTY-NOTICES.txt"));

        // Anchored on this repository's own top-level directories. An unanchored path pattern
        // also matches the tails of upstream URLs quoted in the reproduced notices - for example
        // a github.com/SixLabors/... link to Crc32.cs - which are not ours and are not supposed
        // to exist here.
        var referenced = Regex.Matches(
                notices,
                @"(?<![\w/.-])(?:Scripts|DiffusionNexus\.[A-Za-z]+)/[A-Za-z0-9_./-]+\.(?:cs|ps1|json|csproj)")
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        referenced.Should().NotBeEmpty(
            "the notices cite the guards that keep them honest; if none is cited the citation "
            + "was dropped rather than this check becoming unnecessary");

        foreach (var path in referenced)
        {
            File.Exists(RepoRoot.Combine(path.Replace('/', Path.DirectorySeparatorChar)))
                .Should().BeTrue($"the notices point readers at '{path}', which does not exist in the repository");
        }
    }

    /// <summary>
    /// Returns the scanned payload, or null on a non-Windows host where the Windows-only
    /// PackageReference is conditioned off and there is genuinely nothing to audit. On Windows a
    /// missing payload is a hard failure rather than a skip: silently passing when the thing
    /// under audit is absent is how a licence guard becomes decoration.
    /// </summary>
    private static PayloadScan? RequirePayload()
    {
        var scan = Scan.Value;

        if (scan.Root is null)
        {
            OperatingSystem.IsWindows().Should().BeFalse(
                "VideoLAN.LibVLC.Windows ships in the Windows build, so on Windows its payload "
                + "must be restored and audited - run dotnet restore DiffusionNexus.UI -r win-x64");
            return null;
        }

        scan.Shipped.Should().NotBeEmpty(
            "the libvlc payload copied next to the test binary is what this audit reads; without "
            + "it nothing is being checked");

        return scan;
    }

    private static IEnumerable<JsonElement> AuditSection(string name)
        => Audit.TryGetProperty(name, out var section) ? section.EnumerateArray() : [];

    private static IEnumerable<string> AuditedFiles(string section)
        => AuditSection(section).Select(e => e.GetProperty("file").GetString()!);

    /// <summary>Audit entries may name a directory tree ("lua/**") as well as a single file.</summary>
    private static bool MatchesAuditPattern(string pattern, string file)
        => pattern.EndsWith("/**", StringComparison.Ordinal)
            ? file.StartsWith(pattern[..^2], StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, file, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The shipped set is read from what MSBuild actually copied - the libvlc payload beside the
    /// test binary, which arrives through the ProjectReference to DiffusionNexus.UI - rather than
    /// from the exclusion items declared in the csproj. A guard that reads the same declaration
    /// the build reads cannot see the build ignoring it, which is how an earlier version passed
    /// while `lua\**` (a literal ** that MSBuild expanded to nothing) excluded no files at all.
    /// </summary>
    private sealed class PayloadScan
    {
        public string? Root { get; private init; }
        public IReadOnlySet<string> Shipped { get; private init; } = new HashSet<string>();
        public IReadOnlySet<string> PackageFiles { get; private init; } = new HashSet<string>();
        public IReadOnlyDictionary<string, int> GplEvidence { get; private init; } = new Dictionary<string, int>();

        public static PayloadScan Run()
        {
            var root = VlcPackage.PayloadRoot;
            if (root is null)
                return new PayloadScan();

            var gpl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = Normalize(Path.GetRelativePath(root, path));
                all.Add(rel);

                // Every file is scanned whatever its extension: a *.dll filter is how the GPL
                // lua/** tree and the libbluray .jar files went unexamined the first time.
                // Bytes rather than a decoded string - decoding the 103 MB payload to UTF-16
                // allocated ~310 MB, almost all of it on the large object heap, for what are
                // three ASCII substring searches.
                var bytes = File.ReadAllBytes(path);
                var bare = CountBareGpl(bytes);
                if (bare > 0 || HasGplBuildStamp(bytes))
                    gpl[rel] = bare;
            }

            return new PayloadScan
            {
                Root = root,
                Shipped = ReadShippedPayload(),
                PackageFiles = all,
                GplEvidence = gpl,
            };
        }

        private static IReadOnlySet<string> ReadShippedPayload()
        {
            var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dir = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
            if (!Directory.Exists(dir))
                return shipped;

            foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                shipped.Add(Normalize(Path.GetRelativePath(dir, path)));

            return shipped;
        }

        private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

        public static bool Contains(ReadOnlySpan<byte> haystack, string needle)
            => IndexOf(haystack, needle, 0) >= 0;

        private static int IndexOf(ReadOnlySpan<byte> haystack, string needle, int start)
        {
            var pattern = Encoding.ASCII.GetBytes(needle);
            if (start >= haystack.Length)
                return -1;

            var found = haystack[start..].IndexOf(pattern);
            return found < 0 ? -1 : found + start;
        }

        /// <summary>
        /// Counts occurrences of "General Public License" that are not part of "Lesser General
        /// Public License", decided per occurrence.
        /// <para>
        /// This used to subtract whole-file totals, which is wrong in a way that hides real
        /// findings rather than inventing them: a file with one genuine GPL header and one
        /// incidental mention of the Lesser GPL nets to zero and is never flagged. VLC's own LGPL
        /// boilerplate contains the shorter phrase as a substring, which is why some form of
        /// exclusion is needed at all - a naive scan reports all 323 plugins as GPL.
        /// </para>
        /// </summary>
        public static int CountBareGpl(ReadOnlySpan<byte> bytes)
        {
            const string Phrase = "General Public License";
            const string Prefix = "Lesser ";

            var count = 0;
            var at = 0;

            while ((at = IndexOf(bytes, Phrase, at)) >= 0)
            {
                var precededByLesser = at >= Prefix.Length
                    && bytes.Slice(at - Prefix.Length, Prefix.Length)
                            .SequenceEqual(Encoding.ASCII.GetBytes(Prefix));

                if (!precededByLesser)
                    count++;

                at += Phrase.Length;
            }

            return count;
        }

        /// <summary>
        /// The short-form declarations, which the phrase count above cannot see: libavcodec and
        /// libswscale never spell out "General Public License", carrying FFmpeg's configure stamp
        /// and a "libavcodec license: GPL version 2 or later" string instead. Dropping this signal
        /// once made the two most important binaries in the payload scan clean.
        /// <para>
        /// Each GPL form is checked not to be preceded by an L, because "GPL version 2" and
        /// "GPLv2" are substrings of "LGPL version 2" and "LGPLv2" - so the unguarded forms match
        /// LGPL boilerplate and would classify LGPL files as GPL on the next VLC bump.
        /// </para>
        /// </summary>
        public static bool HasGplBuildStamp(ReadOnlySpan<byte> bytes)
        {
            if (Contains(bytes, "--enable-gpl") || Contains(bytes, "license: GPL version"))
                return true;

            return ContainsNotPrecededByL(bytes, "GPL version 2")
                || ContainsNotPrecededByL(bytes, "GPLv2");
        }

        private static bool ContainsNotPrecededByL(ReadOnlySpan<byte> bytes, string needle)
        {
            var at = 0;
            while ((at = IndexOf(bytes, needle, at)) >= 0)
            {
                if (at == 0 || (bytes[at - 1] != (byte)'L' && bytes[at - 1] != (byte)'l'))
                    return true;
                at += needle.Length;
            }

            return false;
        }
    }
}
