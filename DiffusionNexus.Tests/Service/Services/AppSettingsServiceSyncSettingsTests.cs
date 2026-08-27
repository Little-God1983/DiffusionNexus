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
/// Covers the persistence gap discovered while wiring the Settings page's "Metadata
/// Sync" section (Task 6): <see cref="AppSettingsService.SaveSettingsAsync"/> updates
/// an explicit whitelist of scalar columns on the tracked, freshly-loaded entity
/// rather than attaching the caller's detached snapshot wholesale — Task 1 added the
/// three sync columns to <see cref="AppSettings"/> but never added them to that
/// whitelist, so a Settings-page save silently discarded any change the user made to
/// them. This class proves the fix (the columns now round-trip) and the deliberate
/// non-fix for <see cref="AppSettings.LastLibrarySyncAt"/>: that column is stamped
/// only via the dedicated <see cref="IAppSettingsService.UpdateLastLibrarySyncAtAsync"/>
/// path (Task 5), so it is intentionally left OUT of the whitelist — the freshly
/// loaded tracked entity already carries the real value, and copying a detached
/// snapshot's default-initialized value over it would reproduce the silent-null bug
/// that <c>LastBackupAt</c> had on this exact code path until #531 took it out of the
/// whitelist too (see <c>AppSettingsServiceBackupStampTests</c>).
/// </summary>
public sealed class AppSettingsServiceSyncSettingsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("dn-appsettings-sync-tests-");

    public AppSettingsServiceSyncSettingsTests()
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
        services.AddTransient<ISettingsExportService, SettingsExportService>();

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider
            .GetRequiredService<DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext>();
        context.Database.EnsureCreated();
    }

    private IAppSettingsService CreateService(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<IAppSettingsService>();

    private ISettingsExportService CreateExportService(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<ISettingsExportService>();

    private string PathFor(string fileName) => Path.Combine(_tempDir.FullName, fileName);

    [Fact]
    public async Task SaveSettings_PersistsSyncRetryAndConcurrencyColumns()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var service = CreateService(scope);
            var settings = new AppSettings
            {
                Id = 1,
                SyncNotIdentifiedRetryDays = 60,
                SyncErrorRetryDays = 7,
                SyncThumbnailConcurrency = 8,
            };

            await service.SaveSettingsAsync(settings);
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var service = CreateService(scope);
            var settings = await service.GetSettingsAsync();

            settings.SyncNotIdentifiedRetryDays.Should().Be(60,
                "the Metadata Sync section's not-identified retry window must survive a settings save");
            settings.SyncErrorRetryDays.Should().Be(7,
                "the Metadata Sync section's error retry window must survive a settings save");
            settings.SyncThumbnailConcurrency.Should().Be(8,
                "the Metadata Sync section's thumbnail concurrency must survive a settings save");
        }
    }

    [Fact]
    public async Task SaveSettings_LeavesLastLibrarySyncAtUntouched_WhenTheDetachedSnapshotDoesNotCarryIt()
    {
        var stampedAt = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        using (var scope = _serviceProvider.CreateScope())
        {
            var service = CreateService(scope);
            // Seeds the singleton row (GetSettingsAsync creates it on first read, same as
            // every real startup path); UpdateLastLibrarySyncAtAsync's own read no-ops on
            // a still-missing row, which the real app never hits — Task 5's flow always
            // calls the full GetSettingsAsync (seeding path) before ever stamping.
            await service.GetSettingsAsync();
            await service.UpdateLastLibrarySyncAtAsync(stampedAt);
        }

        // Simulates the Settings page's save command: a detached AppSettings built
        // fresh (LastLibrarySyncAt left at its default null, since the field is not
        // user-editable there).
        using (var scope = _serviceProvider.CreateScope())
        {
            var service = CreateService(scope);
            await service.SaveSettingsAsync(new AppSettings { Id = 1, SyncErrorRetryDays = 3 });
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var service = CreateService(scope);
            var settings = await service.GetSettingsAsync();

            settings.LastLibrarySyncAt.Should().Be(stampedAt,
                "a settings-page save must not null out the sync flow's own stamp");
            settings.SyncErrorRetryDays.Should().Be(3);
        }
    }

    /// <summary>
    /// End-to-end proof for the fix round 1 finding: <c>SettingsExportService.ImportAsync</c>
    /// built its detached <see cref="AppSettings"/> snapshot straight from
    /// <c>SettingsExportData</c>, which had no Sync* properties — so re-importing an export
    /// file always reset these three columns to the DTO's CLR defaults (30/1/4), even when
    /// the user's real values (and the export file itself) carried something else. This
    /// exercises the real <see cref="SettingsExportService"/> against the real
    /// <see cref="AppSettingsService"/> and a real SQLite database, not mocks.
    /// </summary>
    [Fact]
    public async Task ExportImport_RoundTripsSyncRetryAndConcurrencyColumns()
    {
        var path = PathFor("sync-export.json");

        using (var scope = _serviceProvider.CreateScope())
        {
            var service = CreateService(scope);
            await service.SaveSettingsAsync(new AppSettings
            {
                Id = 1,
                SyncNotIdentifiedRetryDays = 60,
                SyncErrorRetryDays = 7,
                SyncThumbnailConcurrency = 8,
            });
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            await CreateExportService(scope).ExportAsync(path);
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            await CreateExportService(scope).ImportAsync(path);
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var settings = await CreateService(scope).GetSettingsAsync();

            settings.SyncNotIdentifiedRetryDays.Should().Be(60,
                "a real export/import round trip must not reset the retry window to the DTO default");
            settings.SyncErrorRetryDays.Should().Be(7);
            settings.SyncThumbnailConcurrency.Should().Be(8);
        }
    }

    /// <summary>
    /// The other half of the <see cref="AppSettings.LastLibrarySyncAt"/> preservation
    /// guarantee (the ViewModel-level half lives in <c>SettingsViewModelSyncSettingsTests</c>,
    /// and the DTO-level half — that <c>ImportAsync</c> never invents a value — lives in
    /// <c>SettingsExportServiceTests</c>): a real settings export followed by a real import
    /// must leave a previously stamped sync timestamp untouched, end to end.
    /// </summary>
    [Fact]
    public async Task ExportImport_DoesNotDisturbLastLibrarySyncAt()
    {
        var stampedAt = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var path = PathFor("sync-stamp-export.json");

        using (var scope = _serviceProvider.CreateScope())
        {
            var service = CreateService(scope);
            // Seeds the row first — see the comment in the sibling test above.
            await service.GetSettingsAsync();
            await service.UpdateLastLibrarySyncAtAsync(stampedAt);
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            await CreateExportService(scope).ExportAsync(path);
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            await CreateExportService(scope).ImportAsync(path);
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var settings = await CreateService(scope).GetSettingsAsync();

            settings.LastLibrarySyncAt.Should().Be(stampedAt,
                "a settings import must not disturb the sync flow's own machine-local stamp");
        }
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
        try
        {
            _tempDir.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort — never fail a test run on temp-folder cleanup.
        }
    }
}
