using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Tests.DataAccess.Repositories;

/// <summary>
/// Covers <c>ModelFileRepository.GetExistingLocalPathsAsync</c> (the case-insensitive
/// dedup set <c>ModelFileSyncService</c> relies on) and
/// <c>FindBySizeWithInvalidPathAsync</c> (moved-file detection).
/// </summary>
public class ModelFileRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public ModelFileRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options =>
            options.UseSqlite(_connection));

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider
            .GetRequiredService<DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext>();
        context.Database.EnsureCreated();
    }

    #region GetExistingLocalPathsAsync

    [Fact]
    public async Task WhenLocalPathsQueriedThenTheSetUsesOrdinalIgnoreCaseComparer()
    {
        await SeedAsync(CreateModel("Alpha", NewFile("alpha.safetensors", @"D:\Models\Lora\Alpha.safetensors")));

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var paths = await uow.ModelFiles.GetExistingLocalPathsAsync();

        paths.Comparer.Should().Be(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenLookupCasingDiffersThenPathIsStillFound()
    {
        await SeedAsync(CreateModel("Alpha", NewFile("alpha.safetensors", @"D:\Models\Lora\Alpha.safetensors")));

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var paths = await uow.ModelFiles.GetExistingLocalPathsAsync();

        paths.Should().Contain(@"D:\Models\Lora\Alpha.safetensors");
        paths.Contains(@"d:\models\lora\alpha.SAFETENSORS").Should()
            .BeTrue("the sync service dedups Windows paths that differ only by casing");
    }

    [Fact]
    public async Task WhenTwoRowsDifferOnlyByCasingThenTheSetCollapsesThem()
    {
        await SeedAsync(
            CreateModel("Alpha", NewFile("alpha.safetensors", @"D:\Models\Lora\Alpha.safetensors")),
            CreateModel("AlphaDupe", NewFile("alpha.safetensors", @"d:\models\lora\alpha.safetensors")));

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var paths = await uow.ModelFiles.GetExistingLocalPathsAsync();

        paths.Should().HaveCount(1, "two DB rows for the same Windows path must not count twice");
    }

    [Fact]
    public async Task WhenFileHasNoLocalPathThenItIsNotInTheExistingPathSet()
    {
        await SeedAsync(
            CreateModel("Local", NewFile("local.safetensors", @"D:\Models\Lora\local.safetensors")),
            CreateModel("Remote", NewFile("remote.safetensors", localPath: null)));

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var paths = await uow.ModelFiles.GetExistingLocalPathsAsync();

        paths.Should().ContainSingle().Which.Should().Be(@"D:\Models\Lora\local.safetensors");
    }

    [Fact]
    public async Task WhenNoFilesExistThenTheExistingPathSetIsEmpty()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var paths = await uow.ModelFiles.GetExistingLocalPathsAsync();

        paths.Should().BeEmpty();
    }

    #endregion

    #region FindBySizeWithInvalidPathAsync

    [Fact]
    public async Task WhenSizeMatchesAndLocalFileIsInvalidThenTheCandidateIsReturned()
    {
        await SeedAsync(CreateModel("Moved", NewFile(
            "moved.safetensors",
            @"D:\Models\Lora\old-location.safetensors",
            fileSizeBytes: 144_703_488,
            isLocalFileValid: false)));

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var candidates = await uow.ModelFiles.FindBySizeWithInvalidPathAsync(144_703_488);

        candidates.Should().ContainSingle();
        candidates[0].FileName.Should().Be("moved.safetensors");
        candidates[0].LocalPath.Should().Be(@"D:\Models\Lora\old-location.safetensors");
    }

    [Fact]
    public async Task WhenNoFileHasTheRequestedSizeThenNoCandidatesAreReturned()
    {
        await SeedAsync(CreateModel("Moved", NewFile(
            "moved.safetensors",
            @"D:\Models\Lora\old-location.safetensors",
            fileSizeBytes: 144_703_488,
            isLocalFileValid: false)));

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var candidates = await uow.ModelFiles.FindBySizeWithInvalidPathAsync(999_999);

        candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenSizeMatchesButLocalFileIsStillValidThenItIsFilteredOut()
    {
        await SeedAsync(CreateModel("Present", NewFile(
            "present.safetensors",
            @"D:\Models\Lora\present.safetensors",
            fileSizeBytes: 144_703_488,
            isLocalFileValid: true)));

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var candidates = await uow.ModelFiles.FindBySizeWithInvalidPathAsync(144_703_488);

        candidates.Should().BeEmpty("a file that is still where we expect it has not been moved");
    }

    [Fact]
    public async Task WhenSizeMatchesButLocalPathIsNullThenItIsFilteredOut()
    {
        await SeedAsync(CreateModel("NeverDownloaded", NewFile(
            "never.safetensors",
            localPath: null,
            fileSizeBytes: 144_703_488,
            isLocalFileValid: false)));

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var candidates = await uow.ModelFiles.FindBySizeWithInvalidPathAsync(144_703_488);

        candidates.Should().BeEmpty("a file that was never downloaded is not a moved-file candidate");
    }

    [Fact]
    public async Task WhenSeveralInvalidFilesShareASizeThenAllAreReturnedForHashComparison()
    {
        await SeedAsync(
            CreateModel("CandidateA", NewFile(
                "a.safetensors", @"D:\Models\Lora\a.safetensors",
                fileSizeBytes: 144_703_488, isLocalFileValid: false)),
            CreateModel("CandidateB", NewFile(
                "b.safetensors", @"D:\Models\Lora\b.safetensors",
                fileSizeBytes: 144_703_488, isLocalFileValid: false)),
            CreateModel("OtherSize", NewFile(
                "c.safetensors", @"D:\Models\Lora\c.safetensors",
                fileSizeBytes: 5_000, isLocalFileValid: false)));

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var candidates = await uow.ModelFiles.FindBySizeWithInvalidPathAsync(144_703_488);

        candidates.Select(c => c.FileName).Should().BeEquivalentTo(new[] { "a.safetensors", "b.safetensors" });
    }

    [Fact]
    public async Task WhenFileSizeBytesIsNullThenItNeverMatchesASizeQuery()
    {
        await SeedAsync(CreateModel("NoSize", NewFile(
            "nosize.safetensors",
            @"D:\Models\Lora\nosize.safetensors",
            fileSizeBytes: null,
            isLocalFileValid: false)));

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var candidates = await uow.ModelFiles.FindBySizeWithInvalidPathAsync(0);

        candidates.Should().BeEmpty("a NULL size must not be treated as 0 bytes");
    }

    #endregion

    #region GetAllWithLocalPathAsync

    [Fact]
    public async Task WhenFilesQueriedByLocalPathThenOnlyRowsWithAPathAreReturned()
    {
        await SeedAsync(
            CreateModel("Local", NewFile("local.safetensors", @"D:\Models\Lora\local.safetensors")),
            CreateModel("Remote", NewFile("remote.safetensors", localPath: null)));

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var files = await uow.ModelFiles.GetAllWithLocalPathAsync();

        files.Should().ContainSingle();
        files[0].FileName.Should().Be("local.safetensors");
    }

    #endregion

    private async Task SeedAsync(params Model[] models)
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        foreach (var model in models)
            await uow.Models.AddAsync(model);

        await uow.SaveChangesAsync();
    }

    private static Model CreateModel(string name, params ModelFile[] files)
    {
        var model = new Model
        {
            Name = name,
            Type = ModelType.LORA,
            Source = DataSource.LocalFile
        };

        var version = new ModelVersion
        {
            Name = name,
            BaseModel = BaseModelType.Other,
            Model = model
        };

        foreach (var file in files)
        {
            file.ModelVersion = version;
            version.Files.Add(file);
        }

        model.Versions.Add(version);
        return model;
    }

    private static ModelFile NewFile(
        string fileName,
        string? localPath,
        long? fileSizeBytes = null,
        bool isLocalFileValid = true)
        => new()
        {
            FileName = fileName,
            LocalPath = localPath,
            FileSizeBytes = fileSizeBytes,
            IsLocalFileValid = isLocalFileValid,
            IsPrimary = true
        };

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
