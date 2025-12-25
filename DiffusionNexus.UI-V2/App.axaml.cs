using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Views;
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

            // Ensure database is created (use EnsureCreated for simplicity)
            var dbContext = Services.GetRequiredService<DiffusionNexusCoreDbContext>();
            dbContext.Database.EnsureCreated();

            // Create main window with modules
            var mainViewModel = new DiffusionNexusMainWindowViewModel();
            RegisterModules(mainViewModel);

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

    private static void ConfigureServices(IServiceCollection services)
    {
        // Database
        services.AddDiffusionNexusCoreDatabase();

        // Infrastructure services (secure storage, image caching)
        services.AddInfrastructureServices();

        // Application services - Scoped works within our app scope
        services.AddScoped<IAppSettingsService, AppSettingsService>();
        services.AddScoped<IModelSyncService, ModelFileSyncService>();

        // ViewModels (scoped to app lifetime)
        services.AddScoped<SettingsViewModel>();
        services.AddScoped<LoraHelperViewModel>();
    }

    private void RegisterModules(DiffusionNexusMainWindowViewModel mainViewModel)
    {
        // LoRA Helper module - main feature
        var loraHelperVm = Services!.GetRequiredService<LoraHelperViewModel>();
        var loraHelperView = new LoraHelperView { DataContext = loraHelperVm };

        mainViewModel.RegisterModule(new ModuleItem(
            "LoRA Helper",
            "avares://DiffusionNexus.UI-V2/Assets/LoraSort.png",
            loraHelperView));

        // Settings module
        var settingsVm = Services!.GetRequiredService<SettingsViewModel>();
        var settingsView = new SettingsView { DataContext = settingsVm };

        mainViewModel.RegisterModule(new ModuleItem(
            "Settings",
            "avares://DiffusionNexus.UI-V2/Assets/settings.png",
            settingsView));

        // Load settings on startup
        settingsVm.LoadCommand.Execute(null);
        
        // Load models on startup
        loraHelperVm.RefreshCommand.Execute(null);
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
