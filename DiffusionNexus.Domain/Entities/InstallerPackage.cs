using DiffusionNexus.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace DiffusionNexus.Domain.Entities
{
    public class InstallerPackage : BaseEntity
    {
        /// <summary>
        /// Display name for the installation (e.g. "Stable Diffusion WebUI Forge").
        /// </summary>
        public required string Name { get; set; }

        /// <summary>
        /// The root directory of the installation. 
        /// Used to infer model/output folders and startup scripts.
        /// </summary>

        public required string InstallationPath { get; set; }

        /// <summary>
        /// The specific software type (A1111, Forge, ComfyUI, etc.) used for behavior logic.
        /// </summary>
        public InstallerType Type { get; set; } = InstallerType.Unknown;

        /// <summary>
        /// Command line arguments to pass during startup (e.g. "--xformers --listen").
        /// </summary>
        public string Arguments { get; set; } = string.Empty;

        /// <summary>
        /// Optional: Override for the executable/script path if non-standard.
        /// TODO: Linux Implementation - Consider separate path or platform-agnostic handling.
        /// </summary>
        public required string? ExecutablePath { get; set; }

        /// <summary>
        /// The currently installed version or commit hash (e.g. "v1.10.1", "0b26121").
        /// </summary>
        public string? Version { get; set; } = string.Empty;

        /// <summary>
        /// The git branch being tracked (e.g. "main", "dev").
        /// </summary>
        public string? Branch { get; set; } = string.Empty;

        /// <summary>
        /// Path to a thumbnail or icon.
        /// </summary>
        public string? IconPath { get; set; } = string.Empty;

        /// <summary>
        /// Indicates if an update is available (cached state).
        /// </summary>
        public bool IsUpdateAvailable { get; set; } = false;
        
    }
}
