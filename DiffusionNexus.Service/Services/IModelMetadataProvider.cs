using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.Service.Services;

public interface IModelMetadataProvider
{
    Task<ModelClass> GetModelMetadataAsync(string identifier, CancellationToken cancellationToken = default);
    Task<bool> CanHandleAsync(string identifier, CancellationToken cancellationToken = default);
}
