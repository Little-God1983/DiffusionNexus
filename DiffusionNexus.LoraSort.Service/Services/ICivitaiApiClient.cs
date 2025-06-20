using System.Threading.Tasks;

namespace DiffusionNexus.LoraSort.Service.Services
{
    /// <summary>
    /// Abstraction over the Civitai HTTP API. Allows mocking in tests and
    /// centralises all endpoint calls.
    /// </summary>
    public interface ICivitaiApiClient
    {
        Task<string> GetModelVersionByHashAsync(string sha256Hash, string apiKey = "");
        Task<string> GetModelAsync(string modelId, string apiKey = "");
        Task<string> GetModelsAsync(string query = "", string apiKey = "");
        Task<string> GetModelVersionAsync(string versionId, string apiKey = "");
        Task<string> GetModelVersionsByModelIdAsync(string modelId, string apiKey = "");
        Task<string> GetImagesAsync(string query = "", string apiKey = "");
        Task<string> GetImageAsync(string imageId, string apiKey = "");
        Task<string> GetTagsAsync(string query = "", string apiKey = "");
        Task<string> GetUserAsync(string userId, string apiKey = "");
        Task<string> GetPostsAsync(string query = "", string apiKey = "");
        Task<string> GetPostAsync(string postId, string apiKey = "");
    }
}

