using DiffusionNexus.DataAccess;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Service.Services;

/// <summary>
/// Focused round-trip coverage for the Civitai Browser's saved-filter persistence pair
/// (<see cref="IAppSettingsService.GetCivitaiBrowserFilterJsonAsync"/> /
/// <see cref="IAppSettingsService.SetCivitaiBrowserFilterJsonAsync"/>), mirroring the LoRA
/// Viewer's equivalent <c>LoraViewerFilterJson</c> pair. Single slot — saving overwrites the
/// previous value, and a blank/whitespace save clears it back to null.
/// </summary>
public sealed class AppSettingsServiceCivitaiBrowserFilterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public AppSettingsServiceCivitaiBrowserFilterTests()
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

    [Fact]
    public async Task GetCivitaiBrowserFilterJsonAsync_ReturnsNull_WhenNeverSaved()
    {
        using var scope = _serviceProvider.CreateScope();
        var service = CreateService(scope);

        var json = await service.GetCivitaiBrowserFilterJsonAsync();

        json.Should().BeNull();
    }

    [Fact]
    public async Task SetThenGetCivitaiBrowserFilterJsonAsync_RoundTrips()
    {
        const string payload = """{"SelectedBaseModels":["Illustrious"],"ShowNsfw":false}""";

        using (var scope = _serviceProvider.CreateScope())
        {
            await CreateService(scope).SetCivitaiBrowserFilterJsonAsync(payload);
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var json = await CreateService(scope).GetCivitaiBrowserFilterJsonAsync();
            json.Should().Be(payload);
        }
    }

    [Fact]
    public async Task SetCivitaiBrowserFilterJsonAsync_SecondSaveOverwritesTheFirst()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            await CreateService(scope).SetCivitaiBrowserFilterJsonAsync("""{"SelectedBaseModels":["Old"]}""");
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            await CreateService(scope).SetCivitaiBrowserFilterJsonAsync("""{"SelectedBaseModels":["New"]}""");
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var json = await CreateService(scope).GetCivitaiBrowserFilterJsonAsync();
            json.Should().Be("""{"SelectedBaseModels":["New"]}""",
                "the filter is a single slot — saving must overwrite, not accumulate");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetCivitaiBrowserFilterJsonAsync_BlankOrWhitespace_ClearsToNull(string blank)
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            await CreateService(scope).SetCivitaiBrowserFilterJsonAsync("""{"SelectedBaseModels":["Illustrious"]}""");
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            await CreateService(scope).SetCivitaiBrowserFilterJsonAsync(blank);
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var json = await CreateService(scope).GetCivitaiBrowserFilterJsonAsync();
            json.Should().BeNull();
        }
    }

    [Fact]
    public async Task SetCivitaiBrowserFilterJsonAsync_BumpsUpdatedAt()
    {
        using var scope = _serviceProvider.CreateScope();
        var service = CreateService(scope);

        var before = await service.GetSettingsAsync();
        var beforeStamp = before.UpdatedAt;

        await Task.Delay(10);
        await service.SetCivitaiBrowserFilterJsonAsync("""{"SelectedBaseModels":["Illustrious"]}""");

        var after = await service.GetSettingsAsync();
        after.UpdatedAt.Should().BeAfter(beforeStamp);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
