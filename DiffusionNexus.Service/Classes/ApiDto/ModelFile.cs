namespace DiffusionNexus.Service.Classes
{
    using System;
    using System.Text.Json.Serialization;

    namespace CivitaiModels
    {
        public class ModelFile
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("sizeKB")]
            public double SizeKB { get; set; }

            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("pickleScanResult")]
            public string? PickleScanResult { get; set; }

            [JsonPropertyName("pickleScanMessage")]
            public string? PickleScanMessage { get; set; }

            [JsonPropertyName("virusScanResult")]
            public string? VirusScanResult { get; set; }

            [JsonPropertyName("virusScanMessage")]
            public string? VirusScanMessage { get; set; }

            [JsonPropertyName("scannedAt")]
            public DateTime ScannedAt { get; set; }

            [JsonPropertyName("metadata")]
            public FileMetadata? Metadata { get; set; }

            [JsonPropertyName("hashes")]
            public FileHashes? Hashes { get; set; }

            [JsonPropertyName("downloadUrl")]
            public string? DownloadUrl { get; set; }

            [JsonPropertyName("primary")]
            public bool Primary { get; set; }
        }
    }
}
