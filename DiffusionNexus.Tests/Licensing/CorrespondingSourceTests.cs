using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiffusionNexus.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Licensing;

/// <summary>
/// The notices tell the reader that the corresponding source for the GPL components accompanies
/// the release, in <c>source/</c>. GPL-2.0 section 3 takes that as the whole discharge of the
/// obligation, which is why no written offer is made and no contact address is given - so if the
/// release ever stops carrying that folder, the notices are not merely stale, they are false.
/// <para>
/// These tests exist because the same failure has happened repeatedly in this area: a claim in
/// the notices that nothing checked, drifting away from what the build does. They check the claim
/// against the publish script and the manifest, not against another piece of prose.
/// </para>
/// <para>
/// What they deliberately do NOT check is whether the sources are reachable - that needs the
/// network, and a test that quietly passes offline would be worse than none. Reachability is
/// checked by <c>Scripts/Fetch-CorrespondingSource.ps1 -Check</c>, which CI runs on every pull
/// request so that mirror rot surfaces there rather than in the middle of a release.
/// </para>
/// </summary>
public class CorrespondingSourceTests
{
    private const string AccompanyClaim = "accompanies this distribution";

    private static readonly Lazy<JsonDocument> ManifestDocument = new(() =>
        JsonDocument.Parse(File.ReadAllText(
            RepoRoot.Combine("Scripts", "license-data", "corresponding-source.json"))), isThreadSafe: true);

    private static JsonElement Manifest => ManifestDocument.Value.RootElement;

    [Fact]
    public void WhenTheNoticesSaySourceAccompaniesTheReleaseThenThePublishScriptStagesIt()
    {
        var notices = File.ReadAllText(RepoRoot.Combine("THIRD-PARTY-NOTICES.txt"));
        if (!notices.Contains(AccompanyClaim, StringComparison.OrdinalIgnoreCase))
            return; // A different section 3 route was chosen.

        var publish = File.ReadAllText(RepoRoot.Combine("publish.ps1"));

        publish.Should().Contain("Fetch-CorrespondingSource.ps1",
            "the notices state that the corresponding source ships with the release, so the "
            + "publish must actually stage it - otherwise the shipped document is false");

        // Staging must also be able to fail the release. A best-effort copy would let a release
        // go out claiming to carry source it does not carry.
        publish.Should().MatchRegex(
            @"Fetch-CorrespondingSource\.ps1[^\n]*\r?\n\s*if \(\$LASTEXITCODE -ne 0\)",
            "a failure to stage the source must stop the publish, as a failure to generate the "
            + "notices already does");
    }

    [Fact]
    public void WhenTheNoticesSaySourceAccompaniesTheReleaseThenCiChecksItIsObtainable()
    {
        var notices = File.ReadAllText(RepoRoot.Combine("THIRD-PARTY-NOTICES.txt"));
        if (!notices.Contains(AccompanyClaim, StringComparison.OrdinalIgnoreCase))
            return;

        // Mirrors rot. Without this, the first sign would be a release that cannot be built -
        // and the -Check switch existed for a while with nothing invoking it at all.
        File.ReadAllText(RepoRoot.Combine(".github", "workflows", "dotnet.yml"))
            .Should().Contain("Fetch-CorrespondingSource.ps1 -Check",
                "the corresponding source must be proven obtainable on pull requests, not "
                + "discovered to be unobtainable during a release");
    }

    [Fact]
    public void WhenTheManifestIsReadThenEverySourceIsPinnedByItsOwnHash()
    {
        var components = Manifest.GetProperty("components").EnumerateArray().ToList();

        components.Should().NotBeEmpty("an empty manifest would stage nothing and pass silently");

        foreach (var c in components)
        {
            var name = c.GetProperty("name").GetString();

            c.GetProperty("describes").GetString().Should().NotBeNullOrWhiteSpace(
                $"{name} must say which shipped binary it is the source of, or the folder is a "
                + "pile of tarballs the recipient cannot map to anything");

            var sources = c.GetProperty("sources").EnumerateArray().ToList();
            sources.Should().NotBeEmpty($"{name} has no source to fetch");

            foreach (var s in sources)
            {
                // Per SOURCE, not per component: Debian's OpenJPEG is a .tar.xz repack of the
                // same tree GitHub serves as .tar.gz, so equally valid corresponding source has
                // different bytes depending on where it comes from.
                s.GetProperty("sha256").GetString().Should().MatchRegex("^[0-9a-f]{64}$",
                    $"{name} sources must each be pinned by hash - a 200 response is not evidence "
                    + "of the right bytes, and one listed host already answers with an HTML page");

                s.GetProperty("file").GetString().Should().NotBeNullOrWhiteSpace(
                    $"{name} sources must name the file they produce");
            }
        }
    }

