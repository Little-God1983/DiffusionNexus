using DiffusionNexus.Service.Classes;
using Serilog;
using System.Collections.Generic;

namespace DiffusionNexus.Service.Services;

public class CompositeMetadataProvider : IModelMetadataProvider
{
    private readonly List<IModelMetadataProvider> _providers;

    public CompositeMetadataProvider(params IModelMetadataProvider[] providers)
    {
        _providers = providers.ToList();
    }

    public async Task<bool> CanHandleAsync(string identifier, CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            if (await provider.CanHandleAsync(identifier, cancellationToken))
                return true;
        }
        return false;
    }

    public async Task<ModelClass> GetModelMetadataAsync(string identifier, CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            if (await provider.CanHandleAsync(identifier, cancellationToken))
            {
                try
                {
                    return await provider.GetModelMetadataAsync(identifier, cancellationToken);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Provider {Provider} failed for {Identifier}", provider.GetType().Name, identifier);
                }
            }
        }
        throw new InvalidOperationException($"No provider could handle identifier: {identifier}");
    }
}
