using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services.DatasetQuality.Checks;
using DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Service.Services.DatasetQuality;

/// <summary>
/// Registers dataset quality analysis services in the DI container.
/// </summary>
public static class DatasetQualityServiceExtensions
{
    /// <summary>
    /// Adds the dataset quality analysis pipeline, caption loader, and all
    /// built-in <see cref="IDatasetCheck"/> implementations to the container.
    /// </summary>
    public static IServiceCollection AddDatasetQualityServices(this IServiceCollection services)
    {
        // Core services
        services.AddSingleton<CaptionLoader>();
        services.AddSingleton<AnalysisPipeline>();

        // Bucket analysis services
        services.AddSingleton<IImageDimensionReader, ImageHeaderReader>();
        services.AddSingleton<BucketAnalyzer>();

        // Built-in checks — each registered as IDatasetCheck so the pipeline discovers them
        services.AddSingleton<IDatasetCheck, FormatConsistencyCheck>();
        services.AddSingleton<IDatasetCheck, TriggerWordCheck>();
        services.AddSingleton<IDatasetCheck, SynonymConsistencyCheck>();
        services.AddSingleton<IDatasetCheck, FeatureConsistencyCheck>();
        services.AddSingleton<IDatasetCheck, TypeSpecificCheck>();
        services.AddSingleton<IDatasetCheck, SpellCheckQualityCheck>();

        // Image quality checks — each registered as IImageQualityCheck
        services.AddSingleton<IImageQualityCheck, BlurDetector>();
        services.AddSingleton<IImageQualityCheck, ExposureAnalyzer>();

        return services;
    }
}
