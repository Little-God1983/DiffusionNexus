using System.Text.Json;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.Lora.Sorting;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Sorter;

public sealed class SorterMetadataResolverTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dn-sortmeta-");
    private readonly Mock<ICivitaiClient> _client = new();

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch (IOException) { }
    }

    private string In(string name) => Path.Combine(_root.FullName, name);

    private string WriteModel(string name = "lora.safetensors")
    {
        var path = In(name);
        File.WriteAllText(path, "weights");
        return path;
    }

    private SorterMetadataResolver Resolver(ICivitaiClient? client = null, string sha = "abc123")
        => new(client, () => Task.FromResult<string?>(null), In("cache"), _ => sha, logger: null);

    [Fact]
    public async Task CivitaiInfoSidecarWinsWithoutTouchingHashOrApi()
    {
        var model = WriteModel();
        File.WriteAllText(In("lora.civitai.info"), """{"id": 555, "baseModel": "Illustrious"}""");
        var resolver = new SorterMetadataResolver(_client.Object, () => Task.FromResult<string?>(null),
            In("cache"), _ => throw new InvalidOperationException("must not hash"), logger: null);

        var meta = await resolver.ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("Illustrious");
        meta.CivitaiVersionId.Should().Be(555);
        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ApiResultIsReturnedAndCachedByHash()
    {
        var model = WriteModel();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 777, BaseModel = "SDXL 1.0" });

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.Should().Be(new ResolvedLoraMetadata("SDXL 1.0", 777, "abc123"));
        File.Exists(In(Path.Combine("cache", "abc123.json"))).Should().BeTrue();
    }

    [Fact]
    public async Task SecondResolveIsServedFromCacheWithoutApiCall()
    {
        var model = WriteModel();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 777, BaseModel = "SDXL 1.0" });
        var resolver = Resolver(_client.Object);

        await resolver.ResolveAsync(model);
        var second = await resolver.ResolveAsync(model);

        second.BaseModelRaw.Should().Be("SDXL 1.0");
        _client.Verify(c => c.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotFoundIsNegativelyCachedAndSortsAsUnknown()
    {
        var model = WriteModel();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CivitaiModelVersion?)null);
        var resolver = Resolver(_client.Object);

        var meta = await resolver.ResolveAsync(model);
        await resolver.ResolveAsync(model);

        meta.BaseModelRaw.Should().BeNull();
        _client.Verify(c => c.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoClientYieldsUnknownWithoutThrowing()
    {
        var model = WriteModel();

        var meta = await Resolver(client: null).ResolveAsync(model);

        meta.Should().Be(new ResolvedLoraMetadata(null, null, "abc123"));
    }

    [Fact]
    public async Task MalformedSidecarFallsThroughToApi()
    {
        var model = WriteModel();
        File.WriteAllText(In("lora.civitai.info"), "{ not json ");
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 1, BaseModel = "Pony" });

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("Pony");
    }

    [Fact]
    public async Task EmptyObjectSidecarIsNotAHitAndFallsThrough()
    {
        var model = WriteModel();
        File.WriteAllText(In("lora.civitai.info"), "{}");
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 2, BaseModel = "Illustrious" });

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.Should().Be(new ResolvedLoraMetadata("Illustrious", 2, "abc123"));
    }

    [Fact]
    public async Task ApiKeyIsReadOncePerPassNotOncePerFile()
    {
        // Review 6.3: the provider opens a DI scope + DbContext + query in production, and
        // it was awaited inline in the API call — 1000 unresolved files meant 1000 reads of
        // a value that cannot change mid-pass.
        var reads = 0;
        var resolver = new SorterMetadataResolver(_client.Object,
            () => { reads++; return Task.FromResult<string?>("key"); },
            In("cache"), p => Path.GetFileNameWithoutExtension(p), logger: null);
        foreach (var i in Enumerable.Range(0, 3)) WriteModel($"lora{i}.safetensors");

        foreach (var i in Enumerable.Range(0, 3))
            await resolver.ResolveAsync(In($"lora{i}.safetensors"));

        reads.Should().Be(1);

        // A new pass re-reads, so a key changed in Settings meanwhile is picked up.
        // (Uses a fresh file: lora0's hash is now in the disk cache and never reaches the API.)
        WriteModel("lora3.safetensors");
        resolver.ResetApiKeyCache();
        await resolver.ResolveAsync(In("lora3.safetensors"));
        reads.Should().Be(2);
    }

    [Fact]
    public async Task ApiKeyProviderFailureDegradesToAnAnonymousLookup()
    {
        var model = WriteModel();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 9, BaseModel = "Pony" });
        var resolver = new SorterMetadataResolver(_client.Object,
            () => throw new InvalidOperationException("settings db down"),
            In("cache"), _ => "abc123", logger: null);

        var meta = await resolver.ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("Pony");
    }

    [Fact]
    public async Task UnreadableFileResolvesAsUnknownInsteadOfThrowing()
    {
        // Review 2.3: _hashFile → File.OpenRead throws for a .safetensors held open by a
        // running backend, and it escaped the "never throws" contract, killing the pass.
        var model = WriteModel();
        var resolver = new SorterMetadataResolver(_client.Object, () => Task.FromResult<string?>(null),
            In("cache"), _ => throw new IOException("in use"), logger: null);

        var meta = await resolver.ResolveAsync(model);

        meta.Should().Be(new ResolvedLoraMetadata(null, null, string.Empty));
        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CivitaiShapeChangeResolvesAsUnknownInsteadOfThrowing()
    {
        // CivitaiClient.DeserializeOrThrow raises JsonException on a response-shape change;
        // this repo has been bitten by that twice.
        var model = WriteModel();
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new JsonException("allowCommercialUse changed shape"));

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.Should().Be(new ResolvedLoraMetadata(null, null, "abc123"));
    }

    [Fact]
    public async Task SidecarTagsAreExposedForCategoryInference()
    {
        // Review 4.2: a fully resolved LoRA in a browsed folder was still forced into
        // Unknown\ because nothing carried its tags out of the sidecar.
        var model = WriteModel();
        File.WriteAllText(In("lora.civitai.info"),
            """{"id": 5, "baseModel": "Pony", "model": {"name": "x", "tags": ["anime", "style"]}}""");

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.Tags.Should().BeEquivalentTo(["anime", "style"]);
        SorterCategoryResolver.InferFolderName(meta.Tags).Should().Be("Style");
    }

    [Fact]
    public async Task TopLevelTagArrayAndTagObjectsAreBothAccepted()
    {
        var model = WriteModel();
        File.WriteAllText(In("lora.civitai.info"),
            """{"id": 6, "baseModel": "Pony", "tags": ["concept", {"name": "extra"}, 42]}""");

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.Tags.Should().BeEquivalentTo(["concept", "extra"]);
    }

    [Fact]
    public async Task SidecarWithoutTagsYieldsAnEmptyTagList()
    {
        var model = WriteModel();
        File.WriteAllText(In("lora.civitai.info"), """{"id": 7, "baseModel": "Pony"}""");

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task MalformedCacheFileIsDeletedAndResolutionFallsThrough()
    {
        var model = WriteModel();
        Directory.CreateDirectory(In("cache"));
        File.WriteAllText(In(Path.Combine("cache", "abc123.json")), "garbage");
        _client.Setup(c => c.GetModelVersionByHashAsync("abc123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 3, BaseModel = "SDXL 1.0" });

        var meta = await Resolver(_client.Object).ResolveAsync(model);

        meta.BaseModelRaw.Should().Be("SDXL 1.0");
    }
}
