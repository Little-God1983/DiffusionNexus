using System.Text.RegularExpressions;
using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DiffusionNexus.Tests.DataAccess.Repositories;

public class ModelRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public ModelRepositoryTests()
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

    [Fact]
    public async Task WhenModelAddedThenGetByIdReturnsIt()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = new Model
        {
            Name = "TestLora",
            Type = ModelType.LORA,
            Source = DataSource.LocalFile
        };

        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        var result = await uow.Models.GetByIdAsync(model.Id);

        result.Should().NotBeNull();
        result!.Name.Should().Be("TestLora");
    }

    [Fact]
    public async Task WhenGetModelsWithLocalFilesThenReturnsOnlyModelsWithLocalFiles()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var modelWithFile = CreateModelWithLocalFile("WithFile", "/path/to/file.safetensors");
        var modelWithoutFile = CreateModelWithLocalFile("WithoutFile", localPath: null);

        await uow.Models.AddAsync(modelWithFile);
        await uow.Models.AddAsync(modelWithoutFile);
        await uow.SaveChangesAsync();

        var results = await uow.Models.GetModelsWithLocalFilesAsync();

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("WithFile");
    }

    [Fact]
    public async Task WhenGetAllWithIncludesThenNavigationPropertiesAreLoaded()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = CreateModelWithLocalFile("FullModel", "/path/file.safetensors");
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        var results = await uow.Models.GetAllWithIncludesAsync();

        results.Should().HaveCount(1);
        results[0].Versions.Should().HaveCount(1);
        results[0].Versions.First().Files.Should().HaveCount(1);
    }

    [Fact]
    public async Task WhenFindByPredicateThenReturnsMatchingModels()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await uow.Models.AddAsync(new Model { Name = "LoraA", Type = ModelType.LORA });
        await uow.Models.AddAsync(new Model { Name = "Checkpoint", Type = ModelType.Checkpoint });
        await uow.SaveChangesAsync();

        var loras = await uow.Models.FindAsync(m => m.Type == ModelType.LORA);

        loras.Should().HaveCount(1);
        loras[0].Name.Should().Be("LoraA");
    }

    [Fact]
    public async Task WhenRemoveModelThenItIsDeleted()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = new Model { Name = "ToDelete" };
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        uow.Models.Remove(model);
        await uow.SaveChangesAsync();

        var result = await uow.Models.GetByIdAsync(model.Id);
        result.Should().BeNull();
    }

    [Fact]
    public async Task WhenDuplicateCivitaiIdAssignedToModelThenSaveThrows()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var modelA = new Model { Name = "ModelA", Type = ModelType.LORA, CivitaiId = 42 };
        var modelB = new Model { Name = "ModelB", Type = ModelType.LORA };
        await uow.Models.AddAsync(modelA);
        await uow.Models.AddAsync(modelB);
        await uow.SaveChangesAsync();

        modelB.CivitaiId = 42;

        var act = () => uow.SaveChangesAsync();
        await act.Should().ThrowAsync<DiffusionNexus.DataAccess.Exceptions.DatabaseOperationException>()
            .WithMessage("*UNIQUE constraint*");
    }

    [Fact]
    public async Task WhenDuplicateCivitaiIdAssignedToVersionThenSaveThrows()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var modelA = CreateModelWithLocalFile("ModelA", "/a.safetensors");
        modelA.Versions.First().CivitaiId = 99;
        var modelB = CreateModelWithLocalFile("ModelB", "/b.safetensors");
        await uow.Models.AddAsync(modelA);
        await uow.Models.AddAsync(modelB);
        await uow.SaveChangesAsync();

        modelB.Versions.First().CivitaiId = 99;

        var act = () => uow.SaveChangesAsync();
        await act.Should().ThrowAsync<DiffusionNexus.DataAccess.Exceptions.DatabaseOperationException>()
            .WithMessage("*UNIQUE constraint*");
    }

    [Fact]
    public async Task WhenCivitaiIdOwnershipCheckedThenDuplicateIsAvoided()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var modelA = new Model { Name = "ModelA", Type = ModelType.LORA, CivitaiId = 42 };
        var modelB = new Model { Name = "ModelB", Type = ModelType.LORA };
        await uow.Models.AddAsync(modelA);
        await uow.Models.AddAsync(modelB);
        await uow.SaveChangesAsync();

        // Guard: only assign if no other model owns the CivitaiId
        var allModels = await uow.Models.GetAllAsync();
        var existingOwner = allModels.FirstOrDefault(m => m.CivitaiId == 42);
        if (existingOwner is null || existingOwner.Id == modelB.Id)
        {
            modelB.CivitaiId = 42;
        }

        // Save should succeed because the guard prevented the duplicate assignment
        var act = () => uow.SaveChangesAsync();
        await act.Should().NotThrowAsync();

        // modelB should still have no CivitaiId
        modelB.CivitaiId.Should().BeNull();
    }

    [Fact]
    public async Task WhenFindByFileHashCalledThenReturnsMatchingModelCaseInsensitive()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = CreateModelWithLocalFile(
            "EdgerunnersLora",
            @"D:\Models\Lora\edgerunners.comfy.safetensors",
            hashSha256: "B9372B072DCC91FF1EFD707E060BE0C842210F073CF985E07166D38B2794028C");
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        // Civitai API returns hashes uppercase; locally we may have stored either case.
        var found = await uow.Models.FindByFileHashAsync(
            "b9372b072dcc91ff1efd707e060be0c842210f073cf985e07166d38b2794028c");

        found.Should().NotBeNull();
        found!.Name.Should().Be("EdgerunnersLora");
        found.Versions.Should().HaveCount(1);
        found.Versions.First().Files.Should().HaveCount(1);
    }

    [Fact]
    public async Task WhenFindByFileHashWithUnknownHashThenReturnsNull()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = CreateModelWithLocalFile(
            "OtherLora", @"D:\Models\Lora\other.safetensors",
            hashSha256: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        var found = await uow.Models.FindByFileHashAsync(
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF");

        found.Should().BeNull();
    }

    [Fact]
    public async Task WhenLocalFileHasSha256ButNoCivitaiIdThenHashIsReportedInstalled()
    {
        // Reproduces the "Civitai Browser does not show installed" bug:
        // a ModelVersion row with no CivitaiId can still be matched by file
        // hash against the API response, so the badge should appear.
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var orphan = CreateModelWithLocalFile(
            "OrphanModel",
            @"D:\Models\Lora\edgerunners.comfy.safetensors",
            hashSha256: "B9372B072DCC91FF1EFD707E060BE0C842210F073CF985E07166D38B2794028C");
        // Mirrors the broken DB state: CivitaiId is null on both Model and Version.
        await uow.Models.AddAsync(orphan);
        await uow.SaveChangesAsync();

        var hashes = await uow.Models.GetInstalledFileHashesAsync();

        hashes.Should().ContainSingle();
        hashes.Should().Contain("b9372b072dcc91ff1efd707e060be0c842210f073cf985e07166d38b2794028c");
    }

    [Fact]
    public async Task WhenLocalFileIsMarkedInvalidThenHashIsExcluded()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = CreateModelWithLocalFile(
            "Stale", @"D:\Models\gone.safetensors",
            hashSha256: "DEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEF");
        // File was verified missing — should not be in the installed set.
        model.Versions.First().Files.First().IsLocalFileValid = false;
        model.Versions.First().Files.First().LocalFileVerifiedAt = DateTimeOffset.UtcNow;
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        var hashes = await uow.Models.GetInstalledFileHashesAsync();

        hashes.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenAllowedRootsExcludeFilePathThenHashIsExcluded()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var inside = CreateModelWithLocalFile(
            "Inside", @"D:\Models\Lora\a.safetensors",
            hashSha256: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        var outside = CreateModelWithLocalFile(
            "Outside", @"E:\Other\b.safetensors",
            hashSha256: "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");
        await uow.Models.AddAsync(inside);
        await uow.Models.AddAsync(outside);
        await uow.SaveChangesAsync();

        var hashes = await uow.Models.GetInstalledFileHashesAsync(new[] { @"D:\Models\Lora" });

        hashes.Should().ContainSingle();
        hashes.Should().Contain("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    }

    private static Model CreateModelWithLocalFile(string name, string? localPath, string? hashSha256 = null)
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

        var file = new ModelFile
        {
            FileName = $"{name}.safetensors",
            LocalPath = localPath,
            IsPrimary = true,
            IsLocalFileValid = localPath is not null,
            HashSHA256 = hashSha256,
            ModelVersion = version
        };

        version.Files.Add(file);
        model.Versions.Add(version);
        return model;
    }

    [Fact]
    public void WhenMediaTypeIsVideoThenIsVideoReturnsTrue()
    {
        var image = new ModelImage { Url = "https://example.com/preview.mp4", MediaType = "video" };
        image.IsVideo.Should().BeTrue();
    }

    [Fact]
    public void WhenMediaTypeIsImageThenIsVideoReturnsFalse()
    {
        var image = new ModelImage { Url = "https://example.com/preview.jpg", MediaType = "image" };
        image.IsVideo.Should().BeFalse();
    }

    [Fact]
    public void WhenMediaTypeIsNullThenIsVideoReturnsFalse()
    {
        var image = new ModelImage { Url = "https://example.com/preview.jpg" };
        image.IsVideo.Should().BeFalse();
    }

    [Fact]
    public async Task WhenModelImageHasVideoMediaTypeThenItIsPersisted()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = CreateModelWithLocalFile("VideoModel", "/video.safetensors");
        var version = model.Versions.First();
        version.Images.Add(new ModelImage
        {
            Url = "https://civitai.com/some-preview.mp4",
            MediaType = "video",
            ModelVersion = version,
            SortOrder = 0
        });

        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        var loaded = (await uow.Models.GetAllWithIncludesAsync()).First();
        var loadedImage = loaded.Versions.First().Images.First();

        loadedImage.MediaType.Should().Be("video");
        loadedImage.IsVideo.Should().BeTrue();
    }

    [Fact]
    public void WhenMediaTypeIsNullAndUrlHasMp4ExtensionThenIsVideoReturnsFalse()
    {
        // IsVideo only checks MediaType, not URL — URL fallback is in the ViewModel
        var image = new ModelImage { Url = "https://example.com/preview.mp4" };
        image.IsVideo.Should().BeFalse();
    }

    [Fact]
    public async Task WhenModelHasLastSyncedAtButNoCivitaiIdThenItIsNotResynced()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var synced = new Model
        {
            Name = "SyncedNoCivitaiId",
            Type = ModelType.LORA,
            Source = DataSource.CivitaiApi,
            LastSyncedAt = DateTimeOffset.UtcNow,
            CivitaiId = null // Guard prevented assignment (duplicate)
        };
        var unsynced = new Model
        {
            Name = "NeverSynced",
            Type = ModelType.LORA,
            Source = DataSource.LocalFile,
            LastSyncedAt = null,
            CivitaiId = null
        };

        await uow.Models.AddAsync(synced);
        await uow.Models.AddAsync(unsynced);
        await uow.SaveChangesAsync();

        var all = await uow.Models.GetAllAsync();
        var needingSync = all.Where(m => m is { CivitaiId: null, LastSyncedAt: null }).ToList();

        needingSync.Should().HaveCount(1);
        needingSync[0].Name.Should().Be("NeverSynced");
    }

    [Fact]
    public async Task WhenModelsShareCivitaiModelPageIdThenBothArePersisted()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var modelA = new Model
        {
            Name = "Ellie ZIT", Type = ModelType.LORA,
            CivitaiId = 100, CivitaiModelPageId = 100
        };
        var modelB = new Model
        {
            Name = "Ellie Flux", Type = ModelType.LORA,
            CivitaiId = null, CivitaiModelPageId = 100
        };

        await uow.Models.AddAsync(modelA);
        await uow.Models.AddAsync(modelB);
        await uow.SaveChangesAsync();

        var all = await uow.Models.GetAllAsync();
        var sameGroup = all.Where(m => m.CivitaiModelPageId == 100).ToList();

        sameGroup.Should().HaveCount(2);
    }

    /// <summary>
    /// The "light" tile load exists precisely so that opening the LoRA viewer does not pull every
    /// thumbnail BLOB in the library into memory — the projection says as much in a comment. It did
    /// it anyway: <c>ThumbnailData.Length</c> has no SQLite translation, so EF answered the
    /// <c>!= null</c> half in SQL, <b>selected the column</b>, and finished the comparison in
    /// process. Asserted against the SQL the provider actually emitted, the same way
    /// <c>SyncStateRepositoryThumbnailTests.ThumbnailCandidates_NeverSelectTheBlobColumn</c> does:
    /// the flag needs the column, so the SQL mentions it — but only inside <c>IS NOT NULL</c> and
    /// <c>&lt;&gt; X''</c>, neither of which hands the bytes over. Neutralise exactly those two
    /// forms and nothing may be left.
    /// </summary>
    [Fact]
    public async Task GetModelsWithLocalFilesLight_NeverSelectsTheThumbnailBlob()
    {
        var sql = new List<string>();

        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options
            .UseSqlite(connection)
            .LogTo(sql.Add, [DbLoggerCategory.Database.Command.Name], LogLevel.Information));

        using var provider = services.BuildServiceProvider();

        using (var seedScope = provider.CreateScope())
        {
            seedScope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>().Database.EnsureCreated();

            var uow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var model = CreateModelWithLocalFile("Heavy", @"C:\m\heavy.safetensors");
            var version = model.Versions.First();
            version.Images.Add(new ModelImage
            {
                Url = "https://image.civitai.com/abc/width=450/still.jpeg",
                SortOrder = 0,
                // A megabyte of it, so a projection that dragged it along would be unmistakable.
                ThumbnailData = new byte[1024 * 1024],
                ThumbnailMimeType = "image/jpeg",
                ModelVersion = version,
            });

            await uow.Models.AddAsync(model);
            await uow.SaveChangesAsync();
        }

        sql.Clear();

        using (var scope = provider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var models = await uow.Models.GetModelsWithLocalFilesLightAsync();

            var image = models.Should().ContainSingle().Subject
                .Versions.Should().ContainSingle().Subject
                .Images.Should().ContainSingle().Subject;

            image.IsThumbnailDeferred.Should().BeTrue(
                "the tile is told a thumbnail exists so it can lazy-load it, and told nothing more");
        }

        var captured = string.Join("\n", sql);

        // Positive first: if the flag ever stopped being computed in SQL the assertion below would
        // pass for the wrong reason.
        captured.Should().Contain("ThumbnailData", "the has-a-thumbnail flag is answered inside SQLite");

        var withoutFlag = Regex.Replace(
            captured,
            @"""\w+""\.""ThumbnailData"" (IS NOT NULL|<> X'')",
            "<has-thumbnail-flag>");

        withoutFlag.Should().NotContain("ThumbnailData",
            "anything left is the BLOB column in the SELECT list, and this query reads every image row in the library");

        // And the whole column list, not just the one that hurts. Task 8 had to add two scalars
        // (ThumbnailAttemptedAt, ThumbnailFailure) so the tile can honour the same retry window the
        // sync step does; pinning the exact set is what stops the next such addition from being an
        // `Include` or a whole-entity select nobody notices until the library is 4 000 models deep.
        ImageColumnsTouchedBy(sql).Should().BeEquivalentTo(ExpectedLightImageColumns);
    }

    /// <summary>
    /// Every column the image projection references, taken from the SQL the provider emitted.
    /// Includes the one the has-a-thumbnail flag tests but never hands over.
    /// </summary>
    private static IReadOnlyCollection<string> ImageColumnsTouchedBy(IEnumerable<string> sql)
    {
        var imageQuery = sql.Single(s => s.Contains("FROM \"ModelImages\""));

        return Regex.Matches(imageQuery, @"""\w+""\.""(\w+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static readonly string[] ExpectedLightImageColumns =
    [
        "Id", "ModelVersionId", "CivitaiId", "Url", "MediaType", "IsNsfw", "NsfwLevel",
        "Width", "Height", "BlurHash", "SortOrder", "CreatedAt", "PostId", "Username",
        "ThumbnailMimeType", "ThumbnailWidth", "ThumbnailHeight",
        "ThumbnailAttemptedAt", "ThumbnailFailure",
        "LocalCachePath", "IsLocalCacheValid", "CachedAt", "CachedFileSize",
        "Prompt", "NegativePrompt", "Seed", "Steps", "Sampler", "CfgScale",
        "GenerationModel", "DenoisingStrength", "LikeCount", "HeartCount", "CommentCount",
        // Tested for emptiness by the has-a-thumbnail flag, never selected — see the test above.
        "ThumbnailData",
    ];

    /// <summary>
    /// The tile's scroll path refuses to re-fetch a row whose last attempt says not to, and the
    /// light query is where the tile gets its rows. A projection that dropped these two stamps
    /// would hand every image a blank slate — which the retry policy reads as "never attempted",
    /// putting the per-scroll re-fetch of a dead poster URL straight back.
    /// </summary>
    [Fact]
    public async Task GetModelsWithLocalFilesLight_CarriesTheRetryStampsTheTileMustHonour()
    {
        var attemptedAt = new DateTimeOffset(2026, 8, 20, 9, 30, 0, TimeSpan.Zero);

        using (var seedScope = _serviceProvider.CreateScope())
        {
            var seed = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var model = CreateModelWithLocalFile("Stamped", @"C:\m\stamped.safetensors");
            var version = model.Versions.First();
            version.Images.Add(new ModelImage
            {
                Url = "https://image.civitai.com/abc/width=450/gone.jpeg",
                SortOrder = 0,
                ThumbnailAttemptedAt = attemptedAt,
                ThumbnailFailure = ThumbnailFailureReason.Http404,
                ModelVersion = version,
            });

            await seed.Models.AddAsync(model);
            await seed.SaveChangesAsync();
        }

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var loaded = await uow.Models.GetModelsWithLocalFilesLightAsync();

        var image = loaded.Should().ContainSingle().Subject
            .Versions.Should().ContainSingle().Subject
            .Images.Should().ContainSingle().Subject;

        image.ThumbnailAttemptedAt.Should().Be(attemptedAt);
        image.ThumbnailFailure.Should().Be(ThumbnailFailureReason.Http404);
        image.ThumbnailData.Should().BeNull("a row that never got bytes is not deferred, it is empty");
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
