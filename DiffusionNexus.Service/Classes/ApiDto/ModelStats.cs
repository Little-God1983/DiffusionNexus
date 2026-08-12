
namespace DiffusionNexus.Service.Classes
{
    using System.Text.Json.Serialization;

    namespace CivitaiModels
    {
        /// <summary>
        /// Counters are null on freshly published models (stats not yet computed
        /// server-side) — the tolerant converter reads those as 0.
        /// </summary>
        public class ModelStats
        {
            [JsonPropertyName("downloadCount")]
            [JsonConverter(typeof(DiffusionNexus.Civitai.Models.TolerantInt32JsonConverter))]
            public int DownloadCount { get; set; }

            [JsonPropertyName("thumbsUpCount")]
            [JsonConverter(typeof(DiffusionNexus.Civitai.Models.TolerantInt32JsonConverter))]
            public int ThumbsUpCount { get; set; }

            [JsonPropertyName("thumbsDownCount")]
            [JsonConverter(typeof(DiffusionNexus.Civitai.Models.TolerantInt32JsonConverter))]
            public int ThumbsDownCount { get; set; }

            [JsonPropertyName("commentCount")]
            [JsonConverter(typeof(DiffusionNexus.Civitai.Models.TolerantInt32JsonConverter))]
            public int CommentCount { get; set; }

            [JsonPropertyName("tippedAmountCount")]
            [JsonConverter(typeof(DiffusionNexus.Civitai.Models.TolerantInt32JsonConverter))]
            public int TippedAmountCount { get; set; }
        }
    }
}
