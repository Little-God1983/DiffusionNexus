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
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI;

public partial class App : Application
{
    /// <summary>
    /// Service provider for dependency injection.
    /// </summary>
    public static IServiceProvider? Services { get; private set; }

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
            Services = services.BuildServiceProvider();

            // Ensure database is created
            using (var scope = Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();
                dbContext.Database.EnsureCreated();
            }

            // Create main window with modules
            var mainViewModel = new DiffusionNexusMainWindowViewModel();
            RegisterModules(mainViewModel);

            desktop.MainWindow = new DiffusionNexusMainWindow
            {
                DataContext = mainViewModel
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

        // Application services
        services.AddScoped<IAppSettingsService, AppSettingsService>();

        // ViewModels (transient - new instance each time)
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<LoraHelperViewModel>();
    }

    private void RegisterModules(DiffusionNexusMainWindowViewModel mainViewModel)
    {
        // LoRA Helper module - main feature
        var loraHelperVm = Services!.GetRequiredService<LoraHelperViewModel>();
        var loraHelperView = new LoraHelperView { DataContext = loraHelperVm };

        mainViewModel.RegisterModule(new ModuleItem(
            "LoRA Helper",
            string.Empty, // Icon path - add asset later
            loraHelperView));

        // Settings module
        var settingsVm = Services!.GetRequiredService<SettingsViewModel>();
        var settingsView = new SettingsView { DataContext = settingsVm };

        mainViewModel.RegisterModule(new ModuleItem(
            "Settings",
            string.Empty, // Icon path - add asset later
            settingsView));

        // Load settings on startup
        settingsVm.LoadCommand.Execute(null);
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
