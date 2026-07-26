using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.UnitOfWork;
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
/// Covers persistence of the Base Model Folders registry: collection sync through
/// <see cref="AppSettingsService.SaveSettingsAsync"/>, the single-default invariant,
/// and the idempotent targeted <c>AddBaseModelFolderAsync</c> used by auto-registration.
/// </summary>
public class AppSettingsServiceBaseModelFolderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public AppSettingsServiceBaseModelFolderTests()
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

    private IAppSettingsService CreateService(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<IAppSettingsService>();

    /// <summary>
    /// Builds the detached snapshot shape the Settings page passes to
    /// <see cref="IAppSettingsService.SaveSettingsAsync"/>.
    /// </summary>
    private static AppSettings Snapshot(params BaseModelFolder[] folders)
        => new() { Id = 1, BaseModelFolders = folders.ToList() };

    [Fact]
    public async Task SaveSettings_AddsUpdatesAndRemovesBaseModelFolders()
    {
        // Add two rows.
        using (var scope = _serviceProvider.CreateScope())
        {
            var service = CreateService(scope);
            await service.SaveSettingsAsync(Snapshot(
                new BaseModelFolder { FolderPath = @"D:\ModelsA", IsEnabled = true },
                new BaseModelFolder { FolderPath = @"D:\ModelsB", IsEnabled = false }));
        }

        // Update one, remove the other (detached snapshot carrying the persisted Id).
        using (var scope = _serviceProvider.CreateScope())
        {
            var service = CreateService(scope);
            var settings = await service.GetSettingsAsync();
            settings.BaseModelFolders.Should().HaveCount(2);
            var keepId = settings.BaseModelFolders.First(f => f.FolderPath == @"D:\ModelsA").Id;

            await service.SaveSettingsAsync(Snapshot(
                new BaseModelFolder { Id = keepId, FolderPath = @"D:\ModelsA", IsEnabled = false }));
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var service = CreateService(scope);
            var settings = await service.GetSettingsAsync();

            var folder = settings.BaseModelFolders.Should().ContainSingle().Subject;
            folder.FolderPath.Should().Be(@"D:\ModelsA");
            folder.IsEnabled.Should().BeFalse();
        }
    }

    [Fact]
    public async Task SaveSettings_KeepsOnlyTheLastDefault_WhenMultipleRowsAreFlagged()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var service = CreateService(scope);
            await service.SaveSettingsAsync(Snapshot(
                new BaseModelFolder { FolderPath = @"D:\First", IsDefault = true },
                new BaseModelFolder { FolderPath = @"D:\Second", IsDefault = true }));
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var service = CreateService(scope);
            var settings = await service.GetSettingsAsync();

            settings.BaseModelFolders.Where(f => f.IsDefault)
                .Should().ContainSingle()
                .Which.FolderPath.Should().Be(@"D:\Second", "the last flagged row wins");
        }
    }

    [Fact]
    public async Task AddBaseModelFolder_IsIdempotentByPath_AndRelinksPackage()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var service = CreateService(scope);
            (await service.AddBaseModelFolderAsync(@"D:\Shared\Models")).Should().BeTrue("a new row was inserted");
            (await service.AddBaseModelFolderAsync(@"d:\shared\MODELS", installerPackageId: null))
                .Should().BeFalse("the path already exists (case-insensitive)");
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            // Path comparison is case-insensitive: still one row.
            var service = CreateService(scope);
            var settings = await service.GetSettingsAsync();
            settings.BaseModelFolders.Should().ContainSingle();
        }

        // Re-adding with a package id links the existing row instead of duplicating it.
        int packageId;
        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var package = new InstallerPackage { Name = "ComfyUI", InstallationPath = @"D:\Comfy", ExecutablePath = @"D:\Comfy\run.bat" };
            await uow.InstallerPackages.AddAsync(package);
            await uow.SaveChangesAsync();
            packageId = package.Id;
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var service = CreateService(scope);
            await service.AddBaseModelFolderAsync(@"D:\Shared\Models", packageId);
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var service = CreateService(scope);
            var settings = await service.GetSettingsAsync();

            var folder = settings.BaseModelFolders.Should().ContainSingle().Subject;
            folder.InstallerPackageId.Should().Be(packageId);
            folder.IsDefault.Should().BeFalse("auto-registration must never claim the default flag");
        }
    }

    [Fact]
    public async Task GetEnabledBaseModelFolders_ReturnsOnlyEnabled_InOrder()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var service = CreateService(scope);
            await service.SaveSettingsAsync(Snapshot(
                new BaseModelFolder { FolderPath = @"D:\Zeta", IsEnabled = true },
                new BaseModelFolder { FolderPath = @"D:\Off", IsEnabled = false },
                new BaseModelFolder { FolderPath = @"D:\Alpha", IsEnabled = true }));
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var service = CreateService(scope);

            var enabled = await service.GetEnabledBaseModelFoldersAsync();

            enabled.Select(f => f.FolderPath).Should().Equal(@"D:\Zeta", @"D:\Alpha");
        }
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
