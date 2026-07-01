using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure;
using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.Service.Services;
using DiffusionNexus.Service.Services.DatasetQuality;
using DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;
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
            Serilog.Log.Fatal(ex, "UNHANDLED DOMAIN EXCEPTION (IsTerminating={IsTerminating})", args.IsTerminating);
            FileLogger.LogError($"UNHANDLED DOMAIN EXCEPTION: {ex?.Message}", ex);

            // Only flush+close when the process is actually terminating; otherwise the
            // static logger becomes a silent sink and all subsequent logs are lost.
            if (args.IsTerminating)
            {
                Serilog.Log.CloseAndFlush();
            }
        };

        // Surface exceptions that are thrown but later caught/swallowed. Helps diagnose
        // silent failures (e.g. native AVE, OOM in worker code paths) where nothing
        // otherwise reaches the log. Kept at Verbose to avoid noise during normal flow.
        AppDomain.CurrentDomain.FirstChanceException += (sender, args) =>
        {
            // Avoid recursion if Serilog itself throws while writing.
            if (args.Exception is OutOfMemoryException ||
                args.Exception is StackOverflowException ||
                args.Exception is AccessViolationException)
            {
                Serilog.Log.Fatal(args.Exception, "FIRST-CHANCE FATAL EXCEPTION ({Type})", args.Exception.GetType().Name);
            }
            else
            {
                Serilog.Log.Verbose(args.Exception, "FirstChanceException: {Type}", args.Exception.GetType().Name);
            }
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

                // Wire the Civitai base-model catalog into the Unified Console:
                // - log how old the on-disk definition is right now
                // - log every refresh attempt (cache hit, live fetch + result, fallback)
                Serilog.Log.Information("Initializing Civitai base-model catalog...");
                InitializeCivitaiBaseModelCatalog();

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

                // Ensure the local-diffusion outputs folder is visible in the Generation Gallery.
                // Fire-and-forget: failure to register must not block startup (registrar logs internally).
                if (DiffusionNexus.UI.Services.Diffusion.DiffusionFeatureFlags.UseLocalDiffusionBackend)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var scope = Services!.CreateScope();
                            var registrar = scope.ServiceProvider.GetRequiredService<DiffusionNexus.UI.Services.Diffusion.OutputsFolderRegistrar>();
                            await registrar.EnsureRegisteredAsync();
                        }
                        catch (Exception ex)
                        {
                            Serilog.Log.Warning(ex, "OutputsFolderRegistrar failed during startup.");
                        }
                    });
                }

                // Force show the window explicitly
                mainWindow.Show();
                Serilog.Log.Information("Main window Show() called");

                // Cleanup on shutdown
                desktop.ShutdownRequested += (_, _) =>
                {
                    // Dispose the instance process manager (unwires events)
                    (Services?.GetService<IInstanceProcessManager>() as IDisposable)?.Dispose();
                    // Release native diffusion contexts (unloads ~12 GB of model weights)
                    var localBackend = Services?.GetService<DiffusionNexus.UI.Services.Diffusion.LocalDiffusionBackendProvider>();
                    if (localBackend is not null)
                    {
                        try { localBackend.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                        catch (Exception ex) { Serilog.Log.Warning(ex, "Local diffusion backend dispose failed."); }
                    }
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

    /// <summary>
    /// Wires the Civitai base-model catalog to the Unified Console:
    /// reports the on-disk definition's age at startup and logs every refresh
    /// attempt (cache hit, live fetch + result, fallback to bundled snapshot).
    /// </summary>
    private static void InitializeCivitaiBaseModelCatalog()
    {
        const string source = "CivitaiBaseModelCatalog";
        var catalog = Services!.GetRequiredService<Civitai.ICivitaiBaseModelCatalog>();
        var logger = Services!.GetService<DiffusionNexus.Domain.Services.UnifiedLogging.IUnifiedLogger>();

        // a) Subscribe BEFORE any fetch so the first refresh is captured.
        catalog.StatusChanged += (_, e) =>
        {
            try
            {
                var category = DiffusionNexus.Domain.Services.UnifiedLogging.LogCategory.Network;
                var detail = e.CacheTimestampUtc.HasValue
                    ? $"Cache file: {catalog.CacheFilePath}\nLast written (UTC): {e.CacheTimestampUtc:O}"
                    : $"Cache file: {catalog.CacheFilePath}\nNo cache on disk yet.";

                switch (e.Kind)
                {
                    case Civitai.CivitaiBaseModelCatalogEventKind.FetchStarted:
                        logger?.Info(category, source, e.Message ?? "Fetching base model list...", detail);
                        break;
                    case Civitai.CivitaiBaseModelCatalogEventKind.FetchSucceeded:
                        logger?.Info(category, source, e.Message ?? $"Fetched {e.Count} base models.", detail);
                        break;
                    case Civitai.CivitaiBaseModelCatalogEventKind.UsedDiskCache:
                        logger?.Info(category, source, e.Message ?? $"Loaded {e.Count} base models from disk cache.", detail);
                        break;
                    case Civitai.CivitaiBaseModelCatalogEventKind.UsedBundledFallback:
                        logger?.Warn(category, source, e.Message ?? $"Using bundled fallback ({e.Count} base models).", detail);
                        break;
                    case Civitai.CivitaiBaseModelCatalogEventKind.FetchFailed:
                        logger?.Warn(category, source, e.Message ?? "Base model fetch failed.", detail);
                        if (e.Exception is not null)
                        {
                            logger?.Error(category, source, e.Message ?? "Base model fetch failed.", e.Exception);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to forward CivitaiBaseModelCatalog status to UnifiedLogger.");
            }
        };

        // b) Report the current definition age on startup, before any fetch runs.
        var ts = catalog.CacheTimestampUtc;
        if (ts.HasValue)
        {
            var age = DateTime.UtcNow - ts.Value;
            var ageText = FormatAge(age);
            logger?.Info(
                DiffusionNexus.Domain.Services.UnifiedLogging.LogCategory.Network,
                source,
                $"Civitai base model definition is {ageText} old (last refreshed {ts.Value:yyyy-MM-dd HH:mm} UTC).",
                $"Cache file: {catalog.CacheFilePath}");
        }
        else
        {
            logger?.Info(
                DiffusionNexus.Domain.Services.UnifiedLogging.LogCategory.Network,
                source,
                "Civitai base model definition has not been cached yet — will fetch from GitHub on first use.",
                $"Cache file: {catalog.CacheFilePath}");
        }

        // Kick off a background load so the FetchStarted/Succeeded events fire even
        // before the user opens the LoRA detail panel.
        _ = Task.Run(async () =>
        {
            try { await catalog.GetBaseModelsAsync().ConfigureAwait(false); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Initial Civitai base model load failed."); }
        });
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1) return $"{(int)age.TotalDays}d {age.Hours}h";
        if (age.TotalHours >= 1) return $"{(int)age.TotalHours}h {age.Minutes}m";
        if (age.TotalMinutes >= 1) return $"{(int)age.TotalMinutes}m";
        return $"{(int)age.TotalSeconds}s";
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
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column"))
        {
            // Migration ADD COLUMN ran against a schema where the column was already present
            // — typically because an earlier startup applied the schema but failed before
            // stamping __EFMigrationsHistory. Repair + stamp so the next startup is clean.
            Serilog.Log.Warning("InitializeDatabase: Column already present from a prior partial migration (continuing): {Message}", ex.Message);
            CheckAndRepairSchema(dbContext);
            MarkPendingMigrationsAsApplied(dbContext);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqlEx && sqlEx.Message.Contains("duplicate column"))
        {
            Serilog.Log.Warning("InitializeDatabase: Column already present from a prior partial migration (continuing): {Message}", ex.Message);
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
                // After repairing the schema, stamp any still-pending migrations as applied
                // so the next startup doesn't re-attempt them and hit the same exception.
                // Defense-in-depth for schema errors not matched by the specific filters above.
                MarkPendingMigrationsAsApplied(dbContext);
            }
            catch (Exception repairEx)
            {
                Serilog.Log.Error(repairEx, "InitializeDatabase: Schema repair also failed");
            }
        }

        Serilog.Log.Information("InitializeDatabase: Completed");
    }

    /// <summary>
    /// Ensures the SDK database (diffusion_nexus.db) is deployed and up-to-date by
    /// delegating to <see cref="SdkDatabaseDeployer"/>, then applies any pending EF Core
    /// migrations. Reports the resulting status (version, up-to-date, replaced) to the
    /// unified activity log.
    /// </summary>
    private static void InitializeSdkDatabase()
    {
        const string databaseFileName = "diffusion_nexus.db";
        const string logSource = "Installer SDK";

        var activityLog = Services!.GetService<IActivityLogService>();

        try
        {
            // The NuGet contentFiles mechanism copies the seed DB next to the executable.
            var shippedDb = Path.Combine(AppContext.BaseDirectory, databaseFileName);

            // Runtime location: directly in %LocalAppData% (no subfolder).
            // TODO: Linux implementation — use XDG_DATA_HOME or ~/.local/share.
            var runtimeDb = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                databaseFileName);

            var result = SdkDatabaseDeployer.EnsureUpToDate(shippedDb, runtimeDb);

            // a) DB version (always)
            activityLog?.LogInfo(
                logSource,
                $"SDK database version: {result.ShippedVersion}",
                $"Path: {runtimeDb}");

            // b) up-to-date / c) replaced
            switch (result.Outcome)
            {
                case SdkDatabaseDeployOutcome.UpToDate:
                case SdkDatabaseDeployOutcome.SamePath:
                    activityLog?.LogInfo(logSource, "SDK database is up to date.");
                    break;
                case SdkDatabaseDeployOutcome.FirstTimeDeploy:
                    activityLog?.LogSuccess(
                        logSource,
                        $"SDK database deployed for the first time (v{result.ShippedVersion}).");
                    break;
                case SdkDatabaseDeployOutcome.Upgraded:
                    activityLog?.LogSuccess(
                        logSource,
                        $"SDK database upgraded from v{result.RuntimeVersionBefore ?? "unknown"} to v{result.ShippedVersion}.",
                        $"Backup saved to: {result.BackupPath}");
                    break;
                case SdkDatabaseDeployOutcome.NoShippedDatabase:
                    activityLog?.LogWarning(
                        logSource,
                        "No shipped SDK database found; using existing runtime DB.");
                    break;
            }

            // Apply any pending schema migrations on top of the (seed) data.
            var sdkContext = Services!.GetRequiredService<SdkContext>();
            Serilog.Log.Information("InitializeSdkDatabase: Applying migrations to SDK database...");
            sdkContext.Database.Migrate();
            Serilog.Log.Information("InitializeSdkDatabase: Migration completed successfully");

            activityLog?.LogInfo(logSource, $"Database loaded from: {runtimeDb}");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "InitializeSdkDatabase: Failed to initialize SDK database");
            activityLog?.LogError(logSource, "Failed to initialize SDK database", ex);
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
                { "ComfyUiServerUrl", "ALTER TABLE AppSettings ADD COLUMN ComfyUiServerUrl TEXT NOT NULL DEFAULT 'http://127.0.0.1:8188/'" },
                { "LoraUpdateCheckStalenessDays", "ALTER TABLE AppSettings ADD COLUMN LoraUpdateCheckStalenessDays INTEGER NOT NULL DEFAULT 3" },
                { "FavoriteLoraSourcePath", "ALTER TABLE AppSettings ADD COLUMN FavoriteLoraSourcePath TEXT" },
                { "EncryptedHuggingfaceApiKey", "ALTER TABLE AppSettings ADD COLUMN EncryptedHuggingfaceApiKey TEXT" }
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

            // Self-heal Models columns added by later migrations. Needed when a previous
            // run marked the migration as applied (MarkPendingMigrationsAsApplied) but the
            // ALTER TABLE never actually executed — leaving queries to fail with
            // "no such column: …". Mirrors the AppSettings repair above.
            RepairModelsTableColumns(dbContext, connection);
        }
        catch (Exception ex)
        {
             Serilog.Log.Error(ex, "CheckAndRepairSchema: Fatal error during check");
        }
    }

    /// <summary>
    /// Ensures every column EF expects on the <c>Models</c> table actually exists,
    /// applying ALTER TABLE statements for any that were lost when a migration row
    /// was force-marked as applied without the schema actually being updated.
    /// Add new entries here whenever a migration adds nullable / defaulted columns
    /// to the Models table.
    /// </summary>
    private static void RepairModelsTableColumns(DiffusionNexusCoreDbContext dbContext, System.Data.Common.DbConnection connection)
    {
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) connection.Open();

        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info('Models');";
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

        if (existingColumns.Count == 0)
        {
            // Table doesn't exist yet — initial migration will create it.
            return;
        }

        // Migration 20260509105621_AddLoraUpdateCheckFields
        var requiredModelsColumns = new Dictionary<string, string>
        {
            { "LastCheckedForUpdatesUtc", "ALTER TABLE Models ADD COLUMN LastCheckedForUpdatesUtc TEXT" },
            { "TotalVersionCount",        "ALTER TABLE Models ADD COLUMN TotalVersionCount INTEGER NOT NULL DEFAULT 0" },
        };

        foreach (var col in requiredModelsColumns)
        {
            if (existingColumns.Contains(col.Key)) continue;

            Serilog.Log.Warning("CheckAndRepairSchema: Missing Models.'{Column}' column. Attempting to add...", col.Key);
            try
            {
                dbContext.Database.ExecuteSqlRaw(col.Value);
                Serilog.Log.Information("CheckAndRepairSchema: Successfully added Models.'{Column}'", col.Key);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "CheckAndRepairSchema: Failed to add Models.'{Column}'", col.Key);
            }
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
        services.AddTransient<ILoraDuplicateFinder, LoraDuplicateFinder>();

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

        // Local diffusion backend (StableDiffusion.NET / stable-diffusion.cpp).
        // Singleton — owns the per-model native context cache for the lifetime of the app.
        services.AddSingleton<DiffusionNexus.UI.Services.Diffusion.LocalDiffusionBackendProvider>();

        // Outputs folder registrar — ensures <exe-dir>/outputs/ is in the gallery list.
        services.AddTransient<DiffusionNexus.UI.Services.Diffusion.OutputsFolderRegistrar>();

        // GPU VRAM + system RAM monitor (reusable widget shown in the canvas and the Pipelines view).
        services.AddSingleton<IResourceMonitorService, ResourceMonitorService>();
        services.AddTransient<ResourceMonitorViewModel>();

        // Diffusion Canvas view model (singleton — frames persist across navigation in v1).
        services.AddSingleton<DiffusionNexus.UI.ViewModels.DiffusionCanvas.DiffusionCanvasViewModel>();


        // ?? Installer SDK services ??
        // Register SDK data access layer (uses shared database at %LocalAppData%\diffusion_nexus.db from NuGet source)
        services.AddDiffusionNexusDataAccess();

        // Register SDK installation pipeline and all step handlers
        services.AddInstallationServices();

        // Gist-backed server message service (operator announcements shown in the main window banner).
        // Edit the Gist to change what users see — no rebuild required. App id "app" filters targeting.
        services.AddSingleton<IServerMessageService>(_ => new GistServerMessageService(
            new ServerMessageServiceOptions
            {
                Url = "https://gist.githubusercontent.com/Little-God1983/358c5fccc6655f6e56aef8470bb17c1c/raw/messages.json"
            },
            new HttpClient(),
            ownsHttpClient: true));
        services.AddSingleton(_ =>
        {
            var settingsPath = DiffusionNexus.Installer.SDK.DataAccess.ServiceCollectionExtensions.GetUserSettingsFilePath();
            var dir = System.IO.Path.GetDirectoryName(settingsPath) ?? AppContext.BaseDirectory;
            return new DismissedMessageStore(System.IO.Path.Combine(dir, "dismissed_messages.json"));
        });

        // Configuration checker (singleton - accessible across the entire application)
        services.AddSingleton<IConfigurationCheckerService, ConfigurationCheckerService>();

        // Workload installation checker — bridges feature readiness to the same disk-walking
        // logic the Installer Manager workload dialog uses. Singleton; resolves scoped
        // dependencies per call via IServiceProvider. Also gets the unified logger so the
        // per-install check results land in the in-app console.
        services.AddSingleton<Domain.Services.IWorkloadInstallationChecker>(sp =>
            new WorkloadInstallationCheckerAdapter(
                sp,
                sp.GetRequiredService<IConfigurationCheckerService>(),
                sp.GetService<Domain.Services.UnifiedLogging.IUnifiedLogger>()));

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
        services.AddSingleton<Domain.Services.IInstallerUpdateService, Service.Services.AIToolkitUpdateService>();

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

        // Backend-agnostic feature readiness pipeline.
        //
        //   IFeatureReadinessService                 (what view-models depend on)
        //     -> IFeatureBackendRouter               (picks the backend per feature)
        //       -> ComfyUIFeatureBackend             (ComfyUI server + workload checker)
        //       -> LocalInferenceFeatureBackend      (LlamaSharp captioning, sd.cpp generation)
        //
        // A feature reports "Ready" iff its backing workload would show as "Full" in the
        // Installer Manager dialog. The unified logger plumb-through makes readiness
        // decisions visible in the in-app console.
        services.AddSingleton<IFeatureBackend>(sp =>
            new ComfyUIFeatureBackend(
                sp.GetRequiredService<IComfyUIWrapperService>(),
                sp.GetRequiredService<IAppSettingsService>(),
                sp.GetRequiredService<Domain.Services.IWorkloadInstallationChecker>(),
                sp.GetService<Domain.Services.UnifiedLogging.IUnifiedLogger>()));

        // Resolves the concrete LocalInferenceCaptioningBackend rather than the
        // ICaptioningBackend collection — going through the collection would force the
        // ComfyUICaptioningBackend to materialize too, and *it* depends on
        // IFeatureReadinessService → router → IEnumerable<IFeatureBackend> → here. That
        // cycle hangs DI on startup.
        services.AddSingleton<IFeatureBackend>(sp =>
            new Inference.LocalInferenceFeatureBackend(
                sp.GetService<Inference.Captioning.LocalInferenceCaptioningBackend>(),
                diffusion: null));

        services.AddSingleton<IFeatureBackendRouter>(sp =>
            new FeatureBackendRouter(sp.GetServices<IFeatureBackend>()));

        services.AddSingleton<IFeatureReadinessService>(sp =>
            new FeatureReadinessService(
                sp.GetRequiredService<IFeatureBackendRouter>(),
                FeatureRegistry.GetRequirements));

        // Civitai API client (singleton - maintains HttpClient)
        services.AddSingleton<Civitai.ICivitaiClient, Civitai.CivitaiClient>();

        // Civitai base-model catalog (singleton - in-memory + on-disk cache, falls back to bundled snapshot)
        services.AddSingleton<Civitai.ICivitaiBaseModelCatalog, Civitai.CivitaiBaseModelCatalog>();

        // Captioning backends. Multiple ICaptioningBackend registrations resolve to a
        // collection via sp.GetServices<ICaptioningBackend>(); the Captioning tab
        // exposes them as a dropdown.
        services.AddSingleton<ICaptioningBackend>(sp =>
            new ComfyUICaptioningBackend(
                sp.GetRequiredService<IComfyUIWrapperService>(),
                sp.GetService<IFeatureReadinessService>()));

        // Local LlamaSharp + MTMD captioning (vision-language inference in-process).
        // Lives in DiffusionNexus.Inference alongside the stable-diffusion.cpp image
        // generation backend — one project owns all native model inference.
        //
        // The model manager is registered via a factory so it can be given a
        // live ComfyUI path discovery callback. That lets GGUF/mmproj files in
        // any existing ComfyUI install (including paths declared via
        // extra_model_paths.yaml) be picked up automatically — no copying, no
        // env var required.
        services.AddSingleton<Inference.Captioning.CaptioningModelManager>(sp =>
        {
            var uow = sp.GetRequiredService<DataAccess.UnitOfWork.IUnitOfWork>();
            return new Inference.Captioning.CaptioningModelManager(
                modelsBasePath: null,
                httpClient: null,
                extraSearchPathsProvider: () => DiscoverComfyUiCaptioningPaths(uow));
        });
        services.AddSingleton<ICaptioningService>(sp =>
            new Inference.Captioning.CaptioningService(
                sp.GetRequiredService<Inference.Captioning.CaptioningModelManager>(),
                sp.GetService<Domain.Services.UnifiedLogging.IUnifiedLogger>()));
        // Registered as a concrete type first so LocalInferenceFeatureBackend can resolve it
        // without going through the ICaptioningBackend collection (which would drag the
        // ComfyUI captioning backend into the readiness pipeline and create a DI cycle).
        services.AddSingleton<Inference.Captioning.LocalInferenceCaptioningBackend>(sp =>
            new Inference.Captioning.LocalInferenceCaptioningBackend(
                sp.GetRequiredService<ICaptioningService>()));
        services.AddSingleton<ICaptioningBackend>(sp =>
            sp.GetRequiredService<Inference.Captioning.LocalInferenceCaptioningBackend>());

        // Dataset Helper services (singletons - shared state across all components)
        services.AddSingleton<IDatasetEventAggregator, DatasetEventAggregator>();
        services.AddSingleton<IDatasetState, DatasetStateService>();
        services.AddSingleton<IDatasetStorageService, DatasetStorageService>();

        // Spell check & autocomplete services (singletons - shared across all caption editors)
        services.AddSingleton<IUserDictionaryService, UserDictionaryService>();
        services.AddSingleton<ISpellCheckService>(sp =>
            new SpellCheckService(sp.GetRequiredService<IUserDictionaryService>()));
        services.AddSingleton<IAutoCompleteService, AutoCompleteService>();

        // Bridge UI spell check into the domain contract used by dataset quality checks
        services.AddSingleton<ISpellChecker>(sp =>
            new SpellCheckerAdapter(sp.GetRequiredService<ISpellCheckService>()));

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
            sp.GetService<ISettingsExportService>(),
            sp.GetService<Civitai.ICivitaiBaseModelCatalog>()));
        
        services.AddSingleton<ILoraUpdateChecker, LoraUpdateChecker>();

        services.AddScoped<LoraViewerViewModel>(sp => new LoraViewerViewModel(
            sp.GetRequiredService<IAppSettingsService>(),
            sp.GetRequiredService<IModelSyncService>(),
            sp.GetService<Civitai.ICivitaiClient>(),
            sp.GetService<ISecureStorage>(),
            sp.GetService<Domain.Services.UnifiedLogging.IUnifiedLogger>(),
            sp.GetService<Civitai.ICivitaiBaseModelCatalog>(),
            sp.GetService<ILoraUpdateChecker>()));
        services.AddScoped<LoraDownloadService>(sp => new LoraDownloadService(
            sp.GetService<Civitai.ICivitaiClient>(),
            sp.GetService<IAppSettingsService>(),
            sp.GetService<Domain.Services.UnifiedLogging.IUnifiedLogger>()));

        // Reusable LoRA catalog for the Multi-LoRA Picker (same sources as the LoRA Viewer Installed tab).
        services.AddSingleton<Services.Lora.ILoraCatalog, Services.Lora.LoraCatalog>();

        // Pipelines: app-side manifest provider + asset installer + module ViewModel.
        services.AddSingleton<Services.Pipelines.IPipelineManifestProvider, Services.Pipelines.PipelineManifestProvider>();
        services.AddScoped<Services.Pipelines.IPipelineAssetInstaller>(sp => new Services.Pipelines.PipelineAssetInstaller(
            sp.GetRequiredService<IDownloadCoordinator>(),
            sp.GetRequiredService<Civitai.ICivitaiClient>(),
            sp.GetRequiredService<LoraDownloadService>(),
            sp.GetRequiredService<IAppSettingsService>(),
            sp.GetRequiredService<Services.Diffusion.LocalDiffusionBackendProvider>(),
            sp.GetService<Domain.Services.UnifiedLogging.IUnifiedLogger>()));

        // Pipeline run UI: output writer + a tile-id -> run-ViewModel factory.
        services.AddScoped<Services.Pipelines.IPipelineOutputWriter, Services.Pipelines.PipelineOutputWriter>();
        services.AddTransient<Func<PipelineTileViewModel, ViewModels.Pipelines.PipelineRunViewModel>>(sp => tile =>
            tile.Id switch
            {
                "anime-to-real" => ActivatorUtilities.CreateInstance<ViewModels.Pipelines.AnimeToRealPipelineRunViewModel>(sp, tile.Manifest),
                "image-to-image" => ActivatorUtilities.CreateInstance<ViewModels.Pipelines.ImageToImagePipelineRunViewModel>(sp, tile.Manifest),
                _ => throw new NotSupportedException($"No run UI is registered for pipeline '{tile.Id}'."),
            });

        services.AddScoped<PipelinesViewModel>(sp => new PipelinesViewModel(
            sp.GetRequiredService<Services.Pipelines.IPipelineManifestProvider>(),
            sp.GetRequiredService<Services.Pipelines.IPipelineAssetInstaller>(),
            sp.GetService<ResourceMonitorViewModel>(),
            sp.GetService<Func<PipelineTileViewModel, ViewModels.Pipelines.PipelineRunViewModel>>(),
            sp.GetService<IDialogService>(),
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
            sp.GetRequiredService<Domain.Services.UnifiedLogging.IUnifiedLogger>(),
            sp.GetService<Inference.Captioning.CaptioningModelManager>(),
            sp.GetService<ICaptioningService>(),
            sp.GetService<IActivityLogService>(),
            sp.GetService<IDownloadCoordinator>()));
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
            sp.GetService<IDatasetBackupService>(),
            sp.GetService<IActivityLogService>(),
            sp.GetService<IComfyUIWrapperService>(),
            sp.GetService<IThumbnailOrchestrator>(),
            sp.GetService<AnalysisPipeline>(),
            sp.GetService<BucketAnalyzer>(),
            sp.GetService<IFeatureReadinessService>(),
            sp.GetServices<IImageQualityCheck>(),
            sp.GetService<AnalysisRunStore>(),
            sp.GetService<DuplicateDetector>(),
            sp.GetService<ColorDistributionAnalyzer>(),
            sp.GetService<IDownloadCoordinator>(),
            sp.GetService<Domain.Services.UnifiedLogging.IUnifiedLogger>(),
            sp.GetService<Civitai.ICivitaiBaseModelCatalog>()));
    }

    /// <summary>
    /// Builds the live list of additional model search paths the captioning
    /// service should scan. For every registered ComfyUI installation, we add
    /// the standard <c>models/</c> root plus any <c>base_path</c>/model-type
    /// entries declared in <c>extra_model_paths.yaml</c>. Invoked lazily by
    /// <see cref="Inference.Captioning.CaptioningModelManager"/> on each file
    /// lookup so install changes are picked up without restarting the app.
    /// </summary>
    private static IReadOnlyList<string> DiscoverComfyUiCaptioningPaths(DataAccess.UnitOfWork.IUnitOfWork uow)
    {
        try
        {
            // Blocking on the async call is acceptable here: it runs on a
            // background captioning lookup, not the UI thread, and queries
            // the local SQLite repository which is effectively synchronous.
            var packages = uow.InstallerPackages.GetAllAsync().GetAwaiter().GetResult();

            var aggregated = new List<string>();
            foreach (var package in packages)
            {
                if (package.Type != Domain.Enums.InstallerType.ComfyUI)
                {
                    continue;
                }

                foreach (var root in DiffusionNexus.UI.Services.ComfyUiPathDiscovery.EnumerateModelSearchPaths(package.InstallationPath))
                {
                    if (!aggregated.Contains(root, StringComparer.OrdinalIgnoreCase))
                    {
                        aggregated.Add(root);
                    }
                }
            }
            return aggregated;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to enumerate ComfyUI captioning paths — falling back to defaults.");
            return [];
        }
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

        // Wire Unified Console ↔ Installer Manager update synchronisation:
        // 1. Console "Update" button delegates to the Installer Manager's centralised logic
        // 2. Installer Manager state changes flow back to the console tabs
        if (mainViewModel.StatusBar?.UnifiedConsole is { } unifiedConsole)
        {
            unifiedConsole.SetUpdateDelegate(installerManagerVm.UpdatePackageByIdAsync);

            installerManagerVm.InstallerUpdateStateChanged += (_, e) =>
                unifiedConsole.OnExternalUpdateStateChanged(e);
        }

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

        // Diffusion Canvas module — local Z-Image-Turbo generation on an Invoke-AI-style canvas.
        // Gated behind DiffusionFeatureFlags.UseLocalDiffusionBackend so the module disappears
        // entirely when the local backend is disabled (e.g., for ComfyUI-only builds).
        // Also hidden by default in the sidebar; the obscure checkbox in the main window
        // (IsDiffusionCanvasEnabled) reveals it.
        if (DiffusionNexus.UI.Services.Diffusion.DiffusionFeatureFlags.UseLocalDiffusionBackend)
        {
            var diffusionCanvasVm = Services!.GetRequiredService<DiffusionNexus.UI.ViewModels.DiffusionCanvas.DiffusionCanvasViewModel>();
            var diffusionCanvasView = new DiffusionNexus.UI.Views.DiffusionCanvas.DiffusionCanvasView
            {
                DataContext = diffusionCanvasVm
            };

            var diffusionCanvasModule = new ModuleItem(
                "Diffusion Canvas",
                "avares://DiffusionNexus.UI/Assets/PromptEdit.png", // TODO: dedicated canvas icon
                diffusionCanvasView,
                isVisible: mainViewModel.IsDiffusionCanvasEnabled)
            {
                ViewModel = diffusionCanvasVm
            };

            mainViewModel.RegisterModule(diffusionCanvasModule);
            mainViewModel.SetDiffusionCanvasModule(diffusionCanvasModule);
        }

        // Image Comparer module
        var datasetState = Services!.GetRequiredService<IDatasetState>();
        var thumbnailOrchestrator = Services!.GetService<IThumbnailOrchestrator>();
        var dialogService = Services!.GetRequiredService<IDialogService>();
        var imageCompareVm = new ImageCompareViewModel(datasetState, thumbnailOrchestrator, dialogService);
        var imageCompareView = new ImageCompareView { DataContext = imageCompareVm };

        var imageComparerModule = new ModuleItem(
            "Image Comparer",
            "avares://DiffusionNexus.UI/Assets/ImageComparer.png",
            imageCompareView)
        {
            ViewModel = imageCompareVm
        };

        mainViewModel.RegisterModule(imageComparerModule);

        // Pipelines module — tile gallery of guided image pipelines (currently Anime-To-Real).
        var pipelinesVm = Services!.GetRequiredService<PipelinesViewModel>();
        var pipelinesView = new PipelinesView { DataContext = pipelinesVm };

        var pipelinesModule = new ModuleItem(
            "Workflows",
            "avares://DiffusionNexus.UI/Assets/HumanCogwheel.png",
            pipelinesView)
        {
            ViewModel = pipelinesVm
        };

        mainViewModel.RegisterModule(pipelinesModule);

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

        eventAggregator.NavigateToBatchUpscaleRequested += (_, _) =>
        {
            mainViewModel.NavigateToModuleCommand.Execute(loraDatasetHelperModule);
        };

        eventAggregator.NavigateToCaptioningRequested += (_, _) =>
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

        eventAggregator.NavigateToWorkflowRequested += (_, e) =>
        {
            mainViewModel.NavigateToModuleCommand.Execute(pipelinesModule);
            _ = pipelinesVm.OpenWorkflowAsync(e.WorkflowId, e.ImagePaths);
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
            // Disclaimer + settings must complete first � other modules depend on them.
            await mainViewModel.CheckDisclaimerStatusAsync();
            await settingsVm.LoadCommand.ExecuteAsync(null);

            // Remaining modules are independent � load in parallel.
            await Task.WhenAll(
                loraViewerVm.RefreshCommand.ExecuteAsync(null),
                generationGalleryVm.LoadMediaCommand.ExecuteAsync(null),
                installerManagerVm.LoadInstallationsCommand.ExecuteAsync(null),
                loraDatasetHelperVm.DatasetManagement
                    .CheckStorageConfigurationCommand.ExecuteAsync(null),
                mainViewModel.LoadServerMessagesAsync());
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
