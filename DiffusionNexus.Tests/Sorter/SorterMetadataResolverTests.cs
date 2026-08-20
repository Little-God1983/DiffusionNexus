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
}
