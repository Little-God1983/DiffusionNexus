using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using DiffusionNexus.Captioning;
using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure;
using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation;
using DiffusionNexus.Service.Services;
using DiffusionNexus.Service.Services.DatasetQuality;
using DiffusionNexus.UI.Converters;
using DiffusionNexus.UI.Controls;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.SpellCheck;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Views;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SdkContext = DiffusionNexus.Installer.SDK.DataAccess.DiffusionNexusContext;

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

                // Initialize spell check and autocomplete for caption editors
                Serilog.Log.Information("Initializing spell check services...");
                InitializeSpellCheckServices();

                // Initialize databases
                Serilog.Log.Information("Initializing app database...");
                InitializeDatabase();

                Serilog.Log.Information("Initializing SDK database...");
                InitializeSdkDatabase();

                // Create main window with modules
                Serilog.Log.Information("Creating main window view model...");
                var mainViewModel = new DiffusionNexusMainWindowViewModel();

                // Initialize status bar with activity log service
                Serilog.Log.Information("Initializing status bar...");
                mainViewModel.InitializeStatusBar();

                // Force-resolve the InstanceProcessManager singleton so its constructor
                // wires PackageProcessManager.OutputReceived ? IUnifiedLogger.
                // Without this, process stdout/stderr never reaches the Unified Console.
                Serilog.Log.Information("Initializing instance process manager...");
                _ = Services!.GetRequiredService<IInstanceProcessManager>();

                // Create and assign the main window before registering modules,
                // because module resolution requires IDialogService which needs MainWindow.
                Serilog.Log.Information("Creating main window...");
                var mainWindow = new DiffusionNexusMainWindow
                {
                    DataContext = mainViewModel
                };
                desktop.MainWindow = mainWindow;
                Serilog.Log.Information("Main window assigned to desktop.MainWindow");

                Serilog.Log.Information("Registering modules...");
                RegisterModules(mainViewModel);

                // Force show the window explicitly
                mainWindow.Show();
                Serilog.Log.Information("Main window Show() called");

                // Cleanup on shutdown
                desktop.ShutdownRequested += (_, _) =>
                {
                    // Dispose the instance process manager (unwires events)
                    (Services?.GetService<IInstanceProcessManager>() as IDisposable)?.Dispose();
                    // Kill all managed child processes before scope disposal
                    Services?.GetService<PackageProcessManager>()?.Dispose();
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
    /// Initializes the ThumbnailOrchestrator and wires it to the converters.
    /// </summary>
    private static void InitializeThumbnailService()
    {
        var orchestrator = Services!.GetRequiredService<IThumbnailOrchestrator>();
        PathToBitmapConverter.ThumbnailOrchestrator = orchestrator;
    }

    /// <summary>
    /// Initializes the spell check and autocomplete services for SpellCheckTextBox controls.
    /// </summary>
    private static void InitializeSpellCheckServices()
    {
        var spellCheck = Services!.GetRequiredService<ISpellCheckService>();
        var autoComplete = Services!.GetRequiredService<IAutoCompleteService>();
        SpellCheckTextBox.Initialize(spellCheck, autoComplete);
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
            var dbDirectory = DiffusionNexusCoreDbContext.GetDatabaseDirectory();
            Serilog.Log.Information("InitializeDatabase: Connection string: {DbPath}", dbPath);

            // Log the database folder path to the activity log so users can find their DB file
            var activityLog = Services!.GetService<IActivityLogService>();
            activityLog?.LogInfo("Database", $"Database loaded from: {dbDirectory}");
            
            // First verify we can connect
            Serilog.Log.Information("InitializeDatabase: Testing connection...");
            if (!dbContext.Database.CanConnect())
            {
                Serilog.Log.Warning("InitializeDatabase: Cannot connect to database - will try to create it");
            }

            // Remove migration history entries for migrations that no longer exist in the codebase
            Serilog.Log.Information("InitializeDatabase: Cleaning stale migration history entries...");
            CleanStaleMigrationHistory(dbContext);

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

            // Post-migration verification to catch schema gaps
            Serilog.Log.Information("InitializeDatabase: Post-migration schema verification...");
            CheckAndRepairSchema(dbContext);
        }
        catch (SqliteException ex) when (ex.Message.Contains("already exists"))
        {
            Serilog.Log.Warning("InitializeDatabase: Table/column already exists (continuing): {Message}", ex.Message);
            CheckAndRepairSchema(dbContext);
            MarkPendingMigrationsAsApplied(dbContext);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqlEx && sqlEx.Message.Contains("already exists"))
        {
            Serilog.Log.Warning("InitializeDatabase: Table/column already exists (continuing): {Message}", ex.Message);
            CheckAndRepairSchema(dbContext);
            MarkPendingMigrationsAsApplied(dbContext);
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such column"))
        {
            Serilog.Log.Warning("InitializeDatabase: Schema mismatch detected: {Message}", ex.Message);
            CheckAndRepairSchema(dbContext);
            MarkPendingMigrationsAsApplied(dbContext);
        }
        catch (SqliteException ex) when (ex.Message.Contains("database is locked") || ex.Message.Contains("busy"))
        {
            Serilog.Log.Error(ex, "InitializeDatabase: Database is locked/busy - this may indicate another process is using the database");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "InitializeDatabase: Unexpected error during migration");
            try
            {
                CheckAndRepairSchema(dbContext);
            }
            catch (Exception repairEx)
            {
                Serilog.Log.Error(repairEx, "InitializeDatabase: Schema repair also failed");
            }
        }

        Serilog.Log.Information("InitializeDatabase: Completed");
    }

    /// <summary>
    /// Ensures the SDK database (diffusion_nexus.db) is deployed and up-to-date.
    /// On first launch the pre-populated file shipped inside the
    /// <c>DiffusionNexus.Installer.SDK.Database</c> NuGet package is copied from the
    /// build output to <c>%LocalAppData%\diffusion_nexus.db</c>. Subsequent launches
    /// only apply pending EF Core migrations.
    /// </summary>
    private static void InitializeSdkDatabase()
    {
        const string databaseFileName = "diffusion_nexus.db";

        try
        {
            // The NuGet contentFiles mechanism copies the seed DB next to the executable
            var shippedDb = Path.Combine(AppContext.BaseDirectory, databaseFileName);

            // Runtime location: directly in %LocalAppData% (no subfolder)
            // TODO: Linux Implementation — use XDG_DATA_HOME or ~/.local/share
            var runtimeDb = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                databaseFileName);

            if (!File.Exists(runtimeDb))
            {
                if (File.Exists(shippedDb))
                {
                    File.Copy(shippedDb, runtimeDb);
                    Serilog.Log.Information(
                        "InitializeSdkDatabase: Deployed seed DB from {Source} to {Target}",
                        shippedDb, runtimeDb);
                }
                else
                {
                    Serilog.Log.Warning(
                        "InitializeSdkDatabase: No seed DB found at {Path} — " +
                        "database will be created empty from migrations only", shippedDb);
                }
            }

            // Apply any pending schema migrations on top of the (seed) data
            var sdkContext = Services!.GetRequiredService<SdkContext>();
            Serilog.Log.Information("InitializeSdkDatabase: Applying migrations to SDK database...");
            sdkContext.Database.Migrate();
            Serilog.Log.Information("InitializeSdkDatabase: Migration completed successfully");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "InitializeSdkDatabase: Failed to initialize SDK database");
        }
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
    /// Checks and repairs the database schema by ensuring all required columns exist.
    /// This is safer than waiting for a crash.
    /// </summary>
    private static void CheckAndRepairSchema(DiffusionNexusCoreDbContext dbContext)
    {
        try
        {
            Serilog.Log.Information("CheckAndRepairSchema: Checking table schema...");
            
            var connection = dbContext.Database.GetDbConnection();
            var wasOpen = connection.State == System.Data.ConnectionState.Open;
            if (!wasOpen) connection.Open();

            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA table_info('AppSettings');";
                using var reader = command.ExecuteReader();
                
                while (reader.Read())
                {
                    var name = reader["name"].ToString();
                    if (!string.IsNullOrEmpty(name)) existingColumns.Add(name);
                }
            }
            finally
            {
                if (!wasOpen) connection.Close();
            }

            Serilog.Log.Information("CheckAndRepairSchema: Found AppSettings columns: {Columns}", string.Join(", ", existingColumns));
            
            // List of columns to verify and their add scripts
            var requiredColumns = new Dictionary<string, string>
            {
                { "MaxBackups", "ALTER TABLE AppSettings ADD COLUMN MaxBackups INTEGER NOT NULL DEFAULT 10" },
                { "LastBackupAt", "ALTER TABLE AppSettings ADD COLUMN LastBackupAt TEXT" },
                { "ComfyUiServerUrl", "ALTER TABLE AppSettings ADD COLUMN ComfyUiServerUrl TEXT NOT NULL DEFAULT 'http://127.0.0.1:8188/'" }
            };

            foreach (var col in requiredColumns)
            {
                if (!existingColumns.Contains(col.Key))
                {
                    Serilog.Log.Warning("CheckAndRepairSchema: Missing '{Column}' column. Attempting to add...", col.Key);
                    try 
                    {
                        dbContext.Database.ExecuteSqlRaw(col.Value);
                        Serilog.Log.Information("CheckAndRepairSchema: Successfully added '{Column}'", col.Key);
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Error(ex, "CheckAndRepairSchema: Failed to add '{Column}'", col.Key);
                        // Don't throw, try next
                    }
                }
            }
        }
        catch (Exception ex)
        {
             Serilog.Log.Error(ex, "CheckAndRepairSchema: Fatal error during check");
        }
    }

    /// <summary>
    /// Removes entries from __EFMigrationsHistory that no longer have corresponding migration classes.
    /// This prevents EF Core from failing when migrations are removed from the codebase.
    /// </summary>
    private static void CleanStaleMigrationHistory(DiffusionNexusCoreDbContext dbContext)
    {
        try
        {
            var connection = dbContext.Database.GetDbConnection();
            var wasOpen = connection.State == System.Data.ConnectionState.Open;
            if (!wasOpen) connection.Open();

            try
            {
                // Check if __EFMigrationsHistory table exists
                using var checkCmd = connection.CreateCommand();
                checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory';";
                var tableExists = checkCmd.ExecuteScalar() is not null;
                if (!tableExists) return;

                // Get all migration IDs known to EF Core from the assembly
                var knownMigrations = dbContext.Database.GetMigrations().ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Get all migration IDs from the history table
                var appliedMigrations = new List<string>();
                using var listCmd = connection.CreateCommand();
                listCmd.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory;";
                using var reader = listCmd.ExecuteReader();
                while (reader.Read())
                {
                    appliedMigrations.Add(reader.GetString(0));
                }

                // Remove stale entries (applied but no longer in codebase)
                foreach (var migrationId in appliedMigrations)
                {
                    if (!knownMigrations.Contains(migrationId))
                    {
                        Serilog.Log.Warning("CleanStaleMigrationHistory: Removing stale entry '{MigrationId}'", migrationId);
                        dbContext.Database.ExecuteSqlRaw(
                            "DELETE FROM __EFMigrationsHistory WHERE MigrationId = {0}", migrationId);
                    }
                }
            }
            finally
            {
                if (!wasOpen) connection.Close();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "CleanStaleMigrationHistory: Failed to clean stale entries");
        }
    }

    /// <summary>
    /// Marks any pending migrations as applied in __EFMigrationsHistory without running them.
    /// Used after schema repair when migrations failed due to "already exists" errors.
    /// </summary>
    private static void MarkPendingMigrationsAsApplied(DiffusionNexusCoreDbContext dbContext)
    {
        try
        {
            var pending = dbContext.Database.GetPendingMigrations().ToList();
            if (pending.Count == 0) return;

            foreach (var migrationId in pending)
            {
                Serilog.Log.Information("MarkPendingMigrationsAsApplied: Marking '{MigrationId}' as applied", migrationId);
                dbContext.Database.ExecuteSqlRaw(
                    "INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ({0}, {1})",
                    migrationId,
                    typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "9.0.0");
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "MarkPendingMigrationsAsApplied: Failed");
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
        // Database + Repositories + Unit of Work
        services.AddDataAccessLayer();

        // Dataset quality analysis pipeline and checks
        services.AddDatasetQualityServices();

        // Infrastructure services (secure storage, image caching, activity logging)
        services.AddInfrastructureServices();

        // Thumbnail service for async image loading with LRU cache (singleton)
        services.AddSingleton<IThumbnailService, ThumbnailService>();

        // Thumbnail orchestrator for priority-based loading across views (singleton)
        services.AddSingleton<IThumbnailOrchestrator>(sp =>
            new ThumbnailOrchestrator(sp.GetRequiredService<IThumbnailService>()));

        // Application services - Transient so each consumer gets its own UoW/DbContext
        services.AddTransient<IAppSettingsService, AppSettingsService>();
        services.AddTransient<IModelSyncService, ModelFileSyncService>();

        // DatasetBackupService - use factory to inject activity log service
        services.AddTransient<IDatasetBackupService>(sp => new DatasetBackupService(
            sp.GetRequiredService<IAppSettingsService>(),
            sp.GetService<IActivityLogService>()));

        services.AddTransient<IDisclaimerService, DisclaimerService>();

        // Video thumbnail service (singleton - maintains FFmpeg initialization state)
        services.AddSingleton<IVideoThumbnailService, VideoThumbnailService>();

        // Background removal service (singleton - maintains ONNX session)
        services.AddSingleton<IBackgroundRemovalService, BackgroundRemovalService>();

        // Image upscaling service (singleton - maintains ONNX session)
        services.AddSingleton<IImageUpscalingService, ImageUpscalingService>();

        // Package process manager (singleton - owns child process lifecycles)
        services.AddSingleton<PackageProcessManager>();

        // Instance process manager - decouples instance lifecycle from views,
        // pipes stdout/stderr through IUnifiedLogger
        services.AddSingleton<IInstanceProcessManager>(sp =>
            new InstanceProcessManager(
                sp.GetRequiredService<PackageProcessManager>(),
                sp.GetRequiredService<Domain.Services.UnifiedLogging.IUnifiedLogger>(),
                sp.GetRequiredService<Domain.Services.UnifiedLogging.ITaskTracker>(),
                sp));

        // ?? Installer SDK services ??
        // Register SDK data access layer (uses shared database at %LocalAppData%\diffusion_nexus.db from NuGet source)
        services.AddDiffusionNexusDataAccess();

        // Register SDK installation pipeline and all step handlers
        services.AddInstallationServices();

        // Configuration checker (singleton - accessible across the entire application)
        services.AddSingleton<IConfigurationCheckerService, ConfigurationCheckerService>();

        // Workload installer (singleton - clones custom nodes + downloads models)
        services.AddSingleton<IWorkloadInstallService>(sp =>
            new WorkloadInstallService(
                sp.GetRequiredService<IGitService>(),
                new HttpClient()));

        // Register SDK core services required by installation steps
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<IPythonService, PythonService>();

        // Installer update services (one per supported type)
        services.AddSingleton<Domain.Services.IInstallerUpdateService, Service.Services.ComfyUIUpdateService>();

        // Register the orchestrator and engine
        services.AddSingleton<IInstallationOrchestrator, InstallationOrchestrator>();
        services.AddSingleton<InstallationEngine>(sp =>
        {
            var orchestrator = sp.GetRequiredService<IInstallationOrchestrator>();
            return new InstallationEngine(orchestrator);
        });

        // ComfyUI workflow execution service (singleton - maintains HttpClient)
        services.AddSingleton<IComfyUIWrapperService>(sp =>
        {
            var settings = sp.GetRequiredService<IAppSettingsService>();
            // Default URL; callers can reconfigure later if settings change
            return new ComfyUIWrapperService();
        });

        // Civitai API client (singleton - maintains HttpClient)
        services.AddSingleton<Civitai.ICivitaiClient, Civitai.CivitaiClient>();

        // Captioning service (singleton - manages local LLM)
        services.AddCaptioningServices();

        // Captioning backends (strategy pattern - local inference + ComfyUI)
        // NOTE: Local Inference is registered but hidden in the UI until fully implemented — do not delete
        services.AddSingleton<ICaptioningBackend>(sp =>
            new LocalInferenceCaptioningBackend(sp.GetRequiredService<ICaptioningService>()));
        services.AddSingleton<ICaptioningBackend>(sp =>
            new ComfyUICaptioningBackend(
                sp.GetRequiredService<IComfyUIWrapperService>(),
                sp.GetRequiredService<IAppSettingsService>()));

        // Dataset Helper services (singletons - shared state across all components)
        services.AddSingleton<IDatasetEventAggregator, DatasetEventAggregator>();
        services.AddSingleton<IDatasetState, DatasetStateService>();
        services.AddSingleton<IDatasetStorageService, DatasetStorageService>();

        // Spell check & autocomplete services (singletons - shared across all caption editors)
        services.AddSingleton<IUserDictionaryService, UserDictionaryService>();
        services.AddSingleton<ISpellCheckService>(sp =>
            new SpellCheckService(sp.GetRequiredService<IUserDictionaryService>()));
        services.AddSingleton<IAutoCompleteService, AutoCompleteService>();

        // Image favorites service (singleton - per-folder .favorites.json persistence)
        services.AddSingleton<IImageFavoritesService, ImageFavoritesService>();

        // Settings export/import
        services.AddScoped<ISettingsExportService, SettingsExportService>();

        // Dialog service - resolves the main window lazily from the application lifetime
        services.AddScoped<IDialogService>(sp =>
        {
            var lifetime = Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var mainWindow = lifetime?.MainWindow
                ?? throw new InvalidOperationException("MainWindow is not available yet.");
            return new DialogService(mainWindow);
        });

        // ViewModels (scoped to app lifetime)
        // SettingsViewModel - use factory to inject all required services including IActivityLogService
        services.AddScoped<SettingsViewModel>(sp => new SettingsViewModel(
            sp.GetRequiredService<IAppSettingsService>(),
            sp.GetRequiredService<ISecureStorage>(),
            sp.GetService<IDatasetBackupService>(),
            sp.GetService<IDatasetEventAggregator>(),
            sp.GetService<IActivityLogService>(),
            sp.GetService<ISettingsExportService>()));
        
        services.AddScoped<LoraViewerViewModel>(sp => new LoraViewerViewModel(
            sp.GetRequiredService<IAppSettingsService>(),
            sp.GetRequiredService<IModelSyncService>(),
            sp.GetService<Civitai.ICivitaiClient>(),
            sp.GetService<ISecureStorage>(),
            sp.GetService<Domain.Services.UnifiedLogging.IUnifiedLogger>()));
        services.AddScoped<InstallerManagerViewModel>(sp => new InstallerManagerViewModel(
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IUnitOfWork>(),
            sp.GetRequiredService<PackageProcessManager>(),
            sp.GetRequiredService<IDatasetEventAggregator>(),
            sp.GetRequiredService<IConfigurationRepository>(),
            sp.GetRequiredService<IConfigurationCheckerService>(),
            sp.GetRequiredService<IWorkloadInstallService>(),
            sp.GetServices<Domain.Services.IInstallerUpdateService>(),
            sp.GetRequiredService<Domain.Services.UnifiedLogging.IUnifiedLogger>()));
        services.AddScoped<GenerationGalleryViewModel>(sp => new GenerationGalleryViewModel(
            sp.GetRequiredService<IAppSettingsService>(),
            sp.GetRequiredService<IDatasetEventAggregator>(),
            sp.GetRequiredService<IDatasetState>(),
            sp.GetService<IVideoThumbnailService>(),
            sp.GetService<IThumbnailOrchestrator>(),
            sp.GetService<IImageFavoritesService>()));
        
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
            sp.GetService<IActivityLogService>(),
            sp.GetService<IComfyUIWrapperService>(),
            sp.GetService<IThumbnailOrchestrator>(),
            sp.GetService<AnalysisPipeline>()));
    }

    private void RegisterModules(DiffusionNexusMainWindowViewModel mainViewModel)
    {
        // Installer Manager module
        var installerManagerVm = Services!.GetRequiredService<InstallerManagerViewModel>();
        var installerManagerView = new InstallerManagerView { DataContext = installerManagerVm };
        var installerManagerModule = new ModuleItem(
            "Installer Manager",
            "avares://DiffusionNexus.UI/Assets/Installer.png", // TODO: add dedicated Installer Manager icon
            installerManagerView)
        {
            ViewModel = installerManagerVm
        };

        mainViewModel.RegisterModule(installerManagerModule);

        // Open the unified console panel when the installer manager requests it (e.g., during updates)
        installerManagerVm.UnifiedConsolePanelRequested += (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (mainViewModel.StatusBar is { } statusBar)
                {
                    statusBar.IsLogPanelOpen = true;
                    if (statusBar.UnifiedConsole is not null)
                        statusBar.UnifiedConsole.IsPanelOpen = true;
                }
            });
        };

        // LoRA Dataset Helper module - default on startup
        var loraDatasetHelperVm = Services!.GetRequiredService<LoraDatasetHelperViewModel>();
        var loraDatasetHelperView = new LoraDatasetHelperView { DataContext = loraDatasetHelperVm };
        var loraDatasetHelperModule = new ModuleItem(
            "LoRA Dataset Helper",
            "avares://DiffusionNexus.UI/Assets/LoraTrain.png",
            loraDatasetHelperView)
        {
            ViewModel = loraDatasetHelperVm
        };

        mainViewModel.RegisterModule(loraDatasetHelperModule);

        // LoRA Viewer module
        var loraViewerVm = Services!.GetRequiredService<LoraViewerViewModel>();
        var loraViewerView = new LoraViewerView { DataContext = loraViewerVm };

        mainViewModel.RegisterModule(new ModuleItem(
            "LoRA Viewer",
            "avares://DiffusionNexus.UI/Assets/LoraSort.png",
            loraViewerView,
            isVisible: true)
        {
            ViewModel = loraViewerVm
        });

        // Generation Gallery module
        var generationGalleryVm = Services!.GetRequiredService<GenerationGalleryViewModel>();
        var generationGalleryView = new GenerationGalleryView { DataContext = generationGalleryVm };

        mainViewModel.RegisterModule(new ModuleItem(
            "Generation Gallery",
            "avares://DiffusionNexus.UI/Assets/GalleryView.png",
            generationGalleryView)
        {
            ViewModel = generationGalleryVm
        });

        // Image Comparer module
        var datasetState = Services!.GetRequiredService<IDatasetState>();
        var thumbnailOrchestrator = Services!.GetService<IThumbnailOrchestrator>();
        var imageCompareVm = new ImageCompareViewModel(datasetState, thumbnailOrchestrator);
        var imageCompareView = new ImageCompareView { DataContext = imageCompareVm };

        var imageComparerModule = new ModuleItem(
            "Image Comparer",
            "avares://DiffusionNexus.UI/Assets/ImageComparer.png",
            imageCompareView)
        {
            ViewModel = imageCompareVm
        };

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

        // Load startup data sequentially to avoid concurrent DbContext access.
        // All scoped services share a single DiffusionNexusCoreDbContext instance
        // which is NOT thread-safe; fire-and-forget Execute() calls run concurrently.
        _ = LoadStartupDataAsync(
            mainViewModel,
            settingsVm,
            loraViewerVm,
            generationGalleryVm,
            installerManagerVm,
            loraDatasetHelperVm);
    }

    /// <summary>
    /// Loads startup data for each module. Disclaimer and settings load first
    /// (needed by other modules), then independent modules load in parallel.
    /// Each service owns its own <see cref="DiffusionNexusCoreDbContext"/> via
    /// <c>IDbContextFactory</c>, so concurrent DB access is safe.
    /// </summary>
    private static async Task LoadStartupDataAsync(
        DiffusionNexusMainWindowViewModel mainViewModel,
        SettingsViewModel settingsVm,
        LoraViewerViewModel loraViewerVm,
        GenerationGalleryViewModel generationGalleryVm,
        InstallerManagerViewModel installerManagerVm,
        LoraDatasetHelperViewModel loraDatasetHelperVm)
    {
        try
        {
            // Disclaimer + settings must complete first — other modules depend on them.
            await mainViewModel.CheckDisclaimerStatusAsync();
            await settingsVm.LoadCommand.ExecuteAsync(null);

            // Remaining modules are independent — load in parallel.
            await Task.WhenAll(
                loraViewerVm.RefreshCommand.ExecuteAsync(null),
                generationGalleryVm.LoadMediaCommand.ExecuteAsync(null),
                installerManagerVm.LoadInstallationsCommand.ExecuteAsync(null),
                loraDatasetHelperVm.DatasetManagement
                    .CheckStorageConfigurationCommand.ExecuteAsync(null));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Error during startup data loading");
        }
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
