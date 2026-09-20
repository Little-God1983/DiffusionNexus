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
/// classified in Scripts/license-data/vlc-payload-audit.json. Two earlier shapes of this test were
/// inert by construction and are worth not repeating: scanning only <c>plugins/**/*.dll</c> missed
/// the GPL-licensed <c>lua/**</c> tree and the .jar files entirely, and reading the "attributed"
/// set out of the notice prose matched the names of the EXCLUDED plugins too, so deleting every
/// exclusion left the test green.
/// </para>
/// <para>
/// TODO: Linux Implementation - the audited payload is the win-x64 one, because
/// VideoLAN.LibVLC.Windows is Windows-only and its PackageReference is conditioned accordingly.
/// A Linux build would use VideoLAN.LibVLC.Linux, whose payload needs its own audit file; the
/// shipped-file model below is the part that would be reused. These tests skip rather than fail
/// where the Windows package is not restored.
/// </para>
/// </summary>
public class VlcPayloadLicenseTests
{
    /// <summary>
    /// Scanning the payload reads ~100 MB across 525 files, so it runs once per test session
    /// rather than once per assertion.
    /// </summary>
    private static readonly Lazy<PayloadScan> Scan = new(PayloadScan.Run, isThreadSafe: true);

    private static readonly Lazy<JsonElement> Audit = new(() =>
        JsonDocument.Parse(File.ReadAllText(
            RepoRoot.Combine("Scripts", "license-data", "vlc-payload-audit.json"))).RootElement);

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

