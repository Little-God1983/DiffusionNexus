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
        Serilog.Log.Information("App.Initialize() starting...");
        AvaloniaXamlLoader.Load(this);
        Serilog.Log.Information("App.Initialize() XAML loaded");
        
        // Set up global exception handlers
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Serilog.Log.Fatal(ex, "UNHANDLED DOMAIN EXCEPTION");
            FileLogger.LogError($"UNHANDLED DOMAIN EXCEPTION: {ex?.Message}", ex);
        };
        
        TaskScheduler.UnobservedTaskException += (sender, args) =>
        {
            Serilog.Log.Error(args.Exception, "UNOBSERVED TASK EXCEPTION");
            FileLogger.LogError($"UNOBSERVED TASK EXCEPTION: {args.Exception?.Message}", args.Exception);
            args.SetObserved(); // Prevent the process from terminating
        };
        Serilog.Log.Information("App.Initialize() completed");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Serilog.Log.Information("OnFrameworkInitializationCompleted starting...");
        
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                // Avoid duplicate validations from both Avalonia and the CommunityToolkit
                Serilog.Log.Information("Disabling data annotation validation...");
                DisableAvaloniaDataAnnotationValidation();

                // Configure services
                Serilog.Log.Information("Configuring services...");
                var services = new ServiceCollection();
                ConfigureServices(services);
                var rootProvider = services.BuildServiceProvider();

                // Create a scope for the application lifetime
                _appScope = rootProvider.CreateScope();
                Services = _appScope.ServiceProvider;

                // Initialize ThumbnailService for converters
                Serilog.Log.Information("Initializing thumbnail service...");
                InitializeThumbnailService();

                // TEMPORARILY SKIP DATABASE - debugging invisible window issue
                Serilog.Log.Information("SKIPPING database initialization for debugging...");
                InitializeDatabase();

                // Create main window with modules
                Serilog.Log.Information("Creating main window view model...");
                var mainViewModel = new DiffusionNexusMainWindowViewModel();
                
                // Initialize status bar with activity log service
                Serilog.Log.Information("Initializing status bar...");
                mainViewModel.InitializeStatusBar();
                
                Serilog.Log.Information("Registering modules...");
                RegisterModules(mainViewModel);

                // Check disclaimer status after services are ready
                Serilog.Log.Information("Checking disclaimer status...");
                _ = mainViewModel.CheckDisclaimerStatusAsync();

                Serilog.Log.Information("Creating main window...");
                var mainWindow = new DiffusionNexusMainWindow
                {
                    DataContext = mainViewModel
                };
                desktop.MainWindow = mainWindow;
                Serilog.Log.Information("Main window assigned to desktop.MainWindow");
                
                // Force show the window explicitly
                mainWindow.Show();
                Serilog.Log.Information("Main window Show() called");

                // Cleanup on shutdown
                desktop.ShutdownRequested += (_, _) =>
                {
                    _appScope?.Dispose();
                };
            }
            catch (Exception ex)
            {
                Serilog.Log.Fatal(ex, "Failed during OnFrameworkInitializationCompleted");
                throw;
            }
        }

        Serilog.Log.Information("Calling base.OnFrameworkInitializationCompleted...");
        base.OnFrameworkInitializationCompleted();
        Serilog.Log.Information("OnFrameworkInitializationCompleted finished");
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
        Serilog.Log.Information("InitializeDatabase: Creating scope...");
        using var scope = Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();

        try
        {
            // Get the database path for logging
            var dbPath = DiffusionNexusCoreDbContext.GetConnectionString();
            Serilog.Log.Information("InitializeDatabase: Connection string: {DbPath}", dbPath);
            
            // First verify we can connect
            Serilog.Log.Information("InitializeDatabase: Testing connection...");
            if (!dbContext.Database.CanConnect())
            {
                Serilog.Log.Warning("InitializeDatabase: Cannot connect to database - will try to create it");
            }
            
            Serilog.Log.Information("InitializeDatabase: Getting pending migrations...");
            var pendingMigrations = dbContext.Database.GetPendingMigrations().ToList();
            
            if (pendingMigrations.Count > 0)
            {
                Serilog.Log.Information("InitializeDatabase: Applying {Count} pending migrations...", pendingMigrations.Count);
                foreach (var migration in pendingMigrations)
                {
                    Serilog.Log.Information("InitializeDatabase:   - {Migration}", migration);
                }
                
                Serilog.Log.Information("InitializeDatabase: Running Migrate()...");
                dbContext.Database.Migrate();
                Serilog.Log.Information("InitializeDatabase: Migration completed successfully");
            }
            else
            {
                Serilog.Log.Information("InitializeDatabase: No pending migrations - SKIPPING Migrate()");
            }
        }
        catch (SqliteException ex) when (ex.Message.Contains("already exists"))
        {
            Serilog.Log.Warning("InitializeDatabase: Table already exists (continuing): {Message}", ex.Message);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqlEx && sqlEx.Message.Contains("already exists"))
        {
            Serilog.Log.Warning("InitializeDatabase: Table already exists (continuing): {Message}", ex.Message);
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such column"))
        {
            Serilog.Log.Warning("InitializeDatabase: Schema mismatch detected: {Message}", ex.Message);
            TryFixMissingColumns(dbContext);
        }
        catch (SqliteException ex) when (ex.Message.Contains("database is locked") || ex.Message.Contains("busy"))
        {
            Serilog.Log.Error(ex, "InitializeDatabase: Database is locked/busy - this may indicate another process is using the database");
            // Don't throw - let app continue without fully initialized database
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "InitializeDatabase: Unexpected error during migration");
            // Don't throw - let app continue
        }

        Serilog.Log.Information("InitializeDatabase: Completed");
    }

    /// <summary>
    /// Attempts to delete a locked database file so it can be recreated fresh.
    /// </summary>
    private static void TryDeleteLockedDatabase()
    {
        try
        {
            var dbDir = DiffusionNexusCoreDbContext.GetDatabaseDirectory();
            var dbFile = Path.Combine(dbDir, "DiffusionNexusCore.sqlite");
            
            Serilog.Log.Warning("TryDeleteLockedDatabase: Attempting to delete locked database at {Path}", dbFile);
            
            if (File.Exists(dbFile))
            {
                // Try to delete the database file
                File.Delete(dbFile);
                Serilog.Log.Information("TryDeleteLockedDatabase: Database file deleted successfully");
            }
            
            // Also delete journal/wal files if they exist
            var walFile = dbFile + "-wal";
            var shmFile = dbFile + "-shm";
            
            if (File.Exists(walFile)) File.Delete(walFile);
            if (File.Exists(shmFile)) File.Delete(shmFile);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "TryDeleteLockedDatabase: Failed to delete database file");
        }
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

        // ComfyUI workflow execution service (singleton - maintains HttpClient)
        services.AddSingleton<IComfyUIWrapperService>(sp =>
        {
            var settings = sp.GetRequiredService<IAppSettingsService>();
            // Default URL; callers can reconfigure later if settings change
            return new ComfyUIWrapperService();
        });

        // Captioning service (singleton - manages local LLM)
        services.AddCaptioningServices();

        // Captioning backends (strategy pattern - local inference + ComfyUI)
        services.AddSingleton<ICaptioningBackend>(sp =>
            new LocalInferenceCaptioningBackend(sp.GetRequiredService<ICaptioningService>()));
        services.AddSingleton<ICaptioningBackend>(sp =>
            new ComfyUICaptioningBackend(sp.GetRequiredService<IComfyUIWrapperService>()));

        // Dataset Helper services (singletons - shared state across all components)
        services.AddSingleton<IDatasetEventAggregator, DatasetEventAggregator>();
        services.AddSingleton<IDatasetState, DatasetStateService>();
        services.AddSingleton<IDatasetStorageService, DatasetStorageService>();

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
            sp.GetRequiredService<IDatasetStorageService>(),
            sp.GetRequiredService<IDatasetEventAggregator>(),
            sp.GetRequiredService<IDatasetState>(),
            sp.GetService<ICaptioningService>(),
            sp.GetServices<ICaptioningBackend>().ToList(),
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
        var loraDatasetHelperModule = new ModuleItem(
            "LoRA Dataset Helper",
            "avares://DiffusionNexus.UI/Assets/LoraTrain.png",
            loraDatasetHelperView);

        mainViewModel.RegisterModule(loraDatasetHelperModule);

        // LoRA Viewer module (hidden for now)
        var loraViewerVm = Services!.GetRequiredService<LoraViewerViewModel>();
        var loraViewerView = new LoraViewerView { DataContext = loraViewerVm };

        mainViewModel.RegisterModule(new ModuleItem(
            "LoRA Viewer",
            "avares://DiffusionNexus.UI/Assets/LoraSort.png",
            loraViewerView,
            isVisible: false));

        // Generation Gallery module
        var generationGalleryVm = Services!.GetRequiredService<GenerationGalleryViewModel>();
        var generationGalleryView = new GenerationGalleryView { DataContext = generationGalleryVm };

        mainViewModel.RegisterModule(new ModuleItem(
            "Generation Gallery",
            "avares://DiffusionNexus.UI/Assets/GalleryView.png",
            generationGalleryView));

        // Image Comparer module
        var datasetState = Services!.GetRequiredService<IDatasetState>();
        var imageCompareVm = new ImageCompareViewModel(datasetState);
        var imageCompareView = new ImageCompareView { DataContext = imageCompareVm };

        var imageComparerModule = new ModuleItem(
            "Image Comparer",
            "avares://DiffusionNexus.UI/Assets/ImageComparer.png",
            imageCompareView);

        mainViewModel.RegisterModule(imageComparerModule);

        // Settings module
        var settingsVm = Services!.GetRequiredService<SettingsViewModel>();
        var settingsView = new SettingsView { DataContext = settingsVm };

        var settingsModule = new ModuleItem(
            "Settings",
            "avares://DiffusionNexus.UI/Assets/settings.png",
            settingsView);

        mainViewModel.RegisterModule(settingsModule);

        // Subscribe to navigation events
        var eventAggregator = Services!.GetRequiredService<IDatasetEventAggregator>();
        
        eventAggregator.NavigateToImageEditorRequested += (_, _) =>
        {
            mainViewModel.NavigateToModuleCommand.Execute(loraDatasetHelperModule);
        };

        eventAggregator.NavigateToBatchCropScaleRequested += (_, _) =>
        {
            mainViewModel.NavigateToModuleCommand.Execute(loraDatasetHelperModule);
        };

        eventAggregator.NavigateToSettingsRequested += (_, _) =>
        {
            mainViewModel.NavigateToModuleCommand.Execute(settingsModule);
        };

        eventAggregator.NavigateToImageComparerRequested += (_, e) =>
        {
            imageCompareVm.LoadExternalImages(e.ImagePaths);
            mainViewModel.NavigateToModuleCommand.Execute(imageComparerModule);
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
