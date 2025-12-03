
namespace DiffusionNexus.Service.Classes
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    namespace CivitaiModels
    {
        public class ModelVersion
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("index")]
            public int Index { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; }

            [JsonPropertyName("baseModel")]
            public string BaseModel { get; set; }

            [JsonPropertyName("baseModelType")]
            public string BaseModelType { get; set; }

            [JsonPropertyName("createdAt")]
            public DateTime CreatedAt { get; set; }

            [JsonPropertyName("publishedAt")]
            public DateTime PublishedAt { get; set; }

            [JsonPropertyName("status")]
            public string Status { get; set; }

            [JsonPropertyName("availability")]
            public string Availability { get; set; }

            [JsonPropertyName("nsfwLevel")]
            public int NsfwLevel { get; set; }

            [JsonPropertyName("description")]
            public string Description { get; set; }

            [JsonPropertyName("trainedWords")]
            public List<string> TrainedWords { get; set; }

            [JsonPropertyName("covered")]
            public bool Covered { get; set; }

            [JsonPropertyName("stats")]
            public ModelStats Stats { get; set; }

            [JsonPropertyName("files")]
            public List<ModelFile> Files { get; set; }

            [JsonPropertyName("images")]
            public List<ModelImage> Images { get; set; }

            [JsonPropertyName("downloadUrl")]
            public string DownloadUrl { get; set; }
        }
    }
}