        // Guards the reverse direction. A stale attribution claim misleads as much as a missing
        // one, and an entry for a file that no longer ships quietly inflates what we appear to
        // have audited.
        foreach (var file in AuditedFiles("shippedGpl"))
        {
            scan.Shipped.Should().Contain(file,
                $"'{file}' is attributed as shipped GPL but the build no longer ships it");
            scan.GplEvidence.Should().ContainKey(file,
                $"'{file}' is attributed as GPL but the binary no longer shows GPL evidence");
        }
    }

    [Fact]
    public void WhenAFileIsRecordedAsExcludedThenTheBuildActuallyExcludesIt()
    {
        var scan = RequirePayload();
        if (scan is null) return;

        // The audit says these carry GPL and are kept out of the product. Without this, the
        // claim and the csproj could drift apart - which is exactly how the previous version of
        // this test came to pass while the exclusions did nothing.
        foreach (var file in AuditedFiles("excludedGpl"))
        {
            scan.Shipped.Should().NotContain(
                f => MatchesAuditPattern(file, f),
                $"'{file}' is recorded as excluded GPL but the build still ships it");
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

            var text = PayloadScan.ReadAscii(Path.Combine(scan.Root, file.Replace('/', Path.DirectorySeparatorChar)));

            // Asserted as a bool: passing the binary itself to Should().Contain() renders the
            // whole file into the failure message.
            text.Contains(matched, StringComparison.Ordinal).Should().BeTrue(
                $"the recorded justification for '{file}' quotes a string that is no longer in the file");
            PayloadScan.CountBareGpl(text).Should().Be(expected,
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

    /// <summary>
    /// Returns the scanned payload, or null on a non-Windows host where the Windows-only
    /// PackageReference is conditioned off and there is genuinely nothing to audit. On Windows a
    /// missing payload is a hard failure rather than a skip: silently passing when the thing
    /// under audit is absent is how a licence guard becomes decoration.
    /// </summary>
    [Fact]
    public void WhenFFmpegDerivedBinariesShipThenTheNoticesDoNotDenyDistributingFFmpeg()
    {
        if (RequirePayload() is null) return;

        // The FFmpeg supplement predates the VLC audit and said flatly "DiffusionNexus does not
        // bundle FFmpeg" while libavcodec_plugin.dll shipped ~110 lines further down the same
        // document. Two statements about the same code, one of them false, in the file whose
        // entire job is being true.
        var notices = File.ReadAllText(RepoRoot.Combine("THIRD-PARTY-NOTICES.txt"));

        foreach (var denial in new[] { "does not bundle FFmpeg", "does not distribute FFmpeg" })
        {
            notices.Contains(denial, StringComparison.OrdinalIgnoreCase).Should().BeFalse(
                $"the notices say '{denial}', but FFmpeg's libavcodec and libswscale ship inside "
                + "the VLC plugins; say which FFmpeg is meant instead of denying it outright");
        }
    }

    private static PayloadScan? RequirePayload()
    {
        var scan = Scan.Value;

        if (scan.Root is null)
        {
            OperatingSystem.IsWindows().Should().BeFalse(
                "VideoLAN.LibVLC.Windows ships in the Windows build, so on Windows its payload "
                + "must be restored and audited - run dotnet restore DiffusionNexus.UI");
            return null;
        }

        // A guard that silently passes when its subject is absent is not a guard. The copied
        // payload is what tells us which files the build really ships, so its absence is a
        // failure rather than a reason to skip.
        scan.Shipped.Should().NotBeEmpty(
            "the libvlc payload copied next to the test binary is what this audit reads; without "
            + "it nothing is being checked");

        return scan;
    }

    private static IEnumerable<JsonElement> AuditSection(string name)
        => Audit.Value.TryGetProperty(name, out var section) ? section.EnumerateArray() : [];

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
    /// from the exclusion items declared in the csproj.
    /// <para>
    /// That distinction is the whole point. A first attempt modelled the exclusions by parsing
    /// their Include text, and passed while excluding nothing: the package's targets resolve
    /// their paths through [MSBuild]::Unescape, so an exclusion written with a literal ** is
    /// expanded early against the wrong directory and quietly becomes an empty item. A guard that
    /// reads the same declaration the build reads cannot see the build ignoring it. This reads
    /// the output.
    /// </para>
    /// </summary>
    private sealed class PayloadScan
    {
        /// <summary>The restored package payload, used to scan files that do NOT ship.</summary>
        public string Root { get; private init; } = null!;

        /// <summary>Paths, relative to the payload root, that MSBuild actually copied.</summary>
        public IReadOnlySet<string> Shipped { get; private init; } = null!;

        /// <summary>Files carrying GPL evidence, over the whole package, keyed by relative path.</summary>
        public IReadOnlyDictionary<string, int> GplEvidence { get; private init; } = null!;

        public static PayloadScan Run()
        {
            var csproj = File.ReadAllText(RepoRoot.Combine("DiffusionNexus.UI", "DiffusionNexus.UI.csproj"));

            var version = Regex.Match(
                csproj,
                "<PackageReference\\b[^>]*Include=\"VideoLAN\\.LibVLC\\.Windows\"[^>]*Version=\"([^\"]+)\"",
                RegexOptions.IgnoreCase);
            if (!version.Success)
                return new PayloadScan();

            var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
            var root = Path.Combine(packages, "videolan.libvlc.windows", version.Groups[1].Value, "build", "x64");
            if (!Directory.Exists(root))
                return new PayloadScan();

            var gpl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                // Every file is scanned whatever its extension: a *.dll filter is how the GPL
                // lua/** tree and the libbluray .jar files went unexamined the first time.
                var text = ReadAscii(path);
                var bare = CountBareGpl(text);
                if (bare > 0 || HasGplBuildStamp(text))
                    gpl[Normalize(Path.GetRelativePath(root, path))] = bare;
            }

            return new PayloadScan { Root = root, Shipped = ReadShippedPayload(), GplEvidence = gpl };
        }

        /// <summary>
        /// The copied payload next to the test binary. Empty when the app has not been built for
        /// Windows, which the callers turn into a hard failure on Windows and a skip elsewhere.
        /// TODO: Linux Implementation - the win-x64 segment is the Windows package's layout.
        /// </summary>
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

        public static string ReadAscii(string path) => Encoding.ASCII.GetString(File.ReadAllBytes(path));

        /// <summary>
        /// Counts "General Public License" mentions that are not "Lesser General Public License".
        /// The subtraction is the whole point: VLC's own LGPL boilerplate contains the shorter
        /// phrase as a substring, so a naive scan reports every one of the 323 plugins as GPL.
        /// </summary>
        public static int CountBareGpl(string text)
            => Regex.Matches(text, "General Public License").Count
               - Regex.Matches(text, "Lesser General Public License").Count;

        /// <summary>
        /// The short-form declarations, which the phrase count above cannot see. libavcodec and
        /// libswscale never spell out "General Public License": they carry FFmpeg's configure
        /// stamp and a "libavcodec license: GPL version 2 or later" string instead. Dropping this
        /// signal is how an earlier draft of this test scanned the two most important binaries in
        /// the payload and reported them clean.
        /// </summary>
        public static bool HasGplBuildStamp(string text)
            => Regex.IsMatch(text, @"--enable-gpl|license: GPL version|GPL version 2|GPLv2");
    }
}
