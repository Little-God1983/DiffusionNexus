using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using DiffusionNexus.Captioning;
using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Converters;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Views;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI;

public partial class App : Application
{
    /// <summary>
    /// Service provider for dependency injection.
    /// </summary>
    public static IServiceProvider? Services { get; private set; }

    /// <summary>
    /// Service scope for the application lifetime.
    /// </summary>
    private static IServiceScope? _appScope;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit
            DisableAvaloniaDataAnnotationValidation();

            // Configure services
            var services = new ServiceCollection();
            ConfigureServices(services);
            var rootProvider = services.BuildServiceProvider();

            // Create a scope for the application lifetime
            _appScope = rootProvider.CreateScope();
            Services = _appScope.ServiceProvider;

            // Initialize ThumbnailService for converters
            InitializeThumbnailService();

            // Ensure database is migrated
            InitializeDatabase();

            // Create main window with modules
            var mainViewModel = new DiffusionNexusMainWindowViewModel();
            
            // Initialize status bar with activity log service
            mainViewModel.InitializeStatusBar();
            
            RegisterModules(mainViewModel);

            // Check disclaimer status after services are ready
            _ = mainViewModel.CheckDisclaimerStatusAsync();

            desktop.MainWindow = new DiffusionNexusMainWindow
            {
                DataContext = mainViewModel
            };

