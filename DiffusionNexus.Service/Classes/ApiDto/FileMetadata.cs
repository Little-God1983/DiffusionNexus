
namespace DiffusionNexus.Service.Classes
{
    using System.Text.Json.Serialization;

    namespace CivitaiModels
    {
        public class FileMetadata
        {
            [JsonPropertyName("format")]
            public string Format { get; set; }
        }
    }
}
