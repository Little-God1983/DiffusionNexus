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
/// These tests exist because the same failure has now happened twice in this area: a claim in the
/// notices that nothing checked, drifting away from what the build does. They check the claim
/// against the publish script and the manifest, not against another piece of prose.
/// </para>
/// </summary>
public class CorrespondingSourceTests
{
    private const string AccompanyClaim = "accompanies this distribution";

    private static JsonElement Manifest => JsonDocument.Parse(
        File.ReadAllText(RepoRoot.Combine("Scripts", "license-data", "corresponding-source.json"))).RootElement;

    [Fact]
    public void WhenTheNoticesSaySourceAccompaniesTheReleaseThenThePublishScriptStagesIt()
    {
        var notices = File.ReadAllText(RepoRoot.Combine("THIRD-PARTY-NOTICES.txt"));
        if (!notices.Contains(AccompanyClaim, StringComparison.OrdinalIgnoreCase))
            return; // A different s3 route was chosen; the offer wording is guarded elsewhere.

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
    public void WhenTheManifestIsReadThenEveryComponentIsPinnedByHashAndReachableFromMoreThanOnePlace()
    {
        var components = Manifest.GetProperty("components").EnumerateArray().ToList();

        components.Should().NotBeEmpty("an empty manifest would stage nothing and pass silently");

        foreach (var c in components)
        {
            var name = c.GetProperty("name").GetString();

            c.GetProperty("sha256").GetString().Should().MatchRegex("^[0-9a-f]{64}$",
                $"{name} must be pinned by hash; mirrors are untrusted and one of them already "
                + "serves an HTML page in place of the archive");

            c.GetProperty("describes").GetString().Should().NotBeNullOrWhiteSpace(
                $"{name} must say which shipped binary it is the source of, or the folder is a "
                + "pile of tarballs the recipient cannot map to anything");
        }

        // libgsm's pinned upstream host has already disappeared once. A single URL is one dead
        // host away from a release that cannot be built.
        components.Count(c => c.GetProperty("urls").GetArrayLength() > 1)
            .Should().BeGreaterThan(components.Count / 2,
                "most components should have a fallback source");
    }

    [Fact]
    public void WhenTheManifestPinsAPackageVersionThenItIsTheVersionTheProjectReferences()
    {
        var pinned = Manifest.GetProperty("correspondsTo").GetProperty("packageVersion").GetString();

        var csproj = File.ReadAllText(RepoRoot.Combine("DiffusionNexus.UI", "DiffusionNexus.UI.csproj"));
        var referenced = Regex.Match(
            csproj,
            "<PackageReference\\b[^>]*Include=\"VideoLAN\\.LibVLC\\.Windows\"[^>]*Version=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);

        referenced.Success.Should().BeTrue();
        referenced.Groups[1].Value.Should().Be(pinned,
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
        // scanning the whole file for offer wording matches the licence bodies themselves. The
        // mistake this guards against would be made in our prose, which is what is read here.
        var supplements = JsonDocument.Parse(
            File.ReadAllText(RepoRoot.Combine("Scripts", "license-data", "supplements.json"))).RootElement;

        var authored = string.Join(
            "\n",
            supplements.GetProperty("supplements").EnumerateArray()
                .Select(s => s.GetProperty("note").GetString()));

        if (!authored.Contains(AccompanyClaim, StringComparison.OrdinalIgnoreCase))
            return; // A different section 3 route was chosen.

        // Routes (a) and (b) are alternatives. Making an offer we did not intend - and could not
        // honour, since no contact point is staffed for it - would create the very obligation
        // this release was structured to avoid.
        foreach (var offerWording in new[] { "valid for three years", "we will supply", "on request" })
        {
            authored.Contains(offerWording, StringComparison.OrdinalIgnoreCase).Should().BeFalse(
                $"the release carries the source itself, so '{offerWording}' would promise "
                + "something nobody is set up to honour");
        }
    }
}
