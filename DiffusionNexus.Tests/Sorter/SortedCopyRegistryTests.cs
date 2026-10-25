using DiffusionNexus.UI.Services.Lora.Sorting;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sorter;

/// <summary>
/// The re-plan after a copy run hashed every source and the copy it had just made, to prove what
/// the run already knew (real run: 106 files, 26 GB, 79 s). The registry vouches for those pairs —
/// only while neither file has changed since.
/// </summary>
public sealed class SortedCopyRegistryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dn-sortedcopies-");

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch (IOException) { }
    }

    private string File(string name, string content = "weights")
    {
        var path = Path.Combine(_root.FullName, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void AnUnchangedCopyIsKnown()
    {
        var source = File("a.safetensors");
        var target = File("copy.safetensors");
        var registry = new SortedCopyRegistry();

        registry.Record([(source, target)]);

        registry.IsKnownCopy(source, target).Should().BeTrue();
        registry.IsKnownCopy(source.ToUpperInvariant(), target.ToUpperInvariant())
            .Should().BeTrue("Windows paths compare without case");
    }

    [Fact]
    public void APairTheRunDidNotMakeIsNotKnown()
    {
        var source = File("a.safetensors");
        var target = File("copy.safetensors");
        var other = File("b.safetensors");
        var registry = new SortedCopyRegistry();

        registry.Record([(source, target)]);

        registry.IsKnownCopy(other, target).Should().BeFalse();
        registry.IsKnownCopy(target, source).Should().BeFalse("a pair has a direction");
    }

    [Fact]
    public void ACopyRewrittenSinceTheRunIsHashedAgain()
    {
        var source = File("a.safetensors");
        var target = File("copy.safetensors");
        var registry = new SortedCopyRegistry();
        registry.Record([(source, target)]);

        System.IO.File.WriteAllText(target, "other weights");

        registry.IsKnownCopy(source, target).Should().BeFalse();
    }

    [Fact]
    public void ASameSizeChangeIsCaughtByTheWriteTime()
    {
        var source = File("a.safetensors");
        var target = File("copy.safetensors");
        var registry = new SortedCopyRegistry();
        registry.Record([(source, target)]);

        System.IO.File.SetLastWriteTimeUtc(source, System.IO.File.GetLastWriteTimeUtc(source).AddMinutes(1));

        registry.IsKnownCopy(source, target).Should().BeFalse();
    }

    [Fact]
    public void ADeletedCopyIsNotKnown()
    {
        var source = File("a.safetensors");
        var target = File("copy.safetensors");
        var registry = new SortedCopyRegistry();
        registry.Record([(source, target)]);

        System.IO.File.Delete(target);

        registry.IsKnownCopy(source, target).Should().BeFalse();
    }

    [Fact]
    public void EachRunReplacesThePreviousRunsPairs()
    {
        var source = File("a.safetensors");
        var target = File("copy.safetensors");
        var registry = new SortedCopyRegistry();
        registry.Record([(source, target)]);

        registry.Record([]);

        registry.IsKnownCopy(source, target).Should().BeFalse();
    }
}
