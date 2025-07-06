using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Service.Services.IO;

/// <summary>
/// Registers IO related services for dependency injection.
/// </summary>
public static class IoServiceCollectionExtensions
{
    /// <summary>
    /// Adds the core IO services to the <see cref="IServiceCollection"/>.
    /// </summary>
    public static IServiceCollection AddIoServices(this IServiceCollection services)
    {
        services.AddSingleton<DiskUtility>();
        services.AddSingleton<HashingService>();
        services.AddSingleton<IProgressReporter, ConsoleProgressReporter>();
        services.AddTransient<FileControllerService>();
        return services;
    }
}
