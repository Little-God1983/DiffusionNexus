namespace DiffusionNexus.Service.Classes
{
    using System.Text.Json.Serialization;

    namespace CivitaiModels
    {
        public class Creator
        {
            [JsonPropertyName("username")]
            public string? Username { get; set; }

            [JsonPropertyName("image")]
            public string? Image { get; set; }
        }
    }
}
