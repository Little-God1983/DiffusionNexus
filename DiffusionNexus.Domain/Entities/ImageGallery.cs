
namespace DiffusionNexus.Domain.Entities
{
    public class ImageGallery
    {
        /// <summary>Local database ID.</summary>
        public int Id { get; set; }

        /// <summary>Parent settings ID.</summary>
        public int AppSettingsId { get; set; }

        /// <summary>Path to the LoRA folder.</summary>
        public string FolderPath { get; set; } = string.Empty;

        /// <summary>Whether this source is enabled for scanning.</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>Display order.</summary>
        public int Order { get; set; }

        /// <summary>Navigation property to parent settings.</summary>
        public AppSettings? AppSettings { get; set; }
    }
}
