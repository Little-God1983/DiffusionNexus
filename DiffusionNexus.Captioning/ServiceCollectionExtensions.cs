using DiffusionNexus.Domain.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Captioning;

/// <summary>
/// Extension methods for registering captioning services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the captioning service and its dependencies to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCaptioningServices(this IServiceCollection services)
    {
        // Register the model manager as a singleton
        services.AddSingleton<CaptioningModelManager>();

        // Register the captioning service as a singleton (manages GPU resources)
        services.AddSingleton<ICaptioningService, CaptioningService>();

        return services;
    }

    /// <summary>
    /// Adds the captioning service with a custom models path.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="modelsPath">Custom path for model storage.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCaptioningServices(this IServiceCollection services, string modelsPath)
    {
        // Register the model manager with custom path
        services.AddSingleton(sp => new CaptioningModelManager(modelsPath, null));

        // Register the captioning service
        services.AddSingleton<ICaptioningService>(sp =>
        {
            var modelManager = sp.GetRequiredService<CaptioningModelManager>();
            return new CaptioningService(modelManager);
        });

        return services;
    }
}