            // Cleanup on shutdown
            desktop.ShutdownRequested += (_, _) =>
            {
                _appScope?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Initializes the ThumbnailService and wires it to the converters.
    /// </summary>
    private static void InitializeThumbnailService()
    {
        var thumbnailService = Services!.GetRequiredService<IThumbnailService>();
        PathToBitmapConverter.ThumbnailService = thumbnailService;
    }

    private static void InitializeDatabase()
    {
        var scope = Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();

        try
        {
            // Get pending migrations
            var pendingMigrations = dbContext.Database.GetPendingMigrations().ToList();
            
            if (pendingMigrations.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"Applying {pendingMigrations.Count} pending migrations...");
                foreach (var migration in pendingMigrations)
                {
                    System.Diagnostics.Debug.WriteLine($"  - {migration}");
                }
            }
            
            dbContext.Database.Migrate();
        }
        catch (SqliteException ex) when (ex.Message.Contains("already exists"))
        {
            // Table already exists - this is usually fine, just continue
            System.Diagnostics.Debug.WriteLine($"Migration warning (continuing): {ex.Message}");
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqlEx && sqlEx.Message.Contains("already exists"))
        {
            // Table already exists - this is usually fine, just continue
            System.Diagnostics.Debug.WriteLine($"Migration warning (continuing): {ex.Message}");
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such column"))
        {
            // Missing column - database schema is out of date
            // Try to add the missing column manually as a fallback
            System.Diagnostics.Debug.WriteLine($"Schema mismatch detected: {ex.Message}");
            TryFixMissingColumns(dbContext);
        }

        scope.Dispose();
    }

    /// <summary>
    /// Attempts to add missing columns to AppSettings table.
    /// This is a fallback for when migrations don't run properly.
    /// </summary>
    private static void TryFixMissingColumns(DiffusionNexusCoreDbContext dbContext)
    {
        try
        {
            // Try to add MaxBackups column if missing
            dbContext.Database.ExecuteSqlRaw(
                "ALTER TABLE AppSettings ADD COLUMN MaxBackups INTEGER NOT NULL DEFAULT 10");
            System.Diagnostics.Debug.WriteLine("Added missing MaxBackups column");
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column"))
        {
            // Column already exists, ignore
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to add MaxBackups column: {ex.Message}");
        }

        try
        {
            // Try to add LastBackupAt column if missing
            dbContext.Database.ExecuteSqlRaw(
                "ALTER TABLE AppSettings ADD COLUMN LastBackupAt TEXT");
            System.Diagnostics.Debug.WriteLine("Added missing LastBackupAt column");
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column"))
        {
            // Column already exists, ignore
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to add LastBackupAt column: {ex.Message}");
        }
    }

    private static void ResetDatabase()
    {
        // WARNING: This deletes ALL user data including settings!
        // Only called in extreme cases - should rarely be needed
        using var freshScope = Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var freshContext = freshScope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
        freshContext.Database.EnsureDeleted();
        freshContext.Database.Migrate();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Database
        services.AddDiffusionNexusCoreDatabase();

        // Infrastructure services (secure storage, image caching, activity logging)
        services.AddInfrastructureServices();

        // Thumbnail service for async image loading with LRU cache (singleton)
        services.AddSingleton<IThumbnailService, ThumbnailService>();

        // Application services - Scoped works within our app scope
        services.AddScoped<IAppSettingsService, AppSettingsService>();
        services.AddScoped<IModelSyncService, ModelFileSyncService>();
        
        // DatasetBackupService - use factory to inject activity log service
        services.AddScoped<IDatasetBackupService>(sp => new DatasetBackupService(
            sp.GetRequiredService<IAppSettingsService>(),
            sp.GetService<IActivityLogService>()));
        
        services.AddScoped<IDisclaimerService, DisclaimerService>();

        // Video thumbnail service (singleton - maintains FFmpeg initialization state)
        services.AddSingleton<IVideoThumbnailService, VideoThumbnailService>();

        // Background removal service (singleton - maintains ONNX session)
        services.AddSingleton<IBackgroundRemovalService, BackgroundRemovalService>();

        // Image upscaling service (singleton - maintains ONNX session)
        services.AddSingleton<IImageUpscalingService, ImageUpscalingService>();

        // Captioning service (singleton - manages local LLM)
        services.AddCaptioningServices();

        // Dataset Helper services (singletons - shared state across all components)
        services.AddSingleton<IDatasetEventAggregator, DatasetEventAggregator>();
        services.AddSingleton<IDatasetState, DatasetStateService>();

        // ViewModels (scoped to app lifetime)
        // SettingsViewModel - use factory to inject all required services including IActivityLogService
        services.AddScoped<SettingsViewModel>(sp => new SettingsViewModel(
            sp.GetRequiredService<IAppSettingsService>(),
            sp.GetRequiredService<ISecureStorage>(),
            sp.GetService<IDatasetBackupService>(),
            sp.GetService<IDatasetEventAggregator>(),
            sp.GetService<IActivityLogService>()));
        
        services.AddScoped<LoraViewerViewModel>();
        services.AddScoped<GenerationGalleryViewModel>(sp => new GenerationGalleryViewModel(
            sp.GetRequiredService<IAppSettingsService>(),
            sp.GetRequiredService<IDatasetEventAggregator>(),
            sp.GetRequiredService<IDatasetState>(),
            sp.GetService<IVideoThumbnailService>()));
        
        // LoraDatasetHelperViewModel - use factory to inject all required services
        services.AddScoped<LoraDatasetHelperViewModel>(sp => new LoraDatasetHelperViewModel(
            sp.GetRequiredService<IAppSettingsService>(),
            sp.GetRequiredService<IDatasetEventAggregator>(),
            sp.GetRequiredService<IDatasetState>(),
            sp.GetService<ICaptioningService>(),
            sp.GetService<IVideoThumbnailService>(),
            sp.GetService<IBackgroundRemovalService>(),
            sp.GetService<IImageUpscalingService>(),
            sp.GetService<IDatasetBackupService>(),
            sp.GetService<IActivityLogService>()));
    }

    private void RegisterModules(DiffusionNexusMainWindowViewModel mainViewModel)
    {
        // LoRA Dataset Helper module - default on startup
        var loraDatasetHelperVm = Services!.GetRequiredService<LoraDatasetHelperViewModel>();
        var loraDatasetHelperView = new LoraDatasetHelperView { DataContext = loraDatasetHelperVm };

        mainViewModel.RegisterModule(new ModuleItem(
            "LoRA Dataset Helper",
            "avares://DiffusionNexus.UI/Assets/LoraTrain.png",
            loraDatasetHelperView));

        // LoRA Viewer module
        var loraViewerVm = Services!.GetRequiredService<LoraViewerViewModel>();
        var loraViewerView = new LoraViewerView { DataContext = loraViewerVm };

        mainViewModel.RegisterModule(new ModuleItem(
            "LoRA Viewer",
            "avares://DiffusionNexus.UI/Assets/LoraSort.png",
            loraViewerView));

        // Generation Gallery module
        var generationGalleryVm = Services!.GetRequiredService<GenerationGalleryViewModel>();
        var generationGalleryView = new GenerationGalleryView { DataContext = generationGalleryVm };

        mainViewModel.RegisterModule(new ModuleItem(
            "Generation Gallery",
            "avares://DiffusionNexus.UI/Assets/GalleryView.png",
            generationGalleryView));

        // Settings module
        var settingsVm = Services!.GetRequiredService<SettingsViewModel>();
        var settingsView = new SettingsView { DataContext = settingsVm };

        var settingsModule = new ModuleItem(
            "Settings",
            "avares://DiffusionNexus.UI/Assets/settings.png",
            settingsView);

        mainViewModel.RegisterModule(settingsModule);

        // Subscribe to navigate to settings event
        var eventAggregator = Services!.GetRequiredService<IDatasetEventAggregator>();
        eventAggregator.NavigateToSettingsRequested += (_, _) =>
        {
            mainViewModel.NavigateToModuleCommand.Execute(settingsModule);
        };

        // Load settings on startup
        settingsVm.LoadCommand.Execute(null);

        // Load models on startup
        loraViewerVm.RefreshCommand.Execute(null);

        // Load Generation Gallery on startup
        generationGalleryVm.LoadMediaCommand.Execute(null);
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
