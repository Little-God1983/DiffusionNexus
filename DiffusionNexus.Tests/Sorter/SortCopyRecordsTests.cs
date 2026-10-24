using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.Services.Lora.Sorting;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Tests.Sorter;

/// <summary>
/// #653: a copy sort left the new file without a record, so the next library scan gave it a bare
/// "???" one and it lost its name, base model and tags. The library keeps one record chain per file
/// on disk; a copy gets its own chain carrying the original's metadata, without the Civitai ids only
/// one chain may hold.
/// </summary>
public sealed class SortCopyRecordsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public SortCopyRecordsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();
        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private const string SourcePath = @"D:\Loras\flat\abyssal.safetensors";
    private const string CopyPath = @"D:\Loras\Sorted\SDXL 1.0\Style\abyssal.safetensors";

    private const string Sha = "B9A53649803F6D350B6525562015065E74A251ED2B71BA9BFE64AD6C7F1A78DA";

    private async Task<int> SeedOriginalAsync(string sourcePath = SourcePath, string? sha = Sha,
        string imageUrl = "https://image.civitai.com/x.jpeg")
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        var style = new Tag { Name = "style" };
        var creator = new Creator { Username = "maker" };
        var model = new Model
        {
            Name = "Abyssal Tech", Type = ModelType.LORA, Source = DataSource.CivitaiApi,
            CivitaiId = 562478, CivitaiModelPageId = 562478, Description = "desc",
            IsNsfw = true, UserNsfwRating = false, IsUserEdited = true, UserCategory = CivitaiCategory.Style,
            AllowDerivatives = true, Creator = creator,
        };
        model.Tags.Add(new ModelTag { Model = model, Tag = style });
        var version = new ModelVersion
        {
            Name = "v1", CivitaiId = 626601, BaseModelRaw = "SDXL 1.0",
            IsUserEdited = true, Model = model,
        };
        version.TriggerWords.Add(new TriggerWord { Word = "abyssal", Order = 0, ModelVersion = version });
        version.Images.Add(new ModelImage
        {
            Url = imageUrl, SortOrder = 0, ThumbnailData = [1, 2, 3],
            ThumbnailMimeType = "image/jpeg", ModelVersion = version,
        });
        model.SyncState = new ModelSyncState
        {
            Model = model, MetadataOutcome = SyncOutcome.Matched, MetadataCheckedAt = DateTimeOffset.UtcNow.AddDays(-3),
        };
        version.Files.Add(new ModelFile
        {
            FileName = "abyssal.safetensors", LocalPath = sourcePath, FileSizeBytes = 228_454_348, SizeKB = 223_100,
            HashSHA256 = sha,
            Format = FileFormat.SafeTensor, IsPrimary = true, IsLocalFileValid = true, ModelVersion = version,
        });
        model.Versions.Add(version);
        context.Models.Add(model);
        await context.SaveChangesAsync();
        return model.Id;
    }

    private async Task<List<Model>> ModelsWithFileAtAsync(string path)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        return await context.Models.AsNoTracking()
            .Include(m => m.Tags).ThenInclude(t => t.Tag)
            .Include(m => m.Versions).ThenInclude(v => v.Files)
            .Include(m => m.Versions).ThenInclude(v => v.TriggerWords)
            .Where(m => m.Versions.Any(v => v.Files.Any(f => f.LocalPath == path)))
            .ToListAsync();
    }

    private DbLocalPathUpdater Updater() => new(_serviceProvider.GetRequiredService<IServiceScopeFactory>());

    [Fact]
    public async Task ACopyGetsItsOwnRecordWithTheOriginalsMetadata()
    {
        var originalId = await SeedOriginalAsync();

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        var copy = (await ModelsWithFileAtAsync(CopyPath)).Should().ContainSingle().Subject;
        copy.Id.Should().NotBe(originalId, "its own chain, as the library keeps one per file");
        (copy.Name, copy.Type, copy.Source, copy.CivitaiModelPageId, copy.Description)
            .Should().Be(("Abyssal Tech", ModelType.LORA, DataSource.CivitaiApi, 562478, "desc"));
        (copy.IsNsfw, copy.UserNsfwRating, copy.IsUserEdited, copy.UserCategory, copy.AllowDerivatives)
            .Should().Be((true, false, true, CivitaiCategory.Style, true));
        copy.CreatorId.Should().NotBeNull();
        copy.CivitaiId.Should().BeNull("only one chain may hold the Civitai model id");
        copy.Tags.Select(t => t.Tag!.Name).Should().Equal("style");

        var version = copy.Versions.Should().ContainSingle().Subject;
        (version.Name, version.BaseModelRaw, version.IsUserEdited)
            .Should().Be(("v1", "SDXL 1.0", true));
        version.CivitaiId.Should().BeNull("only one chain may hold the Civitai version id");
        version.TriggerWords.Select(w => w.Word).Should().Equal("abyssal");

        var file = version.Files.Should().ContainSingle().Subject;
        (file.LocalPath, file.FileName, file.FileSizeBytes, file.HashSHA256, file.IsLocalFileValid, file.IsPrimary)
            .Should().Be((CopyPath, "abyssal.safetensors", 228_454_348L,
                "B9A53649803F6D350B6525562015065E74A251ED2B71BA9BFE64AD6C7F1A78DA", true, true));
    }

    /// <summary>
    /// Review: tiles are grouped per source folder, so a copy in another source is a tile of its
    /// own — and the image sync never fetches for a version without a Civitai id. Without the
    /// original's images that tile had no thumbnail, permanently.
    /// </summary>
    [Fact]
    public async Task ACopyCarriesTheOriginalsImages()
    {
        await SeedOriginalAsync();

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        var image = await context.Set<ModelImage>().AsNoTracking()
            .Where(i => i.ModelVersion!.Files.Any(f => f.LocalPath == CopyPath)).SingleAsync();
        (image.Url, image.ThumbnailMimeType).Should().Be(("https://image.civitai.com/x.jpeg", "image/jpeg"));
        image.ThumbnailData.Should().Equal(1, 2, 3);
    }

    /// <summary>Review: with no sync state, the library sync took the copy for an unidentified
    /// model and hashed it and asked Civitai again for what was just copied.</summary>
    [Fact]
    public async Task ACopyCarriesTheOriginalsSyncState()
    {
        await SeedOriginalAsync();

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        var state = await context.Set<ModelSyncState>().AsNoTracking()
            .Where(s => s.Model!.Versions.Any(v => v.Files.Any(f => f.LocalPath == CopyPath))).SingleAsync();
        state.MetadataOutcome.Should().Be(SyncOutcome.Matched);
        state.MetadataCheckedAt.Should().NotBeNull();
    }

    /// <summary>
    /// Review: the path lookup lowered the path in .NET but compared it with SQLite's lower(), which
    /// only folds ASCII — a path with "Ö" never matched, so the copy went unregistered.
    /// </summary>
    [Fact]
    public async Task AnOriginalUnderANonAsciiFolderIsFound()
    {
        const string source = @"D:\Öffentlich\Loras\abyssal.safetensors";
        await SeedOriginalAsync(source);

        await Updater().RegisterCopiesAsync([(source, CopyPath)]);

        (await ModelsWithFileAtAsync(CopyPath)).Should().ContainSingle();
    }

    /// <summary>
    /// Review: the copy is written with overwrite off, so whatever record still points at its path
    /// is stale — a file deleted outside the app (the test lab's reset leaves exactly these). It
    /// made registration skip, and the library then showed the new file under the stale record's
    /// name and base model.
    /// </summary>
    [Fact]
    public async Task AStaleRecordOnTheCopysPathLetsGoAndTheCopyIsRegistered()
    {
        await SeedOriginalAsync();
        int staleFileId;
        using (var scope = _serviceProvider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
            var stale = new Model { Name = "Some other LoRA", Type = ModelType.LORA, Source = DataSource.LocalFile };
            var version = new ModelVersion { Name = "v1", BaseModelRaw = "Pony", Model = stale };
            var file = new ModelFile { FileName = "abyssal.safetensors", LocalPath = CopyPath, IsLocalFileValid = false,
                HashSHA256 = "0000000000000000000000000000000000000000000000000000000000000000", ModelVersion = version };
            version.Files.Add(file);
            stale.Versions.Add(version);
            context.Models.Add(stale);
            await context.SaveChangesAsync();
            staleFileId = file.Id;
        }

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        (await ModelsWithFileAtAsync(CopyPath)).Should().ContainSingle().Which.Name.Should().Be("Abyssal Tech");
        using var verify = _serviceProvider.CreateScope();
        var staleFile = await verify.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>()
            .Set<ModelFile>().AsNoTracking().SingleAsync(f => f.Id == staleFileId);
        staleFile.LocalPath.Should().BeNull();
        staleFile.IsLocalFileValid.Should().BeFalse();
    }

    /// <summary>Review round 2: an original never hashed (a bare record) let any record on the
    /// copy's path count as "already registered", so a stale one kept it. File size tells them
    /// apart: a copy has the original's size.</summary>
    [Fact]
    public async Task AStaleRecordLetsGoEvenWhenTheOriginalHasNoHash()
    {
        await SeedOriginalAsync(sha: null);
        var staleFileId = await SeedStaleRecordAsync(sizeBytes: 9_999);

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        (await ModelsWithFileAtAsync(CopyPath)).Should().ContainSingle().Which.Name.Should().Be("Abyssal Tech");
        (await FileAsync(staleFileId)).LocalPath.Should().BeNull();
    }

    [Fact]
    public async Task AnUnhashedCopyAlreadyRegisteredGetsNoSecondRecord()
    {
        await SeedOriginalAsync(sha: null);

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);
        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        (await ModelsWithFileAtAsync(CopyPath)).Should().ContainSingle();
    }

    /// <summary>Review round 2: the copy deleted and made again by a second run kept its record
    /// flagged missing until the next full verify.</summary>
    [Fact]
    public async Task ACopyMadeAgainIsMarkedPresentAgain()
    {
        await SeedOriginalAsync();
        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);
        int copyFileId;
        using (var scope = _serviceProvider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
            var copyFile = await context.Set<ModelFile>().SingleAsync(f => f.LocalPath == CopyPath);
            copyFile.IsLocalFileValid = false;
            await context.SaveChangesAsync();
            copyFileId = copyFile.Id;
        }

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        (await FileAsync(copyFileId)).IsLocalFileValid.Should().BeTrue();
    }

    /// <summary>Review round 2: two records on the original's path (a historic duplicate) were
    /// picked by validity alone, so a bare "???" twin could lend the copy its empty metadata.</summary>
    [Fact]
    public async Task TheIdentifiedRecordIsTheOriginalNotABareTwin()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
            var bare = new Model { Name = "abyssal", Type = ModelType.LORA, Source = DataSource.LocalFile };
            var version = new ModelVersion { Name = "abyssal", BaseModelRaw = "???", Model = bare };
            version.Files.Add(new ModelFile { FileName = "abyssal.safetensors", LocalPath = SourcePath, IsLocalFileValid = true, ModelVersion = version });
            bare.Versions.Add(version);
            context.Models.Add(bare);
            await context.SaveChangesAsync();
        }
        await SeedOriginalAsync();

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        var copy = (await ModelsWithFileAtAsync(CopyPath)).Should().ContainSingle().Subject;
        copy.Name.Should().Be("Abyssal Tech");
        copy.Versions.Single().BaseModelRaw.Should().Be("SDXL 1.0");
    }

    /// <summary>Review round 2: a local preview image (file://…) kept pointing at the original's
    /// preview, not the one the sorter copied next to the copy.</summary>
    [Fact]
    public async Task ALocalPreviewImagePointsAtTheCopysOwnPreview()
    {
        await SeedOriginalAsync(imageUrl: @"file://D:\Loras\flat\abyssal.preview.png");

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        using var scope = _serviceProvider.CreateScope();
        var image = await scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>().Set<ModelImage>()
            .AsNoTracking().Where(i => i.ModelVersion!.Files.Any(f => f.LocalPath == CopyPath)).SingleAsync();
        image.Url.Should().Be(@"file://D:\Loras\Sorted\SDXL 1.0\Style\abyssal.preview.png");
    }

    private async Task<int> SeedStaleRecordAsync(long sizeBytes)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        var stale = new Model { Name = "Some other LoRA", Type = ModelType.LORA, Source = DataSource.LocalFile };
        var version = new ModelVersion { Name = "v1", BaseModelRaw = "Pony", Model = stale };
        var file = new ModelFile { FileName = "abyssal.safetensors", LocalPath = CopyPath, FileSizeBytes = sizeBytes,
            IsLocalFileValid = false, ModelVersion = version };
        version.Files.Add(file);
        stale.Versions.Add(version);
        context.Models.Add(stale);
        await context.SaveChangesAsync();
        return file.Id;
    }

    private async Task<ModelFile> FileAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>()
            .Set<ModelFile>().AsNoTracking().SingleAsync(f => f.Id == id);
    }

    [Fact]
    public async Task TheOriginalIsLeftAsItWas()
    {
        var originalId = await SeedOriginalAsync();

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        var original = (await ModelsWithFileAtAsync(SourcePath)).Should().ContainSingle().Subject;
        original.Id.Should().Be(originalId);
        original.CivitaiId.Should().Be(562478);
        original.Versions.Single().CivitaiId.Should().Be(626601);
    }

    /// <summary>A scan, or an earlier run, may already have given the copy a record.</summary>
    [Fact]
    public async Task ACopyThatAlreadyHasARecordGetsNoSecondOne()
    {
        await SeedOriginalAsync();

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);
        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        (await ModelsWithFileAtAsync(CopyPath)).Should().ContainSingle();
    }

    [Fact]
    public async Task ACopyOfAFileWithNoRecordIsLeftToTheScan()
    {
        await Updater().RegisterCopiesAsync([(@"D:\Loras\unknown.safetensors", @"D:\Loras\Sorted\unknown.safetensors")]);

        (await ModelsWithFileAtAsync(@"D:\Loras\Sorted\unknown.safetensors")).Should().BeEmpty();
    }
}
