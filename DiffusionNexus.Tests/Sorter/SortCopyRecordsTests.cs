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

    private async Task<int> SeedOriginalAsync()
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
        version.Files.Add(new ModelFile
        {
            FileName = "abyssal.safetensors", LocalPath = SourcePath, FileSizeBytes = 228_454_348, SizeKB = 223_100,
            HashSHA256 = "B9A53649803F6D350B6525562015065E74A251ED2B71BA9BFE64AD6C7F1A78DA",
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
