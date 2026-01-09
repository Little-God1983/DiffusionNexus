using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Infrastructure;

/// <summary>
/// Extension methods for registering infrastructure services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds infrastructure services to the dependency injection container.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="cacheDirectory">Optional custom cache directory.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        string? cacheDirectory = null)
    {
        // Register HttpClient for image downloads
        services.AddHttpClient<IImageCacheService, ImageCacheService>();

        // Register ImageCacheService
        services.AddSingleton<IImageCacheService>(sp =>
        {
            var httpClientFactory = sp.GetService<IHttpClientFactory>();
            var httpClient = httpClientFactory?.CreateClient(nameof(IImageCacheService));
            return new ImageCacheService(cacheDirectory, httpClient);
        });

        // Register SecureStorage
        services.AddSingleton<ISecureStorage, SecureStorageService>();

        // Register ActivityLogService - singleton so all modules share the same log
        services.AddSingleton<IActivityLogService, ActivityLogService>();

        return services;
    }

    /// <summary>
    /// Adds infrastructure services with custom configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configuration action.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        Action<InfrastructureOptions> configure)
    {
        var options = new InfrastructureOptions();
        configure(options);

        return services.AddInfrastructureServices(options.CacheDirectory);
    }
}

/// <summary>
/// Configuration options for infrastructure services.
/// </summary>
public sealed class InfrastructureOptions
{
    /// <summary>
    /// Custom directory for image cache.
    /// Default: %LOCALAPPDATA%/DiffusionNexus/ImageCache
    /// </summary>
    public string? CacheDirectory { get; set; }

    /// <summary>
    /// Maximum concurrent image downloads.
    /// </summary>
    public int MaxConcurrentDownloads { get; set; } = 4;
}
