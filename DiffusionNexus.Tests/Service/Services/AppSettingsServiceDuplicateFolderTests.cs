using DiffusionNexus.DataAccess;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Service.Services;

/// <summary>
/// The three Settings folder lists (Generation Galleries, LoRA sources, Base Model
/// Folders) must never contain the same folder twice. Duplicates are dropped when a
/// snapshot is saved (case-insensitive, trailing-separator tolerant) and rows that
/// already sit duplicated in the database — historical concurrent startup saves —
/// are pruned on the next settings read, mirroring the existing category dedupe.
/// </summary>
public sealed class AppSettingsServiceDuplicateFolderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public AppSettingsServiceDuplicateFolderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var secureStorageMock = new Mock<ISecureStorage>();
        secureStorageMock.Setup(s => s.Encrypt(It.IsAny<string?>())).Returns<string?>(v => v);
        secureStorageMock.Setup(s => s.Decrypt(It.IsAny<string?>())).Returns<string?>(v => v);

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));
        services.AddSingleton(secureStorageMock.Object);
        services.AddTransient<IAppSettingsService, AppSettingsService>();

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider
            .GetRequiredService<DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext>();
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private IAppSettingsService CreateService(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<IAppSettingsService>();

    private static AppSettings Snapshot(Action<AppSettings> mutate)
    {
        var settings = new AppSettings
        {
            Id = 1,
            BackupDatasetImagesEnabled = false,
            BackupDatabaseEnabled = false,
        };
        mutate(settings);
        return settings;
    }

    [Fact]
    public async Task SaveSettings_DropsDuplicateGalleryRows()
    {
        using var scope = _serviceProvider.CreateScope();
        var service = CreateService(scope);

        await service.SaveSettingsAsync(Snapshot(s =>
        {
            s.ImageGalleries.Add(new ImageGallery { FolderPath = @"E:\AI\outputs", IsEnabled = true, Order = 0 });
            s.ImageGalleries.Add(new ImageGallery { FolderPath = @"e:\ai\OUTPUTS\", IsEnabled = true, Order = 1 });
        }));

        var saved = await service.GetSettingsAsync();
        saved.ImageGalleries.Should().ContainSingle()
            .Which.FolderPath.Should().Be(@"E:\AI\outputs",
                "the first occurrence wins; case and trailing separators do not make a path distinct");
    }

    [Fact]
    public async Task SaveSettings_DropsDuplicateLoraSourceRows()
    {
        using var scope = _serviceProvider.CreateScope();
        var service = CreateService(scope);

        await service.SaveSettingsAsync(Snapshot(s =>
        {
            s.LoraSources.Add(new LoraSource { FolderPath = @"D:\Models\Lora\", IsEnabled = true, Order = 0 });
            s.LoraSources.Add(new LoraSource { FolderPath = @"D:\Models\Lora", IsEnabled = false, Order = 1 });
        }));

        var saved = await service.GetSettingsAsync();
        saved.LoraSources.Should().ContainSingle();
    }

    [Fact]
    public async Task SaveSettings_DropsDuplicateBaseModelFolders_KeepingTheDefaultFlaggedRow()
    {
        using var scope = _serviceProvider.CreateScope();
        var service = CreateService(scope);

        await service.SaveSettingsAsync(Snapshot(s =>
        {
            s.BaseModelFolders.Add(new BaseModelFolder { FolderPath = @"E:\AI\Models", IsEnabled = true, Order = 0 });
            s.BaseModelFolders.Add(new BaseModelFolder { FolderPath = @"e:\ai\models", IsEnabled = true, IsDefault = true, Order = 1 });
        }));

        var saved = await service.GetSettingsAsync();
        saved.BaseModelFolders.Should().ContainSingle()
            .Which.IsDefault.Should().BeTrue("the ⭐ default row must survive the merge");
    }

    [Fact]
    public async Task GetSettings_PrunesDuplicateRows_AlreadyInTheDatabase()
    {
        // Seed duplicates directly through the DbContext — exactly the state older
        // app versions left behind (e.g. the outputs gallery registered twice).
        using (var seedScope = _serviceProvider.CreateScope())
        {
            var service = CreateService(seedScope);
            await service.GetSettingsAsync(); // ensure row Id=1 exists

            var context = seedScope.ServiceProvider
                .GetRequiredService<DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext>();
            context.ImageGalleries.AddRange(
                new ImageGallery { AppSettingsId = 1, FolderPath = @"E:\App\outputs", IsEnabled = true, Order = 0 },
                new ImageGallery { AppSettingsId = 1, FolderPath = @"E:\App\outputs", IsEnabled = true, Order = 1 },
                new ImageGallery { AppSettingsId = 1, FolderPath = @"E:\App\other", IsEnabled = true, Order = 2 });
            context.LoraSources.AddRange(
                new LoraSource { AppSettingsId = 1, FolderPath = @"D:\Loras", IsEnabled = true, Order = 0 },
                new LoraSource { AppSettingsId = 1, FolderPath = @"d:\loras\", IsEnabled = true, Order = 1 });
            context.BaseModelFolders.AddRange(
                new BaseModelFolder { AppSettingsId = 1, FolderPath = @"E:\Models", IsEnabled = true, Order = 0 },
                new BaseModelFolder { AppSettingsId = 1, FolderPath = @"E:\Models", IsEnabled = true, IsDefault = true, Order = 1 });
            await context.SaveChangesAsync();
        }

        using var scope = _serviceProvider.CreateScope();
        var service2 = CreateService(scope);
        var settings = await service2.GetSettingsAsync();

        settings.ImageGalleries.Select(g => g.FolderPath).Should().BeEquivalentTo(@"E:\App\outputs", @"E:\App\other");
        settings.LoraSources.Should().ContainSingle();
        settings.BaseModelFolders.Should().ContainSingle()
            .Which.IsDefault.Should().BeTrue("the ⭐ default duplicate is the one worth keeping");

        // And the database itself is cleaned, not just this context's view.
        using var verifyScope = _serviceProvider.CreateScope();
        var verifyContext = verifyScope.ServiceProvider
            .GetRequiredService<DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext>();
        (await verifyContext.ImageGalleries.CountAsync()).Should().Be(2);
        (await verifyContext.LoraSources.CountAsync()).Should().Be(1);
        (await verifyContext.BaseModelFolders.CountAsync()).Should().Be(1);
    }
}
