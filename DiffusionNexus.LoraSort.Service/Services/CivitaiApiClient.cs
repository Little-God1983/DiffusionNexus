using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace DiffusionNexus.LoraSort.Service.Services
{
    /// <summary>
    /// Default implementation for communicating with the Civitai API.
    /// </summary>
    public class CivitaiApiClient : ICivitaiApiClient
    {
        private readonly HttpClient _httpClient;

        public CivitaiApiClient(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

            if (_httpClient.BaseAddress == null)
            {
                _httpClient.BaseAddress = new Uri("https://civitai.com/api/v1/");
            }
        }

        private async Task<string> SendGetAsync(string url, string apiKey)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", apiKey);
            }

            HttpResponseMessage response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        public Task<string> GetModelVersionByHashAsync(string sha256Hash, string apiKey = "")
        {
            return SendGetAsync($"model-versions/by-hash/{sha256Hash}", apiKey);
        }

        public Task<string> GetModelAsync(string modelId, string apiKey = "")
        {
            return SendGetAsync($"models/{modelId}", apiKey);
        }

        public Task<string> GetModelsAsync(string query = "", string apiKey = "")
        {
            var path = string.IsNullOrWhiteSpace(query) ? "models" : $"models?{query}";
            return SendGetAsync(path, apiKey);
        }

        public Task<string> GetModelVersionAsync(string versionId, string apiKey = "")
        {
            return SendGetAsync($"model-versions/{versionId}", apiKey);
        }

        public Task<string> GetModelVersionsByModelIdAsync(string modelId, string apiKey = "")
        {
            return SendGetAsync($"models/{modelId}/versions", apiKey);
        }

        public Task<string> GetImagesAsync(string query = "", string apiKey = "")
        {
            var path = string.IsNullOrWhiteSpace(query) ? "images" : $"images?{query}";
            return SendGetAsync(path, apiKey);
        }

        public Task<string> GetImageAsync(string imageId, string apiKey = "")
        {
            return SendGetAsync($"images/{imageId}", apiKey);
        }

        public Task<string> GetTagsAsync(string query = "", string apiKey = "")
        {
            var path = string.IsNullOrWhiteSpace(query) ? "tags" : $"tags?{query}";
            return SendGetAsync(path, apiKey);
        }

        public Task<string> GetUserAsync(string userId, string apiKey = "")
        {
            return SendGetAsync($"users/{userId}", apiKey);
        }

        public Task<string> GetPostsAsync(string query = "", string apiKey = "")
        {
            var path = string.IsNullOrWhiteSpace(query) ? "posts" : $"posts?{query}";
            return SendGetAsync(path, apiKey);
        }

        public Task<string> GetPostAsync(string postId, string apiKey = "")
        {
            return SendGetAsync($"posts/{postId}", apiKey);
        }
    }
}

