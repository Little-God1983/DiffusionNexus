using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.Service.Services;

public interface IModelMetadataProvider
{
    Task<ModelMetadata> GetModelMetadataAsync(string identifier, CancellationToken cancellationToken = default);
    Task<bool> CanHandleAsync(string identifier, CancellationToken cancellationToken = default);
}