    [Fact]
    public void WhenAComponentHasOnlyOneSourceThenTheRiskIsWrittenDown()
    {
        foreach (var c in Manifest.GetProperty("components").EnumerateArray())
        {
            var name = c.GetProperty("name").GetString();
            var sources = c.GetProperty("sources").EnumerateArray().ToList();

            // Counting entries is not the same as having a fallback, which is how an earlier
            // version of this test stayed green while two of the listed fallbacks were a 404 and
            // a 403. Distinct HOSTS is the thing that matters, and where there is genuinely only
            // one - FFmpeg's exact commit exists only on GitHub - the manifest must say so, so
            // nobody reads the array length as resilience it does not have.
            var hosts = sources
                .Select(s => new Uri(s.GetProperty("url").GetString()!).Host)
                .Select(h => h.Replace("codeload.", "", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            if (hosts < 2)
            {
                c.TryGetProperty("singleHostRisk", out var risk).Should().BeTrue(
                    $"{name} resolves to a single host, so the manifest must record what happens "
                    + "when that host changes the bytes or goes away");
                risk.GetString().Should().NotBeNullOrWhiteSpace();
            }
        }
    }

    [Fact]
    public void WhenTheManifestPinsAPackageVersionThenItIsTheVersionTheProjectReferences()
    {
        var pinned = Manifest.GetProperty("correspondsTo").GetProperty("packageVersion").GetString();

        VlcPackage.ReferencedVersion.Should().Be(pinned,
            "a VLC bump changes which source corresponds to the shipped binaries; the manifest "
            + "versions come from contrib/src/<name>/rules.mak inside the VLC tarball and must be "
            + "refreshed with it. The publish script fails on this too, but failing here means "
            + "finding out on the PR rather than at release time");
    }

    [Fact]
    public void WhenSourceAccompaniesTheReleaseThenOurOwnProseMakesNoWrittenOffer()
    {
        // Deliberately reads the authored supplement notes rather than the rendered document:
        // the reproduced GPL and LGPL texts describe all three of section 3's routes, so
        // scanning the whole file for offer wording matches the licence bodies themselves.
        using var supplements = JsonDocument.Parse(
            File.ReadAllText(RepoRoot.Combine("Scripts", "license-data", "supplements.json")));

        var authored = string.Join(
            "\n",
            supplements.RootElement.GetProperty("supplements").EnumerateArray()
                .Select(s => s.GetProperty("note").GetString()));

        if (!authored.Contains(AccompanyClaim, StringComparison.OrdinalIgnoreCase))
            return;

        // Matches a COMMITMENT rather than the words "written offer". The note explains what all
        // three of section 3's routes are before saying which one it takes, so forbidding the
        // phrase itself would either fire on that explanation or - as the first version of this
        // test did - be written around it so carefully that it matched nothing and never fired.
        var commitment = new Regex(
            @"\b(we|the publisher|the distributor|DiffusionNexus)\b[^.]{0,80}?\b(will|shall|undertakes? to|commits? to)\b[^.]{0,40}?\b(supply|provide|send|furnish|make available)\b"
            + @"|\bupon\s+(written\s+)?request\b"
            + @"|\bcontact\s+(us|the\s+(author|publisher|maintainer))\b",
            RegexOptions.IgnoreCase);

        var match = commitment.Match(authored);
        match.Success.Should().BeFalse(
            "the release carries the source itself, so an offer to supply it on request would "
            + $"promise something nobody is set up to honour (matched: '{match.Value}')");
    }
}
