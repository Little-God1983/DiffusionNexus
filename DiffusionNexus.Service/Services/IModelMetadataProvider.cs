namespace DiffusionNexus.Service.Services;

using DiffusionNexus.Service.Classes;

public interface IModelMetadataProvider
{
    Task<ModelMetadata> GetModelMetadataAsync(string identifier, CancellationToken cancellationToken = default);
    Task<bool> CanHandleAsync(string identifier, CancellationToken cancellationToken = default);
}
