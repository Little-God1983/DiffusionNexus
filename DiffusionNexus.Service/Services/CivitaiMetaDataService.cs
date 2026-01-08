using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using DiffusionNexus.Service.Enums;
using Serilog;

namespace DiffusionNexus.Service.Services
{
    /// <summary>
    /// Service for fetching metadata from Civitai API using raw JSON parsing.
    /// </summary>
    /// <remarks>
    /// Consider using <see cref="TypedCivitaiMetadataProvider"/> for new code,
    /// which uses the strongly-typed <see cref="DiffusionNexus.Civitai.ICivitaiClient"/>.
    /// </remarks>
#pragma warning disable CS0618 // Type or member is obsolete - using legacy ICivitaiApiClient
    public class CivitaiMetaDataService : ICivitaiMetaDataService
    {
        private readonly ICivitaiApiClient _apiClient;
        private readonly string _apiKey;

        public CivitaiMetaDataService()
            : this(new CivitaiApiClient(new HttpClient()), string.Empty)
        {
        }

        public CivitaiMetaDataService(string apiKey) : this(new CivitaiApiClient(new HttpClient()), apiKey)
        {
        }

        public CivitaiMetaDataService(ICivitaiApiClient apiClient, string apiKey = "")
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _apiKey = apiKey;
        }
#pragma warning restore CS0618

        /// <inheritdoc/>
        public Task<string> GetModelVersionInformationFromCivitaiAsync(string sha256Hash, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
#pragma warning disable CS0618 // Using obsolete API client
            return _apiClient.GetModelVersionByHashAsync(sha256Hash, _apiKey);
#pragma warning restore CS0618
        }

        /// <inheritdoc/>
        public async Task<string> GetModelInformationAsync(string safetensorsFilePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string metadataJson = ExtractMetadata(safetensorsFilePath);
            string civitaiUrl = ExtractModelUrl(metadataJson);
            string modelId = ParseModelId(civitaiUrl);

            cancellationToken.ThrowIfCancellationRequested();
#pragma warning disable CS0618 // Using obsolete API client
            return await _apiClient.GetModelAsync(modelId, _apiKey);
#pragma warning restore CS0618
        }

        /// <summary>
        /// Extracts the JSON metadata from a safetensors file.
        /// The file format begins with an 8-byte little-endian unsigned integer indicating the length of the JSON header,
        /// followed immediately by the JSON header.
        /// </summary>
        /// <param name="filePath">Path to the safetensors file.</param>
        /// <returns>A UTF-8 encoded string containing the JSON metadata.</returns>
        private static string ExtractMetadata(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);

            // Read the first 8 bytes (header length)
            byte[] lengthBytes = new byte[8];
            int bytesRead = fs.Read(lengthBytes, 0, 8);
            if (bytesRead < 8)
            {
                throw new InvalidOperationException("File is too short to contain a valid header length.");
            }

            // Convert the first 8 bytes into a UInt64 (little-endian)
            ulong headerLength = BitConverter.ToUInt64(lengthBytes, 0);

            // Read the JSON header bytes.
            byte[] headerBytes = new byte[headerLength];
            bytesRead = fs.Read(headerBytes, 0, (int)headerLength);
            if ((ulong)bytesRead < headerLength)
            {
                throw new InvalidOperationException("File is too short to contain the full header.");
            }

            // Convert the header bytes into a UTF8 string.
            return Encoding.UTF8.GetString(headerBytes);
        }

        private static string ExtractModelUrl(string metadataJson)
        {
            using JsonDocument doc = JsonDocument.Parse(metadataJson);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("__metadata__", out JsonElement metaElement) &&
                metaElement.TryGetProperty("civitai", out JsonElement civitaiElement))
            {
                return civitaiElement.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("modelUrl", out JsonElement modelUrlElement))
            {
                return modelUrlElement.GetString() ?? string.Empty;
            }

            return string.Empty;
        }

        private static string ParseModelId(string civitaiUrl)
        {
            var match = Regex.Match(civitaiUrl, @"civitai\.com/models/(\d+)", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                throw new InvalidOperationException("Failed to extract model id from the Civitai URL.");
            }

            return match.Groups[1].Value;
        }

        /// <inheritdoc/>
        public string GetBaseModelName(string modelInfoApiResponse)
        {
            using JsonDocument doc = JsonDocument.Parse(modelInfoApiResponse);
            return doc.RootElement.GetProperty("baseModel").ToString();
        }

        /// <inheritdoc/>
        public string GetModelId(string modelInfoApiResponse)
        {
            using JsonDocument doc = JsonDocument.Parse(modelInfoApiResponse);
            return doc.RootElement.GetProperty("modelId").ToString();
        }

        /// <inheritdoc/>
        public Task<string> GetModelInformationFromCivitaiAsync(string modelId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
#pragma warning disable CS0618 // Using obsolete API client
            return _apiClient.GetModelAsync(modelId, _apiKey);
#pragma warning restore CS0618
        }

        /// <inheritdoc/>
        public List<string> GetTagsFromModelInfo(string modelInfoApiResponse)
        {
            var tags = new List<string>();

            using (JsonDocument doc = JsonDocument.Parse(modelInfoApiResponse))
            {
                JsonElement root = doc.RootElement;

                // Check if 'tags' property exists and is an array
                if (root.TryGetProperty("tags", out JsonElement tagsElement) &&
                    tagsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement tagValue in tagsElement.EnumerateArray())
                    {
                        string? tagString = tagValue.GetString();
                        if (!string.IsNullOrWhiteSpace(tagString))
                        {
                            tags.Add(tagString);
                        }
                    }
                }
            }

            return tags;
        }

        /// <inheritdoc/>
        public DiffusionTypes GetModelType(string modelInfoApiResponse)
        {
            using JsonDocument doc = JsonDocument.Parse(modelInfoApiResponse);
            JsonElement root = doc.RootElement;

            // 1. Check if `type` is directly in the root
            if (root.TryGetProperty("type", out JsonElement typeElement) &&
                typeElement.ValueKind == JsonValueKind.String)
            {
                string? typeString = typeElement.GetString()?.Replace(" ", "").ToUpper();

                if (typeString != null && Enum.TryParse(typeString, true, out DiffusionTypes modelType))
                {
                    return modelType;
                }
            }

            // 2. Check if `type` is inside `model`
            if (root.TryGetProperty("model", out JsonElement modelElement) &&
                modelElement.TryGetProperty("type", out JsonElement nestedTypeElement) &&
                nestedTypeElement.ValueKind == JsonValueKind.String)
            {
                string? nestedTypeString = nestedTypeElement.GetString()?.Replace(" ", "").ToUpper();

                if (nestedTypeString != null && Enum.TryParse(nestedTypeString, true, out DiffusionTypes nestedModelType))
                {
                    return nestedModelType;
                }
            }

            return DiffusionTypes.UNASSIGNED;
        }

        /// <summary>
        /// Extracts the model version name from a Civitai API response.
        /// </summary>
        /// <param name="modelInfoApiResponse">The raw JSON response.</param>
        /// <returns>The version name, or null if not found.</returns>
        internal static string? GetModelVersionName(string modelInfoApiResponse)
        {
            using JsonDocument doc = JsonDocument.Parse(modelInfoApiResponse);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("name", out JsonElement nameElement) &&
                nameElement.ValueKind == JsonValueKind.String)
            {
                return nameElement.GetString();
            }

            return null;
        }
    }
}
