using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.Service.Services.Metadata
{
    public class CompositeMetadataProvider : IModelMetadataProvider
    {
        private readonly IModelMetadataProvider[] _providers;

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

        public async Task<ModelClass?> GetModelMetadataAsync(string identifier, CancellationToken cancellationToken = default)
        {
            var result = new ModelClass();
            foreach (var p in _providers)
            {
                if (await p.CanHandleAsync(identifier, cancellationToken))
                {
                    var meta = await p.GetModelMetadataAsync(identifier, cancellationToken);
                    if (meta != null)
                    {
                        Merge(result, meta);
                    }
                }
            }
            return result;
        }

        private static void Merge(ModelClass target, ModelClass source)
        {
            if (!string.IsNullOrWhiteSpace(source.ModelVersionName))
                target.ModelVersionName = source.ModelVersionName;

            if (!string.IsNullOrWhiteSpace(source.DiffusionBaseModel) && target.DiffusionBaseModel == "UNKNOWN")
                target.DiffusionBaseModel = source.DiffusionBaseModel;

            if (source.ModelType != DiffusionTypes.OTHER)
                target.ModelType = source.ModelType;

            if (source.Tags != null && source.Tags.Count > 0)
                target.Tags = source.Tags;

            if (source.CivitaiCategory != CivitaiBaseCategories.UNASSIGNED)
                target.CivitaiCategory = source.CivitaiCategory;
        }
    }
}
