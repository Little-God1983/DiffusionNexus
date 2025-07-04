using DiffusionNexus.Service.Classes;
using System.Text.Json;

namespace DiffusionNexus.Service.Services.Metadata
{
    public class CivitaiApiMetadataProvider : IModelMetadataProvider
    {
        private readonly ICivitaiApiClient _apiClient;
        private readonly string _apiKey;

        public CivitaiApiMetadataProvider(ICivitaiApiClient apiClient, string apiKey)
        {
            _apiClient = apiClient;
            _apiKey = apiKey;
        }

        public Task<bool> CanHandleAsync(string identifier, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(identifier.Length == 64 && System.Text.RegularExpressions.Regex.IsMatch(identifier, "^[a-fA-F0-9]+$"));
        }

        public async Task<ModelClass?> GetModelMetadataAsync(string sha256Hash, CancellationToken cancellationToken = default)
        {
            var metadata = new ModelClass();

            var versionJson = await _apiClient.GetModelVersionByHashAsync(sha256Hash, _apiKey);
            using var versionDoc = JsonDocument.Parse(versionJson);
            var versionRoot = versionDoc.RootElement;

            if (versionRoot.TryGetProperty("modelId", out var modelId))
            {
                metadata.SafeTensorFileName = sha256Hash;
                var modelJson = await _apiClient.GetModelAsync(modelId.GetString()!, _apiKey);
                using var modelDoc = JsonDocument.Parse(modelJson);
                ParseModelInfo(modelDoc.RootElement, metadata);
            }

            if (versionRoot.TryGetProperty("baseModel", out var baseModel))
                metadata.DiffusionBaseModel = baseModel.GetString() ?? metadata.DiffusionBaseModel;

            if (versionRoot.TryGetProperty("name", out var versionName))
                metadata.ModelVersionName = versionName.GetString();

            return metadata;
        }

        private static void ParseModelInfo(JsonElement root, ModelClass metadata)
        {
            if (root.TryGetProperty("type", out var type))
                metadata.ModelType = ParseModelType(type.GetString());

            if (root.TryGetProperty("tags", out var tags))
                metadata.Tags = ParseTags(tags);
        }

        private static DiffusionTypes ParseModelType(string? type)
        {
            if (string.IsNullOrWhiteSpace(type))
                return DiffusionTypes.OTHER;
            var cleaned = type.Replace(" ", string.Empty).ToUpperInvariant();
            return Enum.TryParse(cleaned, true, out DiffusionTypes result) ? result : DiffusionTypes.OTHER;
        }

        private static List<string> ParseTags(JsonElement tags)
        {
            var list = new List<string>();
            foreach (var t in tags.EnumerateArray())
            {
                var s = t.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    list.Add(s);
            }
            return list;
        }
    }
}
