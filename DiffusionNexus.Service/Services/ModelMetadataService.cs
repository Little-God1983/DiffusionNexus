using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Helper;
using DiffusionNexus.Service.Services.Metadata;
using Serilog;
using System.Security.Cryptography;

namespace DiffusionNexus.Service.Services
{
    public class ModelMetadataService
    {
        private readonly CompositeMetadataProvider _provider;
        private readonly CivitaiApiMetadataProvider _apiProvider;
        private readonly CivitaiHelperMetadataProvider _localProvider;

        public ModelMetadataService(ICivitaiApiClient apiClient, string apiKey)
        {
            _apiProvider = new CivitaiApiMetadataProvider(apiClient, apiKey);
            _localProvider = new CivitaiHelperMetadataProvider();
            _provider = new CompositeMetadataProvider(_localProvider, _apiProvider);
        }

        public List<ModelClass> GroupFilesByPrefix(string rootDirectory)
        {
            var fileGroups = new Dictionary<string, List<FileInfo>>();
            string[] files = Directory.GetFiles(rootDirectory, "*", SearchOption.AllDirectories);
            foreach (var filePath in files)
            {
                var fileInfo = new FileInfo(filePath);
                var prefix = ExtractBaseName(fileInfo.Name).ToLowerInvariant();
                if (!fileGroups.ContainsKey(prefix))
                {
                    fileGroups[prefix] = new List<FileInfo>();
                }
                fileGroups[prefix].Add(fileInfo);
            }

            var modelClasses = new List<ModelClass>();
            foreach (var group in fileGroups)
            {
                var model = new ModelClass
                {
                    SafeTensorFileName = group.Key,
                    AssociatedFilesInfo = group.Value,
                    CivitaiCategory = CivitaiBaseCategories.UNKNOWN,
                    NoMetaData = group.Value.Count <= 1
                };
                modelClasses.Add(model);
            }
            return modelClasses;
        }

        public async Task<List<ModelClass>> GetModelData(IProgress<ProgressReport>? progress, string folder, CancellationToken token, bool fetchFromApi = true)
        {
            var models = GroupFilesByPrefix(folder);
            int count = 1;
            foreach (var model in models)
            {
                token.ThrowIfCancellationRequested();
                var safeTensor = model.AssociatedFilesInfo.FirstOrDefault(f => f.Extension == ".safetensors");
                if (safeTensor == null)
                {
                    model.NoMetaData = true;
                    continue;
                }

                var meta = await _localProvider.GetModelMetadataAsync(safeTensor.FullName, token);
                if (meta != null)
                {
                    Merge(model, meta);
                }

                bool needApi = fetchFromApi && (model.DiffusionBaseModel == "UNKNOWN" || model.ModelType == DiffusionTypes.OTHER);
                if (needApi)
                {
                    try
                    {
                        var hash = ComputeSHA256(safeTensor.FullName);
                        var apiMeta = await _apiProvider.GetModelMetadataAsync(hash, token);
                        if (apiMeta != null)
                        {
                            Merge(model, apiMeta);
                            model.NoMetaData = false;
                            model.CivitaiCategory = GetCategoryFromTags(model.Tags);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "API metadata fetch failed for {File}", safeTensor.FullName);
                        model.ErrorOnRetrievingMetaData = true;
                    }
                }
                count++;
            }
            return models;
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
        }

        private static string ExtractBaseName(string fileName)
        {
            var extension = StaticFileTypes.GeneralExtensions.OrderByDescending(e => e.Length).FirstOrDefault(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
            if (extension != null)
                return fileName.Substring(0, fileName.Length - extension.Length);
            return fileName;
        }

        private static string ComputeSHA256(string filePath)
        {
            using FileStream stream = File.OpenRead(filePath);
            using SHA256 sha256 = SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(stream);
            return BitConverter.ToString(hashBytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static CivitaiBaseCategories GetCategoryFromTags(List<string> tags)
        {
            foreach (string tag in tags)
            {
                if (Enum.TryParse(tag.Replace(" ", "_").ToUpperInvariant(), out CivitaiBaseCategories category))
                {
                    return category;
                }
            }
            return CivitaiBaseCategories.UNKNOWN;
        }
    }
}
