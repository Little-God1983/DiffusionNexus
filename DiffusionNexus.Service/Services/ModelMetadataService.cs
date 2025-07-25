using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.Service.Services;

public class ModelMetadataService
{
    private readonly IModelMetadataProvider _provider;

    public ModelMetadataService(IModelMetadataProvider provider)
    {
        _provider = provider;
    }

    public Task<ModelClass> GetModelMetadataAsync(string identifier, CancellationToken cancellationToken = default)
    {
        return _provider.GetModelMetadataAsync(identifier, cancellationToken);
    }
}

