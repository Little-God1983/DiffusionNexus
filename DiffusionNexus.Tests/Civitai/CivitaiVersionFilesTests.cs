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
}
