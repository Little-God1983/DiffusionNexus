using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Service.Services.Sync;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace DiffusionNexus.Tests.Sync.Service;

/// <summary>
/// Covers <see cref="SidecarMetadataApplier"/>: the local <c>.civitai.info</c> / <c>.json</c>
/// sidecar parsing and the local-preview → thumbnail BLOB transcode that used to live inside
/// <c>LoraViewerViewModel.TryApplyLocalMetadataFallbackAsync</c> (#521 WP2).
/// </summary>
public sealed class SidecarMetadataApplierTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly DirectoryInfo _tempDir;

    public SidecarMetadataApplierTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        context.Database.EnsureCreated();

        _tempDir = Directory.CreateTempSubdirectory("dn-sidecar-");
    }

    private IServiceScope NewScope() => _serviceProvider.CreateScope();

    /// <summary>Local-only model: one version, one primary file, no hashes, no Civitai ids.</summary>
    private static Model NewLocalModel(
        string name,
        string path,
        bool modelUserEdited = false,
        bool versionUserEdited = false,
        string versionName = "v1")
    {
        var model = new Model
        {
            Name = name,
            Type = ModelType.LORA,
            Source = DataSource.Manual,
            IsUserEdited = modelUserEdited,
        };

        var version = new ModelVersion
        {
            Name = versionName,
            BaseModelRaw = "???",
            IsUserEdited = versionUserEdited,
        };
        version.Files.Add(new ModelFile
        {
            FileName = Path.GetFileName(path),
            LocalPath = path,
            IsLocalFileValid = true,
            IsPrimary = true,
        });

        model.Versions.Add(version);
        return model;
    }

    /// <summary>Seeds a model whose primary file lives at <paramref name="modelFilePath"/>.</summary>
    private async Task<int> SeedAsync(string modelFilePath)
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var model = NewLocalModel("local", modelFilePath);
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();
        return model.Id;
    }

    /// <summary>Creates an empty model file on disk and returns its full path.</summary>
    private string NewModelFile(string fileName)
    {
        var path = Path.Combine(_tempDir.FullName, fileName);
        File.WriteAllBytes(path, [0x00]);
        return path;
    }

    [Fact]
    public void Find_PrefersCivitaiInfoOverJson_AndSignatureChangesWhenFileChanges()
    {
        var modelPath = NewModelFile("find.safetensors");

        // No sidecar at all → no path, empty signature.
        var none = SidecarMetadataApplier.Find(modelPath);
        none.SidecarPath.Should().BeNull();
        none.Signature.Should().BeEmpty();

        // Only .json → .json.
        var jsonPath = Path.Combine(_tempDir.FullName, "find.json");
        File.WriteAllText(jsonPath, "{}");
        var jsonOnly = SidecarMetadataApplier.Find(modelPath);
        jsonOnly.SidecarPath.Should().Be(jsonPath);
        jsonOnly.Signature.Should().NotBeEmpty();

        // .civitai.info wins over .json.
        var infoPath = Path.Combine(_tempDir.FullName, "find.civitai.info");
        File.WriteAllText(infoPath, "{}");
        var preferred = SidecarMetadataApplier.Find(modelPath);
        preferred.SidecarPath.Should().Be(infoPath);
        preferred.Signature.Should().StartWith(infoPath);

        // Signature tracks content changes (length) and mtime.
        File.WriteAllText(infoPath, "{\"a\":1,\"b\":2,\"c\":3}");
        File.SetLastWriteTimeUtc(infoPath, DateTime.UtcNow.AddMinutes(5));
        var changed = SidecarMetadataApplier.Find(modelPath);
        changed.SidecarPath.Should().Be(infoPath);
        changed.Signature.Should().NotBe(preferred.Signature);
    }

    /// <summary>
    /// The lookup is two exact <c>File.Exists</c> probes, not a directory enumeration: only the
    /// file whose name is exactly <c>{base}.civitai.info</c> / <c>{base}.json</c> counts. Windows
    /// pattern matching would happily hand back an 8.3 short name or a near miss.
    /// </summary>
    [Fact]
    public void Find_DoesNotMatchByShortNameOrWildcard()
    {
        var modelPath = NewModelFile("exact-name-only.safetensors");

        File.WriteAllText(Path.Combine(_tempDir.FullName, "other.civitai.info"), "{}");
        File.WriteAllText(Path.Combine(_tempDir.FullName, "exact-name-onlyx.civitai.info"), "{}");
        File.WriteAllText(Path.Combine(_tempDir.FullName, "exact-name-onlyx.json"), "{}");

        var lookup = SidecarMetadataApplier.Find(modelPath);

        lookup.SidecarPath.Should().BeNull();
        lookup.Signature.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyAsync_CivitaiInfoSetsBaseModelIdsTriggerWordsAndMarksLocalFileSource()
    {
        var modelPath = NewModelFile("info.safetensors");
        var modelId = await SeedAsync(modelPath);

        // The files[] entry carries "name" because the applier matches hashes by file name
        // (unchanged behavior, moved verbatim out of the ViewModel).
        var sidecar = """
        {"id":700,"modelId":77,"baseModel":"Pony","trainedWords":["x"],
         "model":{"name":"N","nsfw":false},
         "files":[{"name":"info.safetensors","primary":true,"hashes":{"SHA256":"ABC"}}],
         "images":[{"url":"https://x/y.jpeg","nsfw":false}]}
        """;
        await File.WriteAllTextAsync(Path.Combine(_tempDir.FullName, "info.civitai.info"), sidecar);

        SidecarApplyResult result;
        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            result = await new SidecarMetadataApplier().ApplyAsync(uow, modelId, modelPath);
        }

        result.Applied.Should().BeTrue();
        result.Signature.Should().NotBeEmpty();
        result.SidecarPath.Should().EndWith("info.civitai.info");

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);

            saved.Should().NotBeNull();
            saved!.Name.Should().Be("N");
            saved.CivitaiModelPageId.Should().Be(77);
            saved.Source.Should().Be(DataSource.LocalFile);
            saved.LastSyncedAt.Should().NotBeNull();

            var version = saved.Versions.Single();
            version.CivitaiId.Should().Be(700);
            version.BaseModelRaw.Should().Be("Pony");
            version.TriggerWords.Select(t => t.Word).Should().Equal("x");
            version.Images.Should().ContainSingle().Which.Url.Should().Be("https://x/y.jpeg");
            version.PrimaryFile.Should().NotBeNull();
            version.PrimaryFile!.HashSHA256.Should().Be("ABC");
        }
    }

    [Fact]
    public async Task ApplyAsync_SimpleJsonSdVersionFallback()
    {
        var modelPath = NewModelFile("simple.safetensors");
        var modelId = await SeedAsync(modelPath);

        await File.WriteAllTextAsync(
            Path.Combine(_tempDir.FullName, "simple.json"),
            """{"sd version":"SD1"}""");

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var result = await new SidecarMetadataApplier().ApplyAsync(uow, modelId, modelPath);
            result.Applied.Should().BeTrue();
            result.SidecarPath.Should().EndWith("simple.json");
        }

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);
            saved!.Versions.Single().BaseModelRaw.Should().Be("SD1");
        }
    }

    // ------------------------------------------------- B1: user edits are not ours to overwrite

    /// <summary>
    /// Seeds a hand-edited model — custom model name, custom version name, one custom trigger word,
    /// both <c>IsUserEdited</c> flags set — whose primary file is <paramref name="modelFilePath"/>.
    /// </summary>
    private async Task<int> SeedEditedAsync(string modelFilePath)
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = NewLocalModel("my name", modelFilePath,
            modelUserEdited: true, versionUserEdited: true, versionName: "my version");
        model.Versions.First().TriggerWords.Add(new TriggerWord { Word = "mytrigger", Order = 0 });

        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();
        return model.Id;
    }

    /// <summary>Applies the sidecar next to <paramref name="modelPath"/> in its own scope.</summary>
    private async Task<SidecarApplyResult> ApplyAsync(int modelId, string modelPath)
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await new SidecarMetadataApplier().ApplyAsync(uow, modelId, modelPath);
    }

    /// <summary>
    /// The authored text survived and the facts still landed — the assertion every one of the
    /// three sidecar formats has to satisfy.
    /// </summary>
    private async Task AssertUserEditsSurvivedAsync(int modelId, bool expectIds)
    {
        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);

        saved!.Name.Should().Be("my name", "the user named this model");

        var version = saved.Versions.Single();
        version.Name.Should().Be("my version", "the user named this version");
        version.TriggerWords.Select(t => t.Word).Should().BeEquivalentTo(
            new[] { "mytrigger" }, "the user's trigger words are not the sidecar's to replace");

        // Facts the user did not author are still applied.
        version.BaseModelRaw.Should().Be("Pony");
        saved.IsNsfw.Should().BeTrue();
        if (expectIds) saved.CivitaiModelPageId.Should().Be(77);
    }

    [Fact]
    public async Task ApplyAsync_PreservesUserEditedNameAndTriggerWords_CivitaiInfoFormat()
    {
        var modelPath = NewModelFile("edited-info.safetensors");
        var modelId = await SeedEditedAsync(modelPath);

        await File.WriteAllTextAsync(Path.Combine(_tempDir.FullName, "edited-info.civitai.info"), """
        {"id":700,"modelId":77,"name":"civitai v1","baseModel":"Pony","trainedWords":["x"],
         "model":{"name":"Civitai Name","nsfw":true}}
        """);

        (await ApplyAsync(modelId, modelPath)).Applied.Should().BeTrue();

        await AssertUserEditsSurvivedAsync(modelId, expectIds: true);
    }

    [Fact]
    public async Task ApplyAsync_PreservesUserEditedNameAndTriggerWords_ModelLevelJsonFormat()
    {
        var modelPath = NewModelFile("edited-model.safetensors");
        var modelId = await SeedEditedAsync(modelPath);

        await File.WriteAllTextAsync(Path.Combine(_tempDir.FullName, "edited-model.json"), """
        {"id":77,"name":"Civitai Name","nsfw":true,
         "modelVersions":[{"id":700,"name":"civitai v1","baseModel":"Pony","trainedWords":["x"],
                           "files":[{"name":"edited-model.safetensors"}]}]}
        """);

        (await ApplyAsync(modelId, modelPath)).Applied.Should().BeTrue();

        await AssertUserEditsSurvivedAsync(modelId, expectIds: true);
    }

    [Fact]
    public async Task ApplyAsync_PreservesUserEditedNameAndTriggerWords_SimpleJsonFormat()
    {
        var modelPath = NewModelFile("edited-simple.safetensors");
        var modelId = await SeedEditedAsync(modelPath);

        await File.WriteAllTextAsync(Path.Combine(_tempDir.FullName, "edited-simple.json"), """
        {"baseModel":"Pony","trainedWords":["x"],
         "model":{"name":"Civitai Name","nsfw":true}}
        """);

        (await ApplyAsync(modelId, modelPath)).Applied.Should().BeTrue();

        // The simple format carries no Civitai ids at all.
        await AssertUserEditsSurvivedAsync(modelId, expectIds: false);
    }

    [Fact]
    public async Task ApplyAsync_NoSidecarButLocalPreviewAppliesThumbnail()
    {
        var modelPath = NewModelFile("preview.safetensors");
        var modelId = await SeedAsync(modelPath);

        await File.WriteAllBytesAsync(
            Path.Combine(_tempDir.FullName, "preview.preview.png"),
            EncodePng(64, 64));

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var result = await new SidecarMetadataApplier().ApplyAsync(uow, modelId, modelPath);

            result.Applied.Should().BeFalse();
            result.ThumbnailApplied.Should().BeTrue();
            result.SidecarPath.Should().BeNull();
            result.Signature.Should().BeEmpty();
        }

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);

            var image = saved!.Versions.Single().Images.Should().ContainSingle().Subject;
            image.Url.Should().StartWith("file://");
            image.ThumbnailData.Should().NotBeNullOrEmpty();
            image.ThumbnailMimeType.Should().Be("image/jpeg");
        }
    }

    [Fact]
    public async Task ApplyAsync_DoesNotOverwriteExistingThumbnail()
    {
        var modelPath = NewModelFile("kept.safetensors");
        byte[] existing = [1, 2, 3];

        int modelId;
        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = NewLocalModel("local", modelPath);
            model.Versions.First().Images.Add(new ModelImage
            {
                Url = "https://civitai/existing.jpeg",
                SortOrder = 0,
                ThumbnailData = existing,
                ThumbnailMimeType = "image/jpeg",
            });
            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
            modelId = model.Id;
        }

        // A perfectly good local preview sits next to the file — spec S4 still wins.
        await File.WriteAllBytesAsync(
            Path.Combine(_tempDir.FullName, "kept.preview.png"),
            EncodePng(64, 64));

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var result = await new SidecarMetadataApplier().ApplyAsync(uow, modelId, modelPath);
            result.ThumbnailApplied.Should().BeFalse();
        }

        using (var scope = NewScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var saved = await uow.Models.GetByIdWithIncludesAsync(modelId);

            var images = saved!.Versions.Single().Images;
            images.Should().ContainSingle("no file:// row may be added when a thumbnail exists");
            images.Single().Url.Should().Be("https://civitai/existing.jpeg");
            images.Single().ThumbnailData.Should().Equal(existing);
        }
    }

    [Fact]
    public async Task ApplyAsync_NothingThereReturnsNotApplied()
    {
        var modelPath = NewModelFile("bare.safetensors");
        var modelId = await SeedAsync(modelPath);

        using var scope = NewScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var result = await new SidecarMetadataApplier().ApplyAsync(uow, modelId, modelPath);

        result.Applied.Should().BeFalse();
        result.ThumbnailApplied.Should().BeFalse();
        result.SidecarPath.Should().BeNull();
        result.Signature.Should().BeEmpty();
    }

    /// <summary>Encodes a solid-color PNG of the given size — a real image the applier can decode.</summary>
    private static byte[] EncodePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0x33, 0x77, 0xCC));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
        try { _tempDir.Delete(recursive: true); } catch { /* best effort */ }
    }
}
