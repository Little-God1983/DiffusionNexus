
namespace DiffusionNexus.Service.Classes
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    namespace CivitaiModels
    {
        public class ModelData
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; }

            [JsonPropertyName("description")]
            public string Description { get; set; }

            [JsonPropertyName("allowNoCredit")]
            public bool AllowNoCredit { get; set; }

            [JsonPropertyName("allowCommercialUse")]
            public string AllowCommercialUse { get; set; }

            [JsonPropertyName("allowDerivatives")]
            public bool AllowDerivatives { get; set; }

            [JsonPropertyName("allowDifferentLicense")]
            public bool AllowDifferentLicense { get; set; }

            [JsonPropertyName("type")]
            public string Type { get; set; }

            [JsonPropertyName("minor")]
            public bool Minor { get; set; }

            [JsonPropertyName("sfwOnly")]
            public bool SfwOnly { get; set; }

            [JsonPropertyName("poi")]
            public bool Poi { get; set; }

            [JsonPropertyName("nsfw")]
            public bool Nsfw { get; set; }

            [JsonPropertyName("nsfwLevel")]
            public int NsfwLevel { get; set; }

            [JsonPropertyName("availability")]
            public string Availability { get; set; }

            [JsonPropertyName("userId")]
            public int UserId { get; set; }

            [JsonPropertyName("cosmetic")]
            public object? Cosmetic { get; set; }

            [JsonPropertyName("supportsGeneration")]
            public bool SupportsGeneration { get; set; }

            [JsonPropertyName("stats")]
            public ModelStats Stats { get; set; }

            [JsonPropertyName("creator")]
            public Creator Creator { get; set; }

            [JsonPropertyName("tags")]
            public List<string> Tags { get; set; }

            [JsonPropertyName("modelVersions")]
            public List<ModelVersion> ModelVersions { get; set; }
        }
    }
}
