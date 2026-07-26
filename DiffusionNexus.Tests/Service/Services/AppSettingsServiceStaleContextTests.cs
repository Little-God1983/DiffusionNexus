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
/// The app holds one long-lived DbContext per view model (each captures a transient
/// <see cref="IAppSettingsService"/> at startup). EF's identity map never evicts
/// entities deleted by ANOTHER context: a tracking re-query returns the stale graph
/// including phantom children, so removed folders would reappear in the Settings UI,
/// keep being scanned, and poison the next save with a 0-row DELETE/UPDATE
/// (DbUpdateConcurrencyException). These tests pin the fix: every settings read
/// clears the unit's change tracker first, so reads always reflect database truth.
/// </summary>
public sealed class AppSettingsServiceStaleContextTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public AppSettingsServiceStaleContextTests()
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

    /// <summary>Detached snapshot with backups disabled so SaveSettingsAsync never vetoes.</summary>
    private static AppSettings Snapshot(Action<AppSettings>? mutate = null)
    {
        var settings = new AppSettings
        {
            Id = 1,
            BackupDatasetImagesEnabled = false,
            BackupDatabaseEnabled = false,
        };
        mutate?.Invoke(settings);
        return settings;
    }

    [Fact]
    public async Task GetSettingsAsync_DoesNotReturnRows_DeletedByAnotherContext()
    {
        // Long-lived service A tracks the settings graph (as every VM does at startup).
        using var scopeA = _serviceProvider.CreateScope();
        var serviceA = scopeA.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await serviceA.SaveSettingsAsync(Snapshot(s =>
        {
            s.LoraSources.Add(new LoraSource { FolderPath = @"D:\Loras", IsEnabled = true });
            s.ImageGalleries.Add(new ImageGallery { FolderPath = @"D:\Output", IsEnabled = true });
            s.BaseModelFolders.Add(new BaseModelFolder { FolderPath = @"D:\Models", IsEnabled = true });
        }));
        (await serviceA.GetSettingsAsync()).LoraSources.Should().ContainSingle();

        // A different context (the Installer Manager's remove flow) deletes the rows.
        using (var scopeB = _serviceProvider.CreateScope())
        {
            var serviceB = scopeB.ServiceProvider.GetRequiredService<IAppSettingsService>();
            await serviceB.SaveSettingsAsync(Snapshot());
        }

        // Service A must see the deletion — no phantom rows from its identity map.
        var reread = await serviceA.GetSettingsAsync();
        reread.LoraSources.Should().BeEmpty("rows deleted by another context must not survive as phantoms");
        reread.ImageGalleries.Should().BeEmpty();
        reread.BaseModelFolders.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveSettingsAsync_Succeeds_AfterAnotherContextDeletedARow()
    {
        using var scopeA = _serviceProvider.CreateScope();
        var serviceA = scopeA.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await serviceA.SaveSettingsAsync(Snapshot(s =>
            s.LoraSources.Add(new LoraSource { FolderPath = @"D:\Loras", IsEnabled = true })));
        var trackedId = (await serviceA.GetSettingsAsync()).LoraSources.Single().Id;

        using (var scopeB = _serviceProvider.CreateScope())
        {
            var serviceB = scopeB.ServiceProvider.GetRequiredService<IAppSettingsService>();
            await serviceB.SaveSettingsAsync(Snapshot());
        }

        // Saving unrelated changes must not replay a DELETE/UPDATE for the phantom
        // (previously: DbUpdateConcurrencyException -> every save fails until restart).
        var act = async () => await serviceA.SaveSettingsAsync(Snapshot(s => s.ShowNsfw = true));

        await act.Should().NotThrowAsync();
        (await serviceA.GetSettingsAsync()).LoraSources.Should().BeEmpty();
    }
}
