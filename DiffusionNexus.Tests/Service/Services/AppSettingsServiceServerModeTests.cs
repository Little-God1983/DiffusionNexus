using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Service.Services;

/// <summary>
/// #606 code review 1 (F2). The feature router reads the Server mode on every governed readiness
/// check, so <see cref="IAppSettingsService.GetComfyUiServerModeAsync"/> must be a light read-only
/// query: no settings row created, no default categories seeded, nothing left tracked.
/// </summary>
public sealed class AppSettingsServiceServerModeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public AppSettingsServiceServerModeTests()
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
        scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    [Theory]
    [InlineData(ComfyUiServerMode.Engine)]
    [InlineData(ComfyUiServerMode.CustomUrl)]
    public async Task ReturnsTheStoredMode_AndLeavesTheDatabaseAlone(ComfyUiServerMode stored)
    {
        using (var seed = _serviceProvider.CreateScope())
        {
            var context = seed.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
            context.AppSettings.Add(new AppSettings
            {
                Id = 1, ComfyUiServerMode = stored, UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            });
            await context.SaveChangesAsync();
        }

        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();

        var mode = await service.GetComfyUiServerModeAsync();

        mode.Should().Be(stored);
        var db = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        db.ChangeTracker.Entries().Should().BeEmpty("the read must not track the settings row");
        (await db.DatasetCategories.CountAsync()).Should().Be(0, "unlike GetSettingsAsync it seeds nothing");
        (await db.AppSettings.AsNoTracking().SingleAsync()).UpdatedAt
            .Should().Be(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task WithoutASettingsRow_ReturnsEngine_AndCreatesNoRow()
    {
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();

        var mode = await service.GetComfyUiServerModeAsync();

        mode.Should().Be(ComfyUiServerMode.Engine, "a fresh database defaults to the Engine");
        var db = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        (await db.AppSettings.CountAsync()).Should().Be(0);
        (await db.DatasetCategories.CountAsync()).Should().Be(0);
    }

    // #606 code review 2 (G7): every Generate and the ComfyUI wrapper singleton read the mode and
    // the URL; that read must be as light as the mode read above.
    [Theory]
    [InlineData(ComfyUiServerMode.Engine, "http://127.0.0.1:8188/")]
    [InlineData(ComfyUiServerMode.CustomUrl, "http://192.168.1.20:8188/")]
    public async Task Connection_ReturnsTheStoredModeAndUrl_AndLeavesTheDatabaseAlone(ComfyUiServerMode stored, string url)
    {
        var updatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using (var seed = _serviceProvider.CreateScope())
        {
            var context = seed.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
            context.AppSettings.Add(new AppSettings
            {
                Id = 1, ComfyUiServerMode = stored, ComfyUiServerUrl = url, UpdatedAt = updatedAt
            });
            await context.SaveChangesAsync();
        }

        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();

        var connection = await service.GetComfyUiServerConnectionAsync();

        connection.Should().Be(new ComfyUiServerConnection(stored, url));
        var db = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        db.ChangeTracker.Entries().Should().BeEmpty("the read must not track the settings row");
        (await db.DatasetCategories.CountAsync()).Should().Be(0, "unlike GetSettingsAsync it seeds nothing");
        (await db.AppSettings.AsNoTracking().SingleAsync()).UpdatedAt.Should().Be(updatedAt);
    }

    [Fact]
    public async Task Connection_WithoutASettingsRow_ReturnsEngineWithNoUrl_AndCreatesNoRow()
    {
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();

        var connection = await service.GetComfyUiServerConnectionAsync();

        connection.Should().Be(new ComfyUiServerConnection(ComfyUiServerMode.Engine, null));
        var db = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        (await db.AppSettings.CountAsync()).Should().Be(0);
        (await db.DatasetCategories.CountAsync()).Should().Be(0);
    }
}
