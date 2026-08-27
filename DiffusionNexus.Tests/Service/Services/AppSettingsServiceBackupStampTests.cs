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
/// #531. <see cref="AppSettings.LastBackupAt"/> is machine-local bookkeeping with a dedicated
/// writer (<see cref="IAppSettingsService.UpdateLastBackupAtAsync"/>) — nothing on the Settings
/// page edits it, so no detached snapshot a caller builds can carry a real value for it. While it
/// sat in <c>SaveSettingsAsync</c>'s scalar whitelist, every such save copied the snapshot's CLR
/// default <c>null</c> over the stored timestamp: open Settings, press Save, and the record of the
/// last backup was gone — <see cref="DiffusionNexus.Service.Services.BackupScheduler"/> then read
/// "never backed up" and treated a fresh backup as overdue.
/// <para>
/// The guarantee is the service's, not any one caller's: it holds however the snapshot was built,
/// which is why the column was removed from the whitelist rather than carried through the Settings
/// ViewModel. Same reasoning, same shape as the <see cref="AppSettings.LastLibrarySyncAt"/> pair in
/// <c>AppSettingsServiceSyncSettingsTests</c>.
/// </para>
/// </summary>
public sealed class AppSettingsServiceBackupStampTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("dn-appsettings-backup-tests-");

    public AppSettingsServiceBackupStampTests()
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

    /// <summary>
    /// Seeds the singleton row (<c>GetSettingsAsync</c> creates it on first read, as every real
    /// startup path does) and stamps a backup through the dedicated writer — the state any user
    /// who has ever run a backup is in.
    /// </summary>
    private async Task StampBackupAsync(DateTimeOffset at)
    {
        using var scope = _serviceProvider.CreateScope();
        var service = CreateService(scope);
        await service.GetSettingsAsync();
        await service.UpdateLastBackupAtAsync(at);
    }

    [Fact]
    public async Task SaveSettings_LeavesLastBackupAtUntouched_WhenTheDetachedSnapshotDoesNotCarryIt()
    {
        var backedUpAt = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        await StampBackupAsync(backedUpAt);

        // The Settings page's save command: a detached AppSettings built fresh, with LastBackupAt
        // left at its default null because the page has no such field to read.
        using (var scope = _serviceProvider.CreateScope())
        {
            await CreateService(scope).SaveSettingsAsync(new AppSettings { Id = 1, MaxBackups = 7 });
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var settings = await CreateService(scope).GetSettingsAsync();

            settings.LastBackupAt.Should().Be(backedUpAt,
                "a settings-page save must not erase the backup flow's own stamp — the scheduler reads it " +
                "to decide staleness, so nulling it silently makes a fresh backup look overdue");
            settings.MaxBackups.Should().Be(7,
                "the save must still persist what the page DOES edit");
        }
    }

    /// <summary>
    /// The same guarantee through the second door: <c>SettingsExportService.ImportAsync</c> builds
    /// its own detached snapshot from the export DTO, which carries no <c>LastBackupAt</c> either.
    /// </summary>
    [Fact]
    public async Task ExportImport_DoesNotDisturbLastBackupAt()
    {
        var backedUpAt = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var path = Path.Combine(_tempDir.FullName, "backup-stamp-export.json");

        await StampBackupAsync(backedUpAt);

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

            settings.LastBackupAt.Should().Be(backedUpAt,
                "a settings import must not disturb machine-local backup bookkeeping");
        }
    }

    /// <summary>
    /// The column is still writable — the dedicated writer is the one path that may change it, and
    /// removing it from the save whitelist must not leave it frozen at its first value.
    /// </summary>
    [Fact]
    public async Task UpdateLastBackupAt_StillMovesTheStampForward()
    {
        var first = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var second = new DateTimeOffset(2026, 8, 20, 6, 30, 0, TimeSpan.Zero);

        await StampBackupAsync(first);
        await StampBackupAsync(second);

        using var scope = _serviceProvider.CreateScope();
        var settings = await CreateService(scope).GetSettingsAsync();

        settings.LastBackupAt.Should().Be(second, "the backup flow's own writer still owns this column");
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
