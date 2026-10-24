using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
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
/// "???" one and it lost its name, base model and tags. A copy is attached to the original's
/// version as one more file, the way a re-download of a known version is (#380,
/// LoraDownloadService): the schema allows many files per version, and the Installed tab builds
/// one tile per (page, source folder) with a version → file map, so the copy shares all of the
/// original's metadata — Civitai ids, images, sync state, later edits — with nothing duplicated.
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

    /// <returns>The original's version id.</returns>
    private async Task<int> SeedOriginalAsync(string sourcePath = SourcePath, string? sha = Sha)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        var model = new Model
        {
            Name = "Abyssal Tech", Type = ModelType.LORA, Source = DataSource.CivitaiApi,
            CivitaiId = 562478, CivitaiModelPageId = 562478,
        };
        model.Tags.Add(new ModelTag { Model = model, Tag = new Tag { Name = "style" } });
        var version = new ModelVersion { Name = "v1", CivitaiId = 626601, BaseModelRaw = "SDXL 1.0", Model = model };
        version.Files.Add(new ModelFile
        {
            FileName = "abyssal.safetensors", LocalPath = sourcePath, FileSizeBytes = 228_454_348, SizeKB = 223_100,
            HashSHA256 = sha, Format = FileFormat.SafeTensor, IsPrimary = true, IsLocalFileValid = true,
            ModelVersion = version,
        });
        model.Versions.Add(version);
        context.Models.Add(model);
        await context.SaveChangesAsync();
        return version.Id;
    }

    private async Task<List<ModelFile>> FilesAtAsync(string path)
    {
        using var scope = _serviceProvider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>().Set<ModelFile>()
            .AsNoTracking().Include(f => f.ModelVersion!).ThenInclude(v => v.Model)
            .Where(f => f.LocalPath == path).ToListAsync();
    }

    private async Task<ModelFile> FileAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>()
            .Set<ModelFile>().AsNoTracking().SingleAsync(f => f.Id == id);
    }

    private async Task<int> ModelCountAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>().Models.CountAsync();
    }

    /// <summary>A record of another LoRA left on <paramref name="path"/>: its file was deleted
    /// outside the app, or a scan gave the copy a bare record before the sorter registered it.</summary>
    private async Task<int> SeedOtherRecordAsync(string path, string? sha, long? sizeBytes, string baseModel = "Pony")
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        var other = new Model { Name = "Some other LoRA", Type = ModelType.LORA, Source = DataSource.LocalFile };
        var version = new ModelVersion { Name = "v1", BaseModelRaw = baseModel, Model = other };
        var file = new ModelFile { FileName = Path.GetFileName(path), LocalPath = path, HashSHA256 = sha,
            FileSizeBytes = sizeBytes, IsLocalFileValid = false, ModelVersion = version };
        version.Files.Add(file);
        other.Versions.Add(version);
        context.Models.Add(other);
        await context.SaveChangesAsync();
        return file.Id;
    }

    private DbLocalPathUpdater Updater() => new(_serviceProvider.GetRequiredService<IServiceScopeFactory>());

    [Fact]
    public async Task ACopyIsAttachedToTheOriginalsVersion()
    {
        var versionId = await SeedOriginalAsync();
        var models = await ModelCountAsync();

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        var copy = (await FilesAtAsync(CopyPath)).Should().ContainSingle().Subject;
        copy.ModelVersionId.Should().Be(versionId, "one more file of the same version, as #380 does for a re-download");
        copy.ModelVersion!.Model!.Name.Should().Be("Abyssal Tech", "so the copy shows the original's metadata");
        (copy.FileName, copy.FileSizeBytes, copy.HashSHA256, copy.Format, copy.IsLocalFileValid, copy.IsPrimary)
            .Should().Be(("abyssal.safetensors", 228_454_348L, Sha, FileFormat.SafeTensor, true, false));
        (await ModelCountAsync()).Should().Be(models, "nothing is cloned");
    }

    [Fact]
    public async Task TheOriginalIsLeftAsItWas()
    {
        await SeedOriginalAsync();

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        var original = (await FilesAtAsync(SourcePath)).Should().ContainSingle().Subject;
        (original.IsPrimary, original.IsLocalFileValid).Should().Be((true, true));
    }

    /// <summary>A scan, or an earlier run, may already have attached the copy.</summary>
    [Fact]
    public async Task ACopyAlreadyAttachedGetsNoSecondRecord()
    {
        await SeedOriginalAsync();

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);
        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        (await FilesAtAsync(CopyPath)).Should().ContainSingle();
    }

    [Fact]
    public async Task ACopyOfAFileWithNoRecordIsLeftToTheScan()
    {
        await Updater().RegisterCopiesAsync([(@"D:\Loras\unknown.safetensors", @"D:\Loras\Sorted\unknown.safetensors")]);

        (await FilesAtAsync(@"D:\Loras\Sorted\unknown.safetensors")).Should().BeEmpty();
    }

    /// <summary>Review: SQLite's lower() folds ASCII only, so a path with "Ö" never matched and the
    /// copy went unregistered.</summary>
    [Fact]
    public async Task AnOriginalUnderANonAsciiFolderIsFound()
    {
        const string source = @"D:\Öffentlich\Loras\abyssal.safetensors";
        await SeedOriginalAsync(source);

        await Updater().RegisterCopiesAsync([(source, CopyPath)]);

        (await FilesAtAsync(CopyPath)).Should().ContainSingle();
    }

    /// <summary>
    /// Review: the copy is written with overwrite off, so a record of another LoRA still on its path
    /// is stale (the test lab's reset leaves exactly these). It made registration skip, and the
    /// library showed the copy under that record's name and base model. It lets go of the path.
    /// </summary>
    [Fact]
    public async Task AnotherLorasStaleRecordOnTheCopysPathLetsGo()
    {
        var versionId = await SeedOriginalAsync();
        var staleId = await SeedOtherRecordAsync(CopyPath, sha: new string('0', 64), sizeBytes: 9_999);

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        (await FilesAtAsync(CopyPath)).Should().ContainSingle().Which.ModelVersionId.Should().Be(versionId);
        var stale = await FileAsync(staleId);
        (stale.LocalPath, stale.IsLocalFileValid).Should().Be((null, false));
    }

    /// <summary>Review round 4: a bare "???" record a scan made for the copy carries the copy's very
    /// hash, and was kept as "the copy's own" — the copy stayed "???" for good. Only a file of the
    /// original's version is the copy's own; a bare one lets go.</summary>
    [Fact]
    public async Task ABareRecordWithTheCopysHashLetsGo()
    {
        var versionId = await SeedOriginalAsync();
        var bareId = await SeedOtherRecordAsync(CopyPath, sha: Sha, sizeBytes: 228_454_348, baseModel: "???");

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        (await FilesAtAsync(CopyPath)).Should().ContainSingle().Which.ModelVersionId.Should().Be(versionId);
        (await FileAsync(bareId)).LocalPath.Should().BeNull();
    }

    /// <summary>Review round 4: for an original never hashed, a record of a different LoRA that only
    /// happened to have the same size was taken for the copy's own.</summary>
    [Fact]
    public async Task ASameSizeRecordOfAnotherLoraLetsGoEvenWithoutAHash()
    {
        var versionId = await SeedOriginalAsync(sha: null);
        var otherId = await SeedOtherRecordAsync(CopyPath, sha: null, sizeBytes: 228_454_348);

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        (await FilesAtAsync(CopyPath)).Should().ContainSingle().Which.ModelVersionId.Should().Be(versionId);
        (await FileAsync(otherId)).LocalPath.Should().BeNull();
    }

    /// <summary>Review round 2: the copy deleted and made again by a later run kept its record
    /// flagged missing until the next full verify.</summary>
    [Fact]
    public async Task ACopyMadeAgainIsMarkedPresentAgain()
    {
        await SeedOriginalAsync();
        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);
        int copyId;
        using (var scope = _serviceProvider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
            var copyFile = await context.Set<ModelFile>().SingleAsync(f => f.LocalPath == CopyPath);
            copyFile.IsLocalFileValid = false;
            await context.SaveChangesAsync();
            copyId = copyFile.Id;
        }

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        (await FileAsync(copyId)).IsLocalFileValid.Should().BeTrue();
    }

    /// <summary>Review round 2: two records on the original's path (a historic duplicate) were
    /// picked by validity alone, so a bare "???" twin could become the copy's version.</summary>
    [Fact]
    public async Task TheIdentifiedRecordIsTheOriginalNotABareTwin()
    {
        await SeedOtherRecordAsync(SourcePath, sha: null, sizeBytes: null, baseModel: "???");
        var versionId = await SeedOriginalAsync();
        using (var scope = _serviceProvider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
            foreach (var twin in context.Set<ModelFile>().Where(f => f.LocalPath == SourcePath))
                twin.IsLocalFileValid = true;
            await context.SaveChangesAsync();
        }

        await Updater().RegisterCopiesAsync([(SourcePath, CopyPath)]);

        (await FilesAtAsync(CopyPath)).Should().ContainSingle().Which.ModelVersionId.Should().Be(versionId);
    }
}
