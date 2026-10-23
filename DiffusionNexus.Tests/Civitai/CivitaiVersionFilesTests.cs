using DiffusionNexus.Civitai.Models;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Civitai;

public class CivitaiVersionFilesTests
{
    private static CivitaiModelFile File(string name, bool? primary) => new() { Name = name, Primary = primary };

    [Fact]
    public void PickPrimary_PrefersThePrimaryFlaggedFile()
    {
        var version = new CivitaiModelVersion { Files = [File("a", false), File("b", true)] };
        CivitaiVersionFiles.PickPrimary(version)!.Name.Should().Be("b");
    }

    [Fact]
    public void PickPrimary_FallsBackToTheFirstFile()
    {
        var version = new CivitaiModelVersion { Files = [File("a", null), File("b", false)] };
        CivitaiVersionFiles.PickPrimary(version)!.Name.Should().Be("a");
    }

    [Fact]
    public void PickPrimary_NullVersionOrNoFilesIsNull()
    {
        CivitaiVersionFiles.PickPrimary((CivitaiModelVersion?)null).Should().BeNull();
        CivitaiVersionFiles.PickPrimary(new CivitaiModelVersion()).Should().BeNull();
    }

    [Fact]
    public void PickPrimary_TwoVersionFallbackWalksAllFourRungs()
    {
        // best has no files at all -> fall through to the original version's primary,
        // exactly LoraDownloadService's 4-level chain (best primary, best first,
        // fallback primary, fallback first).
        var best = new CivitaiModelVersion();
        var fallback = new CivitaiModelVersion { Files = [File("x", false), File("y", true)] };
        CivitaiVersionFiles.PickPrimary(best, fallback)!.Name.Should().Be("y");
    }

    private static CivitaiModelFile Hashed(string name, bool? primary, string? sha) =>
        new() { Name = name, Primary = primary, Hashes = new CivitaiFileHashes { SHA256 = sha } };

    [Fact]
    public void FindBySha256_MatchesCaseInsensitively_AndNullWhenAbsentOrBlank()
    {
        var version = new CivitaiModelVersion { Files = [Hashed("a", true, "AA11"), Hashed("b", false, "bb22"), File("c", false)] };
        CivitaiVersionFiles.FindBySha256(version, "BB22")!.Name.Should().Be("b");
        CivitaiVersionFiles.FindBySha256(version, "cc33").Should().BeNull();
        CivitaiVersionFiles.FindBySha256(version, null).Should().BeNull();
        CivitaiVersionFiles.FindBySha256(null, "AA11").Should().BeNull();
    }

    [Fact]
    public void PickBySha256OrPrimary_PrefersTheHashedFile_ElseThePrimary()
    {
        var version = new CivitaiModelVersion { Files = [Hashed("a", true, "AA11"), Hashed("b", false, "BB22")] };
        CivitaiVersionFiles.PickBySha256OrPrimary(version, "bb22")!.Name.Should().Be("b");
        CivitaiVersionFiles.PickBySha256OrPrimary(version, "CC33")!.Name.Should().Be("a");
        CivitaiVersionFiles.PickBySha256OrPrimary(version, null)!.Name.Should().Be("a");
    }
}
