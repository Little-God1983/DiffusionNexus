using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Service.Services;

/// <summary>
/// Verifies that the Civitai API key is always read fresh from the database,
/// reproducing the stale-entity bug where a long-lived AppSettingsService
/// returns null because its EF Core DbContext cached the AppSettings entity
/// before the key was saved via a separate scope.
/// </summary>
public class AppSettingsServiceCivitaiKeyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public AppSettingsServiceCivitaiKeyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // Use a pass-through "encryption": Encrypt returns the text as-is.
        var secureStorageMock = new Mock<ISecureStorage>();
        secureStorageMock
            .Setup(s => s.Encrypt(It.IsAny<string?>()))
            .Returns<string?>(v => v);
        secureStorageMock
            .Setup(s => s.Decrypt(It.IsAny<string?>()))
            .Returns<string?>(v => v);

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));
        services.AddSingleton(secureStorageMock.Object);
        services.AddTransient<IAppSettingsService, AppSettingsService>();

        _serviceProvider = services.BuildServiceProvider();

        // Ensure database schema exists
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider
            .GetRequiredService<DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext>();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task WhenApiKeySavedInOneScopeThenFreshScopeReturnsLatestKey()
    {
        // Arrange: simulate app startup — read settings with no API key
        using var longLivedScope = _serviceProvider.CreateScope();
        var staleService = longLivedScope.ServiceProvider.GetRequiredService<IAppSettingsService>();

        var initialKey = await staleService.GetCivitaiApiKeyAsync();
        initialKey.Should().BeNull("no key has been saved yet");

        // Act: save the key from a different scope (as Settings VM would)
        using (var settingsScope = _serviceProvider.CreateScope())
        {
            var settingsService = settingsScope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            await settingsService.SetCivitaiApiKeyAsync("my-secret-key");
        }

        // Assert: a fresh scope sees the new key
        using var freshScope = _serviceProvider.CreateScope();
        var freshService = freshScope.ServiceProvider.GetRequiredService<IAppSettingsService>();

        var freshKey = await freshService.GetCivitaiApiKeyAsync();
        freshKey.Should().Be("my-secret-key", "a fresh scope must read the latest value from the database");
    }

    [Fact]
    public async Task WhenApiKeySavedInOneScope_LongLivedServiceSeesUpdate()
    {
        // Historically the inverse held (a long-lived AppSettingsService returned
        // the stale cached entity, and this test pinned that bug). GetSettingsAsync
        // now clears the change tracker before reading, so even a long-lived
        // service always reflects database truth — updates AND deletions made by
        // other contexts (see AppSettingsServiceStaleContextTests).

        // Arrange: read settings to populate the change tracker
        using var longLivedScope = _serviceProvider.CreateScope();
        var longLivedService = longLivedScope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await longLivedService.GetSettingsAsync();

        // Act: save the key from a different scope
        using (var settingsScope = _serviceProvider.CreateScope())
        {
            var settingsService = settingsScope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            await settingsService.SetCivitaiApiKeyAsync("my-secret-key");
        }

        // Assert: the long-lived service reads the fresh value
        var key = await longLivedService.GetCivitaiApiKeyAsync();
        key.Should().Be("my-secret-key", "settings reads must reflect database truth, not the identity map");
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
