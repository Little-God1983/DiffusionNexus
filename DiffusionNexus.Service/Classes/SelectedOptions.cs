namespace DiffusionNexus.Service.Classes
{
    public class SelectedOptions
    {
        public string BasePath { get; set; } = string.Empty;
        public string TargetPath { get; set; } = string.Empty;
        public bool IsMoveOperation { get; set; }
        public bool OverrideFiles { get; set; }
        public bool CreateBaseFolders { get; set; }
        public bool UseCustomMappings { get; set; }
        public bool DeleteEmptySourceFolders { get; set; }
        /// <summary>
        /// Optional Civitai API key used for authenticated requests.
        /// Leave empty to perform anonymous requests.
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;
    }
}
