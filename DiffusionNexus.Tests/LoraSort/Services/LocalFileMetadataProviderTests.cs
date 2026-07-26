using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Enums;
using DiffusionNexus.Service.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.LoraSort.Services;

/// <summary>
/// Direct coverage of <see cref="LocalFileMetadataProvider"/> — previously only
/// exercised incidentally via <c>JsonInfoFileReaderServiceTests</c>. Pins the
/// <c>.civitai.info</c>-over-<c>.json</c> precedence rule and the
/// <c>modelId</c> String/Number coercion.
/// </summary>
public class LocalFileMetadataProviderTests : IDisposable
{
    private readonly string _dir;
    private readonly LocalFileMetadataProvider _sut = new();

    public LocalFileMetadataProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "LocalFileMetadataProviderTests", Guid.NewGuid().ToString("N"));
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

    private string Write(string fileName, string content)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private string ModelFile()
    {
        // The sidecar-lookup key is derived from this file's base name ("model").
        return Write("model.safetensors", string.Empty);
    }

    // ---------------------------------------------------------------------
    // CanHandleAsync
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CanHandleAsync_ExistingFile_True()
    {
        var path = ModelFile();
        (await _sut.CanHandleAsync(path)).Should().BeTrue();
    }

    [Fact]
    public async Task CanHandleAsync_MissingFile_False()
    {
        var path = Path.Combine(_dir, "nope.safetensors");
        (await _sut.CanHandleAsync(path)).Should().BeFalse();
    }

    // ---------------------------------------------------------------------
    // Precedence: .civitai.info wins over .json
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CivitaiInfo_TakesPrecedenceOver_Json_WhenBothPresent()
    {
        var model = ModelFile();
        Write("model.civitai.info", """{ "baseModel": "SD 1.5", "type": "LORA" }""");
        Write("model.json", """{ "sd version": "Pony", "type": "Checkpoint" }""");

        var result = await _sut.GetModelMetadataAsync(model);

        // civitai.info values win; the .json file is not consulted at all.
        result.DiffusionBaseModel.Should().Be("SD 1.5");
        result.ModelType.Should().Be(DiffusionTypes.LORA);
    }

    [Fact]
    public async Task JsonFile_UsedOnly_WhenNoCivitaiInfoPresent()
    {
        var model = ModelFile();
        Write("model.json", """{ "sd version": "SD 1.5", "type": "LORA", "tags": ["character", "style"] }""");

        var result = await _sut.GetModelMetadataAsync(model);

        result.DiffusionBaseModel.Should().Be("SD 1.5");
        result.ModelType.Should().Be(DiffusionTypes.LORA);
        result.Tags.Should().Contain(new[] { "character", "style" });
    }

    // ---------------------------------------------------------------------
    // modelId String/Number coercion (civitai.info)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ModelId_AsJsonNumber_CoercedToString()
    {
        var model = ModelFile();
        Write("model.civitai.info", """{ "modelId": 789 }""");

        var result = await _sut.GetModelMetadataAsync(model);

        result.ModelId.Should().Be("789");
    }

    [Fact]
    public async Task ModelId_AsJsonString_KeptAsString()
    {
        var model = ModelFile();
        Write("model.civitai.info", """{ "modelId": "789" }""");

        var result = await _sut.GetModelMetadataAsync(model);

        result.ModelId.Should().Be("789");
    }

    [Fact]
    public async Task ModelId_AsUnsupportedKind_IsNull()
    {
        var model = ModelFile();
        Write("model.civitai.info", """{ "modelId": true }""");

        var result = await _sut.GetModelMetadataAsync(model);

        result.ModelId.Should().BeNull();
    }

    // ---------------------------------------------------------------------
    // Full civitai.info parse + tolerance
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CivitaiInfo_FullPayload_PopulatesAllFields()
    {
        var model = ModelFile();
        Write("model.civitai.info", """
            {
              "modelId": 123,
              "baseModel": "SD 1.5",
              "trainedWords": ["trigger", "word"],
              "model": {
                "name": "My Model",
                "type": "LORA",
                "tags": ["character"],
                "nsfw": true
              }
            }
            """);

        var result = await _sut.GetModelMetadataAsync(model);

        result.ModelId.Should().Be("123");
        result.DiffusionBaseModel.Should().Be("SD 1.5");
        result.TrainedWords.Should().Equal("trigger", "word");
        result.ModelVersionName.Should().Be("My Model");
        result.ModelType.Should().Be(DiffusionTypes.LORA);
        result.Tags.Should().ContainSingle().Which.Should().Be("character");
        result.Nsfw.Should().BeTrue();
    }

    [Fact]
    public async Task CivitaiInfo_TrainedWords_NonStringEntriesAreIgnored()
    {
        var model = ModelFile();
        Write("model.civitai.info", """{ "trainedWords": ["keep", 42, null, "also"] }""");

        var result = await _sut.GetModelMetadataAsync(model);

        result.TrainedWords.Should().Equal("keep", "also");
    }

    // ---------------------------------------------------------------------
    // No sidecar files
    // ---------------------------------------------------------------------

    [Fact]
    public async Task NoSidecarFiles_LeavesContentFieldsAtDefaults()
    {
        var model = ModelFile();

        var result = await _sut.GetModelMetadataAsync(model);

        result.DiffusionBaseModel.Should().Be("UNKNOWN");
        result.ModelType.Should().Be(DiffusionTypes.UNASSIGNED);
        result.ModelId.Should().BeNull();
        result.Tags.Should().BeEmpty();
        // The base file name is always captured.
        result.SafeTensorFileName.Should().Be("model");
        // NOTE: NoMetaData ends up false even with no sidecars, because
        // SafeTensorFileName is itself a [MetadataField] and is always populated.
        result.NoMetaData.Should().BeFalse();
    }

    [Fact]
    public async Task ProvidedModelInstance_IsMutatedAndReturned()
    {
        var model = ModelFile();
        Write("model.civitai.info", """{ "baseModel": "SD 1.5" }""");
        var existing = new ModelClass { SHA256Hash = "preserve-me" };

        var result = await _sut.GetModelMetadataAsync(model, model: existing);

        result.Should().BeSameAs(existing);
        result.SHA256Hash.Should().Be("preserve-me");
        result.DiffusionBaseModel.Should().Be("SD 1.5");
    }
}
