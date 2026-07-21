using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Tests.DataAccess.Repositories;

/// <summary>
/// Covers <c>AppSettingsRepository.GetSettingsWithIncludesAsync</c>: the get-or-create
/// of the singleton settings row (Id = 1) and the three ordered <c>.Include</c>s.
/// </summary>
public class AppSettingsRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public AppSettingsRepositoryTests()
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
    public async Task WhenNoSettingsRowExistsThenGetSettingsWithIncludesCreatesIt()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // Empty DB: nothing to load.
        (await uow.AppSettings.GetSettingsAsync()).Should().BeNull();

        var settings = await uow.AppSettings.GetSettingsWithIncludesAsync();

        settings.Should().NotBeNull();
        settings.Id.Should().Be(1, "the settings row is a singleton keyed on Id = 1");
        settings.LoraSources.Should().BeEmpty();
        settings.DatasetCategories.Should().BeEmpty();
        settings.ImageGalleries.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenCreatedSettingsAreSavedThenTheyArePersisted()
    {
        // The repository only Adds — the caller owns the SaveChanges (see AppSettingsService).
        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.AppSettings.GetSettingsWithIncludesAsync();
            await uow.SaveChangesAsync();
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var persisted = await uow.AppSettings.GetSettingsAsync();

            persisted.Should().NotBeNull();
            persisted!.Id.Should().Be(1);
        }
    }

    [Fact]
    public async Task WhenCalledTwiceThenTheSameRowIsReturnedAndNoDuplicateIsCreated()
    {
        int firstId;
        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var created = await uow.AppSettings.GetSettingsWithIncludesAsync();
            created.EncryptedCivitaiApiKey = "first-run-marker";
            await uow.SaveChangesAsync();
            firstId = created.Id;
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var second = await uow.AppSettings.GetSettingsWithIncludesAsync();
            await uow.SaveChangesAsync();

            second.Id.Should().Be(firstId);
            second.EncryptedCivitaiApiKey.Should()
                .Be("first-run-marker", "the existing row must be loaded, not replaced by a fresh default");

            var allRows = await uow.AppSettings.GetAllAsync();
            allRows.Should().HaveCount(1, "the singleton invariant forbids a second settings row");
        }
    }

    [Fact]
    public async Task WhenChildCollectionsExistThenTheyAreReturnedOrderedByOrder()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var settings = await uow.AppSettings.GetSettingsWithIncludesAsync();

            // Inserted deliberately out of order, so insertion order (Id) != Order.
            settings.LoraSources.Add(new LoraSource { FolderPath = @"C:\lora\third", Order = 2 });
            settings.LoraSources.Add(new LoraSource { FolderPath = @"C:\lora\first", Order = 0 });
            settings.LoraSources.Add(new LoraSource { FolderPath = @"C:\lora\second", Order = 1 });

            settings.DatasetCategories.Add(new DatasetCategory { Name = "Concept", Order = 2 });
            settings.DatasetCategories.Add(new DatasetCategory { Name = "Character", Order = 0 });
            settings.DatasetCategories.Add(new DatasetCategory { Name = "Style", Order = 1 });

            settings.ImageGalleries.Add(new ImageGallery { FolderPath = @"C:\gallery\third", Order = 2 });
            settings.ImageGalleries.Add(new ImageGallery { FolderPath = @"C:\gallery\first", Order = 0 });
            settings.ImageGalleries.Add(new ImageGallery { FolderPath = @"C:\gallery\second", Order = 1 });

            await uow.SaveChangesAsync();
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            // Fresh UnitOfWork => fresh DbContext, so the ordering really comes from the query.
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var settings = await uow.AppSettings.GetSettingsWithIncludesAsync();

            settings.LoraSources.Select(s => s.FolderPath).Should().Equal(
                new[] { @"C:\lora\first", @"C:\lora\second", @"C:\lora\third" });
            settings.DatasetCategories.Select(c => c.Name).Should().Equal(
                new[] { "Character", "Style", "Concept" });
            settings.ImageGalleries.Select(g => g.FolderPath).Should().Equal(
                new[] { @"C:\gallery\first", @"C:\gallery\second", @"C:\gallery\third" });
        }
    }

    [Fact]
    public async Task WhenChildCollectionsExistThenGetSettingsAsyncDoesNotLoadThem()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var settings = await uow.AppSettings.GetSettingsWithIncludesAsync();
            settings.LoraSources.Add(new LoraSource { FolderPath = @"C:\lora\only", Order = 0 });
            await uow.SaveChangesAsync();
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var lightweight = await uow.AppSettings.GetSettingsAsync();

            lightweight.Should().NotBeNull();
            lightweight!.LoraSources.Should()
                .BeEmpty("GetSettingsAsync is the no-include overload");
        }
    }

    [Fact]
    public async Task WhenLoraSourceAddedThroughRepositoryThenItCanBeFoundById()
    {
        int sourceId;
        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.AppSettings.GetSettingsWithIncludesAsync();
            await uow.SaveChangesAsync();

            var source = new LoraSource { FolderPath = @"D:\Models\Lora", Order = 0, AppSettingsId = 1 };
            await uow.AppSettings.AddLoraSourceAsync(source);
            await uow.SaveChangesAsync();
            sourceId = source.Id;
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var found = await uow.AppSettings.FindLoraSourceByIdAsync(sourceId);

            found.Should().NotBeNull();
            found!.FolderPath.Should().Be(@"D:\Models\Lora");
        }
    }

    [Fact]
    public async Task WhenDatasetCategoriesAddedThenCountReflectsThem()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.AppSettings.GetSettingsWithIncludesAsync();
            await uow.SaveChangesAsync();

            await uow.AppSettings.AddDatasetCategoriesAsync(new[]
            {
                new DatasetCategory { Name = "Character", Order = 0, AppSettingsId = 1 },
                new DatasetCategory { Name = "Style", Order = 1, AppSettingsId = 1 }
            });
            await uow.SaveChangesAsync();
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var count = await uow.AppSettings.GetDatasetCategoryCountAsync();

            count.Should().Be(2);
        }
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
