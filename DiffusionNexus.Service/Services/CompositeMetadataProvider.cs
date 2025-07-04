using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.Service.Services;

public class CompositeMetadataProvider : IModelMetadataProvider
{
    private readonly IEnumerable<IModelMetadataProvider> _providers;

    public CompositeMetadataProvider(params IModelMetadataProvider[] providers)
    {
        _providers = providers;
    }

    public async Task<bool> CanHandleAsync(string identifier, CancellationToken cancellationToken = default)
    {
        foreach (var p in _providers)
        {
            if (await p.CanHandleAsync(identifier, cancellationToken))
                return true;
        }
        return false;
    }

    public async Task<ModelClass> GetModelMetadataAsync(string identifier, CancellationToken cancellationToken = default)
    {
        foreach (var p in _providers)
        {
            if (await p.CanHandleAsync(identifier, cancellationToken))
            {
                var result = await p.GetModelMetadataAsync(identifier, cancellationToken);
                if (result != null)
                    return result;
            }
        }
        return new ModelClass { SafeTensorFileName = Path.GetFileNameWithoutExtension(identifier), NoMetaData = true };
    }
}

