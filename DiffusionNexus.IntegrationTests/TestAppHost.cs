using System.Threading;
using Avalonia;
using Avalonia.Headless;
using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI;
using DiffusionNexus.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.IntegrationTests;

public sealed class TestAppHost : IAsyncLifetime, IDisposable
{
    private static int _avaloniaInitialized;
    private ServiceProvider? _serviceProvider;

    public string RootPath { get; }
    public string DatasetRoot { get; }
    public string DatabaseRoot { get; }
    public string CacheRoot { get; }

    public IServiceProvider Services => _serviceProvider ?? throw new InvalidOperationException("Test host not initialized.");

    public TestAppHost()
    {
        EnsureAvalonia();

        RootPath = Path.Combine(Path.GetTempPath(), "DiffusionNexus.IntegrationTests", Guid.NewGuid().ToString("N"));
        DatasetRoot = Path.Combine(RootPath, "datasets");
        DatabaseRoot = Path.Combine(RootPath, "db");
        CacheRoot = Path.Combine(RootPath, "cache");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DatasetRoot);
        Directory.CreateDirectory(DatabaseRoot);
        Directory.CreateDirectory(CacheRoot);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        await dbContext.Database.MigrateAsync();

        var settingsService = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        var settings = await settingsService.GetSettingsAsync();
        settings.DatasetStoragePath = DatasetRoot;
        await settingsService.SaveSettingsAsync(settings);
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_serviceProvider is not null)
        {
            _serviceProvider.Dispose();
            _serviceProvider = null;
        }

        if (Directory.Exists(RootPath))
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp data.
            }
        }
    }

    public string CreateDatasetFolder(string datasetName)
    {
        var datasetPath = Path.Combine(DatasetRoot, datasetName);
        var versionPath = Path.Combine(datasetPath, "V1");
        Directory.CreateDirectory(versionPath);
        return datasetPath;
    }

    private static void EnsureAvalonia()
    {
        if (Interlocked.Exchange(ref _avaloniaInitialized, 1) == 1)
        {
            return;
        }

        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = true
            })
            .SetupWithoutStarting();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        services.AddDiffusionNexusCoreDatabase(DatabaseRoot);
        services.AddInfrastructureServices(CacheRoot);
        services.AddSingleton<IThumbnailService, ThumbnailService>();

        services.AddScoped<IAppSettingsService, AppSettingsService>();
        services.AddScoped<IModelSyncService, ModelFileSyncService>();
        services.AddScoped<IDisclaimerService, DisclaimerService>();

        services.AddSingleton<IDatasetEventAggregator, DatasetEventAggregator>();
        services.AddSingleton<IDatasetState, DatasetStateService>();
        services.AddSingleton<IDatasetStorageService, DatasetStorageService>();
    }
}
