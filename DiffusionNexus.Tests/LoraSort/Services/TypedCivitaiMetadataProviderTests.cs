using System.Net;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Service.Enums;
using DiffusionNexus.Service.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.LoraSort.Services;

/// <summary>
/// Coverage of <see cref="TypedCivitaiMetadataProvider"/>: SHA256 hashing of a
/// real temp file, the Civitai-response -> <c>ModelClass</c> mapping, the
/// CivitaiModelType -> DiffusionTypes switch, and the NoMetaData fallbacks
/// (null version, HTTP 404). Uses a hand-rolled fake <see cref="ICivitaiClient"/>.
/// </summary>
public class TypedCivitaiMetadataProviderTests : IDisposable
{
    // SHA-256 of the ASCII bytes "hello" (well-known test vector).
    private const string HelloSha256 = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";

    private readonly string _dir;

    public TypedCivitaiMetadataProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TypedCivitaiMetadataProviderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    private string HelloFile()
    {
        var path = Path.Combine(_dir, "model.safetensors");
        File.WriteAllBytes(path, "hello"u8.ToArray());
        return path;
    }

    // ---------------------------------------------------------------------
    // Construction & CanHandleAsync
    // ---------------------------------------------------------------------

    [Fact]
    public void Ctor_NullClient_Throws()
    {
        var act = () => new TypedCivitaiMetadataProvider(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", true)] // valid 64-hex
    [InlineData("2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824", true)] // upper hex
    [InlineData("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b982", false)]  // 63 chars
    [InlineData("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b98244", false)] // 65 chars
    [InlineData("gcf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", false)]  // non-hex 'g'
    public async Task CanHandleAsync_OnlyAccepts64CharHex(string identifier, bool expected)
    {
        var sut = new TypedCivitaiMetadataProvider(new FakeCivitaiClient());
        (await sut.CanHandleAsync(identifier)).Should().Be(expected);
    }

    // ---------------------------------------------------------------------
    // Hashing
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetModelMetadataAsync_ComputesSha256_AndLooksUpByThatHash()
    {
        var file = HelloFile();
        var fake = new FakeCivitaiClient
        {
            OnGetVersionByHash = (_, _, _) => Task.FromResult<CivitaiModelVersion?>(null),
        };
        var sut = new TypedCivitaiMetadataProvider(fake);

        var result = await sut.GetModelMetadataAsync(file);

        result.SHA256Hash.Should().Be(HelloSha256);
        fake.LastHash.Should().Be(HelloSha256);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetModelMetadataAsync_NullOrWhitespacePath_Throws(string? path)
    {
        var sut = new TypedCivitaiMetadataProvider(new FakeCivitaiClient());
        var act = async () => await sut.GetModelMetadataAsync(path!);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ---------------------------------------------------------------------
    // Mapping
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetModelMetadataAsync_MapsVersionAndModelFields()
    {
        var file = HelloFile();
        var fake = new FakeCivitaiClient
        {
            OnGetVersionByHash = (_, _, _) => Task.FromResult<CivitaiModelVersion?>(new CivitaiModelVersion
            {
                Id = 10,
                ModelId = 42,
                Name = "v1.0",
                BaseModel = "SD 1.5",
                TrainedWords = new[] { "trigger" },
            }),
            OnGetModel = (_, _, _) => Task.FromResult<CivitaiModel?>(new CivitaiModel
            {
                Id = 42,
                Type = CivitaiModelType.LORA,
                Tags = new[] { "character" },
                Nsfw = true,
            }),
        };
        var sut = new TypedCivitaiMetadataProvider(fake);

        var result = await sut.GetModelMetadataAsync(file);

        result.ModelId.Should().Be("42");
        result.DiffusionBaseModel.Should().Be("SD 1.5");
        result.ModelVersionName.Should().Be("v1.0");
        result.TrainedWords.Should().Equal("trigger");
        result.ModelType.Should().Be(DiffusionTypes.LORA);
        result.Tags.Should().ContainSingle().Which.Should().Be("character");
        result.CivitaiCategory.Should().Be(CivitaiBaseCategories.CHARACTER);
        result.Nsfw.Should().BeTrue();
        result.NoMetaData.Should().BeFalse();
        fake.LastModelId.Should().Be(42);
    }

    [Theory]
    [InlineData(CivitaiModelType.Checkpoint, DiffusionTypes.CHECKPOINT)]
    [InlineData(CivitaiModelType.LORA, DiffusionTypes.LORA)]
    [InlineData(CivitaiModelType.DoRA, DiffusionTypes.DORA)]
    [InlineData(CivitaiModelType.LoCon, DiffusionTypes.LOCON)]
    [InlineData(CivitaiModelType.VAE, DiffusionTypes.VAE)]
    [InlineData(CivitaiModelType.Controlnet, DiffusionTypes.CONTROLNET)]
    [InlineData(CivitaiModelType.Upscaler, DiffusionTypes.UPSCALER)]
    [InlineData(CivitaiModelType.Other, DiffusionTypes.OTHER)]
    [InlineData(CivitaiModelType.Unknown, DiffusionTypes.UNASSIGNED)]
    public async Task GetModelMetadataAsync_MapsModelType(CivitaiModelType civitaiType, DiffusionTypes expected)
    {
        var file = HelloFile();
        var fake = new FakeCivitaiClient
        {
            OnGetVersionByHash = (_, _, _) => Task.FromResult<CivitaiModelVersion?>(new CivitaiModelVersion { ModelId = 42 }),
            OnGetModel = (_, _, _) => Task.FromResult<CivitaiModel?>(new CivitaiModel { Id = 42, Type = civitaiType }),
        };
        var sut = new TypedCivitaiMetadataProvider(fake);

        var result = await sut.GetModelMetadataAsync(file);

        result.ModelType.Should().Be(expected);
    }

    [Fact]
    public async Task GetModelMetadataAsync_ZeroModelId_DoesNotFetchModel()
    {
        var file = HelloFile();
        var fake = new FakeCivitaiClient
        {
            OnGetVersionByHash = (_, _, _) => Task.FromResult<CivitaiModelVersion?>(new CivitaiModelVersion { ModelId = 0, BaseModel = "SD 1.5" }),
        };
        var sut = new TypedCivitaiMetadataProvider(fake);

        var result = await sut.GetModelMetadataAsync(file);

        fake.GetModelCallCount.Should().Be(0);
        result.ModelId.Should().BeNull();
        result.ModelType.Should().Be(DiffusionTypes.UNASSIGNED);
    }

    // ---------------------------------------------------------------------
    // NoMetaData fallbacks
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetModelMetadataAsync_NullVersion_SetsNoMetaData()
    {
        var file = HelloFile();
        var fake = new FakeCivitaiClient
        {
            OnGetVersionByHash = (_, _, _) => Task.FromResult<CivitaiModelVersion?>(null),
        };
        var sut = new TypedCivitaiMetadataProvider(fake);

        var result = await sut.GetModelMetadataAsync(file);

        result.NoMetaData.Should().BeTrue();
        fake.GetModelCallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetModelMetadataAsync_Http404_SetsNoMetaData()
    {
        var file = HelloFile();
        var fake = new FakeCivitaiClient
        {
            OnGetVersionByHash = (_, _, _) => throw new HttpRequestException("nope", null, HttpStatusCode.NotFound),
        };
        var sut = new TypedCivitaiMetadataProvider(fake);

        var result = await sut.GetModelMetadataAsync(file);

        result.NoMetaData.Should().BeTrue();
    }

    [Fact]
    public async Task GetModelMetadataAsync_NonNotFoundHttpError_Propagates()
    {
        var file = HelloFile();
        var fake = new FakeCivitaiClient
        {
            OnGetVersionByHash = (_, _, _) => throw new HttpRequestException("boom", null, HttpStatusCode.InternalServerError),
        };
        var sut = new TypedCivitaiMetadataProvider(fake);

        var act = async () => await sut.GetModelMetadataAsync(file);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetModelMetadataAsync_ForwardsApiKey_ToClient()
    {
        var file = HelloFile();
        var fake = new FakeCivitaiClient
        {
            OnGetVersionByHash = (_, _, _) => Task.FromResult<CivitaiModelVersion?>(null),
        };
        var sut = new TypedCivitaiMetadataProvider(fake, apiKey: "secret");

        await sut.GetModelMetadataAsync(file);

        fake.LastApiKey.Should().Be("secret");
    }

    /// <summary>Configurable fake; only the two consumed methods are wired.</summary>
    private sealed class FakeCivitaiClient : ICivitaiClient
    {
        public Func<string, string?, CancellationToken, Task<CivitaiModelVersion?>>? OnGetVersionByHash;
        public Func<int, string?, CancellationToken, Task<CivitaiModel?>>? OnGetModel;

        public string? LastHash;
        public string? LastApiKey;
        public int? LastModelId;
        public int GetModelCallCount;

        public Task<CivitaiModelVersion?> GetModelVersionByHashAsync(string hash, string? apiKey = null, CancellationToken cancellationToken = default)
        {
            LastHash = hash;
            LastApiKey = apiKey;
            return OnGetVersionByHash!(hash, apiKey, cancellationToken);
        }

        public Task<CivitaiModel?> GetModelAsync(int modelId, string? apiKey = null, CancellationToken cancellationToken = default)
        {
            LastModelId = modelId;
            GetModelCallCount++;
            return OnGetModel!(modelId, apiKey, cancellationToken);
        }

        // Unused by the provider.
        public Task<CivitaiPagedResponse<CivitaiModel>> GetModelsAsync(CivitaiModelsQuery? query = null, string? apiKey = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<CivitaiModelVersion?> GetModelVersionAsync(int modelVersionId, string? apiKey = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<CivitaiPagedResponse<CivitaiModelImage>> GetImagesAsync(CivitaiImagesQuery? query = null, string? apiKey = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<CivitaiPagedResponse<CivitaiTag>> GetTagsAsync(int? limit = null, int? page = null, string? query = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<CivitaiPagedResponse<CivitaiCreatorInfo>> GetCreatorsAsync(int? limit = null, int? page = null, string? query = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
