using System.Text.Json.Serialization;

namespace DiffusionNexus.UI.Classes
{
    public class SettingsModel
    {
        public string? EncryptedCivitaiApiKey { get; set; }
        public string? LoraHelperFolderPath { get; set; }
    }
}
