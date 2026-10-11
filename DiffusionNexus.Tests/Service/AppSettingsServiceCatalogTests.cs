using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Service;

/// <summary>#618: the app's own catalog channel and "apply automatically", saved only through their own setters.</summary>
public sealed class AppSettingsServiceCatalogTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public AppSettingsServiceCatalogTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDataAccessLayer(o => o.UseSqlite(_connection));
        services.AddSingleton(new Mock<ISecureStorage>().Object);
        services.AddTransient<IAppSettingsService, AppSettingsService>();
        _provider = services.BuildServiceProvider();
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private static IAppSettingsService Service(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IAppSettingsService>();

    [Fact]
    public async Task Without_a_settings_row_nothing_was_chosen_and_nothing_is_created()
    {
        using var scope = _provider.CreateScope();

        var settings = await Service(scope).GetCatalogUpdateSettingsAsync();

        settings.Should().Be(new CatalogUpdateSettings(null, false));
        (await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AppSettings.GetSettingsAsync()).Should().BeNull("a read creates nothing");
    }

    [Fact]
    public async Task The_channel_and_the_toggle_round_trip_and_create_the_row_when_missing()
    {
        using (var scope = _provider.CreateScope())
        {
            await Service(scope).SetCatalogChannelAsync("Preview");
            await Service(scope).SetApplyCatalogUpdatesAutomaticallyAsync(true);
        }

        using (var scope = _provider.CreateScope())
        {
            (await Service(scope).GetCatalogUpdateSettingsAsync()).Should().Be(new CatalogUpdateSettings("Preview", true));
        }
    }

    [Fact]
    public async Task A_settings_save_never_overwrites_the_catalog_settings()
    {
        AppSettings snapshot;
        using (var scope = _provider.CreateScope())
        {
            snapshot = await Service(scope).GetSettingsAsync();   // the Settings page's snapshot, taken before the catalog changes
        }
        using (var scope = _provider.CreateScope())
        {
            await Service(scope).SetCatalogChannelAsync("Preview");
            await Service(scope).SetApplyCatalogUpdatesAutomaticallyAsync(true);
        }

        using (var scope = _provider.CreateScope())
        {
            await Service(scope).SaveSettingsAsync(snapshot);
        }

        using (var scope = _provider.CreateScope())
        {
            (await Service(scope).GetCatalogUpdateSettingsAsync()).Should().Be(new CatalogUpdateSettings("Preview", true));
        }
    }
}
