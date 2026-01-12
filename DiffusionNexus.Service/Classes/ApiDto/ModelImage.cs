namespace DiffusionNexus.Service.Classes
{
    using System.Text.Json.Serialization;

    namespace CivitaiModels
    {
        public class ModelImage
        {
            [JsonPropertyName("url")]
            public string? Url { get; set; }

            [JsonPropertyName("nsfwLevel")]
            public int NsfwLevel { get; set; }

            [JsonPropertyName("width")]
            public int Width { get; set; }

            [JsonPropertyName("height")]
            public int Height { get; set; }

            [JsonPropertyName("hash")]
            public string? Hash { get; set; }

            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("minor")]
            public bool Minor { get; set; }

            [JsonPropertyName("poi")]
            public bool Poi { get; set; }

            [JsonPropertyName("hasMeta")]
            public bool HasMeta { get; set; }

            [JsonPropertyName("hasPositivePrompt")]
            public bool HasPositivePrompt { get; set; }

            [JsonPropertyName("onSite")]
            public bool OnSite { get; set; }

            [JsonPropertyName("remixOfId")]
            public int? RemixOfId { get; set; }
        }
    }
}
