namespace DiffusionNexus.UI.Services.ConfigurationChecker.Models;

/// <summary>
/// Overall installation status for a configuration against a ComfyUI instance.
/// </summary>
public enum ConfigurationStatus
{
    /// <summary>All custom nodes and models are installed. (Green)</summary>
    Full,

    /// <summary>Some custom nodes or models are installed. (Yellow)</summary>
    Partial,

    /// <summary>No custom nodes or models are installed. (Red)</summary>
    None
}
