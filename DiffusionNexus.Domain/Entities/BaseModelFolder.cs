namespace DiffusionNexus.Domain.Entities
{
    /// <summary>
    /// A registered model storage root ("Base Model Folder") — a directory that contains
    /// (or receives) the ComfyUI-style model subfolders (<c>diffusion_models/</c>,
    /// <c>loras/</c>, <c>text_encoders/</c>, <c>vae/</c>, …). Rows are either added manually
    /// in Settings or auto-registered when an installation is added in the Installer Manager.
    /// The row flagged <see cref="IsDefault"/> is the default download target for
    /// Diffusion Nexus Core workloads.
    /// </summary>
    public class BaseModelFolder : BaseEntity
    {
        /// <summary>Parent settings ID (singleton row, always 1).</summary>
        public int AppSettingsId { get; set; }

        /// <summary>Absolute path of the models root.</summary>
        public string FolderPath { get; set; } = string.Empty;

        /// <summary>Whether this folder participates in scanning and the download dropdown.</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>Display + scan order.</summary>
        public int Order { get; set; }

        /// <summary>
        /// Default download target marker. At most one row is default —
        /// enforced by <c>AppSettingsService</c> on save, not by the database.
        /// </summary>
        public bool IsDefault { get; set; }

        /// <summary>Navigation property to parent settings.</summary>
        public AppSettings? AppSettings { get; set; }

        /// <summary>
        /// Optional FK to the installer package whose model folders were auto-registered.
        /// Null for manually added folders; set to null when the package is removed
        /// (the folder row survives as a user-owned entry).
        /// </summary>
        public int? InstallerPackageId { get; set; }

        /// <summary>Navigation to the owning installer package (null for manual rows).</summary>
        public InstallerPackage? InstallerPackage { get; set; }
    }
}
