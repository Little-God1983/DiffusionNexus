
namespace DiffusionNexus.Service.Classes
{
    using System.Text.Json.Serialization;

    namespace CivitaiModels
    {
        public class ModelStats
        {
            [JsonPropertyName("downloadCount")]
            public int DownloadCount { get; set; }

            [JsonPropertyName("thumbsUpCount")]
            public int ThumbsUpCount { get; set; }

            [JsonPropertyName("thumbsDownCount")]
            public int ThumbsDownCount { get; set; }

            [JsonPropertyName("commentCount")]
            public int CommentCount { get; set; }

            [JsonPropertyName("tippedAmountCount")]
            public int TippedAmountCount { get; set; }
        }
    }
}
